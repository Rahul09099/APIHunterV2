using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Base;
using UnsecuredAPIKeys.Providers.Common;

namespace UnsecuredAPIKeys.Providers.AI_Providers
{
    /// <summary>
    /// Provider for Together AI API keys.
    ///
    /// Standard API Base: https://api.together.ai
    /// Auth: Authorization: Bearer {key}
    ///
    /// Verification strategy:
    ///   1. Discovery: GET https://api.together.ai/v1/models (officially documented lightweight read-only endpoint)
    ///      200 OK -> Key authentication valid + returns models catalog
    ///      401 -> Invalid / expired key
    ///      403 -> ValidationUnavailable (access restricted)
    ///      429 -> ValidationUnavailable (rate limit)
    ///      5xx -> ValidationUnavailable (service error)
    ///   2. Active inference test: POST https://api.together.ai/v1/chat/completions
    ///      Model selected from discovered catalog (e.g., meta-llama/Llama-3.3-70B-Instruct-Turbo)
    ///      {"messages": [{"role": "user", "content": "hi"}], "max_tokens": 1}
    /// Official docs: https://docs.together.ai/docs/api-keys-authentication
    /// </summary>
    [ApiProvider]
    public class TogetherAIProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "Together AI";
        public override ApiTypeEnum ApiType => ApiTypeEnum.TogetherAI;

        public override IEnumerable<string> RegexPatterns =>
        [
            // Context-anchored regex patterns to prevent false positives
            @"(?i)\bTOGETHER[\s_-]*API[\s_-]*KEY\s*[:=]\s*['""]?([A-Za-z0-9_-]{20,256})['""]?",
            @"(?i)\bTOGETHER[\s_-]*KEY\s*[:=]\s*['""]?([A-Za-z0-9_-]{20,256})['""]?",
            @"(?i)\bTOGETHER[\s_-]*TOKEN\s*[:=]\s*['""]?([A-Za-z0-9_-]{20,256})['""]?"
        ];

        public TogetherAIProvider() : base() { }
        public TogetherAIProvider(ILogger<TogetherAIProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(string apiKey, HttpClient httpClient)
        {
            try
            {
                // Step 1: Discover models via GET /v1/models (read-only lightweight auth check on api.together.ai)
                using var modelsRequest = new HttpRequestMessage(HttpMethod.Get, "https://api.together.ai/v1/models");
                modelsRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

                var modelsResponse = await httpClient.SendAsync(modelsRequest);
                string modelsBody = await modelsResponse.Content.ReadAsStringAsync();

                _logger?.LogDebug("Together AI models response: Status={Status}, Body={Body}",
                    modelsResponse.StatusCode, TruncateResponse(modelsBody));

                if (modelsResponse.StatusCode == HttpStatusCode.Unauthorized)
                {
                    return ValidationResult.IsUnauthorized(modelsResponse.StatusCode,
                        "Invalid or expired Together AI API key");
                }

                if (modelsResponse.StatusCode == HttpStatusCode.Forbidden)
                {
                    return ValidationResult.ValidationUnavailable(modelsResponse.StatusCode,
                        "Together AI key access forbidden (403)");
                }

                if (modelsResponse.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    return ValidationResult.ValidationUnavailable(modelsResponse.StatusCode,
                        "Together AI models endpoint rate limited (429)");
                }

                if ((int)modelsResponse.StatusCode >= 500)
                {
                    return ValidationResult.ValidationUnavailable(modelsResponse.StatusCode,
                        $"Together AI service error ({modelsResponse.StatusCode}) — validation unavailable");
                }

                if (!IsSuccessStatusCode(modelsResponse.StatusCode))
                {
                    return ValidationResult.HasHttpError(modelsResponse.StatusCode,
                        $"Together AI models request failed: Status {modelsResponse.StatusCode}. Body: {TruncateResponse(modelsBody)}");
                }

                // Authentication confirmed via /v1/models
                List<ModelInfo>? discoveredModels = ParseModels(modelsBody);

                var result = ValidationResult.Success(modelsResponse.StatusCode, "Valid Together AI key");
                result.AvailableModels = discoveredModels;
                result.RawResponse = modelsBody;
                result.Metadata = new Dictionary<string, object>
                {
                    ["authentication_valid"] = true,
                    ["models_parsed"] = discoveredModels != null,
                    ["model_count"] = discoveredModels?.Count ?? 0
                };

                if (discoveredModels == null || discoveredModels.Count == 0)
                {
                    result.Metadata["inference_tested"] = false;
                    result.Metadata["inference_working"] = false;
                    result.Detail = "Valid Together AI key, but no available models were returned for inference testing.";
                    return result;
                }

                var preferredOrder = new[]
                {
                    "meta-llama/Llama-3.3-70B-Instruct-Turbo",
                    "meta-llama/Meta-Llama-3.1-8B-Instruct-Turbo",
                    "meta-llama/Meta-Llama-3-8B-Instruct",
                    "mistralai/Mistral-7B-Instruct-v0.1"
                };

                string modelToUse = discoveredModels
                    .Select(m => m.ModelId)
                    .FirstOrDefault(id => preferredOrder.Any(p => id.Equals(p, StringComparison.OrdinalIgnoreCase)))
                    ?? discoveredModels.First().ModelId;

                try
                {
                    using var chatRequest = new HttpRequestMessage(HttpMethod.Post, "https://api.together.ai/v1/chat/completions");
                    chatRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

                    var payload = new
                    {
                        model = modelToUse,
                        messages = new[] { new { role = "user", content = "hi" } },
                        max_tokens = 1
                    };

                    chatRequest.Content = new StringContent(
                        JsonSerializer.Serialize(payload),
                        System.Text.Encoding.UTF8,
                        "application/json");

                    using var chatResponse = await httpClient.SendAsync(chatRequest);
                    string chatBody = await chatResponse.Content.ReadAsStringAsync();

                    _logger?.LogDebug("Together AI chat response ({Model}): Status={Status}, Body={Body}",
                        modelToUse, chatResponse.StatusCode, TruncateResponse(chatBody));

                    result.RawResponse = chatBody;
                    result.Metadata["inference_tested"] = true;
                    result.Metadata["tested_model"] = modelToUse;

                    if (IsSuccessStatusCode(chatResponse.StatusCode))
                    {
                        result.Metadata["inference_working"] = true;
                        result.Detail = $"Valid Together AI key — Chat completions verified with model '{modelToUse}'.";
                    }
                    else if (chatResponse.StatusCode == HttpStatusCode.PaymentRequired || ContainsAny(chatBody, QuotaIndicators))
                    {
                        result.Metadata["inference_working"] = false;
                        result.IsQuotaExceeded = true;
                        result.Detail = $"Valid Together AI key — insufficient credits or quota limit reached on model '{modelToUse}'.";
                    }
                    else if (chatResponse.StatusCode == HttpStatusCode.Forbidden)
                    {
                        result.Metadata["inference_working"] = false;
                        result.Detail = $"Valid Together AI key — inference access forbidden (403) for model '{modelToUse}'.";
                    }
                    else if (chatResponse.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        result.Metadata["inference_working"] = false;
                        result.Detail = $"Valid Together AI key — inference rate limited (429) on model '{modelToUse}'.";
                    }
                    else
                    {
                        // Auth succeeded at Step 1; non-200 here is an operation/model restriction
                        result.Metadata["inference_working"] = false;
                        result.Detail = $"Valid Together AI key — authenticated, but inference request returned status {chatResponse.StatusCode}.";
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning("Together AI chat completion test failed: {Message}", ex.Message);
                    result.Metadata["inference_tested"] = false;
                    result.Metadata["inference_working"] = false;
                }

                return result;
            }
            catch (Exception ex)
            {
                return ValidationResult.HasNetworkError(ex.Message);
            }
        }

        private List<ModelInfo>? ParseModels(string jsonResponse)
        {
            try
            {
                using var doc = JsonDocument.Parse(jsonResponse);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    return doc.RootElement.EnumerateArray()
                        .Select(m => new ModelInfo { ModelId = m.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "" })
                        .Where(m => !string.IsNullOrEmpty(m.ModelId))
                        .ToList();
                }

                if (doc.RootElement.TryGetProperty("data", out var dataArr) && dataArr.ValueKind == JsonValueKind.Array)
                {
                    return dataArr.EnumerateArray()
                        .Select(m => new ModelInfo { ModelId = m.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "" })
                        .Where(m => !string.IsNullOrEmpty(m.ModelId))
                        .ToList();
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        protected override bool IsValidKeyFormat(string apiKey)
        {
            // Together AI keys are project-scoped tokens (typically 20-256 chars)
            return !string.IsNullOrWhiteSpace(apiKey) &&
                   apiKey.Length >= 20 &&
                   apiKey.Length <= 256;
        }
    }
}

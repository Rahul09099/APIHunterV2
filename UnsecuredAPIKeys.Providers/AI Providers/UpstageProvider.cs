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
    /// Provider for Upstage API keys — Solar LLM and Document AI platform.
    ///
    /// Key format: alphanumeric string, stored in UPSTAGE_API_KEY or SOLAR_API_KEY env var.
    /// Auth: Authorization: Bearer {apiKey}
    /// Base URL: https://api.upstage.ai/v1
    ///
    /// Verification strategy:
    ///   1. Discovery: GET https://api.upstage.ai/v1/models (lightweight read-only auth check)
    ///   2. Active inference test: POST https://api.upstage.ai/v1/chat/completions
    ///      Model: solar-pro3 (or preferred discovered model)
    ///      {"messages": [{"role": "user", "content": "hi"}], "max_tokens": 1}
    /// Official docs: https://console.upstage.ai/docs
    /// </summary>
    [ApiProvider]
    public class UpstageProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "Upstage (Solar)";
        public override ApiTypeEnum ApiType => ApiTypeEnum.Upstage;

        public override IEnumerable<string> RegexPatterns =>
        [
            // Context-anchored env var patterns to prevent false positives
            @"UPSTAGE_API_KEY",
            @"SOLAR_API_KEY",
            @"(?i)\bUPSTAGE[\s_-]*API[\s_-]*KEY\s*[:=]\s*['""]?([A-Za-z0-9_-]{20,256})['""]?",
            @"(?i)\bSOLAR[\s_-]*API[\s_-]*KEY\s*[:=]\s*['""]?([A-Za-z0-9_-]{20,256})['""]?"
        ];

        public UpstageProvider() : base() { }
        public UpstageProvider(ILogger<UpstageProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(string apiKey, HttpClient httpClient)
        {
            try
            {
                // Step 1: Discover available models via GET /v1/models.
                // If this endpoint is unavailable, authentication validity remains inconclusive.
                using var modelsRequest = new HttpRequestMessage(HttpMethod.Get, "https://api.upstage.ai/v1/models");
                modelsRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

                var modelsResponse = await httpClient.SendAsync(modelsRequest);
                string modelsBody = await modelsResponse.Content.ReadAsStringAsync();

                _logger?.LogDebug("Upstage models response: Status={Status}, Body={Body}",
                    modelsResponse.StatusCode, TruncateResponse(modelsBody));

                if (modelsResponse.StatusCode == HttpStatusCode.Unauthorized)
                {
                    return ValidationResult.IsUnauthorized(modelsResponse.StatusCode,
                        "Invalid or expired Upstage API key");
                }

                if (modelsResponse.StatusCode == HttpStatusCode.Forbidden)
                {
                    return ValidationResult.ValidationUnavailable(modelsResponse.StatusCode,
                        "Upstage API key access forbidden (403)");
                }

                if (modelsResponse.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    return ValidationResult.ValidationUnavailable(modelsResponse.StatusCode,
                        "Upstage models endpoint rate limited (429)");
                }

                if ((int)modelsResponse.StatusCode >= 500)
                {
                    return ValidationResult.ValidationUnavailable(modelsResponse.StatusCode,
                        $"Upstage service error ({modelsResponse.StatusCode}) — validation unavailable");
                }

                if (!IsSuccessStatusCode(modelsResponse.StatusCode))
                {
                    return ValidationResult.HasHttpError(modelsResponse.StatusCode,
                        $"Upstage models request failed: Status {modelsResponse.StatusCode}. Body: {TruncateResponse(modelsBody)}");
                }

                // Model catalog parsed
                List<ModelInfo>? discoveredModels = ParseModels(modelsBody);

                var result = ValidationResult.Success(modelsResponse.StatusCode, "Valid Upstage key");
                result.AvailableModels = discoveredModels;
                result.RawResponse = modelsBody;
                result.Balance = "Not available from validation endpoint";
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
                    result.Detail = "Valid Upstage key, but no available models were returned for inference testing.";
                    return result;
                }

                // Step 2: Active inference test via POST /v1/chat/completions with solar-pro3
                var preferredOrder = new[]
                {
                    "solar-pro3",
                    "solar-pro",
                    "solar-mini",
                    "solar-10.7b-instruct"
                };

                string modelToUse = discoveredModels
                    .Select(m => m.ModelId)
                    .FirstOrDefault(id => preferredOrder.Any(p => id.Equals(p, StringComparison.OrdinalIgnoreCase)))
                    ?? discoveredModels.First().ModelId;

                try
                {
                    using var chatRequest = new HttpRequestMessage(HttpMethod.Post, "https://api.upstage.ai/v1/chat/completions");
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

                    _logger?.LogDebug("Upstage chat response ({Model}): Status={Status}, Body={Body}",
                        modelToUse, chatResponse.StatusCode, TruncateResponse(chatBody));

                    result.RawResponse = chatBody;
                    result.Metadata["inference_tested"] = true;
                    result.Metadata["tested_model"] = modelToUse;

                    if (IsSuccessStatusCode(chatResponse.StatusCode))
                    {
                        result.Metadata["inference_working"] = true;
                        result.Detail = $"Valid Upstage key — Chat completions verified with model '{modelToUse}'.";
                    }
                    else if (chatResponse.StatusCode == HttpStatusCode.PaymentRequired || ContainsAny(chatBody, QuotaIndicators))
                    {
                        result.Metadata["inference_working"] = false;
                        result.IsQuotaExceeded = true;
                        result.Detail = $"Valid Upstage key — insufficient credits or quota limit reached on model '{modelToUse}'.";
                    }
                    else if (chatResponse.StatusCode == HttpStatusCode.Forbidden)
                    {
                        result.Metadata["inference_working"] = false;
                        result.Detail = $"Valid Upstage key — chat completion forbidden (403) for model '{modelToUse}'.";
                    }
                    else if (chatResponse.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        result.Metadata["inference_working"] = false;
                        result.Detail = $"Inference validation rate limited (429) on model '{modelToUse}'.";
                    }
                    else
                    {
                        result.Metadata["inference_working"] = false;
                        result.Detail = $"Valid Upstage key — authenticated, but inference request returned status {chatResponse.StatusCode}.";
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning("Upstage chat completion test failed: {Message}", ex.Message);
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
            return !string.IsNullOrWhiteSpace(apiKey) &&
                   apiKey.Length >= 20 &&
                   apiKey.Length <= 256;
        }
    }
}

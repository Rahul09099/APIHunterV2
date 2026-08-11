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
    /// Provider for X.AI (Grok) API keys.
    ///
    /// Key format: xai-{alphanumeric} (e.g., xai-1234567890abcdef...)
    /// Auth: Authorization: Bearer {apiKey}
    /// Base URL: https://api.x.ai/v1
    ///
    /// Verification strategy:
    ///   1. Discovery: GET https://api.x.ai/v1/models (read-only auth check & model discovery)
    ///   2. Active inference test: POST https://api.x.ai/v1/chat/completions
    ///      Model: grok-2-latest (or preferred discovered Grok model)
    ///      {"messages": [{"role": "user", "content": "hi"}], "max_tokens": 1}
    /// Official docs: https://docs.x.ai/developers
    /// </summary>
    [ApiProvider]
    public class XAIProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "X.AI";
        public override ApiTypeEnum ApiType => ApiTypeEnum.XAI;

        public override IEnumerable<string> RegexPatterns =>
        [
            @"\bxai-[A-Za-z0-9]{32,}\b",
            @"XAI_API_KEY",
            @"XAI_SECRET",
            @"XAI_TOKEN",
            @"(?i)\bXAI[\s_-]*API[\s_-]*KEY\s*[:=]\s*['""]?(xai-[A-Za-z0-9_-]{20,})['""]?"
        ];

        public XAIProvider() : base() { }
        public XAIProvider(ILogger<XAIProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(string apiKey, HttpClient httpClient)
        {
            try
            {
                // Step 1: Discover available models via GET /v1/models.
                // If this endpoint is unavailable, authentication validity remains inconclusive.
                using var modelsRequest = new HttpRequestMessage(HttpMethod.Get, "https://api.x.ai/v1/models");
                modelsRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

                var modelsResponse = await httpClient.SendAsync(modelsRequest);
                string modelsBody = await modelsResponse.Content.ReadAsStringAsync();

                _logger?.LogDebug("X.AI models response: Status={Status}, Body={Body}",
                    modelsResponse.StatusCode, TruncateResponse(modelsBody));

                if (modelsResponse.StatusCode == HttpStatusCode.Unauthorized)
                {
                    return ValidationResult.IsUnauthorized(modelsResponse.StatusCode,
                        "Invalid or expired X.AI API key");
                }

                if (modelsResponse.StatusCode == HttpStatusCode.Forbidden)
                {
                    return ValidationResult.ValidationUnavailable(modelsResponse.StatusCode,
                        "X.AI API key access forbidden (403)");
                }

                if (modelsResponse.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    return ValidationResult.ValidationUnavailable(modelsResponse.StatusCode,
                        "X.AI models endpoint rate limited (429)");
                }

                if ((int)modelsResponse.StatusCode >= 500)
                {
                    return ValidationResult.ValidationUnavailable(modelsResponse.StatusCode,
                        $"X.AI service error ({modelsResponse.StatusCode}) — validation unavailable");
                }

                if (!IsSuccessStatusCode(modelsResponse.StatusCode))
                {
                    return ValidationResult.HasHttpError(modelsResponse.StatusCode,
                        $"X.AI models request failed: Status {modelsResponse.StatusCode}. Body: {TruncateResponse(modelsBody)}");
                }

                // Model catalog parsed
                List<ModelInfo>? discoveredModels = ParseModels(modelsBody);

                var result = ValidationResult.Success(modelsResponse.StatusCode, "Valid X.AI key");
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
                    result.Detail = "Valid X.AI key, but no available models were returned for inference testing.";
                    return result;
                }

                // Step 2: Active inference test via POST /v1/chat/completions
                // Dynamically select a Grok model from the returned catalog available to this API key
                var grokModel = discoveredModels
                    .Select(m => m.ModelId)
                    .FirstOrDefault(id => id.Contains("grok", StringComparison.OrdinalIgnoreCase));

                if (string.IsNullOrWhiteSpace(grokModel))
                {
                    result.Metadata["inference_tested"] = false;
                    result.Metadata["inference_working"] = false;
                    result.Detail = "Valid X.AI key, but no Grok chat model is available to this key for inference testing.";
                    return result;
                }

                string modelToUse = grokModel;

                try
                {
                    using var chatRequest = new HttpRequestMessage(HttpMethod.Post, "https://api.x.ai/v1/chat/completions");
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

                    _logger?.LogDebug("X.AI chat response ({Model}): Status={Status}, Body={Body}",
                        modelToUse, chatResponse.StatusCode, TruncateResponse(chatBody));

                    result.RawResponse = chatBody;
                    result.Metadata["inference_tested"] = true;
                    result.Metadata["tested_model"] = modelToUse;

                    if (IsSuccessStatusCode(chatResponse.StatusCode))
                    {
                        result.Metadata["inference_working"] = true;
                        result.Detail = $"Valid X.AI key — Chat completions verified with model '{modelToUse}'.";

                        try
                        {
                            using var doc = JsonDocument.Parse(chatBody);
                            if (doc.RootElement.TryGetProperty("choices", out var choices) &&
                                choices.ValueKind == JsonValueKind.Array &&
                                choices.GetArrayLength() > 0)
                            {
                                var msg = choices[0].GetProperty("message");
                                if (msg.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                                {
                                    result.Metadata["test_response"] = content.GetString() ?? "";
                                }
                            }

                            if (doc.RootElement.TryGetProperty("usage", out var usageProp))
                            {
                                if (usageProp.TryGetProperty("cost_in_usd_ticks", out var ticksProp) && ticksProp.ValueKind == JsonValueKind.Number)
                                {
                                    result.Metadata["cost_in_usd_ticks"] = ticksProp.GetInt64();
                                }
                                if (usageProp.TryGetProperty("total_tokens", out var tokensProp) && tokensProp.ValueKind == JsonValueKind.Number)
                                {
                                    result.Metadata["total_tokens"] = tokensProp.GetInt32();
                                }
                            }
                        }
                        catch { /* Best effort usage parsing */ }
                    }
                    else if (chatResponse.StatusCode == HttpStatusCode.PaymentRequired || ContainsAny(chatBody, QuotaIndicators))
                    {
                        result.Metadata["inference_working"] = false;
                        result.IsQuotaExceeded = true;
                        result.Detail = $"Valid X.AI key — insufficient credits or quota limit reached on model '{modelToUse}'.";
                    }
                    else if (chatResponse.StatusCode == HttpStatusCode.Forbidden)
                    {
                        result.Metadata["inference_working"] = false;
                        result.Detail = $"Valid X.AI key — chat completion forbidden (403) for model '{modelToUse}' (ACL or model permission restriction).";
                    }
                    else if (chatResponse.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        result.Metadata["inference_working"] = false;
                        result.Detail = $"Inference validation rate limited (429) on model '{modelToUse}'.";
                    }
                    else
                    {
                        result.Metadata["inference_working"] = false;
                        result.Detail = $"Valid X.AI key — authenticated, but inference request returned status {chatResponse.StatusCode}.";
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning("X.AI chat completion test failed: {Message}", ex.Message);
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
            // X.AI API keys start with "xai-" per official docs
            return !string.IsNullOrWhiteSpace(apiKey) &&
                   apiKey.StartsWith("xai-", StringComparison.Ordinal);
        }
    }
}

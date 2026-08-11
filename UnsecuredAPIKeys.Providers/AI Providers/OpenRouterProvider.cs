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
    /// Provider for OpenRouter API keys.
    /// OpenRouter is a unified gateway to 400+ models (OpenAI, Anthropic, Google, Meta, etc.)
    /// Keys always start with "sk-or-v1-".
    /// Verification: GET /api/v1/auth/key — returns credits, usage, and key metadata.
    /// Followed by model discovery (GET /api/v1/models) and a minimal inference test (POST /api/v1/chat/completions).
    /// Official docs: https://openrouter.ai/docs/api/authentication
    /// </summary>
    [ApiProvider]
    public class OpenRouterProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "OpenRouter";
        public override ApiTypeEnum ApiType => ApiTypeEnum.OpenRouter;

        public override IEnumerable<string> RegexPatterns =>
        [
            @"\bsk-or-v1-[A-Za-z0-9]{40,80}\b",
            @"OPENROUTER_API_KEY",
            @"openrouter[_-]?key",
            @"OPEN_ROUTER_KEY"
        ];

        public OpenRouterProvider() : base() { }
        public OpenRouterProvider(ILogger<OpenRouterProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(
            string apiKey, HttpClient httpClient)
        {
            // Step 1: GET /api/v1/auth/key — returns key info including credits and usage.
            // Official lightweight authentication endpoint (no generation cost).
            using var authRequest = new HttpRequestMessage(
                HttpMethod.Get, "https://openrouter.ai/api/v1/auth/key");
            authRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            var authResponse = await httpClient.SendAsync(authRequest);
            var authBody = await authResponse.Content.ReadAsStringAsync();

            _logger?.LogDebug("OpenRouter auth/key response: Status={Status}, Body={Body}",
                authResponse.StatusCode, TruncateResponse(authBody));

            // 401 -> invalid or expired key
            if (authResponse.StatusCode == HttpStatusCode.Unauthorized)
            {
                return ValidationResult.IsUnauthorized(authResponse.StatusCode,
                    "Invalid or expired OpenRouter API key");
            }

            // 403 / 429 / 5xx -> validation unavailable at auth check level
            if (authResponse.StatusCode == HttpStatusCode.Forbidden)
            {
                return ValidationResult.ValidationUnavailable(authResponse.StatusCode,
                    "OpenRouter API key access forbidden (403)");
            }

            if (authResponse.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return ValidationResult.ValidationUnavailable(authResponse.StatusCode,
                    "OpenRouter auth endpoint rate limited (429)");
            }

            if ((int)authResponse.StatusCode >= 500)
            {
                return ValidationResult.ValidationUnavailable(authResponse.StatusCode,
                    $"OpenRouter service error ({authResponse.StatusCode}) — validation unavailable");
            }

            if (!authResponse.IsSuccessStatusCode)
            {
                return ValidationResult.HasHttpError(authResponse.StatusCode,
                    $"OpenRouter auth check failed: {TruncateResponse(authBody)}");
            }

            // Key authentication confirmed
            var result = ValidationResult.Success(authResponse.StatusCode, "Valid OpenRouter key");
            result.RawResponse = authBody;

            double usage = 0;
            bool isFreeTier = false;
            double? limit = null;
            double? remaining = null;

            try
            {
                using var doc = JsonDocument.Parse(authBody);
                if (doc.RootElement.TryGetProperty("data", out var data))
                {
                    if (data.TryGetProperty("usage", out var usageProp) && usageProp.ValueKind == JsonValueKind.Number)
                    {
                        usage = usageProp.GetDouble();
                    }

                    if (data.TryGetProperty("is_free_tier", out var freeProp) && freeProp.ValueKind == JsonValueKind.True)
                    {
                        isFreeTier = true;
                    }

                    if (data.TryGetProperty("limit", out var limitProp) && limitProp.ValueKind == JsonValueKind.Number)
                    {
                        limit = limitProp.GetDouble();
                    }

                    if (data.TryGetProperty("limit_remaining", out var limitRemainingProp) &&
                        limitRemainingProp.ValueKind == JsonValueKind.Number)
                    {
                        remaining = limitRemainingProp.GetDouble();
                    }

                    if (isFreeTier)
                    {
                        result.Balance = $"Free Tier Access (Usage: ${usage:F4})";
                    }
                    else if (remaining.HasValue)
                    {
                        string limitInfo = limit.HasValue ? $" / ${limit.Value:F4}" : "";
                        result.Balance = $"${remaining.Value:F4}{limitInfo} remaining";

                        if (remaining.Value <= 0)
                        {
                            result.IsQuotaExceeded = true;
                            result.Detail = "Valid OpenRouter key — no credits remaining.";
                        }
                    }
                    else
                    {
                        result.Balance = $"No key limit (Used: ${usage:F4})";
                    }

                    string tier = isFreeTier ? "Free Tier" : "Paid Tier";
                    if (data.TryGetProperty("label", out var labelProp) && labelProp.ValueKind == JsonValueKind.String)
                    {
                        string? labelStr = labelProp.GetString();
                        if (!string.IsNullOrEmpty(labelStr) && !apiKey.Contains(labelStr))
                        {
                            result.AccountTier = $"{tier} (Label: {labelStr})";
                        }
                        else
                        {
                            result.AccountTier = tier;
                        }
                    }
                    else
                    {
                        result.AccountTier = tier;
                    }

                    result.Metadata = new Dictionary<string, object>();
                    foreach (var prop in data.EnumerateObject())
                    {
                        switch (prop.Value.ValueKind)
                        {
                            case JsonValueKind.String: result.Metadata[prop.Name] = prop.Value.GetString() ?? ""; break;
                            case JsonValueKind.Number: result.Metadata[prop.Name] = prop.Value.GetDouble(); break;
                            case JsonValueKind.True: result.Metadata[prop.Name] = true; break;
                            case JsonValueKind.False: result.Metadata[prop.Name] = false; break;
                            case JsonValueKind.Null: result.Metadata[prop.Name] = "null"; break;
                        }
                    }
                }
            }
            catch
            {
                /* Best effort auth metadata parsing */
            }

            result.Metadata ??= new Dictionary<string, object>();
            result.Metadata["authentication_valid"] = true;

            // Step 2: GET /api/v1/models — discover available models catalog
            List<ModelInfo>? discoveredModels = null;
            try
            {
                using var modelsRequest = new HttpRequestMessage(HttpMethod.Get, "https://openrouter.ai/api/v1/models");
                modelsRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

                var modelsResponse = await httpClient.SendAsync(modelsRequest);
                if (modelsResponse.IsSuccessStatusCode)
                {
                    var modelsBody = await modelsResponse.Content.ReadAsStringAsync();
                    discoveredModels = ParseOpenRouterModels(modelsBody);
                    if (discoveredModels != null && discoveredModels.Count > 0)
                    {
                        result.AvailableModels = discoveredModels;
                        result.Metadata["models_parsed"] = true;
                        result.Metadata["model_count"] = discoveredModels.Count;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug("OpenRouter model listing failed: {Message}", ex.Message);
            }

            if (discoveredModels == null || discoveredModels.Count == 0)
            {
                result.Metadata["inference_tested"] = false;
                result.Detail ??= "Valid OpenRouter key — authenticated (no models returned for inference test).";
                return result;
            }

            var preferredOrder = new[]
            {
                "google/gemini-2.5-flash",
                "google/gemini-2.0-flash-exp:free",
                "meta-llama/llama-3.1-8b-instruct:free",
                "openai/gpt-4o-mini",
                "anthropic/claude-3-haiku"
            };

            string modelToUse = discoveredModels
                .Select(m => m.ModelId)
                .FirstOrDefault(id => preferredOrder.Any(p => id.Equals(p, StringComparison.OrdinalIgnoreCase)))
                ?? discoveredModels.First().ModelId;

            try
            {
                using var chatRequest = new HttpRequestMessage(HttpMethod.Post, "https://openrouter.ai/api/v1/chat/completions");
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
                var chatBody = await chatResponse.Content.ReadAsStringAsync();

                _logger?.LogDebug("OpenRouter chat response ({Model}): Status={Status}, Body={Body}",
                    modelToUse, chatResponse.StatusCode, TruncateResponse(chatBody));

                result.RawResponse = chatBody;
                result.Metadata["inference_tested"] = true;
                result.Metadata["tested_model"] = modelToUse;

                if (IsSuccessStatusCode(chatResponse.StatusCode))
                {
                    result.Metadata["inference_working"] = true;
                    result.Detail = $"Valid OpenRouter key — Chat completions verified with model {modelToUse}.";
                }
                else if (chatResponse.StatusCode == HttpStatusCode.PaymentRequired ||
                         chatBody.Contains("Insufficient credits", StringComparison.OrdinalIgnoreCase) ||
                         chatBody.Contains("out of credits", StringComparison.OrdinalIgnoreCase))
                {
                    result.Metadata["inference_working"] = false;
                    result.IsQuotaExceeded = true;
                    result.Detail = "Valid OpenRouter key — insufficient account credits for inference.";
                    if (string.IsNullOrEmpty(result.Balance))
                    {
                        result.Balance = $"Insufficient account credits (Used: ${usage:F4})";
                    }
                }
                else if (chatResponse.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    result.Metadata["inference_working"] = false;
                    result.Detail = $"Valid OpenRouter key — inference rate limited (429) on model {modelToUse}.";
                }
                else
                {
                    // Note: Auth already succeeded at Step 1, so 401/403 here is an operation/model rejection, NOT an invalid key!
                    result.Metadata["inference_working"] = false;
                    result.Detail = $"Valid OpenRouter key — authenticated, but inference request returned {chatResponse.StatusCode}.";
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning("OpenRouter chat completion test failed with exception: {Message}", ex.Message);
                result.Metadata["inference_tested"] = false;
                result.Metadata["inference_working"] = false;
            }

            return result;
        }

        private List<ModelInfo>? ParseOpenRouterModels(string jsonResponse)
        {
            try
            {
                using var doc = JsonDocument.Parse(jsonResponse);
                if (!doc.RootElement.TryGetProperty("data", out var dataArray) ||
                    dataArray.ValueKind != JsonValueKind.Array)
                {
                    return null;
                }

                var list = new List<ModelInfo>();
                foreach (var el in dataArray.EnumerateArray())
                {
                    if (el.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String)
                    {
                        string id = idProp.GetString() ?? "";
                        if (!string.IsNullOrEmpty(id))
                        {
                            list.Add(new ModelInfo { ModelId = id, DisplayName = id });
                        }
                    }
                }
                return list;
            }
            catch
            {
                return null;
            }
        }

        protected override bool IsValidKeyFormat(string apiKey) =>
            !string.IsNullOrWhiteSpace(apiKey) &&
            apiKey.StartsWith("sk-or-v1-", StringComparison.Ordinal) &&
            apiKey.Length >= 49;
    }
}

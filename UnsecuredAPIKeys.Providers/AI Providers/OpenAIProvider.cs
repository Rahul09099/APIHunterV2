using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Base;
using UnsecuredAPIKeys.Providers.Common;

namespace UnsecuredAPIKeys.Providers.AI_Providers
{
    [ApiProvider]
    public class OpenAIProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "OpenAI";
        public override ApiTypeEnum ApiType => ApiTypeEnum.OpenAI;

        public override IEnumerable<string> RegexPatterns =>
        [
            @"sk-[A-Za-z0-9\-]{20,}",
            @"sk-proj-[A-Za-z0-9\-]{20,}",
            @"sk-svcacct-[A-Za-z0-9\-]{20,}",
            @"sk-[A-Za-z0-9]{48}",
            @"Bearer sk-[A-Za-z0-9\-]{20,}"
        ];

        public OpenAIProvider() : base() { }

        public OpenAIProvider(ILogger<OpenAIProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(string apiKey, HttpClient httpClient)
        {
            // Step 1: Discover models — confirms authentication without consuming inference quota
            using var modelsRequest = new HttpRequestMessage(HttpMethod.Get, "https://api.openai.com/v1/models");
            modelsRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            var modelsResponse = await httpClient.SendAsync(modelsRequest);
            var modelsBody = await modelsResponse.Content.ReadAsStringAsync();

            // 401 → invalid key
            if (modelsResponse.StatusCode == HttpStatusCode.Unauthorized)
            {
                return ValidationResult.IsUnauthorized(modelsResponse.StatusCode,
                    "Invalid or expired OpenAI API key");
            }

            // 403 → authenticated but access restricted; key may still be valid
            if (modelsResponse.StatusCode == HttpStatusCode.Forbidden)
            {
                return ValidationResult.ValidationUnavailable(modelsResponse.StatusCode,
                    "OpenAI API key forbidden (403) — permission or project restriction; key validity could not be conclusively determined");
            }

            // 429 → rate limited at the models step
            if (modelsResponse.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return ValidationResult.ValidationUnavailable(modelsResponse.StatusCode,
                    "OpenAI models endpoint rate limited (429) — key validity could not be determined");
            }

            // 5xx → service unavailable
            if ((int)modelsResponse.StatusCode >= 500)
            {
                return ValidationResult.ValidationUnavailable(modelsResponse.StatusCode,
                    $"OpenAI service error ({modelsResponse.StatusCode}) — validation unavailable");
            }

            if (!IsSuccessStatusCode(modelsResponse.StatusCode))
            {
                return ValidationResult.HasHttpError(modelsResponse.StatusCode,
                    $"Model listing failed: {TruncateResponse(modelsBody)}");
            }

            // Authentication confirmed — parse available models
            var discoveredModels = ParseOpenAIModels(modelsBody);
            if (discoveredModels == null || !discoveredModels.Any())
            {
                // Authenticated but catalog empty or unparseable — still a valid key
                var noModels = ValidationResult.Success(modelsResponse.StatusCode,
                    "Valid OpenAI credential — no models returned in catalog");
                noModels.Metadata = new Dictionary<string, object>
                {
                    ["authentication_valid"] = true,
                    ["inference_tested"] = false,
                    ["models_parsed"] = discoveredModels != null
                };
                noModels.RawResponse = modelsBody;
                return noModels;
            }

            // Step 2: Minimal inference test — confirms the key can call chat completions.
            // Use a positive allowlist of known chat-capable model prefixes rather than a negative
            // exclusion heuristic. "Not matching a non-chat prefix" does not guarantee chat support.
            var knownChatPrefixes = new[] { "gpt-4o", "gpt-4", "gpt-3.5", "o1", "o3", "o4", "chatgpt-" };

            // Prefer the cheapest/most-available known chat model first
            var preferredOrder = new[] { "gpt-4o-mini", "gpt-4o", "gpt-4-turbo", "gpt-4", "gpt-3.5-turbo", "o1-mini", "o3-mini" };
            var modelToUse = discoveredModels
                .Select(m => m.ModelId)
                .FirstOrDefault(id => preferredOrder.Any(p => id.Contains(p, StringComparison.OrdinalIgnoreCase)));

            if (modelToUse == null)
            {
                // No known chat model found. Rather than blindly testing an arbitrary model and
                // producing a misleading failure, report authentication as confirmed.
                var authOnly = ValidationResult.Success(modelsResponse.StatusCode,
                    "Valid OpenAI credential — authenticated, but no known chat-capable model is available for inference test");
                authOnly.AvailableModels = discoveredModels;
                authOnly.Metadata = new Dictionary<string, object>
                {
                    ["authentication_valid"] = true,
                    ["inference_tested"] = false,
                    ["models_parsed"] = true,
                    ["model_count"] = discoveredModels.Count
                };
                authOnly.RawResponse = modelsBody;
                return authOnly;
            }

            using var chatRequest = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
            chatRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            chatRequest.Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    model = modelToUse,
                    messages = new[] { new { role = "user", content = "Hi" } },
                    max_tokens = 5
                }),
                System.Text.Encoding.UTF8, "application/json");

            var chatResponse = await httpClient.SendAsync(chatRequest);
            var responseBody = await chatResponse.Content.ReadAsStringAsync();

            _logger?.LogDebug(
                "OpenAI chat API response ({Model}): Status={StatusCode}, Body={Body}",
                modelToUse, chatResponse.StatusCode, TruncateResponse(responseBody));

            // Inference succeeded
            if (IsSuccessStatusCode(chatResponse.StatusCode))
            {
                var result = ValidationResult.Success(chatResponse.StatusCode,
                    "Valid OpenAI credential — inference test successful");
                result.AvailableModels = discoveredModels;
                result.Metadata = new Dictionary<string, object>
                {
                    ["authentication_valid"] = true,
                    ["inference_tested"] = true,
                    ["inference_working"] = true,
                    ["tested_model"] = modelToUse
                };
                result.RawResponse = responseBody;

                // Note: OpenAI has no official public billing API endpoint.
                // /dashboard/billing/credit_grants is undocumented and returns 404 for PAYG accounts.
                // Balance info is intentionally not fetched.

                return result;
            }

            // 401 during inference — inference was rejected, but /v1/models already succeeded.
            // Authentication is confirmed; the inference request itself was refused.
            // Do NOT return IsUnauthorized — that would contradict the /models 200 result.
            if (chatResponse.StatusCode == HttpStatusCode.Unauthorized)
            {
                var inferenceRejected = ValidationResult.ValidationUnavailable(chatResponse.StatusCode,
                    "OpenAI credential authenticated (models check passed), but inference request was rejected (401) — possible project key scope restriction");
                inferenceRejected.AvailableModels = discoveredModels;
                inferenceRejected.Metadata = new Dictionary<string, object>
                {
                    ["authentication_valid"] = true,
                    ["inference_tested"] = true,
                    ["inference_working"] = false,
                    ["tested_model"] = modelToUse
                };
                inferenceRejected.RawResponse = responseBody;
                return inferenceRejected;
            }

            // 403 during inference — authenticated but this operation/model is not permitted
            if (chatResponse.StatusCode == HttpStatusCode.Forbidden)
            {
                var forbidden = ValidationResult.ValidationUnavailable(chatResponse.StatusCode,
                    "OpenAI credential authenticated but inference was forbidden (403) — project or model permission restriction");
                forbidden.AvailableModels = discoveredModels;
                forbidden.Metadata = new Dictionary<string, object>
                {
                    ["authentication_valid"] = true,
                    ["inference_tested"] = true,
                    ["inference_working"] = false,
                    ["tested_model"] = modelToUse
                };
                forbidden.RawResponse = responseBody;
                return forbidden;
            }

            // 402 — payment required; distinct from rate limiting
            if (chatResponse.StatusCode == HttpStatusCode.PaymentRequired)
            {
                var payment = ValidationResult.Success(chatResponse.StatusCode,
                    "Valid OpenAI credential — payment required (no active payment method or credit)");
                payment.IsQuotaExceeded = true;
                payment.AvailableModels = discoveredModels;
                payment.Metadata = new Dictionary<string, object>
                {
                    ["authentication_valid"] = true,
                    ["inference_tested"] = true,
                    ["inference_working"] = false,
                    ["tested_model"] = modelToUse
                };
                payment.RawResponse = responseBody;
                return payment;
            }

            // 429 — distinguish quota/billing exhaustion from plain rate limiting
            if (chatResponse.StatusCode == HttpStatusCode.TooManyRequests)
            {
                bool isQuota = responseBody.Contains("insufficient_quota", StringComparison.OrdinalIgnoreCase) ||
                               responseBody.Contains("billing_hard_limit_reached", StringComparison.OrdinalIgnoreCase);

                if (isQuota)
                {
                    var quota = ValidationResult.Success(chatResponse.StatusCode,
                        "Valid OpenAI credential — inference quota or billing limit reached");
                    quota.IsQuotaExceeded = true;
                    quota.AvailableModels = discoveredModels;
                    quota.Metadata = new Dictionary<string, object>
                    {
                        ["authentication_valid"] = true,
                        ["inference_tested"] = true,
                        ["inference_working"] = false,
                        ["tested_model"] = modelToUse
                    };
                    quota.RawResponse = responseBody;
                    return quota;
                }

                // Plain rate limit — key validity is still known from /models step
                var limited = ValidationResult.ValidationUnavailable(chatResponse.StatusCode,
                    "OpenAI inference test rate limited (429) — credential validity confirmed by preceding models check");
                limited.AvailableModels = discoveredModels;
                limited.Metadata = new Dictionary<string, object>
                {
                    ["authentication_valid"] = true,
                    ["inference_tested"] = true,
                    ["inference_working"] = false,
                    ["tested_model"] = modelToUse
                };
                limited.RawResponse = responseBody;
                return limited;
            }

            // Quota indicator found in body at other status codes (e.g. deactivated accounts)
            if (responseBody.Contains("insufficient_quota", StringComparison.OrdinalIgnoreCase) ||
                responseBody.Contains("billing_hard_limit_reached", StringComparison.OrdinalIgnoreCase))
            {
                var quota = ValidationResult.Success(chatResponse.StatusCode,
                    "Valid OpenAI credential — quota or billing limit indicated in error body");
                quota.IsQuotaExceeded = true;
                quota.AvailableModels = discoveredModels;
                quota.Metadata = new Dictionary<string, object>
                {
                    ["authentication_valid"] = true,
                    ["inference_tested"] = true,
                    ["inference_working"] = false,
                    ["tested_model"] = modelToUse
                };
                quota.RawResponse = responseBody;
                return quota;
            }

            var errorResult = ValidationResult.HasHttpError(chatResponse.StatusCode,
                $"OpenAI inference test failed: {TruncateResponse(responseBody)}");
            errorResult.AvailableModels = discoveredModels;
            errorResult.RawResponse = responseBody;
            return errorResult;
        }

        protected override bool IsValidKeyFormat(string apiKey)
        {
            return !string.IsNullOrWhiteSpace(apiKey) &&
                   apiKey.StartsWith("sk-") &&
                   apiKey.Length >= 23;
        }

        private List<ModelInfo>? ParseOpenAIModels(string jsonResponse)
        {
            try
            {
                using var doc = JsonDocument.Parse(jsonResponse);
                if (!doc.RootElement.TryGetProperty("data", out var dataArray))
                    return null;

                var models = new List<ModelInfo>();
                foreach (var modelElement in dataArray.EnumerateArray())
                {
                    var modelId = modelElement.GetProperty("id").GetString() ?? "";

                    models.Add(new ModelInfo
                    {
                        ModelId = modelId,
                        DisplayName = modelId,
                        Description = modelElement.TryGetProperty("description", out var desc) ? desc.GetString() : null,
                        ModelGroup = DetermineModelGroup(modelId)
                    });
                }

                return models;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error parsing OpenAI models response");
                return null;
            }
        }

        private string DetermineModelGroup(string modelId)
        {
            if (modelId.StartsWith("gpt-4")) return "GPT-4";
            if (modelId.StartsWith("gpt-3.5")) return "GPT-3.5";
            if (modelId.StartsWith("o1")) return "O1";
            if (modelId.StartsWith("text-embedding")) return "Embeddings";
            if (modelId.StartsWith("dall-e")) return "DALL-E";
            if (modelId.StartsWith("whisper")) return "Whisper";
            if (modelId.StartsWith("tts")) return "TTS";
            return "Other";
        }
    }
}

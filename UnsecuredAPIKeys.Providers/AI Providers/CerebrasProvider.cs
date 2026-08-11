using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Base;
using UnsecuredAPIKeys.Providers.Common;

namespace UnsecuredAPIKeys.Providers.AI_Providers
{
    /// <summary>
    /// Provider for Cerebras Inference API keys.
    /// Keys start with "csk-" (Cerebras Secret Key).
    /// Verification: GET /v1/models (authenticates credential)
    /// then POST /v1/chat/completions (tests inference capability & rate limits).
    /// Official docs: https://inference-docs.cerebras.ai/api-reference/models
    /// </summary>
    [ApiProvider]
    public class CerebrasProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "Cerebras";
        public override ApiTypeEnum ApiType => ApiTypeEnum.Cerebras;

        public override IEnumerable<string> RegexPatterns =>
        [
            @"\bcsk-[A-Za-z0-9]{40,80}\b",
            @"CEREBRAS_API_KEY\s*[:=]\s*['""]?(csk-[A-Za-z0-9]{40,80})['""]?",
            @"CEREBRAS_KEY\s*[:=]\s*['""]?(csk-[A-Za-z0-9]{40,80})['""]?",
            @"cerebras[_-]?api[_-]?key\s*[:=]\s*['""]?(csk-[A-Za-z0-9]{40,80})['""]?"
        ];

        public CerebrasProvider() : base() { }
        public CerebrasProvider(ILogger<CerebrasProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(
            string apiKey, HttpClient httpClient)
        {
            // Step 1: List models — authenticates credential
            using var modelsRequest = new HttpRequestMessage(
                HttpMethod.Get, "https://api.cerebras.ai/v1/models");
            modelsRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            var modelsResponse = await httpClient.SendAsync(modelsRequest);
            var modelsBody = await modelsResponse.Content.ReadAsStringAsync();

            _logger?.LogDebug("Cerebras models response: Status={Status}, Body={Body}",
                modelsResponse.StatusCode, TruncateResponse(modelsBody));

            if (modelsResponse.StatusCode == HttpStatusCode.Unauthorized ||
                modelsResponse.StatusCode == HttpStatusCode.Forbidden)
            {
                var unauthResult = ValidationResult.IsUnauthorized(modelsResponse.StatusCode, "Invalid Cerebras API key");
                unauthResult.RawResponse = modelsBody;
                return unauthResult;
            }

            if (!modelsResponse.IsSuccessStatusCode)
            {
                var errResult = ValidationResult.HasHttpError(modelsResponse.StatusCode,
                    $"Models listing failed: {TruncateResponse(modelsBody)}");
                errResult.RawResponse = modelsBody;
                return errResult;
            }

            var models = ParseModels(modelsBody);

            // Step 2: Quick chat completion to test inference capability
            var preferredModels = new[] { "llama3.1-8b", "llama-3.3-70b", "llama3.3-70b" };
            var modelToUse = models?
                .Select(m => m.ModelId)
                .FirstOrDefault(id => preferredModels.Any(p => id.Contains(p)))
                ?? "llama3.1-8b";

            using var chatRequest = new HttpRequestMessage(
                HttpMethod.Post, "https://api.cerebras.ai/v1/chat/completions");
            chatRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            chatRequest.Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    model = modelToUse,
                    messages = new[] { new { role = "user", content = "Reply with OK" } },
                    max_tokens = 1,
                    temperature = 0
                }),
                Encoding.UTF8, "application/json");

            var chatResponse = await httpClient.SendAsync(chatRequest);
            var chatBody = await chatResponse.Content.ReadAsStringAsync();

            _logger?.LogDebug("Cerebras chat response ({Model}): Status={Status}, Body={Body}",
                modelToUse, chatResponse.StatusCode, TruncateResponse(chatBody));

            ValidationResult result;

            if (IsSuccessStatusCode(chatResponse.StatusCode))
            {
                result = ValidationResult.Success(chatResponse.StatusCode, models);
                result.AvailableModels = models;
                result.Detail = "Valid Cerebras key — inference request succeeded";

                result.Metadata ??= new Dictionary<string, object>();
                result.Metadata["authentication_valid"] = true;
                result.Metadata["inference_tested"] = true;
                result.Metadata["inference_working"] = true;
                result.Metadata["tested_model"] = modelToUse;

                try
                {
                    ExtractRateLimits(chatResponse, result);
                }
                catch { /* Best effort parsing */ }
            }
            else if ((int)chatResponse.StatusCode == 429)
            {
                result = new ValidationResult
                {
                    Status = ValidationAttemptStatus.Valid,
                    HttpStatusCode = chatResponse.StatusCode,
                    IsQuotaExceeded = true,
                    Detail = "Valid Cerebras key — inference request rate/quota limited"
                };
                result.AvailableModels = models;
                result.Metadata = new Dictionary<string, object>
                {
                    ["authentication_valid"] = true,
                    ["inference_tested"] = true,
                    ["inference_working"] = false,
                    ["inference_limited"] = true,
                    ["tested_model"] = modelToUse
                };
            }
            else if (ContainsAny(chatBody.ToLowerInvariant(), QuotaIndicators))
            {
                result = new ValidationResult
                {
                    Status = ValidationAttemptStatus.Valid,
                    HttpStatusCode = chatResponse.StatusCode,
                    IsQuotaExceeded = true,
                    Detail = $"Valid Cerebras key — quota issue: {TruncateResponse(chatBody)}"
                };
                result.AvailableModels = models;
                result.Metadata = new Dictionary<string, object>
                {
                    ["authentication_valid"] = true,
                    ["inference_tested"] = true,
                    ["inference_working"] = false,
                    ["tested_model"] = modelToUse
                };
            }
            else
            {
                // Authenticated key, but chat inference returned unexpected HTTP error
                result = ValidationResult.Success(chatResponse.StatusCode, "Valid Cerebras key (authenticated)");
                result.AvailableModels = models;
                result.Detail = $"Valid Cerebras key (authenticated via /models; chat returned HTTP {(int)chatResponse.StatusCode})";
                result.Metadata = new Dictionary<string, object>
                {
                    ["authentication_valid"] = true,
                    ["inference_tested"] = true,
                    ["inference_working"] = false,
                    ["tested_model"] = modelToUse
                };
            }

            result.RawResponse = chatBody;
            return result;
        }

        private static void ExtractRateLimits(HttpResponseMessage chatResponse, ValidationResult result)
        {
            string? reqsRem = null;
            string? reqsLimit = null;
            string? tokensRem = null;
            string? tokensLimit = null;

            if (chatResponse.Headers.TryGetValues("x-ratelimit-remaining-requests-day", out var reqsRemVals))
                reqsRem = reqsRemVals.FirstOrDefault();
            if (chatResponse.Headers.TryGetValues("x-ratelimit-limit-requests-day", out var reqsLimitVals))
                reqsLimit = reqsLimitVals.FirstOrDefault();
            if (chatResponse.Headers.TryGetValues("x-ratelimit-remaining-tokens-day", out var tokensRemVals))
                tokensRem = tokensRemVals.FirstOrDefault();
            if (chatResponse.Headers.TryGetValues("x-ratelimit-limit-tokens-day", out var tokensLimitVals))
                tokensLimit = tokensLimitVals.FirstOrDefault();

            if (!string.IsNullOrEmpty(reqsRem) && !string.IsNullOrEmpty(reqsLimit))
            {
                var balance = $"{reqsRem} / {reqsLimit} reqs today";
                if (!string.IsNullOrEmpty(tokensRem) && !string.IsNullOrEmpty(tokensLimit))
                {
                    balance += $" ({tokensRem} / {tokensLimit} tokens remaining)";
                }
                result.Balance = balance;

                if (!string.IsNullOrEmpty(tokensLimit) && int.TryParse(tokensLimit, out var limitVal) && limitVal == 1000000)
                {
                    result.AccountTier = "Free Tier";
                }
                else
                {
                    result.AccountTier = "Developer/Paid Tier";
                }

                if (!string.IsNullOrEmpty(reqsRem)) result.Metadata!["ratelimit_remaining_requests_day"] = reqsRem;
                if (!string.IsNullOrEmpty(reqsLimit)) result.Metadata!["ratelimit_limit_requests_day"] = reqsLimit;
                if (!string.IsNullOrEmpty(tokensRem)) result.Metadata!["ratelimit_remaining_tokens_day"] = tokensRem;
                if (!string.IsNullOrEmpty(tokensLimit)) result.Metadata!["ratelimit_limit_tokens_day"] = tokensLimit;
            }
        }

        private List<ModelInfo>? ParseModels(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return null;

                var list = new List<ModelInfo>();
                foreach (var el in data.EnumerateArray())
                {
                    var id = el.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
                    if (!string.IsNullOrEmpty(id))
                    {
                        list.Add(new ModelInfo { ModelId = id, DisplayName = id });
                    }
                }
                return list;
            }
            catch { return null; }
        }

        protected override bool IsValidKeyFormat(string apiKey) =>
            !string.IsNullOrWhiteSpace(apiKey) &&
            apiKey.StartsWith("csk-", StringComparison.Ordinal) &&
            apiKey.Length >= 44;
    }
}

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
    /// Provider for Cerebras Inference API keys.
    /// Keys start with "csk-" (Cerebras Secret Key).
    /// Cerebras uses OpenAI-compatible endpoints at api.cerebras.ai/v1.
    /// Verification: GET /v1/models (lists available models, confirms key validity)
    /// then POST /v1/chat/completions with llama3.1-8b (fastest/cheapest model).
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
            @"CEREBRAS_API_KEY",
            @"cerebras[_-]?api[_-]?key",
            @"CEREBRAS_KEY"
        ];

        public CerebrasProvider() : base() { }
        public CerebrasProvider(ILogger<CerebrasProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(
            string apiKey, HttpClient httpClient)
        {
            // Step 1: List models — confirms key is valid
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
                return ValidationResult.IsUnauthorized(modelsResponse.StatusCode);
            }

            if (!modelsResponse.IsSuccessStatusCode)
            {
                return ValidationResult.HasHttpError(modelsResponse.StatusCode,
                    $"Models listing failed: {TruncateResponse(modelsBody)}");
            }

            var models = ParseModels(modelsBody);

            // Step 2: Quick chat completion to confirm active quota
            // llama3.1-8b is the fastest and cheapest Cerebras model
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
                    messages = new[] { new { role = "user", content = "Hi" } },
                    max_tokens = 1
                }),
                System.Text.Encoding.UTF8, "application/json");

            var chatResponse = await httpClient.SendAsync(chatRequest);
            var chatBody = await chatResponse.Content.ReadAsStringAsync();

            _logger?.LogDebug("Cerebras chat response ({Model}): Status={Status}, Body={Body}",
                modelToUse, chatResponse.StatusCode, TruncateResponse(chatBody));

            if (IsSuccessStatusCode(chatResponse.StatusCode))
            {
                var result = ValidationResult.Success(chatResponse.StatusCode, models);
                result.AvailableModels = models;

                try
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

                        // Infer Account Tier
                        if (!string.IsNullOrEmpty(tokensLimit) && int.TryParse(tokensLimit, out var limitVal) && limitVal == 1000000)
                        {
                            result.AccountTier = "Free Tier";
                        }
                        else
                        {
                            result.AccountTier = "Developer/Paid Tier";
                        }

                        // Populate Metadata
                        result.Metadata ??= new Dictionary<string, object>();
                        if (!string.IsNullOrEmpty(reqsRem)) result.Metadata["ratelimit_remaining_requests_day"] = reqsRem;
                        if (!string.IsNullOrEmpty(reqsLimit)) result.Metadata["ratelimit_limit_requests_day"] = reqsLimit;
                        if (!string.IsNullOrEmpty(tokensRem)) result.Metadata["ratelimit_remaining_tokens_day"] = tokensRem;
                        if (!string.IsNullOrEmpty(tokensLimit)) result.Metadata["ratelimit_limit_tokens_day"] = tokensLimit;
                    }
                }
                catch { /* Best effort parsing */ }

                return result;
            }

            if (chatResponse.StatusCode == HttpStatusCode.Unauthorized ||
                chatResponse.StatusCode == HttpStatusCode.Forbidden)
            {
                return ValidationResult.IsUnauthorized(chatResponse.StatusCode);
            }

            if ((int)chatResponse.StatusCode == 429)
            {
                var limited = ValidationResult.Success(chatResponse.StatusCode, "quota exhausted");
                limited.AvailableModels = models;
                return limited;
            }

            if (ContainsAny(chatBody, QuotaIndicators))
            {
                var limited = ValidationResult.Success(chatResponse.StatusCode,
                    $"Valid key but quota issue: {TruncateResponse(chatBody)}");
                limited.AvailableModels = models;
                return limited;
            }

            return ValidationResult.HasHttpError(chatResponse.StatusCode,
                $"Chat completion failed: {TruncateResponse(chatBody)}");
        }

        private List<ModelInfo>? ParseModels(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("data", out var data)) return null;

                var list = new List<ModelInfo>();
                foreach (var el in data.EnumerateArray())
                {
                    var id = el.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
                    list.Add(new ModelInfo { ModelId = id, DisplayName = id });
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

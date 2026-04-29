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
    /// Provider for Perplexity AI (Sonar) API keys.
    /// Keys always start with "pplx-".
    /// Verification: POST /chat/completions with model "sonar" (cheapest, $1/1M tokens).
    /// Official docs: https://docs.perplexity.ai/api-reference/sonar-post
    /// </summary>
    [ApiProvider]
    public class PerplexityProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "Perplexity";
        public override ApiTypeEnum ApiType => ApiTypeEnum.Perplexity;

        public override IEnumerable<string> RegexPatterns =>
        [
            @"\bpplx-[A-Za-z0-9]{40,60}\b",
            @"PERPLEXITY_API_KEY",
            @"perplexity[_-]?api[_-]?key",
            @"PPLX_API_KEY"
        ];

        public PerplexityProvider() : base() { }
        public PerplexityProvider(ILogger<PerplexityProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(
            string apiKey, HttpClient httpClient)
        {
            // Step 1: GET /models — free check, confirms key is accepted
            // Perplexity is OpenAI-compatible so this endpoint works
            using var modelsRequest = new HttpRequestMessage(
                HttpMethod.Get, "https://api.perplexity.ai/models");
            modelsRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            var modelsResponse = await httpClient.SendAsync(modelsRequest);
            var modelsBody = await modelsResponse.Content.ReadAsStringAsync();

            _logger?.LogDebug("Perplexity models response: Status={Status}, Body={Body}",
                modelsResponse.StatusCode, TruncateResponse(modelsBody));

            if (modelsResponse.StatusCode == HttpStatusCode.Unauthorized ||
                modelsResponse.StatusCode == HttpStatusCode.Forbidden)
            {
                return ValidationResult.IsUnauthorized(modelsResponse.StatusCode);
            }

            if (IsSuccessStatusCode(modelsResponse.StatusCode))
            {
                // Key is valid — models endpoint confirmed it without any generation cost
                return ValidationResult.Success(modelsResponse.StatusCode, "Valid Perplexity key");
            }

            if ((int)modelsResponse.StatusCode == 429)
            {
                return ValidationResult.Success(modelsResponse.StatusCode, "quota exhausted");
            }

            // Step 2: If models endpoint fails for non-auth reasons, try a minimal chat call
            // sonar is cheapest ($1/1M tokens + $0.008/request search fee)
            using var chatRequest = new HttpRequestMessage(
                HttpMethod.Post, "https://api.perplexity.ai/chat/completions");
            chatRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            chatRequest.Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    model = "sonar",
                    messages = new[] { new { role = "user", content = "1" } },
                    max_tokens = 1
                }),
                System.Text.Encoding.UTF8, "application/json");

            var response = await httpClient.SendAsync(chatRequest);
            var body = await response.Content.ReadAsStringAsync();

            _logger?.LogDebug("Perplexity chat response: Status={Status}, Body={Body}",
                response.StatusCode, TruncateResponse(body));

            if (IsSuccessStatusCode(response.StatusCode))
                return ValidationResult.Success(response.StatusCode, "Valid Perplexity key");

            if (response.StatusCode == HttpStatusCode.Unauthorized ||
                response.StatusCode == HttpStatusCode.Forbidden)
                return ValidationResult.IsUnauthorized(response.StatusCode);

            if ((int)response.StatusCode == 429)
                return ValidationResult.Success(response.StatusCode, "quota exhausted");

            if (ContainsAny(body, QuotaIndicators))
                return ValidationResult.Success(response.StatusCode, $"Valid key but quota issue: {TruncateResponse(body)}");

            return ValidationResult.HasHttpError(response.StatusCode, $"API request failed: {TruncateResponse(body)}");
        }

        protected override bool IsValidKeyFormat(string apiKey) =>
            !string.IsNullOrWhiteSpace(apiKey) &&
            apiKey.StartsWith("pplx-", StringComparison.Ordinal) &&
            apiKey.Length >= 45;
    }
}

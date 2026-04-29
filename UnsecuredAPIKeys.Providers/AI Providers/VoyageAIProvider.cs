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
    /// Provider for Voyage AI embedding API keys.
    /// Keys start with "pa-" (Personal API key).
    /// Voyage AI is the leading embedding provider, widely used in RAG pipelines.
    /// Verification: POST /v1/embeddings with voyage-3-lite (cheapest model).
    /// Official docs: https://docs.voyageai.com/reference/embeddings-api
    /// </summary>
    [ApiProvider]
    public class VoyageAIProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "Voyage AI";
        public override ApiTypeEnum ApiType => ApiTypeEnum.VoyageAI;

        public override IEnumerable<string> RegexPatterns =>
        [
            @"\bpa-[A-Za-z0-9]{40,60}\b",
            @"VOYAGE_API_KEY",
            @"voyage[_-]?api[_-]?key",
            @"VOYAGEAI_API_KEY"
        ];

        public VoyageAIProvider() : base() { }
        public VoyageAIProvider(ILogger<VoyageAIProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(
            string apiKey, HttpClient httpClient)
        {
            // POST /v1/embeddings — official validation endpoint
            // voyage-3-lite is the cheapest model ($0.02/1M tokens)
            using var request = new HttpRequestMessage(
                HttpMethod.Post, "https://api.voyageai.com/v1/embeddings");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    input = new[] { "test" },
                    model = "voyage-3-lite"
                }),
                System.Text.Encoding.UTF8, "application/json");

            var response = await httpClient.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            _logger?.LogDebug("Voyage AI API response: Status={Status}, Body={Body}",
                response.StatusCode, TruncateResponse(body));

            if (IsSuccessStatusCode(response.StatusCode))
            {
                var result = ValidationResult.Success(response.StatusCode, "Valid Voyage AI key");

                // Parse token usage from response for display
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("usage", out var usage) &&
                        usage.TryGetProperty("total_tokens", out var tokens))
                    {
                        result.Detail = $"Valid Voyage AI key (embedding confirmed, {tokens.GetInt32()} tokens used)";
                    }
                }
                catch { /* Best effort */ }

                return result;
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized ||
                response.StatusCode == HttpStatusCode.Forbidden)
            {
                return ValidationResult.IsUnauthorized(response.StatusCode);
            }

            if ((int)response.StatusCode == 429)
            {
                return ValidationResult.Success(response.StatusCode, "quota exhausted");
            }

            if (ContainsAny(body, QuotaIndicators))
            {
                return ValidationResult.Success(response.StatusCode,
                    $"Valid key but quota issue: {TruncateResponse(body)}");
            }

            return ValidationResult.HasHttpError(response.StatusCode,
                $"Embeddings request failed: {TruncateResponse(body)}");
        }

        protected override bool IsValidKeyFormat(string apiKey) =>
            !string.IsNullOrWhiteSpace(apiKey) &&
            apiKey.StartsWith("pa-", StringComparison.Ordinal) &&
            apiKey.Length >= 43;
    }
}

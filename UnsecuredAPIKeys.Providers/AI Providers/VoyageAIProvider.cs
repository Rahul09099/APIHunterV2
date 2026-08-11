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
    /// Voyage AI is a leading text and multimodal embedding provider.
    /// Verification: POST /v1/embeddings with voyage-4-lite (recommended low-cost model).
    /// Official docs: https://docs.voyageai.com/reference/embeddings-api-1
    /// </summary>
    [ApiProvider]
    public class VoyageAIProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "Voyage AI";
        public override ApiTypeEnum ApiType => ApiTypeEnum.VoyageAI;

        public override IEnumerable<string> RegexPatterns =>
        [
            @"\bpa-[A-Za-z0-9_-]{20,256}\b",
            @"VOYAGE_API_KEY",
            @"voyage[_-]?api[_-]?key",
            @"VOYAGEAI_API_KEY",
            @"(?i)\bVOYAGE[\s_-]*API[\s_-]*KEY\s*[:=]\s*['""]?([A-Za-z0-9_-]{20,256})['""]?"
        ];

        public VoyageAIProvider() : base() { }
        public VoyageAIProvider(ILogger<VoyageAIProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(
            string apiKey, HttpClient httpClient)
        {
            try
            {
                // POST /v1/embeddings — official validation endpoint using voyage-4-lite
                using var request = new HttpRequestMessage(
                    HttpMethod.Post, "https://api.voyageai.com/v1/embeddings");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                request.Content = new StringContent(
                    JsonSerializer.Serialize(new
                    {
                        input = new[] { "hi" },
                        model = "voyage-4-lite"
                    }),
                    System.Text.Encoding.UTF8, "application/json");

                var response = await httpClient.SendAsync(request);
                var body = await response.Content.ReadAsStringAsync();

                _logger?.LogDebug("Voyage AI API response: Status={Status}, Body={Body}",
                    response.StatusCode, TruncateResponse(body));

                if (IsSuccessStatusCode(response.StatusCode))
                {
                    var result = ValidationResult.Success(response.StatusCode,
                        "Valid Voyage AI key — embedding test successful");
                    result.RawResponse = body;
                    result.Metadata = new Dictionary<string, object>
                    {
                        ["authentication_valid"] = true,
                        ["tested_model"] = "voyage-4-lite"
                    };

                    try
                    {
                        using var doc = JsonDocument.Parse(body);
                        if (doc.RootElement.TryGetProperty("usage", out var usage) &&
                            usage.TryGetProperty("total_tokens", out var tokens) &&
                            tokens.ValueKind == JsonValueKind.Number)
                        {
                            result.Detail = $"Valid Voyage AI key — embedding test successful ({tokens.GetInt32()} tokens)";
                            result.Metadata["total_tokens"] = tokens.GetInt32();
                        }
                    }
                    catch { /* Best effort */ }

                    return result;
                }

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    return ValidationResult.IsUnauthorized(response.StatusCode,
                        "Invalid or expired Voyage AI API key");
                }

                if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    return ValidationResult.ValidationUnavailable(response.StatusCode,
                        "Voyage AI API key forbidden (403) — permission restriction");
                }

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    return ValidationResult.ValidationUnavailable(response.StatusCode,
                        "Voyage AI API rate limited (429) — validation unavailable");
                }

                if (response.StatusCode == HttpStatusCode.PaymentRequired || ContainsAny(body, QuotaIndicators))
                {
                    var quotaResult = ValidationResult.Success(response.StatusCode,
                        "Valid Voyage AI key but quota/usage limit was reached");
                    quotaResult.IsQuotaExceeded = true;
                    quotaResult.RawResponse = body;
                    quotaResult.Metadata = new Dictionary<string, object>
                    {
                        ["authentication_valid"] = true,
                        ["quota_exceeded"] = true
                    };
                    return quotaResult;
                }

                if ((int)response.StatusCode >= 500)
                {
                    return ValidationResult.ValidationUnavailable(response.StatusCode,
                        $"Voyage AI service error ({response.StatusCode}) — validation unavailable");
                }

                return ValidationResult.HasHttpError(response.StatusCode,
                    $"Voyage AI embeddings request failed: {TruncateResponse(body)}");
            }
            catch (Exception ex)
            {
                return ValidationResult.HasNetworkError(ex.Message);
            }
        }

        protected override bool IsValidKeyFormat(string apiKey) =>
            !string.IsNullOrWhiteSpace(apiKey) &&
            apiKey.Length >= 20;
    }
}

using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
using System.Text;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Base;
using UnsecuredAPIKeys.Providers.Common;

namespace UnsecuredAPIKeys.Providers.AI_Providers
{
    /// <summary>
    /// Provider for Upstage API keys — Solar LLM and Document AI platform.
    ///
    /// Key format: alphanumeric string, stored in UPSTAGE_API_KEY env var.
    /// Confirmed env var name from official .NET SDK docs and cookbook examples.
    /// No confirmed prefix (the "up_" prefix is unverified — keys may be plain alphanumeric).
    ///
    /// Auth: Authorization: Bearer {apiKey}
    /// Base URL: https://api.upstage.ai/v1
    ///
    /// Verification endpoint: GET https://api.upstage.ai/v1/models
    ///   OpenAI-compatible response: { "object": "list", "data": [{ "id": "solar-pro", ... }] }
    ///   Returns 401 for invalid keys.
    ///
    /// No balance endpoint available via API — usage tracked in Upstage Console dashboard.
    /// New accounts get $10 free credits on signup.
    /// </summary>
    [ApiProvider]
    public class UpstageProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "Upstage (Solar)";
        public override ApiTypeEnum ApiType => ApiTypeEnum.Upstage;

        public override IEnumerable<string> RegexPatterns =>
        [
            // Primary env var names — confirmed from official SDK docs and cookbook
            @"UPSTAGE_API_KEY",
            @"SOLAR_API_KEY",

            // Context-aware key value patterns
            @"UPSTAGE_API_KEY\s*[=:]\s*['""]?([A-Za-z0-9_\-]{20,})['""]?",
            @"SOLAR_API_KEY\s*[=:]\s*['""]?([A-Za-z0-9_\-]{20,})['""]?",

            // up_ prefix — included but unconfirmed; may match some key formats
            @"up_[A-Za-z0-9]{20,}"
        ];

        public UpstageProvider() : base() { }
        public UpstageProvider(ILogger<UpstageProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(string apiKey, HttpClient httpClient)
        {
            try
            {
                // POST /v1/embeddings with minimal input — definitely requires auth
                // More reliable than GET /v1/models which may be public on some providers
                const string body = """{"model":"solar-embedding-1-large","input":["test"]}""";

                using var request = new HttpRequestMessage(HttpMethod.Post,
                    "https://api.upstage.ai/v1/embeddings");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                request.Content = new StringContent(body,
                    System.Text.Encoding.UTF8, "application/json");

                var response = await httpClient.SendAsync(request);
                string responseBody = await response.Content.ReadAsStringAsync();

                _logger?.LogDebug("Upstage API response: Status={StatusCode}, Body={Body}",
                    response.StatusCode, TruncateResponse(responseBody));

                if (IsSuccessStatusCode(response.StatusCode))
                {
                    var result = ValidationResult.Success(response.StatusCode, "Valid Upstage key");

                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(responseBody);

                        // OpenAI-compatible response: { "object": "list", "data": [...], "usage": {...} }
                        if (doc.RootElement.TryGetProperty("usage", out var usage) &&
                            usage.TryGetProperty("total_tokens", out var tokens))
                        {
                            result.Detail = $"Valid Upstage key — embeddings working ({tokens.GetInt32()} tokens used)";
                        }
                        else if (doc.RootElement.TryGetProperty("data", out var data))
                        {
                            result.Detail = $"Valid Upstage key — {data.GetArrayLength()} embedding(s) returned";
                        }
                        else
                        {
                            result.Detail = "Valid Upstage key";
                        }

                        // No balance endpoint — note it clearly
                        result.Balance = "N/A (check Upstage Console dashboard)";
                    }
                    catch { result.Detail = "Valid Upstage key"; }

                    return result;
                }

                return response.StatusCode switch
                {
                    System.Net.HttpStatusCode.Unauthorized or
                    System.Net.HttpStatusCode.Forbidden =>
                        ValidationResult.IsUnauthorized(response.StatusCode),
                    (System.Net.HttpStatusCode)429 =>
                        ValidationResult.Success(response.StatusCode, "Rate limited (key is valid)"),
                    _ => ValidationResult.HasHttpError(response.StatusCode,
                        $"Unexpected status {response.StatusCode}. Body: {TruncateResponse(responseBody)}")
                };
            }
            catch (Exception ex)
            {
                return ValidationResult.HasNetworkError(ex.Message);
            }
        }

        protected override bool IsValidKeyFormat(string apiKey)
        {
            // No confirmed prefix — just check reasonable length
            // up_ prefix is included as a hint but not enforced
            return !string.IsNullOrWhiteSpace(apiKey) && apiKey.Length >= 20;
        }
    }
}

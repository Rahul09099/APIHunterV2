using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Base;
using UnsecuredAPIKeys.Providers.Common;

namespace UnsecuredAPIKeys.Providers.AI_Providers
{
    /// <summary>
    /// Provider for AI21 Labs API keys — Jamba and Jurassic LLM family.
    ///
    /// Key format: sk-{alphanumeric} (e.g. sk-abc123xyz456)
    /// Auth: Authorization: Bearer {apiKey}
    ///
    /// Verification endpoint: GET https://api.ai21.com/studio/v1/verify
    ///   200 = valid key
    ///   401/403 = invalid/expired key
    ///
    /// No balance endpoint available via API — usage tracked in AI21 Studio dashboard.
    /// </summary>
    [ApiProvider]
    public class AI21LabsProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "AI21 Labs";
        public override ApiTypeEnum ApiType => ApiTypeEnum.AI21Labs;

        public override IEnumerable<string> RegexPatterns =>
        [
            // AI21 keys use sk- prefix — confirmed from official docs and RedHunt Labs research
            @"sk-[A-Za-z0-9]{20,}",

            // Environment variable names commonly found in leaked code
            @"AI21_API_KEY",
            @"AI21_KEY",
            @"AI21_TOKEN",
            @"AI21_SECRET",
            @"AI21_ACCESS_KEY",
            @"AI21LABS_API_KEY"
        ];

        public AI21LabsProvider() : base() { }
        public AI21LabsProvider(ILogger<AI21LabsProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(string apiKey, HttpClient httpClient)
        {
            try
            {
                // Dedicated verify endpoint — lightest possible call, read-only
                // Returns 200 for valid keys, 401/403 for invalid
                using var request = new HttpRequestMessage(HttpMethod.Get,
                    "https://api.ai21.com/studio/v1/verify");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

                var response = await httpClient.SendAsync(request);
                string responseBody = await response.Content.ReadAsStringAsync();

                _logger?.LogDebug("AI21 Labs verify response: Status={StatusCode}, Body={Body}",
                    response.StatusCode, TruncateResponse(responseBody));

                if (IsSuccessStatusCode(response.StatusCode))
                {
                    var result = ValidationResult.Success(response.StatusCode, "Valid AI21 Labs key");
                    result.Detail = "Valid AI21 Labs key — access to Jamba/Jurassic models";
                    // No balance endpoint available via API
                    result.Balance = "N/A (check AI21 Studio dashboard)";
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
            // AI21 keys start with sk- and are at least 22 chars total
            return !string.IsNullOrWhiteSpace(apiKey) &&
                   apiKey.StartsWith("sk-") &&
                   apiKey.Length >= 22;
        }
    }
}

using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Base;
using UnsecuredAPIKeys.Providers.Common;

namespace UnsecuredAPIKeys.Providers.AI_Providers
{
    /// <summary>
    /// Provider for Leonardo.ai API keys — AI image generation platform.
    /// Verification endpoint: GET https://cloud.leonardo.ai/api/rest/v1/me (Bearer auth)
    /// </summary>
    [ApiProvider(false, false)]
    public class LeonardoAIProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "Leonardo.ai";
        public override ApiTypeEnum ApiType => ApiTypeEnum.LeonardoAI;

        public override IEnumerable<string> RegexPatterns =>
        [
            @"[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}",  // UUID format
            @"leonardo[_-]?[A-Za-z0-9-]{32,}",
            @"LEONARDO_API_KEY",
            @"LEONARDO_AI_API_KEY"
        ];

        public LeonardoAIProvider() : base() { }
        public LeonardoAIProvider(ILogger<LeonardoAIProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(string apiKey, HttpClient httpClient)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get,
                    "https://cloud.leonardo.ai/api/rest/v1/me");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

                var response = await httpClient.SendAsync(request);
                string responseBody = await response.Content.ReadAsStringAsync();

                _logger?.LogDebug("Leonardo.ai API response: Status={StatusCode}, Body={Body}",
                    response.StatusCode, TruncateResponse(responseBody));

                if (IsSuccessStatusCode(response.StatusCode))
                {
                    var result = ValidationResult.Success(response.StatusCode, "Valid Leonardo.ai key");

                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(responseBody);
                        // Confirmed response: { "user_details": [{ "user": { "id": "...", "username": "..." },
                        //   "tokenRenewalDate": "2024-...", "apiConcurrencySlots": 1,
                        //   "apiCreditBalance": 150, "subscriptionTokens": 8500 }] }
                        if (doc.RootElement.TryGetProperty("user_details", out var details) &&
                            details.GetArrayLength() > 0)
                        {
                            var first = details[0];

                            if (first.TryGetProperty("user", out var user) &&
                                user.TryGetProperty("username", out var username))
                                result.AccountTier = username.GetString();

                            // apiCreditBalance = purchased credits remaining
                            if (first.TryGetProperty("apiCreditBalance", out var credits))
                                result.Balance = $"{credits.GetInt32():N0} API credits";

                            if (first.TryGetProperty("apiConcurrencySlots", out var slots))
                                result.Detail = $"Valid Leonardo.ai key — {slots.GetInt32()} concurrency slot(s)";
                            else
                                result.Detail = "Valid Leonardo.ai key";

                            if (first.TryGetProperty("tokenRenewalDate", out var renewal))
                                result.Detail += $" | Renews: {renewal.GetString()}";
                        }
                    }
                    catch { /* Best effort */ }

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
            // UUID format: 8-4-4-4-12 hex chars
            return !string.IsNullOrWhiteSpace(apiKey) && apiKey.Length >= 32;
        }
    }
}

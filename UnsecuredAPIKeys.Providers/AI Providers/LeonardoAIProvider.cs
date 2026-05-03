using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Base;
using UnsecuredAPIKeys.Providers.Common;

namespace UnsecuredAPIKeys.Providers.AI_Providers
{
    /// <summary>
    /// Provider for Leonardo.ai API keys — AI image and video generation platform.
    ///
    /// Key format: UUID (XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX)
    /// Confirmed from official docs: https://docs.leonardo.ai/docs/api-error-messages
    /// "It should be added to the request header as authorization: Bearer XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX"
    ///
    /// Auth: Authorization: Bearer {uuid-key}
    ///
    /// Verification endpoint: GET https://cloud.leonardo.ai/api/rest/v1/me
    /// Response: {
    ///   "user_details": [{
    ///     "user": { "id": "...", "username": "..." },
    ///     "tokenRenewalDate": "2025-01-01",
    ///     "apiConcurrencySlots": 1,
    ///     "apiCreditBalance": 150.0,
    ///     "subscriptionTokens": 8500
    ///   }]
    /// }
    ///
    /// Invalid key response:
    /// { "error": "Authentication hook unauthorized this request", "code": "access-denied" }
    /// </summary>
    [ApiProvider]
    public class LeonardoAIProvider : BaseApiKeyProvider
    {
        // UUID regex — used for both pattern matching and format validation
        private static readonly Regex UuidRegex = new(
            @"^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public override string ProviderName => "Leonardo.ai";
        public override ApiTypeEnum ApiType => ApiTypeEnum.LeonardoAI;

        public override IEnumerable<string> RegexPatterns =>
        [
            // Context-aware UUID patterns — only match when adjacent to Leonardo-related context
            // This reduces false positives from generic UUIDs in other codebases
            @"LEONARDO_API_KEY\s*[=:]\s*['""]?([a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12})['""]?",
            @"LEONARDO_AI_API_KEY\s*[=:]\s*['""]?([a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12})['""]?",
            @"leonardo[._-]?ai[._-]?key\s*[=:]\s*['""]?([a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12})['""]?",

            // Env var names — scraper will find these and extract nearby UUID values
            @"LEONARDO_API_KEY",
            @"LEONARDO_AI_API_KEY",
            @"LEONARDO_KEY"
        ];

        public LeonardoAIProvider() : base() { }
        public LeonardoAIProvider(ILogger<LeonardoAIProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(string apiKey, HttpClient httpClient)
        {
            try
            {
                // GET /me — returns user details including credit balance and concurrency slots
                using var request = new HttpRequestMessage(HttpMethod.Get,
                    "https://cloud.leonardo.ai/api/rest/v1/me");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                request.Headers.Add("accept", "application/json");

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
                        // Response: { "user_details": [{ "user": {...}, "apiCreditBalance": 150, ... }] }
                        if (doc.RootElement.TryGetProperty("user_details", out var details) &&
                            details.GetArrayLength() > 0)
                        {
                            var first = details[0];

                            // Username as account identifier
                            if (first.TryGetProperty("user", out var user))
                            {
                                if (user.TryGetProperty("username", out var username))
                                    result.AccountTier = username.GetString();
                            }

                            // apiCreditBalance — purchased API credits remaining (can be decimal)
                            if (first.TryGetProperty("apiCreditBalance", out var credits))
                            {
                                var creditVal = credits.ValueKind == System.Text.Json.JsonValueKind.Number
                                    ? credits.GetDouble() : 0;
                                result.Balance = $"{creditVal:N0} API credits";

                                if (creditVal <= 0)
                                {
                                    result.IsQuotaExceeded = true;
                                    result.Detail = "Valid Leonardo.ai key — 0 API credits remaining";
                                }
                            }

                            // Concurrency slots — indicates plan tier
                            if (first.TryGetProperty("apiConcurrencySlots", out var slots))
                            {
                                var slotCount = slots.GetInt32();
                                result.Detail = string.IsNullOrEmpty(result.Detail)
                                    ? $"Valid Leonardo.ai key — {slotCount} concurrency slot(s)"
                                    : result.Detail + $" | {slotCount} slot(s)";
                            }

                            // Token renewal date — subscription info
                            if (first.TryGetProperty("tokenRenewalDate", out var renewal) &&
                                renewal.ValueKind != System.Text.Json.JsonValueKind.Null)
                            {
                                result.Detail = (result.Detail ?? "Valid Leonardo.ai key")
                                    + $" | Renews: {renewal.GetString()}";
                            }

                            if (string.IsNullOrEmpty(result.Detail))
                                result.Detail = "Valid Leonardo.ai key";
                        }
                    }
                    catch { result.Detail = "Valid Leonardo.ai key"; }

                    return result;
                }

                // Check for Leonardo-specific error body
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
                    response.StatusCode == System.Net.HttpStatusCode.Forbidden ||
                    responseBody.Contains("access-denied") ||
                    responseBody.Contains("unauthorized"))
                {
                    return ValidationResult.IsUnauthorized(response.StatusCode,
                        "Invalid Leonardo.ai API key — check UUID format and key validity");
                }

                return response.StatusCode switch
                {
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
            // Leonardo.ai keys are strictly UUID format
            // e.g. a1b2c3d4-e5f6-7890-abcd-ef1234567890
            return !string.IsNullOrWhiteSpace(apiKey) && UuidRegex.IsMatch(apiKey.Trim());
        }
    }
}

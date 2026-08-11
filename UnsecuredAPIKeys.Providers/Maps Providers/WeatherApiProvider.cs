using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Base;
using UnsecuredAPIKeys.Providers.Common;

namespace UnsecuredAPIKeys.Providers.Maps_Providers
{
    /// <summary>
    /// Provider implementation for validating WeatherAPI keys and inferring subscription tier 
    /// (Free, Starter, Pro+, Business, Enterprise) based on documented feature capabilities:
    /// - Free: Up to 3-day forecast, basic history.
    /// - Starter: Up to 7-day forecast & 7-day history.
    /// - Pro+: Up to 14-day forecast & 365-day history.
    /// - Business: 14-day forecast & 2010-present historical archive.
    /// - Enterprise: Enterprise-only features (e.g. 15-minute interval forecast data `tp=15`).
    /// </summary>
    [ApiProvider]
    public class WeatherApiProvider : BaseApiKeyProvider
    {
        private const string BASE_URL = "https://api.weatherapi.com/v1/";

        public override string ProviderName => "WeatherAPI";
        public override ApiTypeEnum ApiType => ApiTypeEnum.WeatherApi;

        public override IEnumerable<string> RegexPatterns =>
        [
            @"\b[a-f0-9]{31,32}\b"
        ];

        public WeatherApiProvider() : base() { }
        public WeatherApiProvider(ILogger<WeatherApiProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(string apiKey, HttpClient httpClient)
        {
            try
            {
                // Step 1: Base validation request (current weather)
                var currentEndpoint = $"{BASE_URL}current.json?q=London&key={apiKey}";
                var response = await httpClient.GetAsync(currentEndpoint);
                var content = await response.Content.ReadAsStringAsync();

                _logger?.LogDebug("WeatherAPI base response: Status={StatusCode}, Body={Body}",
                    response.StatusCode, TruncateResponse(content));

                // If base authentication fails, parse documented WeatherAPI error codes
                if (!response.IsSuccessStatusCode)
                {
                    int errorCode = ExtractErrorCode(content);
                    string errorMessage = ExtractErrorMessage(content);

                    // Documented Code 2006 (API key invalid) or 1002 (API key not provided)
                    if (errorCode is 2006 or 1002 || (errorCode == 0 && (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)))
                    {
                        return ValidationResult.IsUnauthorized(response.StatusCode, $"Invalid WeatherAPI key (Code {errorCode}: {errorMessage})");
                    }

                    // Documented Code 2007 (Monthly quota exceeded) or 2008 (API key disabled)
                    if (errorCode is 2007 or 2008 || response.StatusCode == HttpStatusCode.PaymentRequired)
                    {
                        string reason = errorCode == 2007 ? "Monthly quota exceeded (2007)" : "API key disabled (2008)";

                        return new ValidationResult
                        {
                            Status = ValidationAttemptStatus.Valid,
                            HttpStatusCode = response.StatusCode,
                            IsQuotaExceeded = true,
                            Detail = $"Valid key but {reason}",
                            RawResponse = content
                        };
                    }

                    // Documented Code 2009 (Resource Restricted)
                    if (errorCode == 2009)
                    {
                        return new ValidationResult
                        {
                            Status = ValidationAttemptStatus.Valid,
                            HttpStatusCode = response.StatusCode,
                            AccountTier = "Resource Restricted",
                            Detail = "Valid WeatherAPI key (Resource Restricted)",
                            RawResponse = content
                        };
                    }

                    return ValidationResult.HasHttpError(response.StatusCode, $"WeatherAPI Error ({errorCode}): {TruncateResponse(errorMessage)}");
                }

                // Step 2: Cascade probe to infer subscription tier (Free, Starter, Pro+, Business, Enterprise)
                string plan = await InferSubscriptionPlanAsync(apiKey, httpClient);

                return new ValidationResult
                {
                    Status = ValidationAttemptStatus.Valid,
                    HttpStatusCode = response.StatusCode,
                    AccountTier = plan,
                    Detail = $"Valid WeatherAPI key ({plan} Plan)",
                    RawResponse = $"Valid WeatherAPI key ({plan} Plan)"
                };
            }
            catch (Exception ex)
            {
                return ValidationResult.HasNetworkError(ex.Message);
            }
        }

        /// <summary>
        /// Capability-based cascade probe to infer WeatherAPI subscription tier.
        /// </summary>
        private async Task<string> InferSubscriptionPlanAsync(string apiKey, HttpClient httpClient)
        {
            try
            {
                // Level 1 Probe: Starter vs Free (7-day forecast check)
                var starterResp = await httpClient.GetAsync($"{BASE_URL}forecast.json?q=London&days=7&key={apiKey}");
                if (!starterResp.IsSuccessStatusCode)
                {
                    return "Free";
                }

                // Level 2 Probe: Pro+ vs Starter (60-day historical weather check)
                var dt60 = DateTime.UtcNow.AddDays(-60).ToString("yyyy-MM-dd");
                var proPlusResp = await httpClient.GetAsync($"{BASE_URL}history.json?q=London&dt={dt60}&key={apiKey}");
                if (!proPlusResp.IsSuccessStatusCode)
                {
                    return "Starter";
                }

                // Level 3 Probe: Business vs Pro+ (730-day / 2-year historical weather check)
                var dt730 = DateTime.UtcNow.AddDays(-730).ToString("yyyy-MM-dd");
                var businessResp = await httpClient.GetAsync($"{BASE_URL}history.json?q=London&dt={dt730}&key={apiKey}");
                if (!businessResp.IsSuccessStatusCode)
                {
                    return "Pro+";
                }

                // Level 4 Probe: Enterprise vs Business (15-minute interval forecast 'tp=15' check)
                var enterpriseResp = await httpClient.GetAsync($"{BASE_URL}forecast.json?q=London&days=1&tp=15&key={apiKey}");
                if (enterpriseResp.IsSuccessStatusCode)
                {
                    return "Enterprise";
                }

                return "Business";
            }
            catch
            {
                return "Free";
            }
        }

        private static int ExtractErrorCode(string content)
        {
            try
            {
                using var doc = JsonDocument.Parse(content);
                if (doc.RootElement.TryGetProperty("error", out var errorObj) &&
                    errorObj.TryGetProperty("code", out var codeProp))
                {
                    return codeProp.GetInt32();
                }
            }
            catch { }
            return 0;
        }

        private static string ExtractErrorMessage(string content)
        {
            try
            {
                using var doc = JsonDocument.Parse(content);
                if (doc.RootElement.TryGetProperty("error", out var errorObj) &&
                    errorObj.TryGetProperty("message", out var msgProp))
                {
                    return msgProp.GetString() ?? string.Empty;
                }
            }
            catch { }
            return string.Empty;
        }

        protected override bool IsValidKeyFormat(string apiKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey)) return false;
            var cleanKey = CleanApiKey(apiKey);
            return cleanKey.Length is 31 or 32 && cleanKey.All(c => char.IsAsciiHexDigit(c));
        }
    }
}

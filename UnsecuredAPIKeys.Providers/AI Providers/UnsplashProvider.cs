using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Base;
using UnsecuredAPIKeys.Providers.Common;

namespace UnsecuredAPIKeys.Providers.AI_Providers
{
    /// <summary>
    /// Provider for Unsplash API Access Keys — high-resolution stock photography and visual media API.
    ///
    /// Auth: Authorization: Client-ID {apiKey}  (Header)
    ///       (Also supports query param ?client_id={apiKey})
    ///
    /// Verification Endpoint:
    ///   GET https://api.unsplash.com/photos?per_page=1
    ///   Header: Authorization: Client-ID {apiKey}
    ///
    /// Rate Limit Header Inspection:
    ///   - X-Ratelimit-Limit: 50 (Demo Mode) or 5,000 (Production Mode)
    ///   - X-Ratelimit-Remaining: Remaining requests in current 1-hour window
    ///
    /// Status Codes:
    ///   - 200 OK: Valid key (extracts rate limit & remaining calls)
    ///   - 401 Unauthorized: Invalid or revoked Access Key
    ///   - 403 / 429: Rate limit exceeded / Hourly quota exhausted
    ///   - 5xx: Service outage (ValidationUnavailable)
    /// </summary>
    [ApiProvider]
    public class UnsplashProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "Unsplash";
        public override ApiTypeEnum ApiType => ApiTypeEnum.Unsplash;

        public override IEnumerable<string> RegexPatterns =>
        [
            // Primary env var and config names
            @"UNSPLASH_ACCESS_KEY",
            @"UNSPLASH_API_KEY",
            @"UNSPLASH_CLIENT_ID",
            @"UNSPLASH_KEY",

            // Context-aware value extraction patterns
            @"UNSPLASH[._-]?ACCESS[._-]?KEY\s*[=:]\s*['""]?([A-Za-z0-9\-_]{30,64})['""]?",
            @"UNSPLASH[._-]?API[._-]?KEY\s*[=:]\s*['""]?([A-Za-z0-9\-_]{30,64})['""]?",
            @"UNSPLASH[._-]?CLIENT[._-]?ID\s*[=:]\s*['""]?([A-Za-z0-9\-_]{30,64})['""]?",
            @"unsplash[._-]?key\s*[=:]\s*['""]?([A-Za-z0-9\-_]{30,64})['""]?",

            // Client-ID header format
            @"Client-ID\s+([A-Za-z0-9\-_]{30,64})"
        ];

        public UnsplashProvider() : base() { }
        public UnsplashProvider(ILogger<UnsplashProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(string apiKey, HttpClient httpClient)
        {
            try
            {
                // Primary Verification: GET https://api.unsplash.com/photos?per_page=1
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.unsplash.com/photos?per_page=1");
                request.Headers.Add("Authorization", $"Client-ID {apiKey}");
                request.Headers.Add("Accept-Version", "v1");

                var response = await httpClient.SendAsync(request);
                string responseBody = await response.Content.ReadAsStringAsync();

                _logger?.LogDebug("Unsplash API response: Status={StatusCode}, Body={Body}",
                    response.StatusCode, TruncateResponse(responseBody));

                // ── 200 OK: Valid Access Key ───────────────────────────────────────────
                if (IsSuccessStatusCode(response.StatusCode))
                {
                    var result = ValidationResult.Success(response.StatusCode, "Valid Unsplash Access Key");

                    // Inspect Unsplash Rate Limit Headers
                    string? rateLimit = response.Headers.TryGetValues("X-Ratelimit-Limit", out var limitVals)
                        ? limitVals.FirstOrDefault() : null;
                    string? rateRemaining = response.Headers.TryGetValues("X-Ratelimit-Remaining", out var remVals)
                        ? remVals.FirstOrDefault() : null;

                    var metadata = new Dictionary<string, object>
                    {
                        ["authentication_valid"] = true,
                        ["tested_endpoint"] = "https://api.unsplash.com/photos",
                        ["rate_limit_hourly"] = rateLimit ?? "Unknown",
                        ["rate_limit_remaining"] = rateRemaining ?? "Unknown"
                    };

                    if (int.TryParse(rateLimit, out int limitNum))
                    {
                        result.AccountTier = limitNum >= 1000 ? $"Production ({limitNum:N0} req/hr)" : $"Demo ({limitNum} req/hr)";
                        if (int.TryParse(rateRemaining, out int remNum))
                        {
                            result.Balance = $"{remNum}/{limitNum} requests remaining this hour";
                        }
                    }

                    result.Metadata = metadata;
                    result.RawResponse = responseBody;
                    return result;
                }

                // ── 401 Unauthorized: Invalid Key ──────────────────────────────────────
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    var result = ValidationResult.IsUnauthorized(response.StatusCode, "Invalid Unsplash Access Key");
                    result.RawResponse = responseBody;
                    return result;
                }

                // ── 403 Forbidden / 429 Too Many Requests: Rate Limited ────────────────
                if (response.StatusCode == HttpStatusCode.Forbidden || (int)response.StatusCode == 429)
                {
                    string? rateRemaining = response.Headers.TryGetValues("X-Ratelimit-Remaining", out var remVals)
                        ? remVals.FirstOrDefault() : null;

                    bool isQuotaExhausted = rateRemaining == "0" || responseBody.Contains("Rate Limit Exceeded", StringComparison.OrdinalIgnoreCase);

                    if (isQuotaExhausted)
                    {
                        var quotaResult = new ValidationResult
                        {
                            Status = ValidationAttemptStatus.Valid,
                            HttpStatusCode = response.StatusCode,
                            IsQuotaExceeded = true,
                            Detail = "Valid Unsplash key — hourly rate limit exhausted"
                        };
                        quotaResult.Metadata = new Dictionary<string, object>
                        {
                            ["authentication_valid"] = true,
                            ["quota_exceeded"] = true,
                            ["rate_limit_remaining"] = "0"
                        };
                        quotaResult.RawResponse = responseBody;
                        return quotaResult;
                    }

                    return new ValidationResult
                    {
                        Status = ValidationAttemptStatus.ValidationUnavailable,
                        HttpStatusCode = response.StatusCode,
                        Detail = "Unsplash rate limit reached (HTTP 429/403)",
                        RawResponse = responseBody
                    };
                }

                // ── 5xx Server Error: Service Outage ───────────────────────────────────
                if ((int)response.StatusCode >= 500)
                {
                    return new ValidationResult
                    {
                        Status = ValidationAttemptStatus.ValidationUnavailable,
                        HttpStatusCode = response.StatusCode,
                        Detail = $"Unsplash server error (HTTP {(int)response.StatusCode})",
                        RawResponse = responseBody
                    };
                }

                return ValidationResult.HasHttpError(response.StatusCode,
                    $"Unsplash returned unexpected status {response.StatusCode}: {TruncateResponse(responseBody)}");
            }
            catch (Exception ex)
            {
                return ValidationResult.HasNetworkError(ex.Message);
            }
        }

        protected override bool IsValidKeyFormat(string apiKey)
        {
            return !string.IsNullOrWhiteSpace(apiKey) &&
                   apiKey.Length >= 30 &&
                   apiKey.Length <= 64 &&
                   !apiKey.Contains(' ');
        }
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Base;
using UnsecuredAPIKeys.Providers.Common;

namespace UnsecuredAPIKeys.Providers.AI_Providers
{
    /// <summary>
    /// Provider for RunwayML API keys — generative AI video platform.
    ///
    /// Key format: key_ followed by 128 hexadecimal characters (132 total chars length: ^key_[0-9a-f]{128}$).
    /// Base URL: https://api.dev.runwayml.com (per official Runway Dev documentation)
    /// Auth: Authorization: Bearer {key_...}
    /// Required header: X-Runway-Version: 2024-11-06
    ///
    /// Verification strategy:
    ///   - Documented read-only endpoint: GET https://api.dev.runwayml.com/v1/tasks/{id}
    ///   - Note: Runway Dev API does not expose a dedicated non-generation authentication endpoint.
    ///     Generative task creation is avoided to prevent billable resource usage.
    ///   - Response status mapping for read-only task probe:
    ///       • 401 Unauthorized -> Invalid / rejected credential
    ///       • 200 OK -> Valid key (requested task found)
    ///       • 404 Not Found -> Inconclusive (synthetic task ID not found)
    ///       • 403 Forbidden -> Inconclusive (access restricted)
    ///       • 402 Payment Required -> Inconclusive
    ///       • 429 Too Many Requests -> Validation unavailable (rate limited)
    ///       • 5xx -> Validation unavailable (service error)
    /// Official docs: https://docs.dev.runwayml.com
    /// </summary>
    [ApiProvider]
    public class RunwayProvider : BaseApiKeyProvider
    {
        private static readonly Regex KeyFormatRegex = new(
            @"^key_[0-9a-fA-F]{128}$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public override string ProviderName => "RunwayML";
        public override ApiTypeEnum ApiType => ApiTypeEnum.RunwayML;

        public override IEnumerable<string> RegexPatterns =>
        [
            @"\bkey_[0-9a-fA-F]{128}\b",
            @"RUNWAYML_API_SECRET",
            @"RUNWAY_API_KEY",
            @"RUNWAY_API_SECRET"
        ];

        public RunwayProvider() : base() { }
        public RunwayProvider(ILogger<RunwayProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(string apiKey, HttpClient httpClient)
        {
            try
            {
                // GET /v1/tasks/{id} on api.dev.runwayml.com — officially documented endpoint per Runway Dev guide.
                const string probeTaskId = "00000000-0000-0000-0000-000000000000";
                using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.dev.runwayml.com/v1/tasks/{probeTaskId}");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                request.Headers.Add("X-Runway-Version", "2024-11-06");

                var response = await httpClient.SendAsync(request);
                string responseBody = await response.Content.ReadAsStringAsync();

                _logger?.LogDebug("RunwayML task probe response: Status={StatusCode}, Body={Body}",
                    response.StatusCode, TruncateResponse(responseBody));

                // 401 Unauthorized -> API rejected the supplied credential
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    return ValidationResult.IsUnauthorized(response.StatusCode,
                        "RunwayML API rejected the supplied credential (401 Unauthorized)");
                }

                // 200 OK -> requested task was found; authentication succeeded
                if (IsSuccessStatusCode(response.StatusCode))
                {
                    var result = ValidationResult.Success(response.StatusCode, "Valid RunwayML API key");
                    result.RawResponse = responseBody;
                    result.Metadata = new Dictionary<string, object>
                    {
                        ["authentication_valid"] = true,
                        ["api_version"] = "2024-11-06",
                        ["probe_method"] = "GET /v1/tasks/{id}",
                        ["probe_status"] = (int)response.StatusCode
                    };

                    try
                    {
                        using var doc = JsonDocument.Parse(responseBody);
                        if (doc.RootElement.TryGetProperty("status", out var statusProp) && statusProp.ValueKind == JsonValueKind.String)
                        {
                            result.Metadata["task_status"] = statusProp.GetString() ?? "";
                        }
                    }
                    catch { /* Best effort */ }

                    return result;
                }

                // 404 Not Found -> synthetic probe task ID not found; live validation is inconclusive without a dedicated non-generation auth endpoint
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    var result = ValidationResult.ValidationUnavailable(response.StatusCode,
                        "RunwayML task probe returned 404 Not Found — task ID not found; credential validity is inconclusive");
                    result.RawResponse = responseBody;
                    result.Metadata = new Dictionary<string, object>
                    {
                        ["api_version"] = "2024-11-06",
                        ["probe_method"] = "GET /v1/tasks/{id}",
                        ["probe_status"] = 404
                    };
                    return result;
                }

                // 403 Forbidden -> access restricted; credential validity is inconclusive
                if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    var result = ValidationResult.ValidationUnavailable(response.StatusCode,
                        "RunwayML API key access forbidden (403) — access restricted; credential validity is inconclusive");
                    result.RawResponse = responseBody;
                    return result;
                }

                // 402 Payment Required -> billing issue; credential validity is inconclusive without a dedicated auth endpoint
                if (response.StatusCode == HttpStatusCode.PaymentRequired || ContainsAny(responseBody, QuotaIndicators))
                {
                    var result = ValidationResult.ValidationUnavailable(response.StatusCode,
                        "RunwayML API returned 402 Payment Required — credential validity is inconclusive without a dedicated auth endpoint");
                    result.RawResponse = responseBody;
                    return result;
                }

                // 429 Too Many Requests -> validation unavailable
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    var result = ValidationResult.ValidationUnavailable(response.StatusCode,
                        "RunwayML API rate limited (429) — validation unavailable");
                    result.RawResponse = responseBody;
                    return result;
                }

                // 5xx Service Error -> validation unavailable
                if ((int)response.StatusCode >= 500)
                {
                    var result = ValidationResult.ValidationUnavailable(response.StatusCode,
                        $"RunwayML service error ({response.StatusCode}) — validation unavailable");
                    result.RawResponse = responseBody;
                    return result;
                }

                var errResult = ValidationResult.HasHttpError(response.StatusCode,
                    $"RunwayML task probe returned status {response.StatusCode}. Body: {TruncateResponse(responseBody)}");
                errResult.RawResponse = responseBody;
                return errResult;
            }
            catch (Exception ex)
            {
                return ValidationResult.HasNetworkError(ex.Message);
            }
        }

        protected override bool IsValidKeyFormat(string apiKey)
        {
            // RunwayML API keys strictly match ^key_[0-9a-f]{128}$ (132 total characters) per official docs
            return !string.IsNullOrWhiteSpace(apiKey) && KeyFormatRegex.IsMatch(apiKey.Trim());
        }
    }
}

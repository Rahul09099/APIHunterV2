using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Base;
using UnsecuredAPIKeys.Providers.Common;

namespace UnsecuredAPIKeys.Providers.Communication_Providers
{
    /// <summary>
    /// Provider for Mailgun API keys.
    /// Auth: Basic Authentication with username 'api' and password '{apiKey}'.
    /// Base URL: https://api.mailgun.net
    ///
    /// Verification strategy:
    ///   1. Primary Auth: GET https://api.mailgun.net/v5/users/me
    ///   2. Optional Usage lookup: GET https://api.mailgun.net/v5/accounts/limit/custom/monthly
    /// Official docs: https://documentation.mailgun.com/docs/mailgun/api-reference/mg-auth
    /// </summary>
    [ApiProvider]
    public class MailgunProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "Mailgun";
        public override ApiTypeEnum ApiType => ApiTypeEnum.Mailgun;

        public override IEnumerable<string> RegexPatterns =>
        [
            @"\bkey-[A-Za-z0-9]{32,}\b",
            @"MAILGUN_API_KEY",
            @"MAILGUN_SECRET",
            @"mailgun[_-]?[A-Za-z0-9]{32,}",
            @"(?i)\bMAILGUN[\s_-]*API[\s_-]*KEY\s*[:=]\s*['""]?([A-Za-z0-9_-]{20,256})['""]?"
        ];

        public MailgunProvider() : base() { }
        public MailgunProvider(ILogger<MailgunProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(string apiKey, HttpClient httpClient)
        {
            try
            {
                // Mailgun uses Basic auth with 'api' as username
                var authValue = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($"api:{apiKey}"));

                // Step 1: Direct authentication check via GET /v5/users/me
                using var userRequest = new HttpRequestMessage(HttpMethod.Get, "https://api.mailgun.net/v5/users/me");
                userRequest.Headers.Authorization = new AuthenticationHeaderValue("Basic", authValue);

                var userResponse = await httpClient.SendAsync(userRequest);
                string userBody = await userResponse.Content.ReadAsStringAsync();

                _logger?.LogDebug("Mailgun users/me response: Status={StatusCode}, Body={Body}",
                    userResponse.StatusCode, TruncateResponse(userBody));

                if (userResponse.StatusCode == HttpStatusCode.Unauthorized)
                {
                    return ValidationResult.IsUnauthorized(userResponse.StatusCode,
                        "Invalid or expired Mailgun API key");
                }

                if (userResponse.StatusCode == HttpStatusCode.Forbidden)
                {
                    return ValidationResult.ValidationUnavailable(userResponse.StatusCode,
                        "Mailgun API key authenticated but access is restricted");
                }

                if (userResponse.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    return ValidationResult.ValidationUnavailable(userResponse.StatusCode,
                        "Mailgun API rate limited (429)");
                }

                if ((int)userResponse.StatusCode >= 500)
                {
                    return ValidationResult.ValidationUnavailable(userResponse.StatusCode,
                        $"Mailgun service error ({userResponse.StatusCode}) — validation unavailable");
                }

                if (!IsSuccessStatusCode(userResponse.StatusCode))
                {
                    return ValidationResult.HasHttpError(userResponse.StatusCode,
                        $"Mailgun authentication failed: Status {userResponse.StatusCode}. Response: {TruncateResponse(userBody)}");
                }

                // Authentication confirmed
                var result = ValidationResult.Success(userResponse.StatusCode, "Valid Mailgun API key");
                result.RawResponse = userBody;
                result.Metadata = new Dictionary<string, object>
                {
                    ["authentication_valid"] = true
                };

                // Step 2: Optional custom monthly limit lookup
                try
                {
                    using var limitRequest = new HttpRequestMessage(HttpMethod.Get, "https://api.mailgun.net/v5/accounts/limit/custom/monthly");
                    limitRequest.Headers.Authorization = new AuthenticationHeaderValue("Basic", authValue);

                    var limitResponse = await httpClient.SendAsync(limitRequest);
                    string limitBody = await limitResponse.Content.ReadAsStringAsync();

                    if (IsSuccessStatusCode(limitResponse.StatusCode))
                    {
                        using var doc = JsonDocument.Parse(limitBody);
                        var root = doc.RootElement;

                        if (root.TryGetProperty("limit", out var limitProp) &&
                            root.TryGetProperty("current", out var currentProp))
                        {
                            double limitVal = limitProp.ValueKind == JsonValueKind.Number ? limitProp.GetDouble() : 0;
                            double currentVal = currentProp.ValueKind == JsonValueKind.Number ? currentProp.GetDouble() : 0;

                            result.Metadata["monthly_limit"] = limitVal;
                            result.Metadata["monthly_used"] = currentVal;
                            result.Balance = $"{currentVal} / {limitVal} messages used";
                            result.Detail = $"Valid Mailgun API key — {currentVal} / {limitVal} messages used this month.";
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug("Mailgun limit lookup failed: {Message}", ex.Message);
                }

                return result;
            }
            catch (Exception ex)
            {
                return ValidationResult.HasNetworkError(ex.Message);
            }
        }

        protected override bool IsValidKeyFormat(string apiKey)
        {
            return !string.IsNullOrWhiteSpace(apiKey) &&
                   apiKey.Length >= 20 &&
                   apiKey.Length <= 256;
        }
    }
}

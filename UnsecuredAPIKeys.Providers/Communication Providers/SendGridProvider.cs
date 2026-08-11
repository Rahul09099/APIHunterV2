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
    /// Provider for SendGrid API keys.
    /// Auth: Authorization: Bearer SG.{id}.{secret}
    /// Base URL: https://api.sendgrid.com/v3
    ///
    /// Verification strategy:
    ///   1. Scopes discovery: GET https://api.sendgrid.com/v3/scopes (returns granted key permissions, e.g. mail.send)
    ///   2. Optional Credits lookup: GET https://api.sendgrid.com/v3/user/credits (if user.credits.read scope present)
    ///   3. Optional Account lookup: GET https://api.sendgrid.com/v3/user/account (if user.account.read scope present)
    /// Official docs: https://docs.sendgrid.com/api-reference/access-settings/api-keys
    /// </summary>
    [ApiProvider]
    public class SendGridProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "SendGrid";
        public override ApiTypeEnum ApiType => ApiTypeEnum.SendGrid;

        public override IEnumerable<string> RegexPatterns =>
        [
            @"\bSG\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\b",
            @"SENDGRID_API_KEY",
            @"SENDGRID_SECRET",
            @"sendgrid[_-]?[A-Za-z0-9]{32,}",
            @"(?i)\bSENDGRID[\s_-]*API[\s_-]*KEY\s*[:=]\s*['""]?([A-Za-z0-9_.-]{20,256})['""]?"
        ];

        public SendGridProvider() : base() { }
        public SendGridProvider(ILogger<SendGridProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(string apiKey, HttpClient httpClient)
        {
            try
            {
                // Step 1: Query API key scopes via GET /v3/scopes
                using var scopesRequest = new HttpRequestMessage(HttpMethod.Get, "https://api.sendgrid.com/v3/scopes");
                scopesRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

                var scopesResponse = await httpClient.SendAsync(scopesRequest);
                string scopesBody = await scopesResponse.Content.ReadAsStringAsync();

                _logger?.LogDebug("SendGrid scopes API response: Status={StatusCode}, Body={Body}",
                    scopesResponse.StatusCode, TruncateResponse(scopesBody));

                if (scopesResponse.StatusCode == HttpStatusCode.Unauthorized)
                {
                    return ValidationResult.IsUnauthorized(scopesResponse.StatusCode,
                        "Invalid or expired SendGrid API key");
                }

                if (scopesResponse.StatusCode == HttpStatusCode.Forbidden)
                {
                    return ValidationResult.ValidationUnavailable(scopesResponse.StatusCode,
                        "SendGrid API key access forbidden (403)");
                }

                if (scopesResponse.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    return ValidationResult.ValidationUnavailable(scopesResponse.StatusCode,
                        "SendGrid API rate limited (429)");
                }

                if ((int)scopesResponse.StatusCode >= 500)
                {
                    return ValidationResult.ValidationUnavailable(scopesResponse.StatusCode,
                        $"SendGrid service error ({scopesResponse.StatusCode}) — validation unavailable");
                }

                if (!IsSuccessStatusCode(scopesResponse.StatusCode))
                {
                    return ValidationResult.HasHttpError(scopesResponse.StatusCode,
                        $"SendGrid scopes request failed: Status {scopesResponse.StatusCode}. Response: {TruncateResponse(scopesBody)}");
                }

                // Authentication succeeded via /v3/scopes
                var result = ValidationResult.Success(scopesResponse.StatusCode, "Valid SendGrid key");
                result.RawResponse = scopesBody;
                result.Metadata = new Dictionary<string, object>
                {
                    ["authentication_valid"] = true
                };

                bool hasMailSend = false;
                bool canReadCredits = false;
                bool canReadAccount = false;

                try
                {
                    using var doc = JsonDocument.Parse(scopesBody);
                    if (doc.RootElement.TryGetProperty("scopes", out var scopesArr) && scopesArr.ValueKind == JsonValueKind.Array)
                    {
                        var scopesList = scopesArr.EnumerateArray()
                            .Select(s => s.GetString() ?? "")
                            .Where(s => !string.IsNullOrEmpty(s))
                            .ToList();

                        result.Metadata["scopes"] = scopesList;
                        hasMailSend = scopesList.Contains("mail.send", StringComparer.OrdinalIgnoreCase);
                        canReadCredits = scopesList.Contains("user.credits.read", StringComparer.OrdinalIgnoreCase);
                        canReadAccount = scopesList.Contains("user.account.read", StringComparer.OrdinalIgnoreCase);

                        result.Metadata["mail_send_enabled"] = hasMailSend;
                        result.Metadata["can_read_credits"] = canReadCredits;
                        result.Metadata["can_read_account"] = canReadAccount;
                    }
                }
                catch { /* Best effort */ }

                result.Detail = hasMailSend
                    ? "Valid SendGrid key — mail.send permission granted."
                    : "Valid SendGrid key — authenticated (restricted permissions).";

                // Step 2: Optional account type lookup via GET /v3/user/account (if user.account.read scope present)
                if (canReadAccount)
                {
                    try
                    {
                        using var accountRequest = new HttpRequestMessage(HttpMethod.Get, "https://api.sendgrid.com/v3/user/account");
                        accountRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

                        var accountResponse = await httpClient.SendAsync(accountRequest);
                        string accountBody = await accountResponse.Content.ReadAsStringAsync();

                        if (IsSuccessStatusCode(accountResponse.StatusCode))
                        {
                            using var doc = JsonDocument.Parse(accountBody);
                            if (doc.RootElement.TryGetProperty("type", out var typeProp) && typeProp.ValueKind == JsonValueKind.String)
                            {
                                string accountType = typeProp.GetString() ?? "";
                                result.AccountTier = accountType;
                                result.Metadata["account_type"] = accountType;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogDebug("SendGrid account lookup failed: {Message}", ex.Message);
                    }
                }

                // Step 3: Optional user credits lookup via GET /v3/user/credits (if user.credits.read scope present)
                if (canReadCredits)
                {
                    try
                    {
                        using var creditsRequest = new HttpRequestMessage(HttpMethod.Get, "https://api.sendgrid.com/v3/user/credits");
                        creditsRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

                        var creditsResponse = await httpClient.SendAsync(creditsRequest);
                        string creditsBody = await creditsResponse.Content.ReadAsStringAsync();

                        if (IsSuccessStatusCode(creditsResponse.StatusCode))
                        {
                            using var doc = JsonDocument.Parse(creditsBody);
                            var root = doc.RootElement;

                            if (root.TryGetProperty("remain", out var remainProp) &&
                                root.TryGetProperty("total", out var totalProp))
                            {
                                long remainVal = remainProp.ValueKind == JsonValueKind.Number ? remainProp.GetInt64() : 0;
                                long totalVal = totalProp.ValueKind == JsonValueKind.Number ? totalProp.GetInt64() : 0;

                                result.Metadata["credits_remaining"] = remainVal;
                                result.Metadata["credits_total"] = totalVal;
                                result.Balance = $"{remainVal} / {totalVal} Credits Remaining";

                                if (remainVal <= 0)
                                {
                                    result.IsQuotaExceeded = true;
                                    result.Detail = "Valid SendGrid key — 0 credits remaining.";
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogDebug("SendGrid credits lookup failed: {Message}", ex.Message);
                    }
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
                   apiKey.StartsWith("SG.", StringComparison.Ordinal) &&
                   apiKey.Length <= 69;
        }
    }
}

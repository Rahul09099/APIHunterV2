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
    /// Provider for Slack API tokens (xoxb- bot, xoxp- user, xapp- app, xwfp- workflow).
    /// Auth: Authorization: Bearer {token}
    /// Base URL: https://slack.com/api
    ///
    /// Verification strategy:
    ///   1. Primary auth: POST https://slack.com/api/auth.test (requires no specific OAuth scopes)
    ///   2. Optional billing plan: POST https://slack.com/api/team.billing.info (requires team.billing:read scope)
    /// Official docs: https://docs.slack.dev/reference/methods/auth.test
    /// </summary>
    [ApiProvider]
    public class SlackProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "Slack";
        public override ApiTypeEnum ApiType => ApiTypeEnum.Slack;

        public override IEnumerable<string> RegexPatterns =>
        [
            @"\b(xoxb|xoxp|xapp|xwfp|xoxr)-[A-Za-z0-9-]{10,256}\b",
            @"SLACK_TOKEN",
            @"SLACK_API_TOKEN",
            @"SLACK_BOT_TOKEN",
            @"SLACK_USER_TOKEN",
            @"(?i)\bSLACK[\s_-]*API[\s_-]*TOKEN\s*[:=]\s*['""]?([A-Za-z0-9_-]{20,256})['""]?"
        ];

        public SlackProvider() : base() { }
        public SlackProvider(ILogger<SlackProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(string apiKey, HttpClient httpClient)
        {
            try
            {
                // Step 1: Query auth.test via POST https://slack.com/api/auth.test
                using var request = new HttpRequestMessage(HttpMethod.Post, "https://slack.com/api/auth.test");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

                var response = await httpClient.SendAsync(request);
                string responseBody = await response.Content.ReadAsStringAsync();

                _logger?.LogDebug("Slack API auth.test response: Status={StatusCode}, Body={Body}",
                    response.StatusCode, TruncateResponse(responseBody));

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    return ValidationResult.ValidationUnavailable(response.StatusCode,
                        "Slack API rate limited (429) — validation unavailable");
                }

                if ((int)response.StatusCode >= 500)
                {
                    return ValidationResult.ValidationUnavailable(response.StatusCode,
                        $"Slack service error ({response.StatusCode}) — validation unavailable");
                }

                if (IsSuccessStatusCode(response.StatusCode))
                {
                    using var doc = JsonDocument.Parse(responseBody);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("ok", out var ok) && ok.GetBoolean())
                    {
                        var result = ValidationResult.Success(response.StatusCode, "Valid Slack token");
                        result.RawResponse = responseBody;

                        string teamName = root.TryGetProperty("team", out var teamProp) ? teamProp.GetString() ?? "" : "";
                        string teamId = root.TryGetProperty("team_id", out var teamIdProp) ? teamIdProp.GetString() ?? "" : "";
                        string userName = root.TryGetProperty("user", out var userProp) ? userProp.GetString() ?? "" : "";
                        string userId = root.TryGetProperty("user_id", out var userIdProp) ? userIdProp.GetString() ?? "" : "";
                        string botId = root.TryGetProperty("bot_id", out var botIdProp) ? botIdProp.GetString() ?? "" : "";

                        string tokenType = apiKey.StartsWith("xoxb-", StringComparison.Ordinal) ? "bot"
                            : apiKey.StartsWith("xoxp-", StringComparison.Ordinal) ? "user"
                            : apiKey.StartsWith("xapp-", StringComparison.Ordinal) ? "app"
                            : apiKey.StartsWith("xwfp-", StringComparison.Ordinal) ? "workflow"
                            : "other";

                        result.AccountTier = !string.IsNullOrEmpty(teamName) ? teamName : "Slack Workspace";
                        result.Detail = $"Valid Slack {tokenType} token — User: {userName} ({userId}), Workspace: {teamName} ({teamId})";

                        result.Metadata = new Dictionary<string, object>
                        {
                            ["authentication_valid"] = true,
                            ["token_type"] = tokenType,
                            ["team_name"] = teamName,
                            ["team_id"] = teamId,
                            ["user_name"] = userName,
                            ["user_id"] = userId,
                            ["bot_id"] = botId
                        };

                        // Step 2: Optional billing plan query via POST https://slack.com/api/team.billing.info
                        try
                        {
                            using var billingRequest = new HttpRequestMessage(HttpMethod.Post, "https://slack.com/api/team.billing.info");
                            billingRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

                            var billingResponse = await httpClient.SendAsync(billingRequest);
                            string billingBody = await billingResponse.Content.ReadAsStringAsync();

                            if (IsSuccessStatusCode(billingResponse.StatusCode))
                            {
                                using var billingDoc = JsonDocument.Parse(billingBody);
                                var billingRoot = billingDoc.RootElement;

                                if (billingRoot.TryGetProperty("ok", out var billingOk) && billingOk.GetBoolean())
                                {
                                    if (billingRoot.TryGetProperty("plan", out var planProp) && planProp.ValueKind == JsonValueKind.String)
                                    {
                                        string planName = planProp.GetString() ?? "";
                                        result.Metadata["billing_plan"] = planName;
                                        result.Metadata["billing_access"] = true;
                                    }
                                }
                                else
                                {
                                    result.Metadata["billing_access"] = false;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogDebug("Slack team.billing.info lookup failed: {Message}", ex.Message);
                        }

                        return result;
                    }
                    else
                    {
                        string error = root.TryGetProperty("error", out var err) ? err.GetString() ?? "unknown_error" : "unknown_error";

                        if (error == "invalid_auth" || error == "token_revoked" || error == "token_expired" || error == "account_inactive")
                        {
                            return ValidationResult.IsUnauthorized(response.StatusCode, $"Slack rejected key: {error}");
                        }

                        return ValidationResult.HasHttpError(response.StatusCode, $"Slack API error: {error}");
                    }
                }

                if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                {
                    return ValidationResult.IsUnauthorized(response.StatusCode, "Invalid or expired Slack token");
                }

                return ValidationResult.HasHttpError(response.StatusCode,
                    $"Slack request failed: Status {response.StatusCode}. Response: {TruncateResponse(responseBody)}");
            }
            catch (Exception ex)
            {
                return ValidationResult.HasNetworkError(ex.Message);
            }
        }

        protected override bool IsValidKeyFormat(string apiKey)
        {
            // Slack tokens strictly start with xoxb-, xoxp-, xapp-, xwfp-, xoxr-, or xox
            return !string.IsNullOrWhiteSpace(apiKey) &&
                   (apiKey.StartsWith("xoxb-", StringComparison.Ordinal) ||
                    apiKey.StartsWith("xoxp-", StringComparison.Ordinal) ||
                    apiKey.StartsWith("xapp-", StringComparison.Ordinal) ||
                    apiKey.StartsWith("xwfp-", StringComparison.Ordinal) ||
                    apiKey.StartsWith("xoxr-", StringComparison.Ordinal) ||
                    apiKey.StartsWith("xox", StringComparison.Ordinal));
        }
    }
}

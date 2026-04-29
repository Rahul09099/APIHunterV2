using Microsoft.Extensions.Logging;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Base;
using UnsecuredAPIKeys.Providers.Common;

namespace UnsecuredAPIKeys.Providers.Communication_Providers
{
    /// <summary>
    /// Provider for Slack API tokens - scraper only (no verification implemented yet)
    /// </summary>
    [ApiProvider]
    public class SlackProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "Slack";
        public override ApiTypeEnum ApiType => ApiTypeEnum.Slack;

        public override IEnumerable<string> RegexPatterns =>
        [
            @"xox[baprs]-[A-Za-z0-9-]{10,}",  // Slack token formats (bot, app, user, refresh, signing)
            @"slack[_-]?[A-Za-z0-9]{32,}",
            @"SLACK_TOKEN",
            @"SLACK_API_TOKEN"
        ];

        public SlackProvider() : base() { }
        public SlackProvider(ILogger<SlackProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(string apiKey, HttpClient httpClient)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "https://slack.com/api/auth.test");
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

                var response = await httpClient.SendAsync(request);
                string responseBody = await response.Content.ReadAsStringAsync();

                _logger?.LogDebug("Slack API response: Status={StatusCode}, Body={Body}",
                    response.StatusCode, TruncateResponse(responseBody));

                if (IsSuccessStatusCode(response.StatusCode))
                {
                    // Slack returns 200 OK even for invalid tokens, but with "ok": false in body
                    using var doc = System.Text.Json.JsonDocument.Parse(responseBody);
                    var root = doc.RootElement;
                    
                    if (root.TryGetProperty("ok", out var ok) && ok.GetBoolean())
                    {
                        var result = ValidationResult.Success(response.StatusCode, "Valid Slack token");
                        
                        string teamName = root.TryGetProperty("team", out var team) ? team.GetString() ?? "" : "";
                        string user = root.TryGetProperty("user", out var usr) ? usr.GetString() ?? "" : "";
                        result.Detail = $"Workspace: {teamName}, User: {user}";

                        // Try to get billing plan type
                        try 
                        {
                            using var billingRequest = new HttpRequestMessage(HttpMethod.Get, "https://slack.com/api/team.billing.info");
                            billingRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
                            var billingResponse = await httpClient.SendAsync(billingRequest);
                            var billingBody = await billingResponse.Content.ReadAsStringAsync();
                            
                            using var billingDoc = System.Text.Json.JsonDocument.Parse(billingBody);
                            if (billingDoc.RootElement.TryGetProperty("plan", out var plan))
                            {
                                result.Balance = $"Plan: {plan.GetString()}";
                            }
                        }
                        catch { /* Best effort */ }

                        return result;
                    }
                    else if (responseBody.Contains("invalid_auth") || responseBody.Contains("account_inactive"))
                    {
                         return ValidationResult.IsUnauthorized(response.StatusCode, $"Slack rejected key: {TruncateResponse(responseBody)}");
                    }
                    else
                    {
                        // Some other error inside 200 OK
                         return ValidationResult.Success(response.StatusCode, $"Technically valid request but error response: {TruncateResponse(responseBody)}");
                    }
                }
                else if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized || 
                         response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    return ValidationResult.IsUnauthorized(response.StatusCode);
                }
                else
                {
                    // Check for rate limits
                    if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests || 
                        responseBody.Contains("ratelimited"))
                    {
                        return ValidationResult.Success(response.StatusCode, $"Valid key but rate limited: {TruncateResponse(responseBody)}");
                    }

                    return ValidationResult.HasHttpError(response.StatusCode, 
                        $"API request failed with status {response.StatusCode}. Response: {TruncateResponse(responseBody)}");
                }
            }
            catch (Exception ex)
            {
                return ValidationResult.HasNetworkError(ex.Message);
            }
        }

        protected override bool IsValidKeyFormat(string apiKey)
        {
            return !string.IsNullOrWhiteSpace(apiKey) && (apiKey.StartsWith("xox") || apiKey.Length >= 32);
        }
    }
}

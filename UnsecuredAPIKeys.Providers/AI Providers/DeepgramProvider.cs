using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Base;
using UnsecuredAPIKeys.Providers.Common;

namespace UnsecuredAPIKeys.Providers.AI_Providers
{
    /// <summary>
    /// Provider for Deepgram API keys — real-time and batch speech-to-text.
    ///
    /// Key format: plain alphanumeric string, NO fixed prefix.
    ///   - GitGuardian confirms: Prefixed=False, High recall=False
    ///   - RedHunt Labs example uses "dg.xxxxxxx" as a placeholder, NOT an actual prefix
    ///   - Keys are typically 40 alphanumeric characters (a-z, A-Z, 0-9)
    ///   - NOT hex-only — contains uppercase and mixed case characters
    ///
    /// Auth: Authorization: Token {apiKey}   (NOT Bearer — confirmed from official docs)
    ///   curl: -H "Authorization: Token [API_KEY]"
    ///
    /// Verification strategy (two-step):
    ///   Step 1: GET https://api.deepgram.com/v1/projects
    ///     - User-specific endpoint — 401 without valid key
    ///     - Response: { "projects": [{ "project_id": "...", "name": "..." }] }
    ///     - Extracts project_id for Step 2
    ///
    ///   Step 2: GET https://api.deepgram.com/v1/projects/{project_id}/balances
    ///     - Response: { "balances": [{ "amount": 1250.75, "units": "USD" }] }
    ///     - Confirmed from official Deepgram API reference
    ///
    /// Common env var names (from RedHunt Labs research):
    ///   DEEPGRAM_API_KEY, DG_API_KEY, DEEPGRAM_KEY, API_KEY_DEEPGRAM, DEEPGRAM_SECRET, DG_KEY
    /// </summary>
    [ApiProvider]
    public class DeepgramProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "Deepgram";
        public override ApiTypeEnum ApiType => ApiTypeEnum.Deepgram;

        public override IEnumerable<string> RegexPatterns =>
        [
            // Primary env var names — confirmed from RedHunt Labs and official docs
            @"DEEPGRAM_API_KEY",
            @"DG_API_KEY",
            @"DEEPGRAM_KEY",
            @"API_KEY_DEEPGRAM",
            @"DEEPGRAM_SECRET",
            @"DG_KEY",

            // Context-aware value extraction — only match alphanumeric values near Deepgram context
            // Keys are plain alphanumeric, no fixed prefix (GitGuardian: Prefixed=False)
            @"DEEPGRAM_API_KEY\s*[=:]\s*['""]?([A-Za-z0-9]{32,})['""]?",
            @"DG_API_KEY\s*[=:]\s*['""]?([A-Za-z0-9]{32,})['""]?",
            @"deepgram[._-]?api[._-]?key\s*[=:]\s*['""]?([A-Za-z0-9]{32,})['""]?"
        ];

        public DeepgramProvider() : base() { }
        public DeepgramProvider(ILogger<DeepgramProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(string apiKey, HttpClient httpClient)
        {
            try
            {
                // Step 1: GET /v1/projects — user-specific, always requires auth
                // Confirmed from RedHunt Labs: "curl -X GET https://api.deepgram.com/v1/projects -H 'Authorization: Token [API_KEY]'"
                using var projectsRequest = new HttpRequestMessage(HttpMethod.Get,
                    "https://api.deepgram.com/v1/projects");
                projectsRequest.Headers.Authorization = new AuthenticationHeaderValue("Token", apiKey);

                var projectsResponse = await httpClient.SendAsync(projectsRequest);
                string projectsBody = await projectsResponse.Content.ReadAsStringAsync();

                _logger?.LogDebug("Deepgram projects response: Status={StatusCode}, Body={Body}",
                    projectsResponse.StatusCode, TruncateResponse(projectsBody));

                if (!IsSuccessStatusCode(projectsResponse.StatusCode))
                {
                    return projectsResponse.StatusCode switch
                    {
                        System.Net.HttpStatusCode.Unauthorized or
                        System.Net.HttpStatusCode.Forbidden =>
                            ValidationResult.IsUnauthorized(projectsResponse.StatusCode),
                        (System.Net.HttpStatusCode)429 =>
                            ValidationResult.Success(projectsResponse.StatusCode, "Rate limited (key is valid)"),
                        _ => ValidationResult.HasHttpError(projectsResponse.StatusCode,
                            $"Unexpected status {projectsResponse.StatusCode}. Body: {TruncateResponse(projectsBody)}")
                    };
                }

                var result = ValidationResult.Success(projectsResponse.StatusCode, "Valid Deepgram key");

                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(projectsBody);
                    // Response: { "projects": [{ "project_id": "...", "name": "..." }] }
                    if (doc.RootElement.TryGetProperty("projects", out var projects) &&
                        projects.GetArrayLength() > 0)
                    {
                        var firstProject = projects[0];
                        string? projectId = firstProject.TryGetProperty("project_id", out var pid)
                            ? pid.GetString() : null;

                        // Project name as account identifier
                        if (firstProject.TryGetProperty("name", out var name))
                            result.AccountTier = name.GetString();

                        result.Detail = $"Valid Deepgram key — {projects.GetArrayLength()} project(s)";

                        // Step 2: GET /v1/projects/{project_id}/balances — get USD balance
                        // Confirmed response: { "balances": [{ "amount": 1250.75, "units": "USD" }] }
                        if (!string.IsNullOrEmpty(projectId))
                        {
                            using var balanceRequest = new HttpRequestMessage(HttpMethod.Get,
                                $"https://api.deepgram.com/v1/projects/{projectId}/balances");
                            balanceRequest.Headers.Authorization = new AuthenticationHeaderValue("Token", apiKey);

                            var balanceResponse = await httpClient.SendAsync(balanceRequest);
                            if (balanceResponse.IsSuccessStatusCode)
                            {
                                string balanceBody = await balanceResponse.Content.ReadAsStringAsync();
                                using var balDoc = System.Text.Json.JsonDocument.Parse(balanceBody);

                                if (balDoc.RootElement.TryGetProperty("balances", out var balances) &&
                                    balances.GetArrayLength() > 0)
                                {
                                    var bal = balances[0];
                                    if (bal.TryGetProperty("amount", out var amount))
                                    {
                                        string units = bal.TryGetProperty("units", out var u)
                                            ? u.GetString() ?? "USD" : "USD";
                                        double amountVal = amount.GetDouble();
                                        result.Balance = $"{amountVal:N2} {units}";

                                        if (amountVal <= 0)
                                        {
                                            result.IsQuotaExceeded = true;
                                            result.Detail += " — 0 balance remaining";
                                        }
                                    }
                                }
                            }
                        }
                    }
                    else
                    {
                        // Valid key but no projects yet (new account)
                        result.Detail = "Valid Deepgram key — no projects yet";
                    }
                }
                catch { /* Best effort */ }

                return result;
            }
            catch (Exception ex)
            {
                return ValidationResult.HasNetworkError(ex.Message);
            }
        }

        protected override bool IsValidKeyFormat(string apiKey)
        {
            // Deepgram keys: plain alphanumeric, no fixed prefix, typically ~40 chars
            // GitGuardian: Prefixed=False — do NOT enforce any prefix
            return !string.IsNullOrWhiteSpace(apiKey) &&
                   apiKey.Length >= 32 &&
                   System.Text.RegularExpressions.Regex.IsMatch(apiKey, @"^[A-Za-z0-9]+$");
        }
    }
}

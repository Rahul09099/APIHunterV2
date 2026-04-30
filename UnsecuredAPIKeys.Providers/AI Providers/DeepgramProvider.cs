using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Base;
using UnsecuredAPIKeys.Providers.Common;

namespace UnsecuredAPIKeys.Providers.AI_Providers
{
    /// <summary>
    /// Provider for Deepgram API keys — real-time and batch speech-to-text.
    /// Step 1: GET /v1/projects → get project_id
    /// Step 2: GET /v1/projects/{project_id}/balances → get USD balance
    /// Auth: Authorization: Token {apiKey}
    /// Balance JSON: { "balances": [{ "balance_id": "...", "amount": 1250.75, "units": "USD" }] }
    /// </summary>
    [ApiProvider(false, false)]
    public class DeepgramProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "Deepgram";
        public override ApiTypeEnum ApiType => ApiTypeEnum.Deepgram;

        public override IEnumerable<string> RegexPatterns =>
        [
            @"[a-f0-9]{40}",                         // Deepgram uses 40-char hex API keys
            @"deepgram[_-]?[A-Za-z0-9]{20,}",
            @"DEEPGRAM_API_KEY",
            @"DG_API_KEY"
        ];

        public DeepgramProvider() : base() { }
        public DeepgramProvider(ILogger<DeepgramProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(string apiKey, HttpClient httpClient)
        {
            try
            {
                // Step 1: list projects to confirm key is valid and get project_id
                using var projectsRequest = new HttpRequestMessage(HttpMethod.Get, "https://api.deepgram.com/v1/projects");
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

                        if (firstProject.TryGetProperty("name", out var name))
                            result.AccountTier = name.GetString();

                        result.Detail = $"Valid Deepgram key — {projects.GetArrayLength()} project(s)";

                        // Step 2: fetch balance for first project
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
                                // Response: { "balances": [{ "amount": 1250.75, "units": "USD" }] }
                                if (balDoc.RootElement.TryGetProperty("balances", out var balances) &&
                                    balances.GetArrayLength() > 0)
                                {
                                    var bal = balances[0];
                                    if (bal.TryGetProperty("amount", out var amount))
                                    {
                                        string units = bal.TryGetProperty("units", out var u)
                                            ? u.GetString() ?? "USD" : "USD";
                                        result.Balance = $"{amount.GetDouble():N2} {units}";
                                    }
                                }
                            }
                        }
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
            return !string.IsNullOrWhiteSpace(apiKey) && apiKey.Length >= 32;
        }
    }
}

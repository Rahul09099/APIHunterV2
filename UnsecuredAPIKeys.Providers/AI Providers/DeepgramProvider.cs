using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Base;
using UnsecuredAPIKeys.Providers.Common;

namespace UnsecuredAPIKeys.Providers.AI_Providers
{
    /// <summary>
    /// Provider for Deepgram API keys — real-time and batch speech-to-text.
    /// Auth: Authorization: Token {apiKey}
    /// 2-step verification:
    ///   1. GET /v1/projects (authenticates credential)
    ///   2. GET /v1/projects/{project_id}/balances (retrieves USD balance)
    /// </summary>
    [ApiProvider]
    public class DeepgramProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "Deepgram";
        public override ApiTypeEnum ApiType => ApiTypeEnum.Deepgram;

        public override IEnumerable<string> RegexPatterns =>
        [
            @"DEEPGRAM_API_KEY",
            @"DG_API_KEY",
            @"DEEPGRAM_KEY",
            @"API_KEY_DEEPGRAM",
            @"DEEPGRAM_SECRET",
            @"DG_KEY",

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
                // Step 1: GET /v1/projects — authenticates key
                using var projectsRequest = new HttpRequestMessage(HttpMethod.Get, "https://api.deepgram.com/v1/projects");
                projectsRequest.Headers.Authorization = new AuthenticationHeaderValue("Token", apiKey);

                var projectsResponse = await httpClient.SendAsync(projectsRequest);
                string projectsBody = await projectsResponse.Content.ReadAsStringAsync();

                _logger?.LogDebug("Deepgram projects response: Status={StatusCode}, Body={Body}",
                    projectsResponse.StatusCode, TruncateResponse(projectsBody));

                if (!IsSuccessStatusCode(projectsResponse.StatusCode))
                {
                    ValidationResult unauthOrErrResult = projectsResponse.StatusCode switch
                    {
                        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                            ValidationResult.IsUnauthorized(projectsResponse.StatusCode, "Invalid Deepgram API key"),
                        (HttpStatusCode)429 =>
                            new ValidationResult
                            {
                                Status = ValidationAttemptStatus.ValidationUnavailable,
                                HttpStatusCode = projectsResponse.StatusCode,
                                Detail = "Deepgram rate limit exceeded (HTTP 429)"
                            },
                        _ => ValidationResult.HasHttpError(projectsResponse.StatusCode,
                            $"Unexpected status {projectsResponse.StatusCode}. Body: {TruncateResponse(projectsBody)}")
                    };
                    unauthOrErrResult.RawResponse = projectsBody;
                    return unauthOrErrResult;
                }

                var result = ValidationResult.Success(projectsResponse.StatusCode, "Valid Deepgram key");
                result.Metadata = new Dictionary<string, object>
                {
                    ["authentication_valid"] = true
                };

                try
                {
                    using var doc = JsonDocument.Parse(projectsBody);
                    if (doc.RootElement.TryGetProperty("projects", out var projects) && projects.GetArrayLength() > 0)
                    {
                        var firstProject = projects[0];
                        string? projectId = firstProject.TryGetProperty("project_id", out var pid) ? pid.GetString() : null;

                        if (firstProject.TryGetProperty("name", out var name))
                            result.AccountTier = name.GetString();

                        result.Detail = $"Valid Deepgram key — {projects.GetArrayLength()} project(s)";

                        // Step 2: GET /v1/projects/{project_id}/balances — USD balance
                        if (!string.IsNullOrEmpty(projectId))
                        {
                            using var balanceRequest = new HttpRequestMessage(HttpMethod.Get,
                                $"https://api.deepgram.com/v1/projects/{projectId}/balances");
                            balanceRequest.Headers.Authorization = new AuthenticationHeaderValue("Token", apiKey);

                            var balanceResponse = await httpClient.SendAsync(balanceRequest);
                            if (balanceResponse.IsSuccessStatusCode)
                            {
                                string balanceBody = await balanceResponse.Content.ReadAsStringAsync();
                                using var balDoc = JsonDocument.Parse(balanceBody);

                                if (balDoc.RootElement.TryGetProperty("balances", out var balances) && balances.GetArrayLength() > 0)
                                {
                                    var bal = balances[0];
                                    if (bal.TryGetProperty("amount", out var amount))
                                    {
                                        string units = bal.TryGetProperty("units", out var u) ? u.GetString() ?? "USD" : "USD";
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
                        result.Detail = "Valid Deepgram key — no projects yet";
                    }
                }
                catch { /* Best effort project/balance parsing */ }

                result.RawResponse = projectsBody;
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
                   apiKey.Length >= 32 &&
                   System.Text.RegularExpressions.Regex.IsMatch(apiKey, @"^[A-Za-z0-9]+$");
        }
    }
}

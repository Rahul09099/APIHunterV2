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

namespace UnsecuredAPIKeys.Providers.Communication_Providers
{
    /// <summary>
    /// Provider for GitHub Access Tokens (classic PAT ghp_, fine-grained github_pat_, OAuth gho_, user-to-server ghu_, server-to-server ghs_, refresh ghr_)
    /// </summary>
    [ApiProvider]
    public class GitHubTokenProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "GitHub";
        public override ApiTypeEnum ApiType => ApiTypeEnum.GitHubToken;

        public override IEnumerable<string> RegexPatterns =>
        [
            @"\bghp_[a-zA-Z0-9_-]{20,}\b",
            @"\bgithub_pat_[a-zA-Z0-9_-]{20,}\b",
            @"\bgho_[a-zA-Z0-9_-]{20,}\b",
            @"\bghu_[a-zA-Z0-9_-]{20,}\b",
            @"\bghs_[a-zA-Z0-9_-]{20,}\b",
            @"\bghr_[a-zA-Z0-9_-]{20,}\b",
            @"GITHUB_TOKEN\s*=\s*['""]?([a-zA-Z0-9_-]{20,})['""]?",
            @"GH_TOKEN\s*=\s*['""]?([a-zA-Z0-9_-]{20,})['""]?",
            @"GITHUB_PAT\s*=\s*['""]?([a-zA-Z0-9_-]{20,})['""]?"
        ];

        public GitHubTokenProvider() : base() { }
        public GitHubTokenProvider(ILogger<GitHubTokenProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(string apiKey, HttpClient httpClient)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                request.Headers.UserAgent.ParseAdd("APIHunter-Verification-Agent/2.0");
                request.Headers.Accept.ParseAdd("application/vnd.github+json");
                request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");

                using var response = await httpClient.SendAsync(request);
                string responseBody = await response.Content.ReadAsStringAsync();

                _logger?.LogDebug("GitHub API response status: {StatusCode}", response.StatusCode);

                string tokenType = InferTokenType(apiKey);

                if (IsSuccessStatusCode(response.StatusCode))
                {
                    var result = ValidationResult.Success(response.StatusCode, "Valid GitHub Token");

                    string scopesInfo;
                    if (response.Headers.TryGetValues("X-OAuth-Scopes", out var scopeValues))
                    {
                        var scopeStr = string.Join(", ", scopeValues);
                        scopesInfo = string.IsNullOrWhiteSpace(scopeStr) ? "Classic Scopes: None" : $"Classic Scopes: {scopeStr}";
                    }
                    else
                    {
                        scopesInfo = "Fine-grained Permissions (Not exposed by /user)";
                    }

                    try
                    {
                        using var doc = JsonDocument.Parse(responseBody);
                        var root = doc.RootElement;

                        var login = root.TryGetProperty("login", out var l) ? l.GetString() : "Unknown";
                        var type = root.TryGetProperty("type", out var t) ? t.GetString() : "User";
                        var name = root.TryGetProperty("name", out var n) ? n.GetString() : login;
                        var planName = "Not Specified";

                        if (root.TryGetProperty("plan", out var planObj) && planObj.ValueKind == JsonValueKind.Object)
                        {
                            if (planObj.TryGetProperty("name", out var pName) && !string.IsNullOrWhiteSpace(pName.GetString()))
                            {
                                planName = pName.GetString()!;
                            }
                        }

                        result.AccountTier = $"{tokenType} ({type}: {login}, Plan: {planName}) | {scopesInfo}";

                        var metadata = new Dictionary<string, object>
                        {
                            ["credential_type"] = tokenType,
                            ["login"] = login ?? "Unknown",
                            ["name"] = name ?? "Unknown",
                            ["account_type"] = type ?? "User",
                            ["plan"] = planName,
                            ["scopes_or_permissions"] = scopesInfo
                        };

                        if (root.TryGetProperty("public_repos", out var pr)) metadata["public_repos"] = pr.GetInt32();
                        if (root.TryGetProperty("total_private_repos", out var tpr)) metadata["total_private_repos"] = tpr.GetInt32();
                        if (root.TryGetProperty("site_admin", out var sa)) metadata["site_admin"] = sa.GetBoolean();

                        result.Metadata = metadata;
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "Failed to parse GitHub /user JSON response");
                        result.AccountTier = $"{tokenType} | {scopesInfo}";
                    }

                    result.RawResponse = responseBody;
                    return result;
                }

                int statusCodeVal = (int)response.StatusCode;

                // 408, 429, or 5xx -> Validation Unavailable
                if (statusCodeVal == 429 || statusCodeVal == 408 || statusCodeVal >= 500)
                {
                    return new ValidationResult
                    {
                        Status = ValidationAttemptStatus.ValidationUnavailable,
                        HttpStatusCode = response.StatusCode,
                        Detail = $"GitHub API endpoint temporarily unavailable (HTTP {statusCodeVal})"
                    };
                }

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    return ValidationResult.IsUnauthorized(response.StatusCode, "Invalid or revoked GitHub Token");
                }

                if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    // Rate limit check
                    if (response.Headers.TryGetValues("X-RateLimit-Remaining", out var rem) && string.Equals(System.Linq.Enumerable.FirstOrDefault(rem), "0"))
                    {
                        return new ValidationResult
                        {
                            Status = ValidationAttemptStatus.HttpError,
                            IsQuotaExceeded = true,
                            HttpStatusCode = response.StatusCode,
                            Detail = "GitHub API rate limit exhausted; token validity could not be determined."
                        };
                    }

                    return ValidationResult.HasHttpError(response.StatusCode, "GitHub access forbidden (SSO/IP policy/scope restriction)");
                }

                return ValidationResult.HasHttpError(response.StatusCode, $"GitHub API returned HTTP {statusCodeVal}");
            }
            catch (HttpRequestException ex)
            {
                _logger?.LogWarning(ex, "Network exception connecting to GitHub API");
                return ValidationResult.HasNetworkError($"Network exception: {ex.Message}");
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Unexpected exception during GitHub token validation");
                return ValidationResult.HasProviderSpecificError($"Validation exception: {ex.Message}");
            }
        }

        private static string InferTokenType(string apiKey)
        {
            if (apiKey.StartsWith("ghp_", StringComparison.OrdinalIgnoreCase)) return "Classic PAT";
            if (apiKey.StartsWith("github_pat_", StringComparison.OrdinalIgnoreCase)) return "Fine-Grained PAT";
            if (apiKey.StartsWith("gho_", StringComparison.OrdinalIgnoreCase)) return "OAuth Access Token";
            if (apiKey.StartsWith("ghu_", StringComparison.OrdinalIgnoreCase)) return "User-to-Server Token";
            if (apiKey.StartsWith("ghs_", StringComparison.OrdinalIgnoreCase)) return "Server-to-Server Token";
            if (apiKey.StartsWith("ghr_", StringComparison.OrdinalIgnoreCase)) return "Refresh Token";
            return "GitHub Token";
        }
    }
}

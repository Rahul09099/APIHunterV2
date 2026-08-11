using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Base;
using UnsecuredAPIKeys.Providers.Common;

namespace UnsecuredAPIKeys.Providers.Communication_Providers
{
    /// <summary>
    /// Provider for Google OAuth Access Tokens, Refresh Tokens, and Client Credential Candidates
    /// </summary>
    [ApiProvider]
    public class GoogleOAuthProvider : BaseApiKeyProvider
    {
        private sealed record GoogleOAuthError(string Code, string Description);

        public override string ProviderName => "Google OAuth";
        public override ApiTypeEnum ApiType => ApiTypeEnum.GoogleOAuth;

        public override IEnumerable<string> RegexPatterns =>
        [
            @"ya29\.[0-9A-Za-z_-]{50,}",
            @"1//[0-9A-Za-z_-]{40,}",
            @"GOCSPX-[A-Za-z0-9_-]{20,}",
            @"[0-9]+-[a-z0-9_-]{20,}\.apps\.googleusercontent\.com",
            @"GOOGLE_CLIENT_SECRET\s*=\s*['""]?([A-Za-z0-9_-]+)['""]?",
            @"GOOGLE_REFRESH_TOKEN\s*=\s*['""]?([A-Za-z0-9_-]+)['""]?"
        ];

        public GoogleOAuthProvider() : base() { }
        public GoogleOAuthProvider(ILogger<GoogleOAuthProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(string apiKey, HttpClient httpClient)
        {
            try
            {
                // Branch 1: Access Tokens (ya29...) - Verification via Google TokenInfo API
                if (apiKey.StartsWith("ya29.", StringComparison.OrdinalIgnoreCase))
                {
                    var tokenInfoUrl = $"https://oauth2.googleapis.com/tokeninfo?access_token={Uri.EscapeDataString(apiKey)}";
                    using var response = await httpClient.GetAsync(tokenInfoUrl);
                    string responseBody = await response.Content.ReadAsStringAsync();

                    _logger?.LogDebug("Google OAuth TokenInfo response: Status={StatusCode}", response.StatusCode);

                    if (IsSuccessStatusCode(response.StatusCode))
                    {
                        var result = ValidationResult.Success(response.StatusCode, "Valid Google OAuth Access Token");
                        result.AccountTier = "OAuth Access Token";
                        try
                        {
                            using var doc = JsonDocument.Parse(responseBody);
                            var root = doc.RootElement;

                            string scopeStr = root.TryGetProperty("scope", out var s) ? s.GetString() ?? "" : "";
                            int scopeCount = !string.IsNullOrWhiteSpace(scopeStr) ? scopeStr.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length : 0;

                            string detailMsg = $"Valid Google OAuth access token. Scopes count: {scopeCount}";

                            if (root.TryGetProperty("expires_in", out var expiresProp))
                            {
                                long expiresIn = 0;
                                if (expiresProp.ValueKind == JsonValueKind.Number && expiresProp.TryGetInt64(out expiresIn))
                                {
                                    result.Metadata ??= new Dictionary<string, object>();
                                    result.Metadata["ExpiresInSeconds"] = expiresIn;
                                    detailMsg += $" | Expires in: {expiresIn}s";
                                }
                                else if (expiresProp.ValueKind == JsonValueKind.String && long.TryParse(expiresProp.GetString(), out expiresIn))
                                {
                                    result.Metadata ??= new Dictionary<string, object>();
                                    result.Metadata["ExpiresInSeconds"] = expiresIn;
                                    detailMsg += $" | Expires in: {expiresIn}s";
                                }
                            }

                            result.Detail = detailMsg;
                        }
                        catch
                        {
                            result.Detail = "Valid Google OAuth access token.";
                        }

                        return result;
                    }

                    var googleError = ExtractGoogleError(responseBody);

                    // Confirmed invalid token signals on 400/401
                    if (response.StatusCode == HttpStatusCode.BadRequest || response.StatusCode == HttpStatusCode.Unauthorized)
                    {
                        if (googleError.Code.Contains("invalid_token", StringComparison.OrdinalIgnoreCase) ||
                            googleError.Description.Contains("invalid_token", StringComparison.OrdinalIgnoreCase) ||
                            googleError.Description.Contains("expired", StringComparison.OrdinalIgnoreCase))
                        {
                            return ValidationResult.IsUnauthorized(response.StatusCode, "Google OAuth access token was rejected as invalid or expired.");
                        }
                    }

                    // Handle transient rate limits, timeouts, or Google 5xx server issues
                    if (response.StatusCode == HttpStatusCode.RequestTimeout ||
                        response.StatusCode == HttpStatusCode.TooManyRequests ||
                        (int)response.StatusCode >= 500)
                    {
                        return new ValidationResult
                        {
                            Status = ValidationAttemptStatus.ValidationUnavailable,
                            HttpStatusCode = response.StatusCode,
                            Detail = "Google TokenInfo validation endpoint is temporarily unavailable."
                        };
                    }

                    return ValidationResult.HasHttpError(response.StatusCode, "Google OAuth token validation could not be completed.");
                }

                // Branch 2: Refresh Tokens (1//...) - Candidate Detection
                if (apiKey.StartsWith("1//", StringComparison.OrdinalIgnoreCase))
                {
                    return new ValidationResult
                    {
                        Status = ValidationAttemptStatus.Candidate,
                        AccountTier = "OAuth Refresh Token Candidate",
                        Detail = "Google OAuth refresh token candidate detected; live exchange requires associated Client ID and Client Secret."
                    };
                }

                // Branch 3: Static Client Secret Candidates (GOCSPX-...)
                if (apiKey.StartsWith("GOCSPX-", StringComparison.OrdinalIgnoreCase))
                {
                    return new ValidationResult
                    {
                        Status = ValidationAttemptStatus.Candidate,
                        AccountTier = "OAuth Client Secret Candidate",
                        Detail = "Google OAuth Client Secret candidate detected; requires associated Client ID and OAuth flow for live validation."
                    };
                }

                // Branch 4: Client ID Candidates (*.apps.googleusercontent.com)
                if (apiKey.Contains(".apps.googleusercontent.com", StringComparison.OrdinalIgnoreCase))
                {
                    return new ValidationResult
                    {
                        Status = ValidationAttemptStatus.Candidate,
                        AccountTier = "OAuth Client ID Candidate",
                        Detail = "Google OAuth Client ID candidate detected; application identifier, not an authentication credential by itself."
                    };
                }

                return new ValidationResult
                {
                    Status = ValidationAttemptStatus.Candidate,
                    AccountTier = "OAuth Credential Candidate",
                    Detail = "Google OAuth credential candidate detected."
                };
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to process Google OAuth credential");
                return ValidationResult.HasProviderSpecificError("Google OAuth credential processing failed.");
            }
        }

        private static GoogleOAuthError ExtractGoogleError(string responseBody)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(responseBody))
                    return new GoogleOAuthError(string.Empty, string.Empty);

                using var doc = JsonDocument.Parse(responseBody);
                var root = doc.RootElement;

                string code = string.Empty;
                string description = string.Empty;

                if (root.TryGetProperty("error", out var errProp))
                {
                    if (errProp.ValueKind == JsonValueKind.String)
                    {
                        code = errProp.GetString() ?? string.Empty;
                    }
                    else if (errProp.ValueKind == JsonValueKind.Object)
                    {
                        if (errProp.TryGetProperty("code", out var cProp))
                        {
                            code = cProp.ValueKind switch
                            {
                                JsonValueKind.String => cProp.GetString() ?? string.Empty,
                                JsonValueKind.Number => cProp.ToString(),
                                _ => string.Empty
                            };
                        }

                        if (errProp.TryGetProperty("message", out var mProp))
                            description = mProp.GetString() ?? string.Empty;
                    }
                }

                if (root.TryGetProperty("error_description", out var descProp))
                {
                    description = descProp.GetString() ?? description;
                }

                return new GoogleOAuthError(code, description);
            }
            catch (JsonException)
            {
                return new GoogleOAuthError(string.Empty, string.Empty);
            }
        }
    }
}

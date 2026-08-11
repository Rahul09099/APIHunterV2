using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Base;
using UnsecuredAPIKeys.Providers.Common;

namespace UnsecuredAPIKeys.Providers.Maps_Providers
{
    /// <summary>
    /// Provider for Mapbox access tokens (pk. public, sk. secret, tk. temporary).
    /// Auth: access_token query parameter. Mapbox APIs also support Bearer token authentication.
    ///
    /// Verification strategy:
    ///   1. Local JWT parsing: Extracts non-secret token metadata (token_type, scopes, allowedURLs, usage, exp)
    ///   2. Live verification: GET https://api.mapbox.com/styles/v1/mapbox/streets-v12?access_token={apiKey}
    /// Official docs: https://docs.mapbox.com/api/accounts/tokens/
    /// </summary>
    [ApiProvider]
    public class MapboxProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "Mapbox";
        public override ApiTypeEnum ApiType => ApiTypeEnum.Mapbox;

        private static readonly Regex TokenRegex = new(
            @"^(?:pk|sk|tk)\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public override IEnumerable<string> RegexPatterns =>
        [
            @"\b(?:pk|sk|tk)\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\b",
            @"MAPBOX_TOKEN",
            @"MAPBOX_ACCESS_TOKEN",
            @"MAPBOX_API_KEY",
            @"(?i)\bMAPBOX[\s_-]*API[\s_-]*KEY\s*[:=]\s*['""]?((?:pk|sk|tk)\.[A-Za-z0-9_.-]{20,256})['""]?"
        ];

        public MapboxProvider() : base() { }
        public MapboxProvider(ILogger<MapboxProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(string apiKey, HttpClient httpClient)
        {
            try
            {
                string tokenType = apiKey.StartsWith("pk.", StringComparison.Ordinal) ? "public"
                    : apiKey.StartsWith("sk.", StringComparison.Ordinal) ? "secret"
                    : apiKey.StartsWith("tk.", StringComparison.Ordinal) ? "temporary"
                    : "unknown";

                var metadata = new Dictionary<string, object>
                {
                    ["token_type"] = tokenType
                };

                // Decode non-secret JWT payload metadata locally if possible
                try
                {
                    var parts = apiKey.Split('.');
                    if (parts.Length >= 2)
                    {
                        string payload = parts[1].Replace('-', '+').Replace('_', '/');
                        switch (payload.Length % 4)
                        {
                            case 2: payload += "=="; break;
                            case 3: payload += "="; break;
                        }
                        byte[] decodedBytes = Convert.FromBase64String(payload);
                        string decodedJson = System.Text.Encoding.UTF8.GetString(decodedBytes);

                        using var jwtDoc = JsonDocument.Parse(decodedJson);
                        var jwtRoot = jwtDoc.RootElement;

                        if (jwtRoot.TryGetProperty("id", out var idProp)) metadata["token_id"] = idProp.GetString() ?? "";
                        if (jwtRoot.TryGetProperty("client", out var clientProp)) metadata["client"] = clientProp.GetString() ?? "";
                        if (jwtRoot.TryGetProperty("usage", out var usageProp)) metadata["usage"] = usageProp.GetString() ?? "";

                        if (jwtRoot.TryGetProperty("exp", out var expProp) && expProp.ValueKind == JsonValueKind.Number && expProp.TryGetInt64(out var expSec))
                        {
                            metadata["expires_at"] = DateTimeOffset.FromUnixTimeSeconds(expSec).ToString("o");
                        }

                        if (jwtRoot.TryGetProperty("scopes", out var scopesArr) && scopesArr.ValueKind == JsonValueKind.Array)
                        {
                            var scopesList = scopesArr.EnumerateArray()
                                .Select(s => s.GetString() ?? "")
                                .Where(s => !string.IsNullOrEmpty(s))
                                .ToList();
                            metadata["scopes"] = scopesList;
                        }

                        if (jwtRoot.TryGetProperty("allowedURLs", out var urlsArr) && urlsArr.ValueKind == JsonValueKind.Array)
                        {
                            var urlsList = urlsArr.EnumerateArray()
                                .Select(u => u.GetString() ?? "")
                                .Where(u => !string.IsNullOrEmpty(u))
                                .ToList();
                            metadata["allowed_urls"] = urlsList;
                            metadata["url_restricted"] = urlsList.Count > 0;
                        }
                    }
                }
                catch { /* Best effort local JWT parsing */ }

                // Live verification call using Mapbox Styles API (streets-v12)
                var endpoint = $"https://api.mapbox.com/styles/v1/mapbox/streets-v12?access_token={Uri.EscapeDataString(apiKey)}";
                var response = await httpClient.GetAsync(endpoint);
                string responseBody = await response.Content.ReadAsStringAsync();

                _logger?.LogDebug("Mapbox API response ({TokenType}): Status={StatusCode}", tokenType, response.StatusCode);

                if (IsSuccessStatusCode(response.StatusCode))
                {
                    var result = ValidationResult.Success(response.StatusCode, $"Valid Mapbox {tokenType} access token");
                    metadata["authentication_valid"] = true;
                    result.Metadata = metadata;
                    result.AccountTier = $"{tokenType} token";
                    result.Detail = $"Valid Mapbox {tokenType} access token — live API request verified.";
                    return result;
                }

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    return ValidationResult.IsUnauthorized(response.StatusCode,
                        "Invalid or expired Mapbox access token");
                }

                if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    bool isUrlRestricted = metadata.TryGetValue("url_restricted", out var restrictedObj) && restrictedObj is bool restricted && restricted;
                    var restrictedResult = ValidationResult.ValidationUnavailable(response.StatusCode,
                        isUrlRestricted
                            ? "Mapbox token returned 403 — URL restriction may prevent this request."
                            : "Mapbox token received 403 — access may be restricted by scope, URL restriction, or resource permissions.");

                    metadata["access_restricted"] = true;
                    restrictedResult.Metadata = metadata;
                    return restrictedResult;
                }

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    return ValidationResult.ValidationUnavailable(response.StatusCode,
                        "Mapbox API rate limited (429) — validation unavailable");
                }

                if ((int)response.StatusCode >= 500)
                {
                    return ValidationResult.ValidationUnavailable(response.StatusCode,
                        $"Mapbox service error ({response.StatusCode}) — validation unavailable");
                }

                return ValidationResult.HasHttpError(response.StatusCode,
                    $"Mapbox API request failed with status {response.StatusCode}. Response: {TruncateResponse(responseBody)}");
            }
            catch (Exception ex)
            {
                return ValidationResult.HasNetworkError(ex.Message);
            }
        }

        protected override bool IsValidKeyFormat(string apiKey)
        {
            return !string.IsNullOrWhiteSpace(apiKey) &&
                   TokenRegex.IsMatch(apiKey.Trim());
        }
    }
}

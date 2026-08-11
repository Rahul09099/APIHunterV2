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
    /// Provider for Facebook Graph API access tokens
    /// </summary>
    [ApiProvider]
    public class FacebookProvider : BaseApiKeyProvider
    {
        private static string GraphApiVersion => Environment.GetEnvironmentVariable("FACEBOOK_GRAPH_VERSION") ?? "v20.0";

        public override string ProviderName => "Facebook";
        public override ApiTypeEnum ApiType => ApiTypeEnum.Facebook;

        public override IEnumerable<string> RegexPatterns =>
        [
            @"EAA[A-Za-z0-9]{80,}",
            @"FACEBOOK_ACCESS_TOKEN\s*=\s*['""]?([A-Za-z0-9_-]+)['""]?",
            @"FB_ACCESS_TOKEN\s*=\s*['""]?([A-Za-z0-9_-]+)['""]?"
        ];

        public FacebookProvider() : base() { }
        public FacebookProvider(ILogger<FacebookProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(string apiKey, HttpClient httpClient)
        {
            try
            {
                var requestUrl = $"https://graph.facebook.com/{GraphApiVersion}/me?fields=id,name";
                using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

                using var response = await httpClient.SendAsync(request);
                string responseBody = await response.Content.ReadAsStringAsync();

                _logger?.LogDebug("Facebook API response: Status={StatusCode}", response.StatusCode);

                if (IsSuccessStatusCode(response.StatusCode))
                {
                    var result = ValidationResult.Success(response.StatusCode, "Valid Facebook Access Token");
                    try
                    {
                        using var doc = JsonDocument.Parse(responseBody);
                        var root = doc.RootElement;
                        string name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "User" : "User";
                        string id = root.TryGetProperty("id", out var i) ? i.GetString() ?? "Unknown" : "Unknown";

                        result.AccountTier = "Standard";
                        result.Detail = $"User: {name} (ID: {id})";
                    }
                    catch
                    {
                        result.AccountTier = "Standard";
                        result.Detail = "Valid Facebook token";
                    }

                    return result;
                }

                var (errorCode, errorMessage) = ExtractGraphError(responseBody);

                // Code 190 = OAuthException (Invalid, expired, or revoked access token)
                if (errorCode == 190 || response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    return ValidationResult.IsUnauthorized(response.StatusCode, "Invalid, expired, or revoked Facebook access token.");
                }

                string formattedError = errorCode > 0
                    ? $"Facebook Graph API error code ({errorCode})."
                    : $"Facebook API HTTP error {(int)response.StatusCode}.";

                return ValidationResult.HasHttpError(response.StatusCode, formattedError);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to validate Facebook token");
                return ValidationResult.HasProviderSpecificError("Facebook token validation failed.");
            }
        }

        private static (int Code, string Message) ExtractGraphError(string responseBody)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(responseBody))
                    return (0, string.Empty);

                using var doc = JsonDocument.Parse(responseBody);
                if (!doc.RootElement.TryGetProperty("error", out var error))
                    return (0, string.Empty);

                int code = error.TryGetProperty("code", out var codeProp) && codeProp.ValueKind == JsonValueKind.Number
                    ? codeProp.GetInt32()
                    : 0;

                string message = error.TryGetProperty("message", out var messageProp)
                    ? messageProp.GetString() ?? string.Empty
                    : string.Empty;

                return (code, message);
            }
            catch (JsonException)
            {
                return (0, string.Empty);
            }
        }
    }
}

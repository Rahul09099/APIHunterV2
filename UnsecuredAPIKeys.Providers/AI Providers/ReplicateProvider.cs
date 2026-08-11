using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Base;
using UnsecuredAPIKeys.Providers.Common;

namespace UnsecuredAPIKeys.Providers.AI_Providers
{
    /// <summary>
    /// Provider for Replicate API tokens.
    /// Replicate API tokens always start with "r8_".
    /// Verification: GET https://api.replicate.com/v1/account — returns user/organization account details.
    /// Official docs: https://replicate.com/docs/reference/http#account.get
    /// </summary>
    [ApiProvider]
    public class ReplicateProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "Replicate";
        public override ApiTypeEnum ApiType => ApiTypeEnum.Replicate;

        public override IEnumerable<string> RegexPatterns =>
        [
            @"\br8_[A-Za-z0-9]{32,}\b",
            @"REPLICATE_API_TOKEN",
            @"REPLICATE_API_KEY",
            @"replicate[_-]?token"
        ];

        public ReplicateProvider() : base() { }
        public ReplicateProvider(ILogger<ReplicateProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(string apiKey, HttpClient httpClient)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.replicate.com/v1/account");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                request.Headers.UserAgent.ParseAdd("UnsecuredAPIKeys-Lite/1.0");

                var response = await httpClient.SendAsync(request);
                string responseBody = await response.Content.ReadAsStringAsync();

                _logger?.LogDebug("Replicate account response: Status={Status}, Body={Body}",
                    response.StatusCode, TruncateResponse(responseBody));

                if (IsSuccessStatusCode(response.StatusCode))
                {
                    var result = ValidationResult.Success(response.StatusCode, "Valid Replicate token");
                    result.RawResponse = responseBody;
                    result.Metadata = new Dictionary<string, object>
                    {
                        ["authentication_valid"] = true
                    };

                    try
                    {
                        using var doc = JsonDocument.Parse(responseBody);
                        var root = doc.RootElement;

                        if (root.TryGetProperty("type", out var tProp) && tProp.ValueKind == JsonValueKind.String)
                        {
                            string type = tProp.GetString() ?? "";
                            result.Metadata["account_type"] = type;
                            if (!string.IsNullOrEmpty(type))
                                result.Detail = $"Valid Replicate token ({type} account)";
                        }

                        if (root.TryGetProperty("username", out var uProp) && uProp.ValueKind == JsonValueKind.String)
                        {
                            string username = uProp.GetString() ?? "";
                            result.Metadata["username"] = username;
                            if (!string.IsNullOrEmpty(username))
                                result.AccountTier = username;
                        }

                        if (root.TryGetProperty("name", out var nProp) && nProp.ValueKind == JsonValueKind.String)
                        {
                            result.Metadata["name"] = nProp.GetString() ?? "";
                        }

                        if (root.TryGetProperty("github_url", out var ghProp) && ghProp.ValueKind == JsonValueKind.String)
                        {
                            result.Metadata["github_url"] = ghProp.GetString() ?? "";
                        }
                    }
                    catch
                    {
                        // Best-effort account metadata extraction
                    }

                    return result;
                }

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    return ValidationResult.IsUnauthorized(response.StatusCode,
                        "Invalid or expired Replicate API token");
                }

                if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    return ValidationResult.ValidationUnavailable(response.StatusCode,
                        "Replicate API token forbidden (403) — permission restriction");
                }

                if (response.StatusCode == HttpStatusCode.PaymentRequired)
                {
                    var result = ValidationResult.Success(response.StatusCode,
                        "Valid Replicate token — 402 Payment Required (no active payment method or balance)");
                    result.IsQuotaExceeded = true;
                    result.RawResponse = responseBody;
                    result.Metadata = new Dictionary<string, object>
                    {
                        ["authentication_valid"] = true,
                        ["quota_exceeded"] = true
                    };
                    return result;
                }

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    return ValidationResult.ValidationUnavailable(response.StatusCode,
                        "Replicate API rate limited (429)");
                }

                if ((int)response.StatusCode >= 500)
                {
                    return ValidationResult.ValidationUnavailable(response.StatusCode,
                        $"Replicate service error ({response.StatusCode}) — validation unavailable");
                }

                return ValidationResult.HasHttpError(response.StatusCode,
                    $"Unexpected status {response.StatusCode}. Body: {TruncateResponse(responseBody)}");
            }
            catch (Exception ex)
            {
                return ValidationResult.HasNetworkError(ex.Message);
            }
        }

        protected override bool IsValidKeyFormat(string apiKey)
        {
            return !string.IsNullOrWhiteSpace(apiKey) &&
                   apiKey.StartsWith("r8_", StringComparison.Ordinal) &&
                   apiKey.Length >= 35;
        }
    }
}

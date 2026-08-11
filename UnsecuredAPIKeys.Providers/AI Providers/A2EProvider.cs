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
    /// Provider for A2E AI API keys.
    /// Auth: Authorization: Bearer {apiKey}
    /// Endpoint: GET https://video.a2e.ai/api/v1/user/remainingCoins
    /// Response format: { "code": 0, "data": { "coins": 1293, "diamonds": 3 } }
    /// Official docs: https://api.a2e.ai/get-user-remaining-credits-12138806e0
    /// </summary>
    [ApiProvider]
    public class A2EProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "A2E AI";
        public override ApiTypeEnum ApiType => ApiTypeEnum.A2E;

        public override IEnumerable<string> RegexPatterns =>
        [
            @"\bsk_[A-Za-z0-9_-]{20,256}\b",
            @"A2E_API_KEY",
            @"A2E_TOKEN",
            @"A2E_SECRET",
            @"(?i)\bA2E[\s_-]*API[\s_-]*KEY\s*[:=]\s*['""]?([A-Za-z0-9_-]{20,256})['""]?"
        ];

        public A2EProvider() : base() { }
        public A2EProvider(ILogger<A2EProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(string apiKey, HttpClient httpClient)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://video.a2e.ai/api/v1/user/remainingCoins");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

                var response = await httpClient.SendAsync(request);
                string responseBody = await response.Content.ReadAsStringAsync();

                _logger?.LogDebug("A2E AI API response: Status={StatusCode}, Body={Body}",
                    response.StatusCode, TruncateResponse(responseBody));

                if (IsSuccessStatusCode(response.StatusCode))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(responseBody);
                        var root = doc.RootElement;

                        // Check internal application response code
                        if (root.TryGetProperty("code", out var codeProp) &&
                            codeProp.ValueKind == JsonValueKind.Number &&
                            codeProp.GetInt32() != 0)
                        {
                            string message = root.TryGetProperty("msg", out var msgProp) && msgProp.ValueKind == JsonValueKind.String
                                ? msgProp.GetString() ?? "A2E API returned an error"
                                : "A2E API returned an application error";

                            return ValidationResult.HasHttpError(response.StatusCode,
                                $"A2E API error (code {codeProp.GetInt32()}): {message}");
                        }

                        var result = ValidationResult.Success(response.StatusCode, "Valid A2E AI key");
                        result.RawResponse = responseBody;
                        result.Metadata = new Dictionary<string, object>
                        {
                            ["authentication_valid"] = true
                        };

                        if (root.TryGetProperty("data", out var data))
                        {
                            int coinCount = 0;
                            int diamondCount = 0;

                            if (data.ValueKind == JsonValueKind.Object)
                            {
                                if (data.TryGetProperty("coins", out var coins) && coins.ValueKind == JsonValueKind.Number)
                                {
                                    coinCount = coins.GetInt32();
                                }
                                if (data.TryGetProperty("diamonds", out var diamonds) && diamonds.ValueKind == JsonValueKind.Number)
                                {
                                    diamondCount = diamonds.GetInt32();
                                }
                            }
                            else if (data.ValueKind == JsonValueKind.Number)
                            {
                                coinCount = data.GetInt32();
                            }

                            result.Metadata["coins"] = coinCount;
                            result.Metadata["diamonds"] = diamondCount;
                            result.Balance = $"{coinCount} Coins";

                            if (coinCount <= 0)
                            {
                                result.IsQuotaExceeded = true;
                                result.Detail = "Valid A2E AI key — 0 coins remaining";
                            }
                            else
                            {
                                result.Detail = $"Valid A2E AI key — {coinCount} coins, {diamondCount} diamonds available";
                            }
                        }

                        return result;
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "A2E returned a 200 HTTP status but an unexpected response format");
                        return ValidationResult.HasHttpError(response.StatusCode, "A2E returned an unexpected response format");
                    }
                }

                if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                {
                    return ValidationResult.IsUnauthorized(response.StatusCode, "Invalid or expired A2E AI key");
                }

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    return ValidationResult.ValidationUnavailable(response.StatusCode, "A2E AI API rate limited (429) — validation unavailable");
                }

                if ((int)response.StatusCode >= 500)
                {
                    return ValidationResult.ValidationUnavailable(response.StatusCode, $"A2E AI service error ({response.StatusCode}) — validation unavailable");
                }

                return ValidationResult.HasHttpError(response.StatusCode,
                    $"A2E AI request failed: Status {response.StatusCode}. Body: {TruncateResponse(responseBody)}");
            }
            catch (Exception ex)
            {
                return ValidationResult.HasNetworkError(ex.Message);
            }
        }

        protected override bool IsValidKeyFormat(string apiKey) =>
            !string.IsNullOrWhiteSpace(apiKey) &&
            apiKey.Length >= 20;
    }
}

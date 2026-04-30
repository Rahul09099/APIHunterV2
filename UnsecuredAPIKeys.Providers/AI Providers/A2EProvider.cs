using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Base;
using UnsecuredAPIKeys.Providers.Common;

namespace UnsecuredAPIKeys.Providers.AI_Providers
{
    /// <summary>
    /// Provider for A2E AI API keys
    /// </summary>
    [ApiProvider]
    public class A2EProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "A2E AI";
        public override ApiTypeEnum ApiType => ApiTypeEnum.A2E;

        public override IEnumerable<string> RegexPatterns =>
        [
            @"\bsk_[A-Za-z0-9]{32,}\b"
        ];

        public A2EProvider() : base() { }

        public A2EProvider(ILogger<A2EProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(string apiKey, HttpClient httpClient)
        {
            httpClient.Timeout = TimeSpan.FromSeconds(15);

            using var request = new HttpRequestMessage(HttpMethod.Get, "https://video.a2e.ai/api/v1/user/remainingCoins");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            try
            {
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

                        // A2E returns 200 OK even for invalid tokens, check internal code
                        if (root.TryGetProperty("code", out var code) && code.GetInt32() != 200)
                        {
                            return ValidationResult.IsUnauthorized(HttpStatusCode.Unauthorized, 
                                root.TryGetProperty("msg", out var msg) ? msg.GetString() : "Invalid token (internal code)");
                        }

                        var result = ValidationResult.Success(response.StatusCode, "Valid A2E AI key");

                        if (root.TryGetProperty("data", out var data) &&
                            data.TryGetProperty("coins", out var coins))
                        {
                            var coinCount = coins.GetInt32();
                            result.Balance = $"{coinCount} Coins";
                            
                            if (coinCount <= 0)
                            {
                                result.IsQuotaExceeded = true;
                                result.Detail = "Valid key but 0 coins remaining.";
                            }
                        }

                        return result;
                    }
                    catch 
                    { 
                        return ValidationResult.Success(response.StatusCode, "Valid A2E AI key (parsing failed)"); 
                    }
                }
                else if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                {
                    return ValidationResult.IsUnauthorized(response.StatusCode);
                }
                else if ((int)response.StatusCode == 429)
                {
                    return ValidationResult.Success(response.StatusCode, "Rate limited (key is valid)");
                }
                else
                {
                    // Check for quota/billing issues in body
                    var bodyLower = responseBody.ToLowerInvariant();
                    if (bodyLower.Contains("quota") || bodyLower.Contains("balance") || 
                        bodyLower.Contains("insufficient") || bodyLower.Contains("limit"))
                    {
                        return ValidationResult.Success(response.StatusCode, $"Valid key but access issue: {TruncateResponse(responseBody)}");
                    }

                    return ValidationResult.HasHttpError(response.StatusCode,
                        $"API request failed with status {response.StatusCode}. Response: {TruncateResponse(responseBody)}");
                }
            }
            catch (Exception ex)
            {
                return ValidationResult.HasHttpError(HttpStatusCode.ServiceUnavailable, $"Connection failed: {ex.Message}");
            }
        }

        protected override bool IsValidKeyFormat(string apiKey)
        {
            return !string.IsNullOrWhiteSpace(apiKey) && apiKey.StartsWith("sk_") && apiKey.Length >= 35;
        }
    }
}

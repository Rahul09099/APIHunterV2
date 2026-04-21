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
    /// Provider for PiAPI.ai API keys
    /// </summary>
    [ApiProvider]
    public class PiAPIProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "PiAPI";
        public override ApiTypeEnum ApiType => ApiTypeEnum.PiAPI;

        public override IEnumerable<string> RegexPatterns =>
        [
            // Variable assignments in code
            @"(?i)PIAPI[_-]?KEY.*?[""']([a-zA-Z0-9]{20,})[""']",
            @"(?i)X[_-]API[_-]KEY.*?[""']([a-zA-Z0-9]{20,})[""']",
            @"(?i)piapi[_-]?secret.*?[""']([a-zA-Z0-9]{20,})[""']",
            // Raw keys (risky but PiAPI keys are often standalone alphanumeric strings)
            @"\b[a-zA-Z0-9]{32,}\b"
        ];

        public PiAPIProvider() : base() { }

        public PiAPIProvider(ILogger<PiAPIProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(string apiKey, HttpClient httpClient)
        {
            httpClient.Timeout = TimeSpan.FromSeconds(15);

            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.piapi.ai/account/info");
            request.Headers.Add("x-api-key", apiKey);

            try
            {
                var response = await httpClient.SendAsync(request);
                string responseBody = await response.Content.ReadAsStringAsync();

                _logger?.LogDebug("PiAPI API response: Status={StatusCode}, Body={Body}",
                    response.StatusCode, TruncateResponse(responseBody));

                if (IsSuccessStatusCode(response.StatusCode))
                {
                    var result = ValidationResult.Success(response.StatusCode, "Valid PiAPI key");

                    try
                    {
                        using var doc = JsonDocument.Parse(responseBody);
                        var root = doc.RootElement;

                        // Official docs say equivalent_in_usd or remaining_credits
                        if (root.TryGetProperty("equivalent_in_usd", out var balance))
                        {
                            result.Balance = $"${balance} USD";
                        }
                        else if (root.TryGetProperty("remaining_credits", out var credits))
                        {
                            result.Balance = $"{credits} Credits";
                        }
                        
                        if (root.TryGetProperty("account_name", out var name))
                        {
                            result.AccountTier = name.GetString();
                        }
                    }
                    catch { /* Best effort parsing */ }

                    return result;
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
            return !string.IsNullOrWhiteSpace(apiKey) && apiKey.Length >= 20;
        }
    }
}

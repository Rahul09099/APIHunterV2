using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Http.Headers;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Base;
using UnsecuredAPIKeys.Providers.Common;

namespace UnsecuredAPIKeys.Providers.AI_Providers
{
    /// <summary>
    /// Provider for DeepSeek API keys with real API call verification
    /// </summary>
    [ApiProvider]
    public class DeepSeekProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "DeepSeek";
        public override ApiTypeEnum ApiType => ApiTypeEnum.DeepSeek;

        public override IEnumerable<string> RegexPatterns =>
        [
            @"sk-[A-Za-z0-9]{32,}",  // DeepSeek uses sk- prefix
            @"deepseek[_-]?[A-Za-z0-9]{32,}",
            @"DEEPSEEK_API_KEY"
        ];

        public DeepSeekProvider() : base() { }
        public DeepSeekProvider(ILogger<DeepSeekProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(string apiKey, HttpClient httpClient)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.deepseek.com/user/balance");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            try 
            {
                var response = await httpClient.SendAsync(request);
                string responseBody = await response.Content.ReadAsStringAsync();

                _logger?.LogDebug("DeepSeek balance API response: Status={StatusCode}, Body={Body}",
                    response.StatusCode, TruncateResponse(responseBody));

                if (IsSuccessStatusCode(response.StatusCode))
                {
                    var result = ValidationResult.Success(response.StatusCode, "Valid DeepSeek key");
                    
                    try 
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(responseBody);
                        if (doc.RootElement.TryGetProperty("data", out var data) && 
                            data.TryGetProperty("balance_infos", out var balanceInfos) && 
                            balanceInfos.GetArrayLength() > 0)
                        {
                            var firstBalance = balanceInfos[0];
                            if (firstBalance.TryGetProperty("total_balance", out var total))
                            {
                                string currency = firstBalance.TryGetProperty("currency", out var curr) ? curr.GetString() ?? "USD" : "USD";
                                result.Balance = $"{total} {currency}";
                            }
                        }
                    }
                    catch { /* Best effort */ }

                    return result;
                }
                else if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    return ValidationResult.IsUnauthorized(response.StatusCode);
                }
                else if ((int)response.StatusCode == 429)
                {
                    return ValidationResult.Success(response.StatusCode, "Rate limited (key is valid)");
                }
                else
                {
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
            return !string.IsNullOrWhiteSpace(apiKey) && apiKey.Length >= 32;
        }
    }
}

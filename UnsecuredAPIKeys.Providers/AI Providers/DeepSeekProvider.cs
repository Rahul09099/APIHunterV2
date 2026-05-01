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
            // Fetch balance
            using var balanceRequest = new HttpRequestMessage(HttpMethod.Get, "https://api.deepseek.com/user/balance");
            balanceRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            try 
            {
                var response = await httpClient.SendAsync(balanceRequest);
                string responseBody = await response.Content.ReadAsStringAsync();

                _logger?.LogDebug("DeepSeek balance API response: Status={StatusCode}, Body={Body}",
                    response.StatusCode, TruncateResponse(responseBody));

                if (IsSuccessStatusCode(response.StatusCode))
                {
                    var result = ValidationResult.Success(response.StatusCode, "Valid DeepSeek key");
                    
                    try 
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(responseBody);
                        var root = doc.RootElement;
                        
                        // Handle balance info (can be at root or wrapped in 'data')
                        JsonElement balanceInfos;
                        bool hasBalance = false;
                        
                        if (root.TryGetProperty("balance_infos", out balanceInfos))
                        {
                            hasBalance = true;
                        }
                        else if (root.TryGetProperty("data", out var data) && data.TryGetProperty("balance_infos", out balanceInfos))
                        {
                            hasBalance = true;
                        }

                        if (hasBalance && balanceInfos.ValueKind == System.Text.Json.JsonValueKind.Array && balanceInfos.GetArrayLength() > 0)
                        {
                            var firstBalance = balanceInfos[0];
                            string total = firstBalance.TryGetProperty("total_balance", out var t) ? (t.ValueKind == JsonValueKind.String ? t.GetString() : t.GetRawText()) ?? "0" : "0";
                            string granted = firstBalance.TryGetProperty("granted_balance", out var g) ? (g.ValueKind == JsonValueKind.String ? g.GetString() : g.GetRawText()) ?? "0" : "0";
                            string toppedUp = firstBalance.TryGetProperty("topped_up_balance", out var tu) ? (tu.ValueKind == JsonValueKind.String ? tu.GetString() : tu.GetRawText()) ?? "0" : "0";
                            string currency = firstBalance.TryGetProperty("currency", out var curr) && curr.ValueKind == JsonValueKind.String ? curr.GetString() ?? "USD" : "USD";
                            
                            result.Balance = $"{total} {currency} (Grant: {granted}, Paid: {toppedUp})";
                            
                            // Determine tier
                            if (decimal.TryParse(toppedUp, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out decimal paid) && paid > 0)
                            {
                                result.AccountTier = "Paid Account";
                            }
                            else
                            {
                                result.AccountTier = "Free/Grant Account";
                            }
                        }

                        // Check availability
                        if (root.TryGetProperty("is_available", out var isAvailable))
                        {
                            bool available = isAvailable.GetBoolean();
                            
                            if (!available)
                            {
                                result.Detail = "Key is valid but not currently available (insufficient balance/quota)";
                                result.IsQuotaExceeded = true;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "Error parsing DeepSeek balance response");
                    }

                    // Truncate RawResponse to save space as requested by user
                    result.RawResponse = TruncateResponse(responseBody, 200);
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
                    var res = ValidationResult.HasHttpError(response.StatusCode, 
                        $"API request failed with status {response.StatusCode}. Response: {TruncateResponse(responseBody)}");
                    res.RawResponse = TruncateResponse(responseBody, 200);
                    return res;
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

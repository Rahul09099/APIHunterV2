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
                        
                        // PiAPI often wraps the actual data in a "data" property
                        JsonElement data;
                        if (!root.TryGetProperty("data", out data))
                        {
                            data = root;
                        }

                        // Extract Account Tier and Info
                        if (data.TryGetProperty("plan", out var plan))
                        {
                            result.AccountTier = plan.ValueKind == JsonValueKind.String ? plan.GetString() : plan.GetRawText();
                        }

                        if (data.TryGetProperty("name", out var name))
                        {
                            result.Detail = $"Account: {(name.ValueKind == JsonValueKind.String ? name.GetString() : name.GetRawText())}";
                        }

                        // Extract Balance/Credits
                        if (data.TryGetProperty("equivalent_in_usd", out var usdBalance))
                        {
                            result.Balance = $"${(usdBalance.ValueKind == JsonValueKind.String ? usdBalance.GetString() : usdBalance.GetRawText())} USD";
                        }
                        else if (data.TryGetProperty("remaining_credits", out var credits))
                        {
                            result.Balance = $"{(credits.ValueKind == JsonValueKind.String ? credits.GetString() : credits.GetRawText())} Credits";
                        }

                        // Extract Detailed Wallet Info
                        if (data.TryGetProperty("wallet", out var wallet) && wallet.ValueKind == JsonValueKind.Object)
                        {
                            var details = new List<string>();
                            var walletData = new Dictionary<string, object>();
                            
                            string[] walletFields = ["mj_remain", "llm_remain", "suno_remain", "luma_remain", "gpts_remain", "point_remain"];
                            foreach (var field in walletFields)
                            {
                                if (wallet.TryGetProperty(field, out var val))
                                {
                                    string label = field.Split('_')[0].ToUpper();
                                    string valStr = val.ValueKind == JsonValueKind.String ? val.GetString()! : val.GetRawText();
                                    details.Add($"{label}: {valStr}");
                                    walletData[field] = valStr;
                                }
                            }

                            result.Metadata ??= new Dictionary<string, object>();
                            result.Metadata["wallet"] = walletData;

                            if (details.Any())
                            {
                                if (string.IsNullOrEmpty(result.Balance))
                                {
                                    result.Balance = string.Join(", ", details);
                                }
                                else
                                {
                                    result.Balance += $" ({string.Join(", ", details)})";
                                }
                            }
                        }

                        // Structured capture for other fields
                        result.Metadata ??= new Dictionary<string, object>();
                        if (data.TryGetProperty("plan", out var p)) result.Metadata["plan"] = p.ValueKind == JsonValueKind.String ? p.GetString()! : p.GetRawText();
                        if (data.TryGetProperty("name", out var n)) result.Metadata["name"] = n.ValueKind == JsonValueKind.String ? n.GetString()! : n.GetRawText();
                        if (data.TryGetProperty("equivalent_in_usd", out var usd)) result.Metadata["equivalent_in_usd"] = usd.ValueKind == JsonValueKind.String ? usd.GetString()! : usd.GetRawText();
                        if (data.TryGetProperty("remaining_credits", out var rem)) result.Metadata["remaining_credits"] = rem.ValueKind == JsonValueKind.String ? rem.GetString()! : rem.GetRawText();
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "Error parsing PiAPI response");
                    }

                    result.RawResponse = responseBody;
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
            finally
            {
                // Ensure raw response is captured even on some errors if possible
            }
        }

        protected override bool IsValidKeyFormat(string apiKey)
        {
            return !string.IsNullOrWhiteSpace(apiKey) && apiKey.Length >= 20;
        }
    }
}

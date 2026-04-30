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
                            result.AccountTier = plan.GetString();
                        }

                        if (data.TryGetProperty("name", out var name))
                        {
                            result.Detail = $"Account: {name.GetString()}";
                        }

                        // Extract Balance/Credits
                        if (data.TryGetProperty("equivalent_in_usd", out var usdBalance))
                        {
                            result.Balance = $"${usdBalance} USD";
                        }
                        else if (data.TryGetProperty("remaining_credits", out var credits))
                        {
                            result.Balance = $"{credits} Credits";
                        }

                        // Extract Detailed Wallet Info
                        if (data.TryGetProperty("wallet", out var wallet))
                        {
                            var details = new List<string>();
                            var walletData = new Dictionary<string, object>();
                            
                            if (wallet.TryGetProperty("mj_remain", out var mj)) { details.Add($"MJ: {mj}"); walletData["mj_remain"] = mj.GetRawText(); }
                            if (wallet.TryGetProperty("llm_remain", out var llm)) { details.Add($"LLM: {llm}"); walletData["llm_remain"] = llm.GetRawText(); }
                            if (wallet.TryGetProperty("suno_remain", out var suno)) { details.Add($"Suno: {suno}"); walletData["suno_remain"] = suno.GetRawText(); }
                            if (wallet.TryGetProperty("luma_remain", out var luma)) { details.Add($"Luma: {luma}"); walletData["luma_remain"] = luma.GetRawText(); }
                            if (wallet.TryGetProperty("gpts_remain", out var gpts)) { details.Add($"GPTs: {gpts}"); walletData["gpts_remain"] = gpts.GetRawText(); }
                            if (wallet.TryGetProperty("point_remain", out var points)) { details.Add($"Points: {points}"); walletData["point_remain"] = points.GetRawText(); }

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
                        if (data.TryGetProperty("plan", out var p)) result.Metadata["plan"] = p.GetString() ?? "";
                        if (data.TryGetProperty("name", out var n)) result.Metadata["name"] = n.GetString() ?? "";
                        if (data.TryGetProperty("equivalent_in_usd", out var usd)) result.Metadata["equivalent_in_usd"] = usd.GetRawText();
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

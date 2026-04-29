using Microsoft.Extensions.Logging;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Base;
using UnsecuredAPIKeys.Providers.Common;

namespace UnsecuredAPIKeys.Providers.AI_Providers
{
    /// <summary>
    /// Provider for XAI (X.AI/Grok) API keys - scraper only (no verification implemented yet)
    /// </summary>
    [ApiProvider]
    public class XAIProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "X.AI";
        public override ApiTypeEnum ApiType => ApiTypeEnum.XAI;

        public override IEnumerable<string> RegexPatterns =>
        [
            @"\bxai-[A-Za-z0-9]{32,}\b",  // XAI prefix pattern
            @"\bgrok[_-]?[A-Za-z0-9]{32,}\b",
            @"XAI_API_KEY",
            @"GROK_API_KEY",
            @"XAI_SECRET"
        ];

        public XAIProvider() : base() { }
        public XAIProvider(ILogger<XAIProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(string apiKey, HttpClient httpClient)
        {
            try
            {
                // 1. Get Team ID
                using var teamsRequest = new HttpRequestMessage(HttpMethod.Get, "https://api.x.ai/v1/teams");
                teamsRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

                var teamsResponse = await httpClient.SendAsync(teamsRequest);
                string teamsBody = await teamsResponse.Content.ReadAsStringAsync();

                if (teamsResponse.StatusCode == System.Net.HttpStatusCode.Unauthorized || 
                    teamsResponse.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    return ValidationResult.IsUnauthorized(teamsResponse.StatusCode);
                }

                if (teamsResponse.IsSuccessStatusCode)
                {
                    try 
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(teamsBody);
                        if (doc.RootElement.TryGetProperty("teams", out var teams) && 
                            teams.ValueKind == System.Text.Json.JsonValueKind.Array && 
                            teams.GetArrayLength() > 0)
                        {
                            var firstTeam = teams[0];
                            if (firstTeam.TryGetProperty("id", out var teamId))
                            {
                                string id = teamId.GetString() ?? "";
                                
                                // 2. Get Balance for this team
                                using var balanceRequest = new HttpRequestMessage(HttpMethod.Get, $"https://api.x.ai/v1/billing/teams/{id}/prepaid/balance");
                                balanceRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
                                
                                var balanceResponse = await httpClient.SendAsync(balanceRequest);
                                string balanceBody = await balanceResponse.Content.ReadAsStringAsync();
                                
                                if (balanceResponse.IsSuccessStatusCode)
                                {
                                    using var balanceDoc = System.Text.Json.JsonDocument.Parse(balanceBody);
                                    if (balanceDoc.RootElement.TryGetProperty("balance", out var balance))
                                    {
                                        var result = ValidationResult.Success(balanceResponse.StatusCode, "Valid X.AI key");
                                        result.Balance = $"{balance} Credits";
                                        result.AccountTier = firstTeam.TryGetProperty("name", out var name) ? name.GetString() : id;
                                        return result;
                                    }
                                }
                            }
                        }
                    }
                    catch { /* Fallback */ }

                    return ValidationResult.Success(teamsResponse.StatusCode, "Valid X.AI key (Account Found)");
                }

                return ValidationResult.HasHttpError(teamsResponse.StatusCode, 
                    $"Account check failed: {teamsResponse.StatusCode}");
            }
            catch (Exception ex)
            {
                return ValidationResult.HasNetworkError(ex.Message);
            }
        }

        protected override bool IsValidKeyFormat(string apiKey)
        {
            return !string.IsNullOrWhiteSpace(apiKey) && 
                   (apiKey.StartsWith("xai-") || apiKey.StartsWith("grok-") || apiKey.Length >= 32);
        }
    }
}

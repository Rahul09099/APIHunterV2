using Microsoft.Extensions.Logging;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Base;
using UnsecuredAPIKeys.Providers.Common;
using System.Net.Http.Headers;

namespace UnsecuredAPIKeys.Providers.AI_Providers
{
    [ApiProvider]
    public class FireworksProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "Fireworks AI";
        public override ApiTypeEnum ApiType => ApiTypeEnum.FireworksAI;

        public override IEnumerable<string> RegexPatterns =>
        [
            @"fw_[A-Za-z0-9]{32,}"
        ];

        public FireworksProvider() : base() { }
        public FireworksProvider(ILogger<FireworksProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(string apiKey, HttpClient httpClient)
        {
            try
            {
                // 1. Get Account ID
                using var accountRequest = new HttpRequestMessage(HttpMethod.Get, "https://api.fireworks.ai/v1/accounts");
                accountRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

                var accountResponse = await httpClient.SendAsync(accountRequest);
                string accountBody = await accountResponse.Content.ReadAsStringAsync();

                if (accountResponse.StatusCode == System.Net.HttpStatusCode.Unauthorized || 
                    accountResponse.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    return ValidationResult.IsUnauthorized(accountResponse.StatusCode);
                }

                if (accountResponse.IsSuccessStatusCode)
                {
                    try 
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(accountBody);
                        if (doc.RootElement.TryGetProperty("accounts", out var accounts) && 
                            accounts.ValueKind == System.Text.Json.JsonValueKind.Array && 
                            accounts.GetArrayLength() > 0)
                        {
                            var firstAccount = accounts[0];
                            if (firstAccount.TryGetProperty("name", out var accountName))
                            {
                                string name = accountName.GetString() ?? "";
                                
                                // 2. Get Credits for this account
                                using var creditsRequest = new HttpRequestMessage(HttpMethod.Get, $"https://api.fireworks.ai/v1/{name}/credits");
                                creditsRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                                
                                var creditsResponse = await httpClient.SendAsync(creditsRequest);
                                string creditsBody = await creditsResponse.Content.ReadAsStringAsync();
                                
                                if (creditsResponse.IsSuccessStatusCode)
                                {
                                    using var creditsDoc = System.Text.Json.JsonDocument.Parse(creditsBody);
                                    if (creditsDoc.RootElement.TryGetProperty("totalAmountUsd", out var total))
                                    {
                                        var result = ValidationResult.Success(creditsResponse.StatusCode, "Valid Fireworks AI key");
                                        result.Balance = $"{total} USD";
                                        result.AccountTier = name;
                                        return result;
                                    }
                                }
                            }
                        }
                    }
                    catch { /* Fallback to success if account found but credit call failed */ }

                    return ValidationResult.Success(accountResponse.StatusCode, "Valid Fireworks AI key (Account Found)");
                }
                
                return ValidationResult.HasHttpError(accountResponse.StatusCode, 
                    $"Account check failed: {accountResponse.StatusCode}");
            }
            catch (Exception ex)
            {
                return ValidationResult.HasNetworkError(ex.Message);
            }
        }

        private List<ModelInfo>? ParseFireworksModels(string jsonResponse)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(jsonResponse);
                if (!doc.RootElement.TryGetProperty("data", out var dataArray)) return null;

                var models = new List<ModelInfo>();
                foreach (var modelElement in dataArray.EnumerateArray())
                {
                    var id = modelElement.GetProperty("id").GetString() ?? "";
                    models.Add(new ModelInfo { ModelId = id, DisplayName = id });
                }
                return models;
            }
            catch { return null; }
        }

        protected override bool IsValidKeyFormat(string apiKey)
        {
            return !string.IsNullOrWhiteSpace(apiKey) && apiKey.StartsWith("fw_") && apiKey.Length >= 35;
        }
    }
}

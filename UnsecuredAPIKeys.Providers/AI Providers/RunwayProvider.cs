using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Http.Headers;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Base;
using UnsecuredAPIKeys.Providers.Common;

namespace UnsecuredAPIKeys.Providers.AI_Providers
{
    [ApiProvider]
    public class RunwayProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "RunwayML";
        public override ApiTypeEnum ApiType => ApiTypeEnum.RunwayML;

        public override IEnumerable<string> RegexPatterns =>
        [
            @"\bkey_[a-zA-Z0-9]{32,}\b",
            @"RUNWAYML_API_SECRET",
            @"RUNWAY_API_KEY"
        ];

        public RunwayProvider() : base() { }

        public RunwayProvider(ILogger<RunwayProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(string apiKey, HttpClient httpClient)
        {
            // Runway uses Bearer token and needs a version header
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.runwayml.com/v1/organization");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Headers.Add("X-Runway-Version", "2024-11-06");

            try 
            {
                var response = await httpClient.SendAsync(request);
                string responseBody = await response.Content.ReadAsStringAsync();

                 _logger?.LogDebug("RunwayML API response: Status={StatusCode}, Body={Body}",
                    response.StatusCode, TruncateResponse(responseBody));

                if (IsSuccessStatusCode(response.StatusCode))
                {
                    var result = ValidationResult.Success(response.StatusCode, "Valid RunwayML key");
                    
                    try 
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(responseBody);
                        var root = doc.RootElement;
                        
                        if (root.TryGetProperty("usage_tier", out var tier))
                            result.AccountTier = tier.GetString();
                            
                        if (root.TryGetProperty("creditBalance", out var credits))
                            result.Balance = $"{credits} Credits";
                        else if (root.TryGetProperty("billing", out var billing) && billing.TryGetProperty("credits", out var billingCredits))
                            result.Balance = $"{billingCredits} Credits";
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
            return !string.IsNullOrWhiteSpace(apiKey) && (apiKey.StartsWith("key_") || apiKey.Length > 20);
        }
    }
}

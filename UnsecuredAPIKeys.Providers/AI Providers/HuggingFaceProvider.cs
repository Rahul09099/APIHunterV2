using Microsoft.Extensions.Logging;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Base;
using UnsecuredAPIKeys.Providers.Common;
using System.Net.Http.Headers;

namespace UnsecuredAPIKeys.Providers.AI_Providers
{
    [ApiProvider]
    public class HuggingFaceProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "HuggingFace";
        public override ApiTypeEnum ApiType => ApiTypeEnum.HuggingFace;

        public override IEnumerable<string> RegexPatterns =>
        [
            @"hf_[A-Za-z0-9]{32,}"
        ];

        public HuggingFaceProvider() : base() { }
        public HuggingFaceProvider(ILogger<HuggingFaceProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(string apiKey, HttpClient httpClient)
        {
            try 
            {
                // WhoAmI-v2 endpoint provides more details including credit balance
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://huggingface.co/api/whoami-v2");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

                var response = await httpClient.SendAsync(request);
                string responseBody = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    var result = ValidationResult.Success(response.StatusCode, "Valid HuggingFace token");

                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(responseBody);
                        var root = doc.RootElement;
                        
                        if (root.TryGetProperty("name", out var name))
                            result.AccountTier = name.GetString();

                        if (root.TryGetProperty("creditBalance", out var balance))
                            result.Balance = $"{balance} Credits";
                    }
                    catch { /* Best effort */ }

                    return result;
                }
                
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    return ValidationResult.IsUnauthorized(response.StatusCode);
                }

                return ValidationResult.HasHttpError(response.StatusCode, $"Status: {response.StatusCode} Body: {TruncateResponse(responseBody)}");
            }
            catch (Exception ex)
            {
                // Network-level failure — use HasNetworkError, not HasHttpError
                return ValidationResult.HasNetworkError(ex.Message);
            }
        }

        protected override bool IsValidKeyFormat(string apiKey)
        {
            return !string.IsNullOrWhiteSpace(apiKey) && apiKey.StartsWith("hf_") && apiKey.Length >= 35;
        }
    }
}

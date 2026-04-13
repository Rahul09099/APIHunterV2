using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Http.Headers;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Base;
using UnsecuredAPIKeys.Providers.Common;

namespace UnsecuredAPIKeys.Providers.AI_Providers
{
    [ApiProvider]
    public class PolloAIProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "PolloAI";
        public override ApiTypeEnum ApiType => ApiTypeEnum.PolloAI;

        public override IEnumerable<string> RegexPatterns =>
        [
            @"\bpollo_[a-zA-Z0-9]{24,}\b",
            @"pollo_api_key",
            @"POLLO_API_KEY",
            @"POLLO_SECRET"
        ];

        public PolloAIProvider() : base() { }

        public PolloAIProvider(ILogger<PolloAIProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(string apiKey, HttpClient httpClient)
        {
            // Pollo AI uses x-api-key header
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://pollo.ai/api/platform/credit/balance");
            request.Headers.Add("x-api-key", apiKey);

            try 
            {
                var response = await httpClient.SendAsync(request);
                string responseBody = await response.Content.ReadAsStringAsync();

                 _logger?.LogDebug("PolloAI API response: Status={StatusCode}, Body={Body}",
                    response.StatusCode, TruncateResponse(responseBody));

                if (IsSuccessStatusCode(response.StatusCode))
                {
                    return ValidationResult.Success(response.StatusCode, "Valid PolloAI key");
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
            // Accept keys starting with pollo_ or non-empty strings of reasonable length
            return !string.IsNullOrWhiteSpace(apiKey) && (apiKey.StartsWith("pollo_") || apiKey.Length > 20);
        }
    }
}

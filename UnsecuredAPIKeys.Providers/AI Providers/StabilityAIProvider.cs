using Microsoft.Extensions.Logging;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Base;
using UnsecuredAPIKeys.Providers.Common;

namespace UnsecuredAPIKeys.Providers.AI_Providers
{
    /// <summary>
    /// Provider for StabilityAI API keys - scraper only (no verification implemented yet)
    /// </summary>
    [ApiProvider]
    public class StabilityAIProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "Stability AI";
        public override ApiTypeEnum ApiType => ApiTypeEnum.StabilityAI;

        public override IEnumerable<string> RegexPatterns =>
        [
            @"sk-[A-Za-z0-9]{32,}",  // StabilityAI uses sk- prefix similar to OpenAI
            @"stability[_-]?ai[_-]?[A-Za-z0-9]{32,}",
            @"STABILITY_API_KEY"
        ];

        public StabilityAIProvider() : base() { }
        public StabilityAIProvider(ILogger<StabilityAIProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(string apiKey, HttpClient httpClient)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.stability.ai/v1/user/account");
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

                var response = await httpClient.SendAsync(request);
                string responseBody = await response.Content.ReadAsStringAsync();

                _logger?.LogDebug("Stability AI API response: Status={StatusCode}, Body={Body}",
                    response.StatusCode, TruncateResponse(responseBody));

                if (IsSuccessStatusCode(response.StatusCode))
                {
                    return ValidationResult.Success(response.StatusCode, $"Key is valid. Account check successful.");
                }
                else if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized || 
                         response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    return ValidationResult.IsUnauthorized(response.StatusCode);
                }
                else
                {
                    // Check for quota/billing issues
                    if (ContainsAny(responseBody, new HashSet<string> { "quota", "billing", "limit", "credits", "insufficient" }))
                    {
                        return ValidationResult.Success(response.StatusCode, $"Valid key but access issue: {TruncateResponse(responseBody)}");
                    }

                    return ValidationResult.HasHttpError(response.StatusCode, 
                        $"API request failed with status {response.StatusCode}. Response: {TruncateResponse(responseBody)}");
                }
            }
            catch (Exception ex)
            {
                return ValidationResult.HasNetworkError(ex.Message);
            }
        }

        protected override bool IsValidKeyFormat(string apiKey)
        {
            return !string.IsNullOrWhiteSpace(apiKey) && apiKey.Length >= 32;
        }
    }
}

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
            @"stability[_-]?ai[_-]?[A-Za-z0-9]{32,}"
        ];

        public StabilityAIProvider() : base() { }
        public StabilityAIProvider(ILogger<StabilityAIProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(string apiKey, HttpClient httpClient)
        {
            try
            {
                httpClient.Timeout = TimeSpan.FromSeconds(15);
                
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
                
                var bodyLower = responseBody.ToLowerInvariant();

                switch (response.StatusCode)
                {
                    case System.Net.HttpStatusCode.Unauthorized: // 401
                        return ValidationResult.IsUnauthorized(response.StatusCode);

                    case System.Net.HttpStatusCode.Forbidden: // 403
                        _logger?.LogInformation("API key has permission restrictions but is valid (403)");
                        return ValidationResult.Success(response.StatusCode, "Valid key (restricted)");

                    case System.Net.HttpStatusCode.NotFound: // 404
                        return ValidationResult.HasHttpError(response.StatusCode, 
                            $"Endpoint not found (not a key issue): {TruncateResponse(responseBody)}");

                    case (System.Net.HttpStatusCode)429: // 429
                        _logger?.LogInformation("API key is valid but rate limited (429)");
                        return ValidationResult.Success(response.StatusCode, "Rate limited (valid key)");

                    default:
                        // Check for quota/billing issues in any other status code
                        if (bodyLower.Contains("quota") || bodyLower.Contains("billing") || 
                            bodyLower.Contains("limit") || bodyLower.Contains("credits") || 
                            bodyLower.Contains("insufficient"))
                        {
                            _logger?.LogInformation("API key is valid but has quota/billing issues ({StatusCode})", response.StatusCode);
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

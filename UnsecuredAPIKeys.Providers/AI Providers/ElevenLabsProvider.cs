using Microsoft.Extensions.Logging;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Base;
using UnsecuredAPIKeys.Providers.Common;

namespace UnsecuredAPIKeys.Providers.AI_Providers
{
    /// <summary>
    /// Provider for ElevenLabs API keys - scraper only (no verification implemented yet)
    /// </summary>
    [ApiProvider]
    public class ElevenLabsProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "ElevenLabs";
        public override ApiTypeEnum ApiType => ApiTypeEnum.ElevenLabs;

        public override IEnumerable<string> RegexPatterns =>
        [
            @"[A-Za-z0-9]{32}",  // ElevenLabs uses 32-char alphanumeric tokens
            @"elevenlabs[_-]?[A-Za-z0-9]{32,}",
            @"ELEVEN_API_KEY",
            @"ELEVENLABS_API_KEY"
        ];

        public ElevenLabsProvider() : base() { }
        public ElevenLabsProvider(ILogger<ElevenLabsProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(string apiKey, HttpClient httpClient)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.elevenlabs.io/v1/user/subscription");
                request.Headers.Add("xi-api-key", apiKey);

                var response = await httpClient.SendAsync(request);
                string responseBody = await response.Content.ReadAsStringAsync();

                _logger?.LogDebug("ElevenLabs API response: Status={StatusCode}, Body={Body}",
                    response.StatusCode, TruncateResponse(responseBody));

                if (IsSuccessStatusCode(response.StatusCode))
                {
                    return ValidationResult.Success(response.StatusCode, $"Subscription check successful: {TruncateResponse(responseBody)}");
                }
                else if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized || 
                         response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    return ValidationResult.IsUnauthorized(response.StatusCode);
                }
                else
                {
                    // Check for quota/billing issues
                    if (ContainsAny(responseBody, new HashSet<string> { "quota", "billing", "limit", "insufficient" }))
                    {
                        return ValidationResult.Success(response.StatusCode, $"Valid key but subscription issue: {TruncateResponse(responseBody)}");
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

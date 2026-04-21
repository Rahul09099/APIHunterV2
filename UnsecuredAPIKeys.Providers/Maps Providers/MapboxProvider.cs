using Microsoft.Extensions.Logging;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Base;
using UnsecuredAPIKeys.Providers.Common;

namespace UnsecuredAPIKeys.Providers.Maps_Providers
{
    /// <summary>
    /// Provider for Mapbox API keys - scraper only (no verification implemented yet)
    /// </summary>
    [ApiProvider]
    public class MapboxProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "Mapbox";
        public override ApiTypeEnum ApiType => ApiTypeEnum.Mapbox;

        public override IEnumerable<string> RegexPatterns =>
        [
            @"pk\.[A-Za-z0-9_-]{60,}",  // Mapbox public token format
            @"sk\.[A-Za-z0-9_-]{60,}",  // Mapbox secret token format
            @"mapbox[_-]?[A-Za-z0-9]{32,}",
            @"MAPBOX_TOKEN",
            @"MAPBOX_API_KEY"
        ];

        public MapboxProvider() : base() { }
        public MapboxProvider(ILogger<MapboxProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(string apiKey, HttpClient httpClient)
        {
            try
            {
                // Verify using the Styles API - it's a standard endpoint available for most tokens
                var endpoint = $"https://api.mapbox.com/styles/v1/mapbox/streets-v11?access_token={apiKey}";
                
                var response = await httpClient.GetAsync(endpoint);
                var content = await response.Content.ReadAsStringAsync();

                _logger?.LogDebug("Mapbox API response: Status={StatusCode}", response.StatusCode);

                if (response.IsSuccessStatusCode)
                {
                    return ValidationResult.Success(response.StatusCode, "Token is valid and active.");
                }
                
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized || 
                    response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    return ValidationResult.IsUnauthorized(response.StatusCode);
                }

                // Handle other errors (quota/unrecognized)
                if (content.Contains("quota", StringComparison.OrdinalIgnoreCase) || 
                    content.Contains("limit", StringComparison.OrdinalIgnoreCase))
                {
                    return ValidationResult.Success(response.StatusCode, $"Valid but check quota: {TruncateResponse(content)}");
                }

                return ValidationResult.HasHttpError(response.StatusCode, $"API error: {TruncateResponse(content)}");
            }
            catch (Exception ex)
            {
                return ValidationResult.HasNetworkError(ex.Message);
            }
        }

        protected override bool IsValidKeyFormat(string apiKey)
        {
            return !string.IsNullOrWhiteSpace(apiKey) && 
                   ((apiKey.StartsWith("pk.") || apiKey.StartsWith("sk.")) || apiKey.Length >= 32);
        }
    }
}

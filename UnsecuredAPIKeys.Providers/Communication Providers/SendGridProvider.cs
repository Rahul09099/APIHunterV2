using Microsoft.Extensions.Logging;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Base;
using UnsecuredAPIKeys.Providers.Common;

namespace UnsecuredAPIKeys.Providers.Communication_Providers
{
    /// <summary>
    /// Provider for SendGrid API keys - scraper only (no verification implemented yet)
    /// </summary>
    [ApiProvider]
    public class SendGridProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "SendGrid";
        public override ApiTypeEnum ApiType => ApiTypeEnum.SendGrid;

        public override IEnumerable<string> RegexPatterns =>
        [
            @"SG\.[A-Za-z0-9_-]{22}\.[A-Za-z0-9_-]{43}",  // SendGrid standard format
            @"sendgrid[_-]?[A-Za-z0-9]{32,}",
            @"SENDGRID_API_KEY"
        ];

        public SendGridProvider() : base() { }
        public SendGridProvider(ILogger<SendGridProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(string apiKey, HttpClient httpClient)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.sendgrid.com/v3/scopes");
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

                var response = await httpClient.SendAsync(request);
                string responseBody = await response.Content.ReadAsStringAsync();

                _logger?.LogDebug("SendGrid API response: Status={StatusCode}", response.StatusCode);

                if (IsSuccessStatusCode(response.StatusCode))
                {
                    return ValidationResult.Success(response.StatusCode, $"Key is valid. Scopes retrieved successfully.");
                }
                else if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized || 
                         response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    return ValidationResult.IsUnauthorized(response.StatusCode);
                }
                else
                {
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
            return !string.IsNullOrWhiteSpace(apiKey) && (apiKey.StartsWith("SG.") || apiKey.Length >= 32);
        }
    }
}

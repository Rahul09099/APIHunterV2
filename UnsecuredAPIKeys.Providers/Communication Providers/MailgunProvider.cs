using Microsoft.Extensions.Logging;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Base;
using UnsecuredAPIKeys.Providers.Common;

namespace UnsecuredAPIKeys.Providers.Communication_Providers
{
    /// <summary>
    /// Provider for Mailgun API keys - scraper only (no verification implemented yet)
    /// </summary>
    [ApiProvider]
    public class MailgunProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "Mailgun";
        public override ApiTypeEnum ApiType => ApiTypeEnum.Mailgun;

        public override IEnumerable<string> RegexPatterns =>
        [
            @"key-[A-Za-z0-9]{32}",  // Mailgun standard format
            @"mailgun[_-]?[A-Za-z0-9]{32,}",
            @"MAILGUN_API_KEY"
        ];

        public MailgunProvider() : base() { }
        public MailgunProvider(ILogger<MailgunProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(string apiKey, HttpClient httpClient)
        {
            try
            {
                // Mailgun uses Basic auth with 'api' as username
                var authValue = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($"api:{apiKey}"));
                
                // Get monthly custom limits
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.mailgun.net/v5/accounts/limit/custom/monthly");
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", authValue);

                var response = await httpClient.SendAsync(request);
                string responseBody = await response.Content.ReadAsStringAsync();

                _logger?.LogDebug("Mailgun limit API response: Status={StatusCode}, Body={Body}", 
                    response.StatusCode, TruncateResponse(responseBody));

                if (IsSuccessStatusCode(response.StatusCode))
                {
                    var result = ValidationResult.Success(response.StatusCode, "Valid Mailgun key");
                    
                    try 
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(responseBody);
                        if (doc.RootElement.TryGetProperty("limit", out var limit) && 
                            doc.RootElement.TryGetProperty("current", out var current))
                        {
                            result.Balance = $"{current} / {limit} Messages Used";
                        }
                    }
                    catch { /* Best effort */ }

                    return result;
                }
                else if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized || 
                         response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    return ValidationResult.IsUnauthorized(response.StatusCode);
                }
                else
                {
                    // Fallback to domain check if v5 limit endpoint fails
                    using var domainsRequest = new HttpRequestMessage(HttpMethod.Get, "https://api.mailgun.net/v3/domains");
                    domainsRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", authValue);
                    var domainsResponse = await httpClient.SendAsync(domainsRequest);
                    
                    if (IsSuccessStatusCode(domainsResponse.StatusCode))
                    {
                        return ValidationResult.Success(domainsResponse.StatusCode, "Valid Mailgun key (Domain check passed)");
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
            return !string.IsNullOrWhiteSpace(apiKey) && (apiKey.StartsWith("key-") || apiKey.Length >= 32);
        }
    }
}

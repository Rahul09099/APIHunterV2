using System.Net;
using Microsoft.Extensions.Logging;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Base;
using UnsecuredAPIKeys.Providers.Common;

namespace UnsecuredAPIKeys.Providers.AI_Providers
{

    [ApiProvider]
    public class CohereProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "Cohere";
        public override ApiTypeEnum ApiType => ApiTypeEnum.Cohere;

        public override IEnumerable<string> RegexPatterns =>
        [
            @"[A-Za-z0-9]{40}",  // Cohere uses 40-char tokens
            @"cohere[_-]?[A-Za-z0-9]{32,}",
            @"COHERE_API_KEY",
            @"CO_API_KEY"
        ];

        public CohereProvider() : base() { }
        public CohereProvider(ILogger<CohereProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(string apiKey, HttpClient httpClient)
        {
            try
            {
                // Use v2 chat endpoint (v1 is deprecated as of 2024)
                using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.cohere.com/v2/chat");
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
                request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

                var requestBody = new
                {
                    model = "command-r",
                    messages = new[]
                    {
                        new { role = "user", content = "hi" }
                    },
                    max_tokens = 1
                };

                var jsonContent = System.Text.Json.JsonSerializer.Serialize(requestBody);
                request.Content = new StringContent(jsonContent, System.Text.Encoding.UTF8, "application/json");

                var response = await httpClient.SendAsync(request);
                string responseBody = await response.Content.ReadAsStringAsync();

                _logger?.LogDebug("Cohere API response: Status={StatusCode}, Body={Body}",
                    response.StatusCode, TruncateResponse(responseBody));

                if (IsSuccessStatusCode(response.StatusCode))
                {
                    var result = ValidationResult.Success(response.StatusCode, "Key is valid and generation working.");
                    
                    // Identify trial keys in success response headers or body if possible
                    if (responseBody.Contains("Trial key", StringComparison.OrdinalIgnoreCase))
                    {
                        result.AccountTier = "Trial";
                    }
                    
                    return result;
                }
                else if (response.StatusCode == HttpStatusCode.Unauthorized || 
                         response.StatusCode == HttpStatusCode.Forbidden)
                {
                    return ValidationResult.IsUnauthorized(response.StatusCode);
                }
                else
                {
                    var result = ValidationResult.Success(response.StatusCode, $"Valid key but access issue: {TruncateResponse(responseBody)}");

                    // Check for quota/billing/trial issues
                    if (ContainsAny(responseBody, new HashSet<string> { "quota", "billing", "limit", "insufficient", "trial" }))
                    {
                        result.IsQuotaExceeded = true;
                        
                        if (responseBody.Contains("trial", StringComparison.OrdinalIgnoreCase))
                        {
                            result.AccountTier = "Trial";
                            result.Detail = "Valid trial key but limit reached.";
                        }
                        else
                        {
                            result.Detail = "Valid key but quota/billing issue.";
                        }
                    }
                    else
                    {
                        return ValidationResult.HasHttpError(response.StatusCode, 
                            $"API request failed with status {response.StatusCode}. Response: {TruncateResponse(responseBody)}");
                    }

                    return result;
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

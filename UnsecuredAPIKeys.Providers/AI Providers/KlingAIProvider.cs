using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Http.Headers;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Base;
using UnsecuredAPIKeys.Providers.Common;

namespace UnsecuredAPIKeys.Providers.AI_Providers
{
    [ApiProvider]
    public class KlingAIProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "KlingAI";
        public override ApiTypeEnum ApiType => ApiTypeEnum.KlingAI;

        public override IEnumerable<string> RegexPatterns =>
        [
            @"kling_api_key",
            @"KLING_API_KEY",
            @"KLING_ACCESS_KEY",
            @"kling_access_key"
        ];

        public KlingAIProvider() : base() { }

        public KlingAIProvider(ILogger<KlingAIProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(string apiKey, HttpClient httpClient)
        {
            // KlingAI typically uses JWTs or Access/Secret keys. 
            // We'll attempt a request to a likely endpoint assuming a Bearer token.
            // Note: Official docs for exact validation endpoint are scarce without login, 
            // but we can try the standard model listing or similar if they offer it.
            // Replicate integration often uses "Token <token>".
            
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.klingai.com/v1/videos"); // Guessing endpoint based on function
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            try 
            {
                var response = await httpClient.SendAsync(request);
                string responseBody = await response.Content.ReadAsStringAsync();

                 _logger?.LogDebug("KlingAI API response: Status={StatusCode}, Body={Body}",
                    response.StatusCode, TruncateResponse(responseBody));

                if (IsSuccessStatusCode(response.StatusCode))
                {
                    return ValidationResult.Success(response.StatusCode, "Valid KlingAI key");
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
            // Since we don't have a strict regex, we'll accept non-empty strings of reasonable length
            return !string.IsNullOrWhiteSpace(apiKey) && apiKey.Length > 20;
        }
    }
}

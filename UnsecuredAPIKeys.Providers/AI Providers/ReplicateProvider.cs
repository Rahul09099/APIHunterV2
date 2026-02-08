using Microsoft.Extensions.Logging;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Base;
using UnsecuredAPIKeys.Providers.Common;
using System.Net.Http.Headers;

namespace UnsecuredAPIKeys.Providers.AI_Providers
{
    [ApiProvider]
    public class ReplicateProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "Replicate";
        public override ApiTypeEnum ApiType => ApiTypeEnum.Replicate;

        public override IEnumerable<string> RegexPatterns =>
        [
            @"r8_[A-Za-z0-9]{32,}"
        ];

        public ReplicateProvider() : base() { }
        public ReplicateProvider(ILogger<ReplicateProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(string apiKey, HttpClient httpClient)
        {
            try 
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.replicate.com/v1/account");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                // Replicate requires User-Agent
                request.Headers.UserAgent.ParseAdd("UnsecuredAPIKeys-Lite/1.0");

                var response = await httpClient.SendAsync(request);
                string responseBody = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    return new ValidationResult 
                    { 
                        Status = ValidationAttemptStatus.Valid, 
                        HttpStatusCode = response.StatusCode, 
                        Detail = responseBody 
                    };
                }
                
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    return ValidationResult.IsUnauthorized(response.StatusCode);
                }

                return ValidationResult.HasHttpError(response.StatusCode, $"Status: {response.StatusCode} Body: {TruncateResponse(responseBody)}");
            }
            catch (Exception ex)
            {
                return ValidationResult.HasHttpError(System.Net.HttpStatusCode.InternalServerError, ex.Message);
            }
        }

        protected override bool IsValidKeyFormat(string apiKey)
        {
            return !string.IsNullOrWhiteSpace(apiKey) && apiKey.StartsWith("r8_") && apiKey.Length >= 35;
        }
    }
}

using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Base;
using UnsecuredAPIKeys.Providers.Common;

namespace UnsecuredAPIKeys.Providers.AI_Providers
{
    /// <summary>
    /// Provider for Anyscale API keys — managed Ray platform and LLM endpoints.
    /// Verification endpoint: GET https://api.endpoints.anyscale.com/v1/models (Bearer auth)
    /// </summary>
    [ApiProvider]
    public class AnyscaleProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "Anyscale";
        public override ApiTypeEnum ApiType => ApiTypeEnum.Anyscale;

        public override IEnumerable<string> RegexPatterns =>
        [
            @"esecret_[A-Za-z0-9]{32,}",            // Anyscale uses esecret_ prefix
            @"anyscale[_-]?[A-Za-z0-9]{20,}",
            @"ANYSCALE_API_KEY",
            @"ANYSCALE_TOKEN"
        ];

        public AnyscaleProvider() : base() { }
        public AnyscaleProvider(ILogger<AnyscaleProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(string apiKey, HttpClient httpClient)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get,
                    "https://api.endpoints.anyscale.com/v1/models");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

                var response = await httpClient.SendAsync(request);
                string responseBody = await response.Content.ReadAsStringAsync();

                _logger?.LogDebug("Anyscale API response: Status={StatusCode}, Body={Body}",
                    response.StatusCode, TruncateResponse(responseBody));

                if (IsSuccessStatusCode(response.StatusCode))
                {
                    var result = ValidationResult.Success(response.StatusCode, "Valid Anyscale key");

                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(responseBody);
                        if (doc.RootElement.TryGetProperty("data", out var data))
                        {
                            var count = data.GetArrayLength();
                            result.Detail = $"Valid Anyscale key — {count} endpoint models available";
                        }
                    }
                    catch { /* Best effort */ }

                    return result;
                }

                return response.StatusCode switch
                {
                    System.Net.HttpStatusCode.Unauthorized or
                    System.Net.HttpStatusCode.Forbidden =>
                        ValidationResult.IsUnauthorized(response.StatusCode),
                    (System.Net.HttpStatusCode)429 =>
                        ValidationResult.Success(response.StatusCode, "Rate limited (key is valid)"),
                    _ => ValidationResult.HasHttpError(response.StatusCode,
                        $"Unexpected status {response.StatusCode}. Body: {TruncateResponse(responseBody)}")
                };
            }
            catch (Exception ex)
            {
                return ValidationResult.HasNetworkError(ex.Message);
            }
        }

        protected override bool IsValidKeyFormat(string apiKey)
        {
            return !string.IsNullOrWhiteSpace(apiKey) &&
                   (apiKey.StartsWith("esecret_") || apiKey.Length >= 32);
        }
    }
}

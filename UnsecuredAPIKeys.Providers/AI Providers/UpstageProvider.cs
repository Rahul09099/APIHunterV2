using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Base;
using UnsecuredAPIKeys.Providers.Common;

namespace UnsecuredAPIKeys.Providers.AI_Providers
{
    /// <summary>
    /// Provider for Upstage API keys — Solar LLM and Document AI.
    /// Verification endpoint: GET https://api.upstage.ai/v1/models (Bearer auth)
    /// </summary>
    [ApiProvider(false, false)]
    public class UpstageProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "Upstage (Solar)";
        public override ApiTypeEnum ApiType => ApiTypeEnum.Upstage;

        public override IEnumerable<string> RegexPatterns =>
        [
            @"up_[A-Za-z0-9]{40,}",                 // Upstage uses up_ prefix
            @"upstage[_-]?[A-Za-z0-9]{20,}",
            @"UPSTAGE_API_KEY",
            @"SOLAR_API_KEY"
        ];

        public UpstageProvider() : base() { }
        public UpstageProvider(ILogger<UpstageProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(string apiKey, HttpClient httpClient)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.upstage.ai/v1/models");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

                var response = await httpClient.SendAsync(request);
                string responseBody = await response.Content.ReadAsStringAsync();

                _logger?.LogDebug("Upstage API response: Status={StatusCode}, Body={Body}",
                    response.StatusCode, TruncateResponse(responseBody));

                if (IsSuccessStatusCode(response.StatusCode))
                {
                    var result = ValidationResult.Success(response.StatusCode, "Valid Upstage key");

                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(responseBody);
                        if (doc.RootElement.TryGetProperty("data", out var data))
                        {
                            var count = data.GetArrayLength();
                            result.Detail = $"Valid Upstage key — {count} models available";
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
                   (apiKey.StartsWith("up_") || apiKey.Length >= 32);
        }
    }
}

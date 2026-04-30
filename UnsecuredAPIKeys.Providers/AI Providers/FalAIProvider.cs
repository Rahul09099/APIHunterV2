using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Base;
using UnsecuredAPIKeys.Providers.Common;

namespace UnsecuredAPIKeys.Providers.AI_Providers
{
    /// <summary>
    /// Provider for Fal.ai API keys — fast serverless image and video generation.
    /// Verification endpoint: GET https://rest.alpha.fal.ai/tokens (Key-Id auth header)
    /// </summary>
    [ApiProvider]
    public class FalAIProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "Fal.ai";
        public override ApiTypeEnum ApiType => ApiTypeEnum.FalAI;

        public override IEnumerable<string> RegexPatterns =>
        [
            @"[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}:[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}",  // fal key-id:key-secret UUID format
            @"FAL_KEY\s*=\s*['""]?([A-Za-z0-9_-]{20,})['""]?",
            @"FAL_KEY",
            @"FAL_API_KEY"
        ];

        public FalAIProvider() : base() { }
        public FalAIProvider(ILogger<FalAIProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(string apiKey, HttpClient httpClient)
        {
            try
            {
                // Fal.ai uses "Key <key>" authorization — confirmed base URL: api.fal.ai
                using var request = new HttpRequestMessage(HttpMethod.Get,
                    "https://api.fal.ai/v1/models");
                request.Headers.Authorization = new AuthenticationHeaderValue("Key", apiKey);

                var response = await httpClient.SendAsync(request);
                string responseBody = await response.Content.ReadAsStringAsync();

                _logger?.LogDebug("Fal.ai API response: Status={StatusCode}, Body={Body}",
                    response.StatusCode, TruncateResponse(responseBody));

                if (IsSuccessStatusCode(response.StatusCode))
                {
                    var result = ValidationResult.Success(response.StatusCode, "Valid Fal.ai key");

                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(responseBody);
                        if (doc.RootElement.TryGetProperty("data", out var data))
                        {
                            var count = data.GetArrayLength();
                            result.Detail = $"Valid Fal.ai key — {count} model(s) available";
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
            return !string.IsNullOrWhiteSpace(apiKey) && apiKey.Length >= 32;
        }
    }
}

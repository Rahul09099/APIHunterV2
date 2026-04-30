using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Base;
using UnsecuredAPIKeys.Providers.Common;

namespace UnsecuredAPIKeys.Providers.AI_Providers
{
    /// <summary>
    /// Provider for AI21 Labs API keys — Jurassic and Jamba LLM family.
    /// Verification endpoint: GET https://api.ai21.com/studio/v1/models (Bearer auth)
    /// </summary>
    [ApiProvider]
    public class AI21LabsProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "AI21 Labs";
        public override ApiTypeEnum ApiType => ApiTypeEnum.AI21Labs;

        public override IEnumerable<string> RegexPatterns =>
        [
            @"[A-Za-z0-9]{32,}",                    // AI21 uses plain alphanumeric tokens
            @"ai21[_-]?[A-Za-z0-9]{20,}",
            @"AI21_API_KEY",
            @"AI21LABS_API_KEY"
        ];

        public AI21LabsProvider() : base() { }
        public AI21LabsProvider(ILogger<AI21LabsProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(string apiKey, HttpClient httpClient)
        {
            try
            {
                // List models — lightweight, read-only, confirms key validity
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.ai21.com/studio/v1/models");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

                var response = await httpClient.SendAsync(request);
                string responseBody = await response.Content.ReadAsStringAsync();

                _logger?.LogDebug("AI21 Labs API response: Status={StatusCode}, Body={Body}",
                    response.StatusCode, TruncateResponse(responseBody));

                if (IsSuccessStatusCode(response.StatusCode))
                {
                    var result = ValidationResult.Success(response.StatusCode, "Valid AI21 Labs key");

                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(responseBody);
                        // Response is an array of model objects
                        if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            var count = doc.RootElement.GetArrayLength();
                            result.Detail = $"Valid AI21 Labs key — {count} models accessible";
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
            return !string.IsNullOrWhiteSpace(apiKey) && apiKey.Length >= 20;
        }
    }
}

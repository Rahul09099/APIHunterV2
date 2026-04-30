using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Base;
using UnsecuredAPIKeys.Providers.Common;

namespace UnsecuredAPIKeys.Providers.AI_Providers
{
    /// <summary>
    /// Provider for Jina AI API keys — embeddings and multimodal AI.
    /// Verification endpoint: GET https://api.jina.ai/v1/models (Bearer auth)
    /// </summary>
    [ApiProvider]
    public class JinaAIProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "Jina AI";
        public override ApiTypeEnum ApiType => ApiTypeEnum.JinaAI;

        public override IEnumerable<string> RegexPatterns =>
        [
            @"jina_[A-Za-z0-9]{40,}",               // Jina uses jina_ prefix
            @"JINA_API_KEY",
            @"JINA_AI_API_KEY"
        ];

        public JinaAIProvider() : base() { }
        public JinaAIProvider(ILogger<JinaAIProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(string apiKey, HttpClient httpClient)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.jina.ai/v1/models");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

                var response = await httpClient.SendAsync(request);
                string responseBody = await response.Content.ReadAsStringAsync();

                _logger?.LogDebug("Jina AI API response: Status={StatusCode}, Body={Body}",
                    response.StatusCode, TruncateResponse(responseBody));

                if (IsSuccessStatusCode(response.StatusCode))
                {
                    var result = ValidationResult.Success(response.StatusCode, "Valid Jina AI key");

                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(responseBody);
                        if (doc.RootElement.TryGetProperty("data", out var data))
                        {
                            var count = data.GetArrayLength();
                            result.Detail = $"Valid Jina AI key — {count} models available";
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
                   (apiKey.StartsWith("jina_") || apiKey.Length >= 32);
        }
    }
}

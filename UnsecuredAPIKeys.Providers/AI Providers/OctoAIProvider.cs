using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Base;
using UnsecuredAPIKeys.Providers.Common;

namespace UnsecuredAPIKeys.Providers.AI_Providers
{
    /// <summary>
    /// Provider for OctoAI API keys — inference platform for open-source AI models.
    /// Verification endpoint: GET https://text.octoai.run/v1/models (Bearer auth)
    /// </summary>
    [ApiProvider(false, false)]
    public class OctoAIProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "OctoAI";
        public override ApiTypeEnum ApiType => ApiTypeEnum.OctoAI;

        public override IEnumerable<string> RegexPatterns =>
        [
            @"[A-Za-z0-9]{32,}",                    // OctoAI uses alphanumeric tokens
            @"octoai[_-]?[A-Za-z0-9]{20,}",
            @"OCTOAI_TOKEN",
            @"OCTOAI_API_KEY"
        ];

        public OctoAIProvider() : base() { }
        public OctoAIProvider(ILogger<OctoAIProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(string apiKey, HttpClient httpClient)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.octoai.cloud/v1/models");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

                var response = await httpClient.SendAsync(request);
                string responseBody = await response.Content.ReadAsStringAsync();

                _logger?.LogDebug("OctoAI API response: Status={StatusCode}, Body={Body}",
                    response.StatusCode, TruncateResponse(responseBody));

                if (IsSuccessStatusCode(response.StatusCode))
                {
                    var result = ValidationResult.Success(response.StatusCode, "Valid OctoAI token");

                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(responseBody);
                        if (doc.RootElement.TryGetProperty("data", out var data))
                        {
                            var modelCount = data.GetArrayLength();
                            result.Detail = $"Valid OctoAI token — {modelCount} models available";
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

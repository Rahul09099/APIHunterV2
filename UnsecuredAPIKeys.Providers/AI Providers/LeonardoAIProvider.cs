using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Base;
using UnsecuredAPIKeys.Providers.Common;

namespace UnsecuredAPIKeys.Providers.AI_Providers
{
    /// <summary>
    /// Provider for Leonardo.ai API keys — AI image generation platform.
    ///
    /// Key format: UUID (XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX)
    /// Confirmed from official docs: https://docs.leonardo.ai/docs/api-error-messages
    /// "It should be added to the request header as authorization: Bearer XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX"
    ///
    /// Auth: Authorization: Bearer {uuid-key}
    ///
    /// Verification endpoint: GET https://cloud.leonardo.ai/api/rest/v2/models
    /// Documented at: https://docs.leonardo.ai/reference/getmodels
    /// Returns a list of available platform models on a valid key (200).
    ///
    /// Note: /v1/me is NOT used here — it is not listed in the current official Leonardo API reference.
    ///       The current billing model is PAYG (USD balance); no credit-balance field is parsed.
    ///
    /// Invalid key response:
    /// { "error": "Authentication hook unauthorized this request", "code": "access-denied" }
    /// </summary>
    [ApiProvider]
    public class LeonardoAIProvider : BaseApiKeyProvider
    {
        // UUID regex — used for both pattern matching and format validation
        private static readonly Regex UuidRegex = new(
            @"^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public override string ProviderName => "Leonardo.ai";
        public override ApiTypeEnum ApiType => ApiTypeEnum.LeonardoAI;

        public override IEnumerable<string> RegexPatterns =>
        [
            // Context-aware UUID patterns — only match when adjacent to Leonardo-related context
            // This reduces false positives from generic UUIDs in other codebases
            @"LEONARDO_API_KEY\s*[=:]\s*['""]?([a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12})['""]?",
            @"LEONARDO_AI_API_KEY\s*[=:]\s*['""]?([a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12})['""]?",
            @"leonardo[._-]?ai[._-]?key\s*[=:]\s*['""]?([a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12})['""]?",

            // Env var names — scraper will find these and extract nearby UUID values
            @"LEONARDO_API_KEY",
            @"LEONARDO_AI_API_KEY",
            @"LEONARDO_KEY"
        ];

        public LeonardoAIProvider() : base() { }
        public LeonardoAIProvider(ILogger<LeonardoAIProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(string apiKey, HttpClient httpClient)
        {
            try
            {
                // GET /api/rest/v2/models — officially documented, authenticated, read-only endpoint.
                // Returns a list of available platform models. Does NOT consume any account balance.
                // Ref: https://docs.leonardo.ai/reference/getmodels
                using var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    "https://cloud.leonardo.ai/api/rest/v2/models");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

                var response = await httpClient.SendAsync(request);
                string responseBody = await response.Content.ReadAsStringAsync();

                _logger?.LogDebug("Leonardo.ai API response: Status={StatusCode}, Body={Body}",
                    response.StatusCode, TruncateResponse(responseBody));

                if (IsSuccessStatusCode(response.StatusCode))
                {
                    var result = ValidationResult.Success(response.StatusCode, "Valid Leonardo.ai key");

                    // Best-effort: parse model count from the array response
                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(responseBody);
                        // Response is an array of model objects
                        if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            int modelCount = doc.RootElement.GetArrayLength();
                            result.Metadata = new System.Collections.Generic.Dictionary<string, object>
                            {
                                ["models_available"] = modelCount,
                                ["authentication_valid"] = true
                            };
                            result.Detail = $"Valid Leonardo.ai key — {modelCount} model(s) available";
                        }
                        else if (doc.RootElement.TryGetProperty("models", out var modelsEl) &&
                                 modelsEl.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            int modelCount = modelsEl.GetArrayLength();
                            result.Metadata = new System.Collections.Generic.Dictionary<string, object>
                            {
                                ["models_available"] = modelCount,
                                ["authentication_valid"] = true
                            };
                            result.Detail = $"Valid Leonardo.ai key — {modelCount} model(s) available";
                        }
                    }
                    catch { result.Detail = "Valid Leonardo.ai key"; }

                    result.RawResponse = responseBody;
                    return result;
                }

                // 401 — invalid or expired key
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
                    responseBody.Contains("access-denied") ||
                    responseBody.Contains("unauthorized"))
                {
                    return ValidationResult.IsUnauthorized(response.StatusCode,
                        "Invalid Leonardo.ai API key — check UUID format and key validity");
                }

                // 403 — key may be valid but access is restricted (plan or permission issue)
                if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    return ValidationResult.HasHttpError(response.StatusCode,
                        "Leonardo.ai API key forbidden (403) — permission or plan restriction; key may be valid");
                }

                // 429 — rate limited; validation inconclusive
                if ((int)response.StatusCode == 429)
                {
                    return ValidationResult.HasHttpError(response.StatusCode,
                        "Leonardo.ai validation unavailable — request rate limited (429)");
                }

                // 5xx — provider unavailable
                if ((int)response.StatusCode >= 500)
                {
                    return ValidationResult.HasHttpError(response.StatusCode,
                        $"Leonardo.ai service error ({response.StatusCode}) — validation unavailable");
                }

                return ValidationResult.HasHttpError(response.StatusCode,
                    $"Unexpected status {response.StatusCode}. Body: {TruncateResponse(responseBody)}");
            }
            catch (Exception ex)
            {
                return ValidationResult.HasNetworkError(ex.Message);
            }
        }

        protected override bool IsValidKeyFormat(string apiKey)
        {
            // Leonardo.ai keys are strictly UUID format
            // e.g. a1b2c3d4-e5f6-7890-abcd-ef1234567890
            return !string.IsNullOrWhiteSpace(apiKey) && UuidRegex.IsMatch(apiKey.Trim());
        }
    }
}

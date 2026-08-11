using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Base;
using UnsecuredAPIKeys.Providers.Common;

namespace UnsecuredAPIKeys.Providers.AI_Providers
{
    /// <summary>
    /// Provider for Mistral AI API keys.
    ///
    /// Key format: alphanumeric string, no fixed prefix. Length is not guaranteed to be exactly 32
    /// characters — Mistral may change key formats; we accept ≥ 20 alphanumeric characters.
    ///
    /// Auth: Authorization: Bearer {key}
    ///
    /// Verification: GET https://api.mistral.ai/v1/models
    ///   — officially documented, read-only, authenticated endpoint.
    ///   — does NOT consume any inference quota.
    ///   — 200 → valid key; response body contains { "data": [...models...] }
    ///   — 401 → invalid or expired key
    ///   — 403 → key may be valid but access restricted (plan/permission)
    ///   — 429 → rate-limited; key validity cannot be determined
    ///   — 5xx → Mistral service unavailable
    ///
    /// Official docs: https://docs.mistral.ai/api/endpoint/models
    /// </summary>
    [ApiProvider]
    public class MistralProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "Mistral AI";
        public override ApiTypeEnum ApiType => ApiTypeEnum.MistralAI;

        public override IEnumerable<string> RegexPatterns =>
        [
            // Context-anchored patterns — require a Mistral-related variable name to avoid
            // false positives from generic alphanumeric strings in unrelated codebases
            @"(?i)\bMISTRAL[\s_-]*API[\s_-]*KEY\s*[:=]\s*['""]?([A-Za-z0-9]{20,})['""]?",
            @"(?i)\bMISTRAL[\s_-]*KEY\s*[:=]\s*['""]?([A-Za-z0-9]{20,})['""]?",
            @"(?i)\bMISTRAL[\s_-]*SECRET\s*[:=]\s*['""]?([A-Za-z0-9]{20,})['""]?",
        ];

        public MistralProvider() : base() { }
        public MistralProvider(ILogger<MistralProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(
            string apiKey, HttpClient httpClient)
        {
            try
            {
                // GET /v1/models — officially documented, authenticated, read-only endpoint.
                // Returns the list of available Mistral models without consuming any inference quota.
                // Ref: https://docs.mistral.ai/api/endpoint/models
                using var modelsRequest = new HttpRequestMessage(
                    HttpMethod.Get, "https://api.mistral.ai/v1/models");
                modelsRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                modelsRequest.Headers.Accept.Add(
                    new MediaTypeWithQualityHeaderValue("application/json"));

                var modelsResponse = await httpClient.SendAsync(modelsRequest);
                var modelsBody = await modelsResponse.Content.ReadAsStringAsync();

                _logger?.LogDebug("Mistral models response: Status={Status}, Body={Body}",
                    modelsResponse.StatusCode, TruncateResponse(modelsBody));

                // 401 — invalid or expired key
                if (modelsResponse.StatusCode == HttpStatusCode.Unauthorized)
                {
                    return ValidationResult.IsUnauthorized(modelsResponse.StatusCode,
                        "Invalid or expired Mistral API key");
                }

                // 403 — key may be valid but access restricted; conclusive determination unavailable
                if (modelsResponse.StatusCode == HttpStatusCode.Forbidden)
                {
                    return ValidationResult.ValidationUnavailable(modelsResponse.StatusCode,
                        "Mistral API returned 403 Forbidden — permission or plan restriction; key validity could not be conclusively determined");
                }

                // 429 — rate limited; key validity cannot be determined
                if (modelsResponse.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    return ValidationResult.ValidationUnavailable(modelsResponse.StatusCode,
                        "Mistral API rate limit exceeded (429) — key validity could not be determined");
                }

                // 5xx — Mistral service unavailable
                if ((int)modelsResponse.StatusCode >= 500)
                {
                    return ValidationResult.ValidationUnavailable(modelsResponse.StatusCode,
                        $"Mistral service error ({modelsResponse.StatusCode}) — validation unavailable");
                }

                if (!IsSuccessStatusCode(modelsResponse.StatusCode))
                {
                    return ValidationResult.HasHttpError(modelsResponse.StatusCode,
                        $"Unexpected status from Mistral models endpoint: {TruncateResponse(modelsBody)}");
                }

                // 200 — valid key; parse available models
                var models = ParseModels(modelsBody);

                var result = ValidationResult.Success(modelsResponse.StatusCode, "Valid Mistral AI key");
                result.AvailableModels = models;
                result.Metadata = new Dictionary<string, object>
                {
                    ["authentication_valid"] = true,
                    ["models_parsed"] = models != null,
                    ["model_count"] = models?.Count ?? 0
                };
                result.RawResponse = modelsBody;
                return result;
            }
            catch (Exception ex)
            {
                return ValidationResult.HasNetworkError(ex.Message);
            }
        }

        private List<ModelInfo>? ParseModels(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("data", out var data)) return null;

                var list = new List<ModelInfo>();
                foreach (var el in data.EnumerateArray())
                {
                    var id = el.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
                    if (!string.IsNullOrEmpty(id))
                        list.Add(new ModelInfo { ModelId = id, DisplayName = id });
                }
                // Return list even if empty — empty model list is a valid authenticated response;
                // null means parsing itself failed, which is distinct from "zero models returned".
                return list;
            }
            catch { return null; }
        }

        protected override bool IsValidKeyFormat(string apiKey)
        {
            // Mistral keys are alphanumeric with no fixed length guarantee.
            // We accept any alphanumeric string of at least 20 characters and let the API
            // determine actual validity — avoids rejecting keys if Mistral changes its format.
            return !string.IsNullOrWhiteSpace(apiKey) &&
                   apiKey.Length >= 20 &&
                   apiKey.All(char.IsLetterOrDigit);
        }
    }
}

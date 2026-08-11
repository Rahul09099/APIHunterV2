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
    /// Provider for Perplexity AI (Sonar) API keys.
    /// Keys always start with "pplx-".
    /// Verification: GET /v1/models (authentication check + model discovery) followed by
    /// POST /chat/completions with model "sonar" (minimal active inference test).
    /// Official docs: https://docs.perplexity.ai/api-reference/sonar-post
    /// </summary>
    [ApiProvider]
    public class PerplexityProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "Perplexity";
        public override ApiTypeEnum ApiType => ApiTypeEnum.Perplexity;

        public override IEnumerable<string> RegexPatterns =>
        [
            @"\bpplx-[A-Za-z0-9]{40,60}\b",
            @"PERPLEXITY_API_KEY",
            @"perplexity[_-]?api[_-]?key",
            @"PPLX_API_KEY"
        ];

        public PerplexityProvider() : base() { }
        public PerplexityProvider(ILogger<PerplexityProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(
            string apiKey, HttpClient httpClient)
        {
            // Step 1: GET /v1/models — authentication check + model discovery (no generation cost)
            using var modelsRequest = new HttpRequestMessage(
                HttpMethod.Get, "https://api.perplexity.ai/v1/models");
            modelsRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            var modelsResponse = await httpClient.SendAsync(modelsRequest);
            var modelsBody = await modelsResponse.Content.ReadAsStringAsync();

            _logger?.LogDebug("Perplexity models response: Status={Status}, Body={Body}",
                modelsResponse.StatusCode, TruncateResponse(modelsBody));

            // 401 -> invalid or expired key
            if (modelsResponse.StatusCode == HttpStatusCode.Unauthorized)
            {
                return ValidationResult.IsUnauthorized(modelsResponse.StatusCode,
                    "Invalid or expired Perplexity API key");
            }

            // 403 / 429 / 5xx -> validation unavailable at models step
            if (modelsResponse.StatusCode == HttpStatusCode.Forbidden)
            {
                return ValidationResult.ValidationUnavailable(modelsResponse.StatusCode,
                    "Perplexity API key access forbidden (403)");
            }

            if (modelsResponse.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return ValidationResult.ValidationUnavailable(modelsResponse.StatusCode,
                    "Perplexity models endpoint rate limited (429)");
            }

            if ((int)modelsResponse.StatusCode >= 500)
            {
                return ValidationResult.ValidationUnavailable(modelsResponse.StatusCode,
                    $"Perplexity service error ({modelsResponse.StatusCode}) — validation unavailable");
            }

            if (!modelsResponse.IsSuccessStatusCode)
            {
                return ValidationResult.HasHttpError(modelsResponse.StatusCode,
                    $"Models check failed: {TruncateResponse(modelsBody)}");
            }

            // Authentication confirmed
            List<ModelInfo>? availableModels = null;
            try
            {
                using var doc = JsonDocument.Parse(modelsBody);
                if (doc.RootElement.TryGetProperty("data", out var dataArr) && dataArr.ValueKind == JsonValueKind.Array)
                {
                    availableModels = dataArr.EnumerateArray()
                        .Select(m => new ModelInfo { ModelId = m.GetProperty("id").GetString() ?? "" })
                        .Where(m => !string.IsNullOrEmpty(m.ModelId))
                        .ToList();
                }
            }
            catch { /* Best effort model discovery */ }

            var result = ValidationResult.Success(modelsResponse.StatusCode, "Valid Perplexity key");
            result.AvailableModels = availableModels;
            result.RawResponse = modelsBody;
            result.Metadata = new Dictionary<string, object>
            {
                ["authentication_valid"] = true,
                ["models_parsed"] = availableModels != null,
                ["model_count"] = availableModels?.Count ?? 0
            };

            // Select a suitable chat model from discovered models
            string? modelToUse = null;
            if (availableModels != null && availableModels.Count > 0)
            {
                var preferred = new[] { "sonar", "sonar-pro", "llama-3.1-8b-instruct", "sonar-reasoning" };
                modelToUse = availableModels
                    .Select(m => m.ModelId)
                    .FirstOrDefault(id => preferred.Any(p => id.Equals(p, StringComparison.OrdinalIgnoreCase) ||
                                                             id.Equals($"perplexity/{p}", StringComparison.OrdinalIgnoreCase)));
            }

            if (string.IsNullOrEmpty(modelToUse))
            {
                // If no recognized chat model is in the catalog, do not send a random doomed request.
                // Report authentication confirmed cleanly without active inference test.
                result.Metadata["inference_tested"] = false;
                result.Detail = "Valid Perplexity key — authenticated (no suitable chat model available for inference test)";
                return result;
            }

            // Step 2: Minimal active inference test (POST /chat/completions)
            try
            {
                using var chatRequest = new HttpRequestMessage(
                    HttpMethod.Post, "https://api.perplexity.ai/chat/completions");
                chatRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                chatRequest.Content = new StringContent(
                    JsonSerializer.Serialize(new
                    {
                        model = modelToUse,
                        messages = new[] { new { role = "user", content = "hi" } },
                        max_tokens = 1
                    }),
                    System.Text.Encoding.UTF8, "application/json");

                var response = await httpClient.SendAsync(chatRequest);
                var body = await response.Content.ReadAsStringAsync();

                _logger?.LogDebug("Perplexity chat response ({Model}): Status={Status}, Body={Body}",
                    modelToUse, response.StatusCode, TruncateResponse(body));

                result.RawResponse = body;
                result.Metadata["inference_tested"] = true;
                result.Metadata["tested_model"] = modelToUse;

                // Capture rate-limit headers if present
                if (response.Headers.TryGetValues("x-ratelimit-limit", out var limits) ||
                    response.Headers.TryGetValues("x-ratelimit-limit-requests", out limits))
                {
                    var limitVal = limits.FirstOrDefault();
                    if (limitVal != null) result.Metadata["rate_limit"] = limitVal;
                }

                if (response.Headers.TryGetValues("x-ratelimit-remaining", out var remainings) ||
                    response.Headers.TryGetValues("x-ratelimit-remaining-requests", out remainings))
                {
                    var remVal = remainings.FirstOrDefault();
                    if (remVal != null) result.Metadata["rate_limit_remaining"] = remVal;
                }

                if (IsSuccessStatusCode(response.StatusCode))
                {
                    result.Metadata["inference_working"] = true;
                    result.Detail = $"Valid Perplexity key — Chat completions verified with model '{modelToUse}'.";

                    try
                    {
                        using var doc = JsonDocument.Parse(body);
                        if (doc.RootElement.TryGetProperty("usage", out var usage) &&
                            usage.TryGetProperty("cost", out var cost))
                        {
                            result.Metadata["last_request_cost"] = cost.GetRawText();
                        }
                    }
                    catch { }
                }
                else if ((int)response.StatusCode == 402 || ContainsAny(body, QuotaIndicators))
                {
                    result.Metadata["inference_working"] = false;
                    result.IsQuotaExceeded = true;
                    result.Detail = $"Valid Perplexity key — quota or billing limit exceeded: {TruncateResponse(body)}";
                }
                else if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    result.Metadata["inference_working"] = false;
                    result.Detail = $"Valid Perplexity key — inference rate limited (429) on model '{modelToUse}'.";
                }
                else
                {
                    // Note: Auth succeeded at Step 1, so 401/403 at step 2 indicates operation/model permission restriction.
                    result.Metadata["inference_working"] = false;
                    result.Detail = $"Valid Perplexity key — authenticated, but inference request returned status {response.StatusCode}.";
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning("Perplexity chat completion test failed with exception: {Message}", ex.Message);
                result.Metadata["inference_tested"] = false;
                result.Metadata["inference_working"] = false;
            }

            return result;
        }

        protected override bool IsValidKeyFormat(string apiKey) =>
            !string.IsNullOrWhiteSpace(apiKey) &&
            apiKey.StartsWith("pplx-", StringComparison.Ordinal) &&
            apiKey.Length >= 45;
    }
}

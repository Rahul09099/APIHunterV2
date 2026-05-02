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
    /// Verification: POST /chat/completions with model "sonar" (cheapest, $1/1M tokens).
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
            // Step 1: GET /v1/models — confirms key is accepted and gets available models
            using var modelsRequest = new HttpRequestMessage(
                HttpMethod.Get, "https://api.perplexity.ai/v1/models");
            modelsRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            var modelsResponse = await httpClient.SendAsync(modelsRequest);
            var modelsBody = await modelsResponse.Content.ReadAsStringAsync();

            _logger?.LogDebug("Perplexity models response: Status={Status}, Body={Body}",
                modelsResponse.StatusCode, TruncateResponse(modelsBody));

            if (modelsResponse.StatusCode == HttpStatusCode.Unauthorized ||
                modelsResponse.StatusCode == HttpStatusCode.Forbidden)
            {
                return ValidationResult.IsUnauthorized(modelsResponse.StatusCode);
            }

            List<ModelInfo>? availableModels = null;
            if (IsSuccessStatusCode(modelsResponse.StatusCode))
            {
                try
                {
                    using var doc = JsonDocument.Parse(modelsBody);
                    if (doc.RootElement.TryGetProperty("data", out var dataArr) && dataArr.ValueKind == JsonValueKind.Array)
                    {
                        availableModels = dataArr.EnumerateArray()
                            .Select(m => new ModelInfo { ModelId = m.GetProperty("id").GetString() ?? "" })
                            .ToList();
                    }
                }
                catch { /* Ignore parsing errors for models */ }
            }

            // Step 2: Try a minimal chat call to confirm generation and capture usage/cost
            // sonar is cheapest ($1/1M tokens + $0.008/request search fee)
            string modelToUse = "sonar";
            if (availableModels != null && availableModels.Any())
            {
                var preferred = new[] { "sonar", "sonar-pro", "llama-3.1-8b-instruct" };
                foreach (var p in preferred)
                {
                    if (availableModels.Any(m => m.ModelId == p || m.ModelId == $"perplexity/{p}"))
                    {
                        modelToUse = p;
                        break;
                    }
                }
                if (modelToUse == "sonar" && !availableModels.Any(m => m.ModelId == "sonar" || m.ModelId == "perplexity/sonar"))
                {
                    modelToUse = availableModels.First().ModelId;
                }
            }
            
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

            _logger?.LogDebug("Perplexity chat response: Status={Status}, Body={Body}",
                response.StatusCode, TruncateResponse(body));

            // Extract metadata from headers and body
            var metadata = new Dictionary<string, object>();
            string? tier = null;
            
            // Perplexity rate limit headers (if present)
            if (response.Headers.TryGetValues("x-ratelimit-limit", out var limits) || 
                response.Headers.TryGetValues("x-ratelimit-limit-requests", out limits))
            {
                var limit = limits.FirstOrDefault();
                if (limit != null)
                {
                    metadata["limit"] = limit;
                    if (int.TryParse(limit, out int lVal))
                    {
                        tier = lVal switch
                        {
                            <= 50 => "Tier 0",
                            <= 150 => "Tier 1",
                            <= 500 => "Tier 2",
                            <= 1000 => "Tier 3",
                            _ => "Tier 4+"
                        };
                    }
                }
            }
            
            if (response.Headers.TryGetValues("x-ratelimit-remaining", out var remainings) ||
                response.Headers.TryGetValues("x-ratelimit-remaining-requests", out remainings))
            {
                var rem = remainings.FirstOrDefault();
                if (rem != null) metadata["remaining"] = rem;
            }

            if (IsSuccessStatusCode(response.StatusCode))
            {
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("usage", out var usage) && 
                        usage.TryGetProperty("cost", out var cost))
                    {
                        metadata["last_request_cost"] = cost.GetRawText();
                    }
                }
                catch { }

                return new ValidationResult
                {
                    Status = ValidationAttemptStatus.Valid,
                    HttpStatusCode = response.StatusCode,
                    Detail = "Active Perplexity key",
                    AvailableModels = availableModels,
                    AccountTier = tier,
                    Metadata = metadata,
                    RawResponse = body
                };
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized ||
                response.StatusCode == HttpStatusCode.Forbidden)
                return ValidationResult.IsUnauthorized(response.StatusCode);

            if ((int)response.StatusCode == 429)
            {
                return new ValidationResult
                {
                    Status = ValidationAttemptStatus.HttpError,
                    IsQuotaExceeded = true,
                    HttpStatusCode = response.StatusCode,
                    Detail = "quota exhausted",
                    AvailableModels = availableModels,
                    AccountTier = tier,
                    Metadata = metadata,
                    RawResponse = body
                };
            }

            if (ContainsAny(body, QuotaIndicators))
            {
                 return new ValidationResult
                {
                    Status = ValidationAttemptStatus.HttpError,
                    IsQuotaExceeded = true,
                    HttpStatusCode = response.StatusCode,
                    Detail = $"Valid key but quota issue: {TruncateResponse(body)}",
                    AvailableModels = availableModels,
                    AccountTier = tier,
                    Metadata = metadata,
                    RawResponse = body
                };
            }

            // If models worked but chat failed
            if (IsSuccessStatusCode(modelsResponse.StatusCode))
            {
                 return new ValidationResult
                {
                    Status = ValidationAttemptStatus.Valid,
                    HttpStatusCode = response.StatusCode,
                    Detail = $"Valid key but chat failed ({response.StatusCode}): {TruncateResponse(body)}",
                    AvailableModels = availableModels,
                    AccountTier = tier,
                    Metadata = metadata,
                    RawResponse = body
                };
            }

            return ValidationResult.HasHttpError(response.StatusCode, $"API request failed: {TruncateResponse(body)}");
        }

        protected override bool IsValidKeyFormat(string apiKey) =>
            !string.IsNullOrWhiteSpace(apiKey) &&
            apiKey.StartsWith("pplx-", StringComparison.Ordinal) &&
            apiKey.Length >= 45;
    }
}

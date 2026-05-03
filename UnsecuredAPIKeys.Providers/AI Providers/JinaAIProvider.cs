using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
using System.Text;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Base;
using UnsecuredAPIKeys.Providers.Common;

namespace UnsecuredAPIKeys.Providers.AI_Providers
{
    /// <summary>
    /// Provider for Jina AI API keys — embeddings, rerankers, and search foundation models.
    /// Acquired by Elastic in October 2025. API at api.jina.ai still active.
    ///
    /// Key format: jina_{alphanumeric}
    ///   - Confirmed prefix "jina_" from official docs, Qdrant example: "jina_xxxxxxxxxxx"
    ///   - GitGuardian confirms: Prefixed=True, High recall=True
    ///   - Typical length: jina_ (5) + ~40-60 alphanumeric chars
    ///
    /// Auth: Authorization: Bearer {apiKey}
    ///   Confirmed from Qdrant docs: headers = {"Authorization": f"Bearer {JINA_API_KEY}"}
    ///
    /// Verification: POST https://api.jina.ai/v1/embeddings
    ///   - Uses jina-embeddings-v4 (latest model, confirmed from Qdrant docs May 2025)
    ///   - Minimal input: ["test"] — costs ~1 token
    ///   - DEFINITELY requires auth — 401 without valid key
    ///   - Valid response: 200 { "model": "...", "data": [...], "usage": { "prompt_tokens": N, "total_tokens": N } }
    ///   - Invalid key: 401 Unauthorized
    ///   - Quota exhausted: 402 Payment Required (key is valid but no tokens left)
    ///   - Rate limited: 429 (key is valid)
    ///
    /// Balance: Token-based prepaid model. No balance API endpoint.
    ///   Free tier: 1M tokens on signup. Top-up available at jina.ai/embeddings.
    /// </summary>
    [ApiProvider]
    public class JinaAIProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "Jina AI";
        public override ApiTypeEnum ApiType => ApiTypeEnum.JinaAI;

        public override IEnumerable<string> RegexPatterns =>
        [
            // Primary pattern — confirmed jina_ prefix, flexible length (20+ after prefix)
            @"jina_[A-Za-z0-9]{20,}",

            // Environment variable names — most common leak pattern
            @"JINA_API_KEY",
            @"JINA_AI_API_KEY",

            // Context-aware value extraction patterns
            @"JINA_API_KEY\s*[=:]\s*['""]?(jina_[A-Za-z0-9]{20,})['""]?",
            @"JINA_AI_API_KEY\s*[=:]\s*['""]?(jina_[A-Za-z0-9]{20,})['""]?"
        ];

        public JinaAIProvider() : base() { }
        public JinaAIProvider(ILogger<JinaAIProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(string apiKey, HttpClient httpClient)
        {
            try
            {
                // POST /v1/embeddings — DEFINITELY requires auth (confirmed from multiple sources)
                // Using jina-embeddings-v4 (latest model as of May 2025, per Qdrant docs)
                // Minimal input ["test"] costs ~1 token — negligible cost
                const string body = """{"model":"jina-embeddings-v4","input":["test"]}""";

                using var request = new HttpRequestMessage(HttpMethod.Post,
                    "https://api.jina.ai/v1/embeddings");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");

                var response = await httpClient.SendAsync(request);
                string responseBody = await response.Content.ReadAsStringAsync();

                _logger?.LogDebug("Jina AI embeddings response: Status={StatusCode}, Body={Body}",
                    response.StatusCode, TruncateResponse(responseBody));

                if (IsSuccessStatusCode(response.StatusCode))
                {
                    var result = ValidationResult.Success(response.StatusCode, "Valid Jina AI key");

                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(responseBody);
                        // OpenAI-compatible response:
                        // { "model": "jina-embeddings-v4", "data": [...], "usage": { "prompt_tokens": 1, "total_tokens": 1 } }
                        if (doc.RootElement.TryGetProperty("usage", out var usage))
                        {
                            // Try total_tokens first, fall back to prompt_tokens
                            int tokenCount = 0;
                            if (usage.TryGetProperty("total_tokens", out var total))
                                tokenCount = total.GetInt32();
                            else if (usage.TryGetProperty("prompt_tokens", out var prompt))
                                tokenCount = prompt.GetInt32();

                            result.Detail = tokenCount > 0
                                ? $"Valid Jina AI key — embeddings working ({tokenCount} token(s) used)"
                                : "Valid Jina AI key — embeddings accessible";
                        }
                        else if (doc.RootElement.TryGetProperty("data", out var data))
                        {
                            result.Detail = $"Valid Jina AI key — {data.GetArrayLength()} embedding(s) returned";
                        }
                        else
                        {
                            result.Detail = "Valid Jina AI key";
                        }

                        // No balance API — token balance only visible in dashboard
                        result.Balance = "N/A (check jina.ai dashboard)";
                    }
                    catch { result.Detail = "Valid Jina AI key"; }

                    return result;
                }

                // 402 = valid key but token quota exhausted
                if ((int)response.StatusCode == 402)
                {
                    var result = ValidationResult.Success(response.StatusCode,
                        "Valid Jina AI key — token quota exhausted");
                    result.IsQuotaExceeded = true;
                    result.Balance = "0 tokens remaining — top up at jina.ai/embeddings";
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
            // Jina keys always start with jina_ — confirmed from official docs and examples
            // Minimum total length: jina_ (5) + 20 chars = 25
            return !string.IsNullOrWhiteSpace(apiKey) &&
                   apiKey.StartsWith("jina_") &&
                   apiKey.Length >= 25;
        }
    }
}

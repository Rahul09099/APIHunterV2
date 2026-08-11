using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Base;
using UnsecuredAPIKeys.Providers.Common;

namespace UnsecuredAPIKeys.Providers.AI_Providers
{
    /// <summary>
    /// Provider for Jina AI API keys — embeddings, rerankers, and search foundation models.
    /// Key format: jina_{alphanumeric}
    /// Auth: Authorization: Bearer {apiKey}
    /// Verification: POST https://api.jina.ai/v1/embeddings
    ///   - Uses jina-embeddings-v4 with minimal input ["test"]
    /// Token billing: 10M free tokens on signup (per official docs). Top-up available at jina.ai.
    /// Docs: https://api.jina.ai/docs
    /// </summary>
    [ApiProvider]
    public class JinaAIProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "Jina AI";
        public override ApiTypeEnum ApiType => ApiTypeEnum.JinaAI;

        public override IEnumerable<string> RegexPatterns =>
        [
            @"\bjina_[A-Za-z0-9]{20,}\b",
            @"JINA_API_KEY\s*[=:]\s*['""]?(jina_[A-Za-z0-9]{20,})['""]?",
            @"JINA_AI_API_KEY\s*[=:]\s*['""]?(jina_[A-Za-z0-9]{20,})['""]?"
        ];

        public JinaAIProvider() : base() { }
        public JinaAIProvider(ILogger<JinaAIProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(string apiKey, HttpClient httpClient)
        {
            try
            {
                const string body = """{"model":"jina-embeddings-v4","input":["test"]}""";

                using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.jina.ai/v1/embeddings");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");

                var response = await httpClient.SendAsync(request);
                string responseBody = await response.Content.ReadAsStringAsync();

                _logger?.LogDebug("Jina AI embeddings response: Status={StatusCode}, Body={Body}",
                    response.StatusCode, TruncateResponse(responseBody));

                ValidationResult result;

                if (IsSuccessStatusCode(response.StatusCode))
                {
                    result = ValidationResult.Success(response.StatusCode, "Valid Jina AI key");
                    result.Metadata = new Dictionary<string, object>
                    {
                        ["authentication_valid"] = true,
                        ["api_operation_tested"] = true,
                        ["api_operation_working"] = true,
                        ["operation"] = "embeddings",
                        ["tested_model"] = "jina-embeddings-v4"
                    };

                    try
                    {
                        using var doc = JsonDocument.Parse(responseBody);
                        if (doc.RootElement.TryGetProperty("usage", out var usage))
                        {
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
                    }
                    catch
                    {
                        result.Detail = "Valid Jina AI key";
                    }

                    result.Balance = "N/A (check Jina AI billing/dashboard)";
                    result.RawResponse = responseBody;
                    return result;
                }

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    result = ValidationResult.IsUnauthorized(response.StatusCode, "Invalid Jina AI API key");
                    result.RawResponse = responseBody;
                    return result;
                }

                if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    if (responseBody.Contains("AUTHZ_INSUFFICIENT_BALANCE", StringComparison.OrdinalIgnoreCase))
                    {
                        result = ValidationResult.Success(response.StatusCode, "Valid Jina AI key — insufficient account balance");
                        result.IsQuotaExceeded = true;
                        result.Balance = "Insufficient balance";
                        result.Metadata = new Dictionary<string, object>
                        {
                            ["authentication_valid"] = true,
                            ["api_operation_tested"] = true,
                            ["api_operation_working"] = false,
                            ["operation"] = "embeddings",
                            ["tested_model"] = "jina-embeddings-v4"
                        };
                        result.RawResponse = responseBody;
                        return result;
                    }

                    if (responseBody.Contains("AUTHZ_RESOURCE_LIMIT_EXCEEDED", StringComparison.OrdinalIgnoreCase))
                    {
                        result = new ValidationResult
                        {
                            Status = ValidationAttemptStatus.ValidationUnavailable,
                            HttpStatusCode = response.StatusCode,
                            Detail = "Jina AI resource limit exceeded; key validity could not be determined."
                        };
                        result.RawResponse = responseBody;
                        return result;
                    }

                    result = new ValidationResult
                    {
                        Status = ValidationAttemptStatus.ValidationUnavailable,
                        HttpStatusCode = response.StatusCode,
                        Detail = "Jina AI request forbidden; key validity could not be conclusively determined."
                    };
                    result.RawResponse = responseBody;
                    return result;
                }

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    result = new ValidationResult
                    {
                        Status = ValidationAttemptStatus.ValidationUnavailable,
                        HttpStatusCode = response.StatusCode,
                        Detail = "Jina AI rate limit exceeded."
                    };
                    result.RawResponse = responseBody;
                    return result;
                }

                if ((int)response.StatusCode >= 500)
                {
                    result = new ValidationResult
                    {
                        Status = ValidationAttemptStatus.ValidationUnavailable,
                        HttpStatusCode = response.StatusCode,
                        Detail = $"Jina AI service unavailable (HTTP {(int)response.StatusCode})"
                    };
                    result.RawResponse = responseBody;
                    return result;
                }

                result = ValidationResult.HasHttpError(response.StatusCode,
                    $"Jina AI request failed: {TruncateResponse(responseBody)}");
                result.RawResponse = responseBody;
                return result;
            }
            catch (Exception ex)
            {
                return ValidationResult.HasNetworkError(ex.Message);
            }
        }

        protected override bool IsValidKeyFormat(string apiKey)
        {
            return !string.IsNullOrWhiteSpace(apiKey) &&
                   apiKey.StartsWith("jina_", StringComparison.Ordinal) &&
                   apiKey.Length >= 25;
        }
    }
}

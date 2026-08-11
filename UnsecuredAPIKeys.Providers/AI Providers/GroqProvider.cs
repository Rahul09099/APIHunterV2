using System;
using System.Collections.Generic;
using System.Linq;
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
    /// Provider for Groq API keys.
    /// Groq uses OpenAI-compatible endpoints at api.groq.com/openai/v1.
    /// Keys start with "gsk_".
    /// 2-step verification strategy:
    ///   1. GET /openai/v1/models (authenticates credential)
    ///   2. POST /openai/v1/chat/completions (verifies live inference capability)
    /// </summary>
    [ApiProvider]
    public class GroqProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "Groq";
        public override ApiTypeEnum ApiType => ApiTypeEnum.Groq;

        public override IEnumerable<string> RegexPatterns =>
        [
            @"\bgsk_[A-Za-z0-9_-]{16,256}\b",
            @"GROQ_API_KEY\s*[:=]\s*['""]?(gsk_[A-Za-z0-9_-]{16,256})['""]?",
            @"GROQ_KEY\s*[:=]\s*['""]?(gsk_[A-Za-z0-9_-]{16,256})['""]?",
            @"groq[_-]?api[_-]?key\s*[:=]\s*['""]?(gsk_[A-Za-z0-9_-]{16,256})['""]?"
        ];

        public GroqProvider() : base() { }
        public GroqProvider(ILogger<GroqProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(
            string apiKey, HttpClient httpClient)
        {
            try
            {
                // Step 1: List models — authenticates credential
                using var modelsRequest = new HttpRequestMessage(
                    HttpMethod.Get, "https://api.groq.com/openai/v1/models");
                modelsRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

                var modelsResponse = await httpClient.SendAsync(modelsRequest);
                var modelsBody = await modelsResponse.Content.ReadAsStringAsync();

                _logger?.LogDebug("Groq models response: Status={Status}, Body={Body}",
                    modelsResponse.StatusCode, TruncateResponse(modelsBody));

                if (modelsResponse.StatusCode == HttpStatusCode.Unauthorized)
                {
                    var unauth = ValidationResult.IsUnauthorized(modelsResponse.StatusCode, "Invalid or expired Groq API key");
                    unauth.RawResponse = modelsBody;
                    return unauth;
                }

                if (modelsResponse.StatusCode == HttpStatusCode.Forbidden)
                {
                    var restricted = ValidationResult.ValidationUnavailable(
                        modelsResponse.StatusCode,
                        "Groq API access forbidden — organization, project, or permission restriction.");
                    restricted.RawResponse = modelsBody;
                    return restricted;
                }

                if (!modelsResponse.IsSuccessStatusCode)
                {
                    var err = ValidationResult.HasHttpError(modelsResponse.StatusCode,
                        $"Models listing failed: {TruncateResponse(modelsBody)}");
                    err.RawResponse = modelsBody;
                    return err;
                }

                var models = ParseModels(modelsBody);

                // Step 2: Select dedicated validation model from official production catalog
                var validationModels = new[]
                {
                    "llama-3.3-70b-versatile",
                    "llama-3.1-8b-instant",
                    "openai/gpt-oss-20b",
                    "openai/gpt-oss-120b"
                };

                var modelToUse = models?
                    .Select(m => m.ModelId)
                    .FirstOrDefault(id => validationModels.Contains(id, StringComparer.OrdinalIgnoreCase));

                if (string.IsNullOrEmpty(modelToUse))
                {
                    var resultAuthOnly = ValidationResult.Success(
                        modelsResponse.StatusCode, 
                        "Valid Groq key — models endpoint authenticated, but no compatible validation model is available.");
                    resultAuthOnly.AvailableModels = models;
                    resultAuthOnly.Metadata = new Dictionary<string, object>
                    {
                        ["authentication_valid"] = true,
                        ["models_parsed"] = models != null,
                        ["inference_tested"] = false
                    };
                    resultAuthOnly.RawResponse = modelsBody;
                    return resultAuthOnly;
                }

                using var chatRequest = new HttpRequestMessage(
                    HttpMethod.Post, "https://api.groq.com/openai/v1/chat/completions");
                chatRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                chatRequest.Content = new StringContent(
                    JsonSerializer.Serialize(new
                    {
                        model = modelToUse,
                        messages = new[] { new { role = "user", content = "Hi" } },
                        max_completion_tokens = 1
                    }),
                    Encoding.UTF8, "application/json");

                var chatResponse = await httpClient.SendAsync(chatRequest);
                var chatBody = await chatResponse.Content.ReadAsStringAsync();
                string bodyLower = chatBody.ToLowerInvariant();

                _logger?.LogDebug("Groq chat response ({Model}): Status={Status}, Body={Body}",
                    modelToUse, chatResponse.StatusCode, TruncateResponse(chatBody));

                ValidationResult result;

                if (IsSuccessStatusCode(chatResponse.StatusCode))
                {
                    result = ValidationResult.Success(chatResponse.StatusCode, models);
                    result.AvailableModels = models;
                    result.Detail = "Valid Groq key — live inference successful";
                    var meta = new Dictionary<string, object>
                    {
                        ["authentication_valid"] = true,
                        ["inference_tested"] = true,
                        ["inference_working"] = true,
                        ["tested_model"] = modelToUse
                    };

                    ExtractRateLimitMetadata(chatResponse, meta, out string? rateLimitSummary);
                    if (string.IsNullOrEmpty(rateLimitSummary))
                    {
                        ExtractRateLimitMetadata(modelsResponse, meta, out rateLimitSummary);
                    }

                    if (!string.IsNullOrEmpty(rateLimitSummary))
                    {
                        meta["rate_limit_summary"] = rateLimitSummary;
                    }

                    result.Metadata = meta;
                    result.RawResponse = chatBody;
                    return result;
                }

                if (chatResponse.StatusCode == HttpStatusCode.Forbidden)
                {
                    // 403 during chat inference is model/organization access restriction, NOT invalid key
                    result = ValidationResult.Success(chatResponse.StatusCode, "Valid Groq key (chat access restricted)");
                    result.AvailableModels = models;
                    result.Detail = "Key is valid but chat endpoint returned 403 Forbidden — model or access restriction.";
                    result.Metadata = new Dictionary<string, object>
                    {
                        ["authentication_valid"] = true,
                        ["inference_tested"] = true,
                        ["inference_working"] = false,
                        ["access_restricted"] = true,
                        ["tested_model"] = modelToUse
                    };
                    result.RawResponse = chatBody;
                    return result;
                }

                if (chatResponse.StatusCode == HttpStatusCode.Unauthorized)
                {
                    result = ValidationResult.IsUnauthorized(chatResponse.StatusCode, "Groq authentication failed during chat inference");
                    result.Metadata = new Dictionary<string, object>
                    {
                        ["authentication_valid"] = false,
                        ["inference_tested"] = true,
                        ["inference_working"] = false,
                        ["tested_model"] = modelToUse
                    };
                    result.RawResponse = chatBody;
                    return result;
                }

                if ((int)chatResponse.StatusCode == 429)
                {
                    string? retryAfter = GetHeaderValue(chatResponse, "Retry-After");
                    string? resetRequests = GetHeaderValue(chatResponse, "x-ratelimit-reset-requests");
                    bool isQuotaExceeded = bodyLower.Contains("quota exceeded") || bodyLower.Contains("quota_exceeded") || bodyLower.Contains("insufficient quota");
                    
                    string detailMsg = isQuotaExceeded 
                        ? "Valid Groq key; account quota exhausted" 
                        : $"Valid Groq key; rate limited (Retry-After: {retryAfter ?? resetRequests ?? "N/A"})";

                    result = ValidationResult.Success(chatResponse.StatusCode, detailMsg);
                    result.IsQuotaExceeded = isQuotaExceeded;
                    result.AvailableModels = models;

                    var meta = new Dictionary<string, object>
                    {
                        ["authentication_valid"] = true,
                        ["inference_tested"] = true,
                        ["inference_working"] = false,
                        ["rate_limited"] = !isQuotaExceeded,
                        ["quota_exhausted"] = isQuotaExceeded,
                        ["tested_model"] = modelToUse
                    };

                    if (!string.IsNullOrEmpty(retryAfter)) meta["retry_after"] = retryAfter;
                    if (!string.IsNullOrEmpty(resetRequests)) meta["ratelimit_reset_requests"] = resetRequests;

                    ExtractRateLimitMetadata(chatResponse, meta, out string? rateLimitSummary);
                    if (!string.IsNullOrEmpty(rateLimitSummary))
                    {
                        meta["rate_limit_summary"] = rateLimitSummary;
                    }

                    result.Metadata = meta;
                    result.RawResponse = chatBody;
                    return result;
                }

                bool isQuotaIssue = bodyLower.Contains("quota") || bodyLower.Contains("insufficient");
                if (isQuotaIssue)
                {
                    result = ValidationResult.Success(
                        chatResponse.StatusCode,
                        $"Valid key but quota issue: {TruncateResponse(chatBody)}");
                    result.IsQuotaExceeded = true;
                    result.AvailableModels = models;
                    result.Metadata = new Dictionary<string, object>
                    {
                        ["authentication_valid"] = true,
                        ["inference_tested"] = true,
                        ["inference_working"] = false,
                        ["tested_model"] = modelToUse
                    };
                    result.RawResponse = chatBody;
                    return result;
                }

                result = ValidationResult.HasHttpError(chatResponse.StatusCode,
                    $"Chat completion failed: {TruncateResponse(chatBody)}");
                result.AvailableModels = models;
                result.Metadata = new Dictionary<string, object>
                {
                    ["authentication_valid"] = true,
                    ["inference_tested"] = true,
                    ["inference_working"] = false,
                    ["tested_model"] = modelToUse
                };
                result.RawResponse = chatBody;
                return result;
            }
            catch (Exception ex)
            {
                return ValidationResult.HasNetworkError(ex.Message);
            }
        }

        private static void ExtractRateLimitMetadata(HttpResponseMessage response, Dictionary<string, object> metadata, out string? rateLimitSummary)
        {
            rateLimitSummary = null;
            try
            {
                string? limitReq = GetHeaderValue(response, "x-ratelimit-limit-requests");
                string? remReq = GetHeaderValue(response, "x-ratelimit-remaining-requests");
                string? limitTok = GetHeaderValue(response, "x-ratelimit-limit-tokens");
                string? remTok = GetHeaderValue(response, "x-ratelimit-remaining-tokens");
                string? resetTok = GetHeaderValue(response, "x-ratelimit-reset-tokens");
                string? resetReq = GetHeaderValue(response, "x-ratelimit-reset-requests");
                string? retryAfter = GetHeaderValue(response, "Retry-After");

                if (!string.IsNullOrEmpty(limitReq)) metadata["ratelimit_limit_requests"] = limitReq;
                if (!string.IsNullOrEmpty(remReq)) metadata["ratelimit_remaining_requests"] = remReq;
                if (!string.IsNullOrEmpty(limitTok)) metadata["ratelimit_limit_tokens"] = limitTok;
                if (!string.IsNullOrEmpty(remTok)) metadata["ratelimit_remaining_tokens"] = remTok;
                if (!string.IsNullOrEmpty(resetTok)) metadata["ratelimit_reset_tokens"] = resetTok;
                if (!string.IsNullOrEmpty(resetReq)) metadata["ratelimit_reset_requests"] = resetReq;
                if (!string.IsNullOrEmpty(retryAfter)) metadata["retry_after"] = retryAfter;

                if (!string.IsNullOrEmpty(remReq) && !string.IsNullOrEmpty(limitReq))
                {
                    rateLimitSummary = $"Rate Limits: {remReq}/{limitReq} Requests";
                    if (!string.IsNullOrEmpty(remTok) && !string.IsNullOrEmpty(limitTok))
                    {
                        rateLimitSummary += $", {remTok}/{limitTok} Tokens Remaining";
                    }
                }
                else if (!string.IsNullOrEmpty(remTok) && !string.IsNullOrEmpty(limitTok))
                {
                    rateLimitSummary = $"Rate Limits: {remTok}/{limitTok} Tokens Remaining";
                }
            }
            catch { /* Best effort */ }
        }

        private static string? GetHeaderValue(HttpResponseMessage response, string headerName)
        {
            if (response.Headers.TryGetValues(headerName, out var values))
            {
                return values.FirstOrDefault();
            }
            return null;
        }

        private List<ModelInfo>? ParseModels(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return null;

                var list = new List<ModelInfo>();
                foreach (var el in data.EnumerateArray())
                {
                    var id = el.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
                    if (!string.IsNullOrEmpty(id))
                    {
                        list.Add(new ModelInfo { ModelId = id, DisplayName = id });
                    }
                }
                return list;
            }
            catch { return null; }
        }

        protected override bool IsValidKeyFormat(string apiKey) =>
            !string.IsNullOrWhiteSpace(apiKey) &&
            apiKey.StartsWith("gsk_", StringComparison.Ordinal) &&
            apiKey.Length >= 20 &&
            apiKey.Length <= 256;
    }
}

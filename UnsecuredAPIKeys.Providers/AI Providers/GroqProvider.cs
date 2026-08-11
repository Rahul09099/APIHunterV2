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
            @"\bgsk_[A-Za-z0-9]{40,60}\b",
            @"GROQ_API_KEY\s*[:=]\s*['""]?(gsk_[A-Za-z0-9]{40,60})['""]?",
            @"GROQ_KEY\s*[:=]\s*['""]?(gsk_[A-Za-z0-9]{40,60})['""]?",
            @"groq[_-]?api[_-]?key\s*[:=]\s*['""]?(gsk_[A-Za-z0-9]{40,60})['""]?"
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

                if (modelsResponse.StatusCode == HttpStatusCode.Unauthorized ||
                    modelsResponse.StatusCode == HttpStatusCode.Forbidden)
                {
                    var unauth = ValidationResult.IsUnauthorized(modelsResponse.StatusCode, "Invalid Groq API key");
                    unauth.RawResponse = modelsBody;
                    return unauth;
                }

                if (!modelsResponse.IsSuccessStatusCode)
                {
                    var err = ValidationResult.HasHttpError(modelsResponse.StatusCode,
                        $"Models listing failed: {TruncateResponse(modelsBody)}");
                    err.RawResponse = modelsBody;
                    return err;
                }

                var models = ParseModels(modelsBody);

                // Step 2: Minimal chat completion to verify live inference.
                var preferredModels = new[] { "llama-3.1-8b-instant", "llama3-8b-8192", "gemma2-9b-it" };
                var modelToUse = models?
                    .Select(m => m.ModelId)
                    .FirstOrDefault(id => preferredModels.Any(p => id.Equals(p, StringComparison.OrdinalIgnoreCase)))
                    ?? models?.Select(m => m.ModelId).FirstOrDefault();

                if (string.IsNullOrEmpty(modelToUse))
                {
                    var resultAuthOnly = ValidationResult.Success(modelsResponse.StatusCode, "Valid Groq key (models listed)");
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

                _logger?.LogDebug("Groq chat response ({Model}): Status={Status}, Body={Body}",
                    modelToUse, chatResponse.StatusCode, TruncateResponse(chatBody));

                ValidationResult result;

                if (IsSuccessStatusCode(chatResponse.StatusCode))
                {
                    result = ValidationResult.Success(chatResponse.StatusCode, models);
                    result.AvailableModels = models;
                    result.Detail = "Valid Groq key — live inference successful";
                    result.Metadata = new Dictionary<string, object>
                    {
                        ["authentication_valid"] = true,
                        ["inference_tested"] = true,
                        ["inference_working"] = true,
                        ["tested_model"] = modelToUse
                    };
                    result.RawResponse = chatBody;
                    return result;
                }

                if (chatResponse.StatusCode == HttpStatusCode.Unauthorized ||
                    chatResponse.StatusCode == HttpStatusCode.Forbidden)
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
                    result = ValidationResult.Success(
                        chatResponse.StatusCode,
                        "Valid key; request rate or quota limited");
                    result.IsQuotaExceeded = true;
                    result.AvailableModels = models;
                    result.Metadata = new Dictionary<string, object>
                    {
                        ["authentication_valid"] = true,
                        ["inference_tested"] = true,
                        ["inference_working"] = false,
                        ["inference_limited"] = true,
                        ["tested_model"] = modelToUse
                    };
                    result.RawResponse = chatBody;
                    return result;
                }

                if (ContainsAny(chatBody.ToLowerInvariant(), QuotaIndicators))
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
            apiKey.Length >= 44 &&
            apiKey.Length <= 64;
    }
}

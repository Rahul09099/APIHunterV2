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
    /// Provider for Groq API keys.
    /// Groq uses OpenAI-compatible endpoints at api.groq.com/openai/v1.
    /// Keys always start with "gsk_".
    /// Verification: GET /openai/v1/models (lists available models, confirms key validity)
    /// then POST /openai/v1/chat/completions with llama-3.1-8b-instant (cheapest/fastest).
    /// </summary>
    [ApiProvider]
    public class GroqProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "Groq";
        public override ApiTypeEnum ApiType => ApiTypeEnum.Groq;

        public override IEnumerable<string> RegexPatterns =>
        [
            @"\bgsk_[A-Za-z0-9]{40,60}\b",
            @"GROQ_API_KEY",
            @"groq[_-]?api[_-]?key"
        ];

        public GroqProvider() : base() { }
        public GroqProvider(ILogger<GroqProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(
            string apiKey, HttpClient httpClient)
        {
            // Step 1: List models — confirms key is valid and accepted
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
                return ValidationResult.IsUnauthorized(modelsResponse.StatusCode);
            }

            if (!modelsResponse.IsSuccessStatusCode)
            {
                return ValidationResult.HasHttpError(modelsResponse.StatusCode,
                    $"Models listing failed: {TruncateResponse(modelsBody)}");
            }

            // Parse available models
            var models = ParseModels(modelsBody);

            // Step 2: Quick chat completion to confirm the key has active quota
            // llama-3.1-8b-instant is Groq's fastest and cheapest model
            var preferredModels = new[] { "llama-3.1-8b-instant", "llama3-8b-8192", "gemma2-9b-it" };
            var modelToUse = models?
                .Select(m => m.ModelId)
                .FirstOrDefault(id => preferredModels.Any(p => id.Contains(p)))
                ?? "llama-3.1-8b-instant";

            using var chatRequest = new HttpRequestMessage(
                HttpMethod.Post, "https://api.groq.com/openai/v1/chat/completions");
            chatRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            chatRequest.Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    model = modelToUse,
                    messages = new[] { new { role = "user", content = "Hi" } },
                    max_tokens = 1
                }),
                System.Text.Encoding.UTF8, "application/json");

            var chatResponse = await httpClient.SendAsync(chatRequest);
            var chatBody = await chatResponse.Content.ReadAsStringAsync();

            _logger?.LogDebug("Groq chat response ({Model}): Status={Status}, Body={Body}",
                modelToUse, chatResponse.StatusCode, TruncateResponse(chatBody));

            if (IsSuccessStatusCode(chatResponse.StatusCode))
            {
                var result = ValidationResult.Success(chatResponse.StatusCode, models);
                result.AvailableModels = models;
                return result;
            }

            if (chatResponse.StatusCode == HttpStatusCode.Unauthorized ||
                chatResponse.StatusCode == HttpStatusCode.Forbidden)
            {
                return ValidationResult.IsUnauthorized(chatResponse.StatusCode);
            }

            if ((int)chatResponse.StatusCode == 429)
            {
                // Rate limited = key is valid but quota exhausted
                var limited = ValidationResult.Success(chatResponse.StatusCode,
                    "quota exhausted");
                limited.AvailableModels = models;
                return limited;
            }

            if (ContainsAny(chatBody, QuotaIndicators))
            {
                var limited = ValidationResult.Success(chatResponse.StatusCode,
                    $"Valid key but quota issue: {TruncateResponse(chatBody)}");
                limited.AvailableModels = models;
                return limited;
            }

            return ValidationResult.HasHttpError(chatResponse.StatusCode,
                $"Chat completion failed: {TruncateResponse(chatBody)}");
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
                    list.Add(new ModelInfo { ModelId = id, DisplayName = id });
                }
                return list;
            }
            catch { return null; }
        }

        protected override bool IsValidKeyFormat(string apiKey) =>
            !string.IsNullOrWhiteSpace(apiKey) &&
            apiKey.StartsWith("gsk_", StringComparison.Ordinal) &&
            apiKey.Length >= 44;
    }
}

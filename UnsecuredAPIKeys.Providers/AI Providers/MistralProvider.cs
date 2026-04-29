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
    /// Mistral keys have no fixed prefix — they are 32-char alphanumeric strings.
    /// Verification: GET /v1/models (official endpoint, confirms key validity + lists models)
    /// then POST /v1/chat/completions with mistral-small-latest (cheapest model).
    /// Official docs: https://docs.mistral.ai/api/endpoint/models
    /// </summary>
    [ApiProvider]
    public class MistralProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "Mistral AI";
        public override ApiTypeEnum ApiType => ApiTypeEnum.MistralAI;

        public override IEnumerable<string> RegexPatterns =>
        [
            // Mistral keys are 32-char alphanumeric — must be anchored to env var names to avoid false positives
            @"(?i)MISTRAL[_-]?API[_-]?KEY[""'\s]*[:=][""'\s]*([A-Za-z0-9]{32})",
            @"(?i)mistral[_-]?key[""'\s]*[:=][""'\s]*([A-Za-z0-9]{32})",
            @"(?i)MISTRAL_SECRET[""'\s]*[:=][""'\s]*([A-Za-z0-9]{32})",
        ];

        public MistralProvider() : base() { }
        public MistralProvider(ILogger<MistralProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(
            string apiKey, HttpClient httpClient)
        {
            // Step 1: List models — official validation endpoint
            using var modelsRequest = new HttpRequestMessage(
                HttpMethod.Get, "https://api.mistral.ai/v1/models");
            modelsRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            modelsRequest.Headers.Accept.Add(
                new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

            var modelsResponse = await httpClient.SendAsync(modelsRequest);
            var modelsBody = await modelsResponse.Content.ReadAsStringAsync();

            _logger?.LogDebug("Mistral models response: Status={Status}, Body={Body}",
                modelsResponse.StatusCode, TruncateResponse(modelsBody));

            if (modelsResponse.StatusCode == HttpStatusCode.Unauthorized ||
                modelsResponse.StatusCode == HttpStatusCode.Forbidden)
            {
                return ValidationResult.IsUnauthorized(modelsResponse.StatusCode);
            }

            if ((int)modelsResponse.StatusCode == 429)
            {
                return ValidationResult.Success(modelsResponse.StatusCode, "quota exhausted");
            }

            if (!modelsResponse.IsSuccessStatusCode)
            {
                return ValidationResult.HasHttpError(modelsResponse.StatusCode,
                    $"Models listing failed: {TruncateResponse(modelsBody)}");
            }

            var models = ParseModels(modelsBody);

            // Step 2: Quick chat completion to confirm active quota
            var preferredModels = new[] { "mistral-small-latest", "mistral-small", "open-mistral-7b" };
            var modelToUse = models?
                .Select(m => m.ModelId)
                .FirstOrDefault(id => preferredModels.Any(p => id.Contains(p)))
                ?? "mistral-small-latest";

            using var chatRequest = new HttpRequestMessage(
                HttpMethod.Post, "https://api.mistral.ai/v1/chat/completions");
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

            _logger?.LogDebug("Mistral chat response ({Model}): Status={Status}, Body={Body}",
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
                var limited = ValidationResult.Success(chatResponse.StatusCode, "quota exhausted");
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
            !string.IsNullOrWhiteSpace(apiKey) && apiKey.Length == 32 &&
            apiKey.All(c => char.IsLetterOrDigit(c));
    }
}

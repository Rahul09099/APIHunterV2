using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Base;
using UnsecuredAPIKeys.Providers.Common;

namespace UnsecuredAPIKeys.Providers.AI_Providers
{
    [ApiProvider]
    public class OpenAIProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "OpenAI";
        public override ApiTypeEnum ApiType => ApiTypeEnum.OpenAI;

        public override IEnumerable<string> RegexPatterns =>
        [
            @"sk-[A-Za-z0-9\-]{20,}",
            @"sk-proj-[A-Za-z0-9\-]{20,}",
            @"sk-svcacct-[A-Za-z0-9\-]{20,}",
            @"sk-[A-Za-z0-9]{48}",
            @"Bearer sk-[A-Za-z0-9\-]{20,}"
        ];

        public OpenAIProvider() : base() { }

        public OpenAIProvider(ILogger<OpenAIProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(string apiKey, HttpClient httpClient)
        {
            // 1. Discover models
            using var modelsRequest = new HttpRequestMessage(HttpMethod.Get, "https://api.openai.com/v1/models");
            modelsRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            var modelsResponse = await httpClient.SendAsync(modelsRequest);
            var modelsBody = await modelsResponse.Content.ReadAsStringAsync();

            if (modelsResponse.StatusCode == HttpStatusCode.Unauthorized ||
                modelsResponse.StatusCode == HttpStatusCode.Forbidden)
            {
                return ValidationResult.IsUnauthorized(modelsResponse.StatusCode);
            }

            if (!modelsResponse.IsSuccessStatusCode)
            {
                return ValidationResult.HasHttpError(
                    modelsResponse.StatusCode,
                    $"Model listing failed: {TruncateResponse(modelsBody)}");
            }

            var discoveredModels = ParseOpenAIModels(modelsBody);
            if (discoveredModels == null || !discoveredModels.Any())
            {
                return ValidationResult.Success(
                    modelsResponse.StatusCode,
                    "Valid key but no models returned");
            }

            // 2. Select a model
            var preferredModels = new[] { "gpt-4o", "gpt-4o-mini", "gpt-4-turbo", "gpt-4", "gpt-3.5-turbo" };

            var modelToUse = discoveredModels
                .Select(m => m.ModelId)
                .FirstOrDefault(id => preferredModels.Any(p => id.Contains(p)))
                ?? discoveredModels.First().ModelId;

            // 3. Test chat completion
            using var chatRequest = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
            chatRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            var requestBody = new
            {
                model = modelToUse,
                messages = new[]
                {
                    new { role = "user", content = "Hi" }
                },
                max_tokens = 5
            };

            chatRequest.Content = new StringContent(
                JsonSerializer.Serialize(requestBody),
                System.Text.Encoding.UTF8,
                "application/json");

            var chatResponse = await httpClient.SendAsync(chatRequest);
            var responseBody = await chatResponse.Content.ReadAsStringAsync();

            _logger?.LogDebug(
                "OpenAI chat API response ({Model}): Status={StatusCode}, Body={Body}",
                modelToUse,
                chatResponse.StatusCode,
                TruncateResponse(responseBody));

            if (IsSuccessStatusCode(chatResponse.StatusCode))
            {
                var success = ValidationResult.Success(chatResponse.StatusCode, discoveredModels);
                success.AvailableModels = discoveredModels;
                return success;
            }

            if (chatResponse.StatusCode == HttpStatusCode.Unauthorized ||
                chatResponse.StatusCode == HttpStatusCode.Forbidden)
            {
                return ValidationResult.IsUnauthorized(chatResponse.StatusCode);
            }

            if ((int)chatResponse.StatusCode == 429 ||
                chatResponse.StatusCode == HttpStatusCode.PaymentRequired)
            {
                 // Check body for specific quota details if possible
                 var details = "Valid key but quota/billing/rate limited";
                 if (responseBody.Contains("insufficient_quota") || responseBody.Contains("billing_hard_limit_reached"))
                 {
                     details = "Valid key but insufficient quota";
                 }

                var limited = ValidationResult.Success(
                    chatResponse.StatusCode,
                    details);

                limited.AvailableModels = discoveredModels;
                return limited;
            }

            // Check for quota error in 401/403 (sometimes happens with deactivated accounts)
            if (responseBody.Contains("insufficient_quota") || 
                responseBody.Contains("billing_hard_limit_reached"))
            {
                 var limited = ValidationResult.Success(
                    chatResponse.StatusCode,
                    "Valid key but insufficient quota (in error body)");
                 limited.AvailableModels = discoveredModels;
                 return limited;
            }

            var errorResult = ValidationResult.HasHttpError(
                chatResponse.StatusCode,
                $"API request failed: {TruncateResponse(responseBody)}");

            errorResult.AvailableModels = discoveredModels;
            return errorResult;
        }

        protected override bool IsValidKeyFormat(string apiKey)
        {
            return !string.IsNullOrWhiteSpace(apiKey) &&
                   apiKey.StartsWith("sk-") &&
                   apiKey.Length >= 23;
        }

        private List<ModelInfo>? ParseOpenAIModels(string jsonResponse)
        {
            try
            {
                using var doc = JsonDocument.Parse(jsonResponse);
                if (!doc.RootElement.TryGetProperty("data", out var dataArray))
                    return null;

                var models = new List<ModelInfo>();
                foreach (var modelElement in dataArray.EnumerateArray())
                {
                    var modelId = modelElement.GetProperty("id").GetString() ?? "";

                    models.Add(new ModelInfo
                    {
                        ModelId = modelId,
                        DisplayName = modelId,
                        Description = modelElement.TryGetProperty("description", out var desc) ? desc.GetString() : null,
                        ModelGroup = DetermineModelGroup(modelId)
                    });
                }

                return models;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error parsing OpenAI models response");
                return null;
            }
        }

        private string DetermineModelGroup(string modelId)
        {
            if (modelId.StartsWith("gpt-4")) return "GPT-4";
            if (modelId.StartsWith("gpt-3.5")) return "GPT-3.5";
            if (modelId.StartsWith("o1")) return "O1";
            if (modelId.StartsWith("text-embedding")) return "Embeddings";
            if (modelId.StartsWith("dall-e")) return "DALL-E";
            if (modelId.StartsWith("whisper")) return "Whisper";
            if (modelId.StartsWith("tts")) return "TTS";
            return "Other";
        }
    }
}

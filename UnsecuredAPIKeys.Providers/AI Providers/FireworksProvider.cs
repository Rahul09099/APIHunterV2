using Microsoft.Extensions.Logging;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Base;
using UnsecuredAPIKeys.Providers.Common;
using System.Net.Http.Headers;

namespace UnsecuredAPIKeys.Providers.AI_Providers
{
    [ApiProvider]
    public class FireworksProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "Fireworks AI";
        public override ApiTypeEnum ApiType => ApiTypeEnum.FireworksAI;

        public override IEnumerable<string> RegexPatterns =>
        [
            @"fw_[A-Za-z0-9]{32,}"
        ];

        public FireworksProvider() : base() { }
        public FireworksProvider(ILogger<FireworksProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(string apiKey, HttpClient httpClient)
        {
            try
            {
                // 1. Discover available models
                using var modelsRequest = new HttpRequestMessage(HttpMethod.Get, "https://api.fireworks.ai/inference/v1/models");
                modelsRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

                var modelsResponse = await httpClient.SendAsync(modelsRequest);
                string modelsBody = await modelsResponse.Content.ReadAsStringAsync();

                if (modelsResponse.StatusCode == System.Net.HttpStatusCode.Unauthorized || 
                    modelsResponse.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    return ValidationResult.IsUnauthorized(modelsResponse.StatusCode);
                }

                // If models call failed for other reasons, we'll try a fallback check
                string modelToUse = "accounts/fireworks/models/llama-v3p1-8b-instruct"; // Reasonable default
                List<ModelInfo>? discoveredModels = null;

                if (modelsResponse.IsSuccessStatusCode)
                {
                    discoveredModels = ParseFireworksModels(modelsBody);
                    if (discoveredModels != null && discoveredModels.Any())
                    {
                        // Use the first available model that looks like a chat model
                        var chatModel = discoveredModels.FirstOrDefault(m => m.ModelId.Contains("instruct") || m.ModelId.Contains("chat"));
                        if (chatModel != null)
                        {
                            modelToUse = chatModel.ModelId;
                        }
                    }
                }

                // 2. Test chat completion to check for quota/credits
                using var chatRequest = new HttpRequestMessage(HttpMethod.Post, "https://api.fireworks.ai/inference/v1/chat/completions");
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

                var jsonContent = System.Text.Json.JsonSerializer.Serialize(requestBody);
                chatRequest.Content = new StringContent(jsonContent, System.Text.Encoding.UTF8, "application/json");

                var chatResponse = await httpClient.SendAsync(chatRequest);
                string chatBody = await chatResponse.Content.ReadAsStringAsync();

                _logger?.LogDebug("Fireworks chat API response ({Model}): Status={StatusCode}, Body={Body}",
                    modelToUse, chatResponse.StatusCode, TruncateResponse(chatBody));

                if (IsSuccessStatusCode(chatResponse.StatusCode))
                {
                    return ValidationResult.Success(chatResponse.StatusCode, discoveredModels);
                }
                else
                {
                    // Check for quota/billing issues
                    if (ContainsAny(chatBody, new HashSet<string> { "quota", "billing", "insufficient", "balance", "credit" }))
                    {
                        var result = ValidationResult.Success(chatResponse.StatusCode, $"Valid key but access issue: {TruncateResponse(chatBody)}");
                        result.AvailableModels = discoveredModels;
                        return result;
                    }

                    var errorResult = ValidationResult.HasHttpError(chatResponse.StatusCode, 
                        $"API request failed with status {chatResponse.StatusCode}. Response: {TruncateResponse(chatBody)}");
                    errorResult.AvailableModels = discoveredModels;
                    return errorResult;
                }
            }
            catch (Exception ex)
            {
                return ValidationResult.HasNetworkError(ex.Message);
            }
        }

        private List<ModelInfo>? ParseFireworksModels(string jsonResponse)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(jsonResponse);
                if (!doc.RootElement.TryGetProperty("data", out var dataArray)) return null;

                var models = new List<ModelInfo>();
                foreach (var modelElement in dataArray.EnumerateArray())
                {
                    var id = modelElement.GetProperty("id").GetString() ?? "";
                    models.Add(new ModelInfo { ModelId = id, DisplayName = id });
                }
                return models;
            }
            catch { return null; }
        }

        protected override bool IsValidKeyFormat(string apiKey)
        {
            return !string.IsNullOrWhiteSpace(apiKey) && apiKey.StartsWith("fw_") && apiKey.Length >= 35;
        }
    }
}

using Microsoft.Extensions.Logging;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Base;
using UnsecuredAPIKeys.Providers.Common;

namespace UnsecuredAPIKeys.Providers.AI_Providers
{
    /// <summary>
    /// Provider for TogetherAI API keys
    /// </summary>
    [ApiProvider]
    public class TogetherAIProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "Together AI";
        public override ApiTypeEnum ApiType => ApiTypeEnum.TogetherAI;

        public override IEnumerable<string> RegexPatterns =>
        [
            @"[0-9a-f]{64}",  // TogetherAI uses 64-char hex tokens
            @"together[_-]?ai[_-]?[A-Za-z0-9]{32,}",
            @"TOGETHER_API_KEY"
        ];

        public TogetherAIProvider() : base() { }
        public TogetherAIProvider(ILogger<TogetherAIProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(string apiKey, HttpClient httpClient)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.together.xyz/v1/chat/completions");
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

                var requestBody = new
                {
                    model = "meta-llama/Meta-Llama-3.1-8B-Instruct-Turbo", // Llama-2-7b-chat-hf is deprecated; use current serverless model
                    messages = new[]
                    {
                        new { role = "user", content = "hi" }
                    },
                    max_tokens = 1
                };

                var jsonContent = System.Text.Json.JsonSerializer.Serialize(requestBody);
                request.Content = new StringContent(jsonContent, System.Text.Encoding.UTF8, "application/json");

                var response = await httpClient.SendAsync(request);
                string responseBody = await response.Content.ReadAsStringAsync();

                _logger?.LogDebug("Together AI API response: Status={StatusCode}, Body={Body}",
                    response.StatusCode, TruncateResponse(responseBody));

                if (IsSuccessStatusCode(response.StatusCode))
                {
                    return ValidationResult.Success(response.StatusCode, $"Key is valid and generation working.");
                }
                else if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized || 
                         response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    return ValidationResult.IsUnauthorized(response.StatusCode);
                }
                else
                {
                    // Check for quota/billing issues
                    if (ContainsAny(responseBody, new HashSet<string> { "quota", "billing", "limit", "credits", "insufficient" }))
                    {
                        return ValidationResult.Success(response.StatusCode, $"Valid key but access issue: {TruncateResponse(responseBody)}");
                    }

                    return ValidationResult.HasHttpError(response.StatusCode, 
                        $"API request failed with status {response.StatusCode}. Response: {TruncateResponse(responseBody)}");
                }
            }
            catch (Exception ex)
            {
                return ValidationResult.HasNetworkError(ex.Message);
            }
        }

        protected override bool IsValidKeyFormat(string apiKey)
        {
            return !string.IsNullOrWhiteSpace(apiKey) && apiKey.Length >= 32;
        }
    }
}

using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Http.Headers;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Base;
using UnsecuredAPIKeys.Providers.Common;

namespace UnsecuredAPIKeys.Providers.AI_Providers
{
    /// <summary>
    /// Provider for DeepSeek API keys with real API call verification
    /// </summary>
    [ApiProvider]
    public class DeepSeekProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "DeepSeek";
        public override ApiTypeEnum ApiType => ApiTypeEnum.DeepSeek;

        public override IEnumerable<string> RegexPatterns =>
        [
            @"sk-[A-Za-z0-9]{32,}",  // DeepSeek uses sk- prefix
            @"deepseek[_-]?[A-Za-z0-9]{32,}",
            @"DEEPSEEK_API_KEY"
        ];

        public DeepSeekProvider() : base() { }
        public DeepSeekProvider(ILogger<DeepSeekProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(string apiKey, HttpClient httpClient)
        {
            // DeepSeek uses OpenAI-compatible API
            using var chatRequest = new HttpRequestMessage(HttpMethod.Post, "https://api.deepseek.com/v1/chat/completions");
            chatRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            
            // Simple test with their cheapest model
            var requestBody = new
            {
                model = "deepseek-chat",
                messages = new[]
                {
                    new { role = "user", content = "Hi" }
                },
                max_tokens = 5
            };
            
            var jsonContent = System.Text.Json.JsonSerializer.Serialize(requestBody);
            chatRequest.Content = new StringContent(jsonContent, System.Text.Encoding.UTF8, "application/json");
            
            var chatResponse = await httpClient.SendAsync(chatRequest);
            string responseBody = await chatResponse.Content.ReadAsStringAsync();

            _logger?.LogDebug("DeepSeek chat API response: Status={StatusCode}, Body={Body}",
                chatResponse.StatusCode, TruncateResponse(responseBody));

            if (IsSuccessStatusCode(chatResponse.StatusCode))
            {
                // Successfully generated content - key has credits
                return ValidationResult.Success(chatResponse.StatusCode, "Chat completion successful");
            }
            else if (chatResponse.StatusCode == HttpStatusCode.Unauthorized)
            {
                return ValidationResult.IsUnauthorized(chatResponse.StatusCode);
            }
            else if ((int)chatResponse.StatusCode == 429)
            {
                // Check if it's quota exhausted or temporary rate limit
                if (ContainsAny(responseBody, new HashSet<string> { "insufficient", "balance", "quota", "billing", "exceeded" }))
                {
                    // Insufficient balance - key exists but unusable
                    return ValidationResult.HasHttpError(chatResponse.StatusCode, $"Insufficient balance (key unusable): {TruncateResponse(responseBody)}");
                }
                // Temporary rate limit means the key is valid
                return ValidationResult.Success(chatResponse.StatusCode, "Rate limited (key is valid)");
            }
            else if (chatResponse.StatusCode == HttpStatusCode.PaymentRequired)
            {
                // Payment required means key exists but is unusable - don't count as valid
                return ValidationResult.HasHttpError(chatResponse.StatusCode, "Payment required (key unusable)");
            }
            else
            {
                // Check response body for quota/billing issues
                if (ContainsAny(responseBody, new HashSet<string> { "insufficient", "balance", "quota", "billing", "exceeded" }))
                {
                    return ValidationResult.HasHttpError(chatResponse.StatusCode, $"Balance/quota issue (key unusable): {TruncateResponse(responseBody)}");
                }
                
                return ValidationResult.HasHttpError(chatResponse.StatusCode, 
                    $"API request failed with status {chatResponse.StatusCode}. Response: {TruncateResponse(responseBody)}");
            }
        }

        protected override bool IsValidKeyFormat(string apiKey)
        {
            return !string.IsNullOrWhiteSpace(apiKey) && apiKey.Length >= 32;
        }
    }
}

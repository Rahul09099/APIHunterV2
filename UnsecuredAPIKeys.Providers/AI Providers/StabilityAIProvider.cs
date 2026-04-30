using Microsoft.Extensions.Logging;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Base;
using UnsecuredAPIKeys.Providers.Common;

namespace UnsecuredAPIKeys.Providers.AI_Providers
{
    /// <summary>
    /// Provider for StabilityAI API keys - scraper only (no verification implemented yet)
    /// </summary>
    [ApiProvider]
    public class StabilityAIProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "Stability AI";
        public override ApiTypeEnum ApiType => ApiTypeEnum.StabilityAI;

        public override IEnumerable<string> RegexPatterns =>
        [
            @"sk-[A-Za-z0-9]{32,}",  // StabilityAI uses sk- prefix similar to OpenAI
            @"stability[_-]?ai[_-]?[A-Za-z0-9]{32,}"
        ];

        public StabilityAIProvider() : base() { }
        public StabilityAIProvider(ILogger<StabilityAIProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(string apiKey, HttpClient httpClient)
        {
            try
            {
                httpClient.Timeout = TimeSpan.FromSeconds(15);
                
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.stability.ai/v1/user/balance");
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

                var response = await httpClient.SendAsync(request);
                string responseBody = await response.Content.ReadAsStringAsync();

                _logger?.LogDebug("Stability AI API response: Status={StatusCode}, Body={Body}",
                    response.StatusCode, TruncateResponse(responseBody));

                if (IsSuccessStatusCode(response.StatusCode))
                {
                    var result = ValidationResult.Success(response.StatusCode, "Valid Stability AI key");
                    
                    try 
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(responseBody);
                        if (doc.RootElement.TryGetProperty("credits", out var credits))
                        {
                            var creditVal = credits.GetDouble();
                            result.Balance = $"{creditVal:N2} Credits";

                            if (creditVal <= 0)
                            {
                                result.IsQuotaExceeded = true;
                                result.Detail = "Valid key but 0 credits remaining.";
                            }
                        }
                    }
                    catch { /* Best effort parsing */ }

                    return result;
                }
                
                var bodyLower = responseBody.ToLowerInvariant();

                switch (response.StatusCode)
                {
                    case System.Net.HttpStatusCode.Unauthorized:
                        return ValidationResult.IsUnauthorized(response.StatusCode);

                    case System.Net.HttpStatusCode.Forbidden:
                        return ValidationResult.IsUnauthorized(response.StatusCode,
                            "Key forbidden (invalid or insufficient permissions)");

                    case (System.Net.HttpStatusCode)429:
                        return ValidationResult.Success(response.StatusCode, "Rate limited (valid key)");

                    default:
                        if (bodyLower.Contains("quota") || bodyLower.Contains("billing") ||
                            bodyLower.Contains("credits") || bodyLower.Contains("insufficient"))
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

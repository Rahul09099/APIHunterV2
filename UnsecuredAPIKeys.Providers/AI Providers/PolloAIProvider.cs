using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Http.Headers;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Base;
using UnsecuredAPIKeys.Providers.Common;

namespace UnsecuredAPIKeys.Providers.AI_Providers
{
    [ApiProvider]
    public class PolloAIProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "PolloAI";
        public override ApiTypeEnum ApiType => ApiTypeEnum.PolloAI;

        public override IEnumerable<string> RegexPatterns =>
        [
            @"\bpollo_[a-zA-Z0-9]{24,}\b"
        ];

        public PolloAIProvider() : base() { }

        public PolloAIProvider(ILogger<PolloAIProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(string apiKey, HttpClient httpClient)
        {
            httpClient.Timeout = TimeSpan.FromSeconds(15);

            // Pollo AI uses x-api-key header
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://pollo.ai/api/platform/credit/balance");
            request.Headers.Add("x-api-key", apiKey);

            try 
            {
                var response = await httpClient.SendAsync(request);
                string responseBody = await response.Content.ReadAsStringAsync();

                 _logger?.LogDebug("PolloAI API response: Status={StatusCode}, Body={Body}",
                    response.StatusCode, TruncateResponse(responseBody));

                if (IsSuccessStatusCode(response.StatusCode))
                {
                    var result = ValidationResult.Success(response.StatusCode, "Valid PolloAI key");

                    try 
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(responseBody);
                        if (doc.RootElement.TryGetProperty("data", out var data))
                        {
                            string? credits = null;
                            if (data.TryGetProperty("availableCredits", out var available))
                            {
                                credits = available.ToString();
                                if (data.TryGetProperty("totalCredits", out var total))
                                {
                                    credits += $" / {total}";
                                }
                            }
                            else if (data.TryGetProperty("balance", out var balance))
                            {
                                credits = balance.ToString();
                            }

                            if (credits != null)
                            {
                                result.Balance = $"{credits} Credits";
                            }
                        }
                    }
                    catch { /* Best effort parsing */ }

                    return result;
                }
                else if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                {
                    return ValidationResult.IsUnauthorized(response.StatusCode);
                }
                else if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return ValidationResult.HasHttpError(response.StatusCode, 
                        "Endpoint not found (not a key issue)");
                }
                 else if ((int)response.StatusCode == 429)
                {
                    return ValidationResult.Success(response.StatusCode, "Rate limited (key is valid)");
                }
                else
                {
                     return ValidationResult.HasHttpError(response.StatusCode, 
                        $"API request failed with status {response.StatusCode}. Response: {TruncateResponse(responseBody)}");
                }
            }
            catch (Exception ex)
            {
                return ValidationResult.HasHttpError(HttpStatusCode.ServiceUnavailable, $"Connection failed: {ex.Message}");
            }
        }

        protected override bool IsValidKeyFormat(string apiKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
                return false;

            return apiKey.StartsWith("pollo_") && apiKey.Length >= 24;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Base;
using UnsecuredAPIKeys.Providers.Common;

namespace UnsecuredAPIKeys.Providers.AI_Providers
{
    /// <summary>
    /// Provider for Fal.ai API keys — fast serverless image and video generation.
    /// Auth: Authorization: Key {apiKey}   (NOT Bearer)
    /// Docs: https://fal.ai/docs/reference/platform-apis/authentication
    /// </summary>
    [ApiProvider]
    public class FalAIProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "Fal.ai";
        public override ApiTypeEnum ApiType => ApiTypeEnum.FalAI;

        public override IEnumerable<string> RegexPatterns =>
        [
            @"FAL_KEY\s*[:=]\s*['""]?([A-Za-z0-9_\-]{32,})['""]?",
            @"FAL_API_KEY\s*[:=]\s*['""]?([A-Za-z0-9_\-]{32,})['""]?",
            @"fal[._-]?api[._-]?key\s*[:=]\s*['""]?([A-Za-z0-9_\-]{32,})['""]?"
        ];

        public FalAIProvider() : base() { }
        public FalAIProvider(ILogger<FalAIProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(string apiKey, HttpClient httpClient)
        {
            try
            {
                // Primary verification: GET /v1/models — requires API scope key
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.fal.ai/v1/models?limit=5");
                request.Headers.Add("Authorization", $"Key {apiKey}");

                var response = await httpClient.SendAsync(request);
                string responseBody = await response.Content.ReadAsStringAsync();

                _logger?.LogDebug("Fal.ai models response: Status={StatusCode}, Body={Body}",
                    response.StatusCode, TruncateResponse(responseBody));

                ValidationResult result;

                if (!IsSuccessStatusCode(response.StatusCode))
                {
                    result = response.StatusCode switch
                    {
                        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                            ValidationResult.IsUnauthorized(response.StatusCode, "Invalid Fal.ai API key"),
                        (HttpStatusCode)429 =>
                            new ValidationResult
                            {
                                Status = ValidationAttemptStatus.ValidationUnavailable,
                                HttpStatusCode = response.StatusCode,
                                Detail = "Fal.ai rate limit exceeded (HTTP 429)"
                            },
                        _ => ValidationResult.HasHttpError(response.StatusCode,
                            $"Unexpected status {response.StatusCode}. Body: {TruncateResponse(responseBody)}")
                    };
                    result.RawResponse = responseBody;
                    return result;
                }

                result = ValidationResult.Success(response.StatusCode, "Valid Fal.ai key");

                try
                {
                    using var doc = JsonDocument.Parse(responseBody);
                    if (doc.RootElement.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
                    {
                        var count = models.GetArrayLength();
                        result.Detail = $"Valid Fal.ai key — {count} model(s) returned";
                    }
                }
                catch { result.Detail = "Valid Fal.ai key"; }

                // Attempt to fetch balance (requires Admin scope)
                await TryFetchBalanceAsync(apiKey, httpClient, result);

                result.RawResponse = responseBody;
                return result;
            }
            catch (Exception ex)
            {
                return ValidationResult.HasNetworkError(ex.Message);
            }
        }

        private async Task TryFetchBalanceAsync(string apiKey, HttpClient httpClient, ValidationResult result)
        {
            try
            {
                using var billingRequest = new HttpRequestMessage(HttpMethod.Get,
                    "https://api.fal.ai/v1/account/billing?expand=credits");
                billingRequest.Headers.Add("Authorization", $"Key {apiKey}");

                var billingResponse = await httpClient.SendAsync(billingRequest);
                string billingBody = await billingResponse.Content.ReadAsStringAsync();

                _logger?.LogDebug("Fal.ai billing response: Status={Status}, Body={Body}",
                    billingResponse.StatusCode, TruncateResponse(billingBody));

                if (billingResponse.IsSuccessStatusCode)
                {
                    using var billingDoc = JsonDocument.Parse(billingBody);

                    if (billingDoc.RootElement.TryGetProperty("credits", out var credits))
                    {
                        if (credits.TryGetProperty("current_balance", out var balance) ||
                            credits.TryGetProperty("balance", out balance))
                        {
                            string currency = credits.TryGetProperty("currency", out var curr)
                                ? curr.GetString() ?? "USD" : "USD";

                            if (balance.TryGetDouble(out var amountVal))
                            {
                                result.Balance = $"{amountVal:N2} {currency} credits";
                            }
                            else if (double.TryParse(balance.ToString(), out var parsedVal))
                            {
                                result.Balance = $"{parsedVal:N2} {currency} credits";
                            }
                        }
                    }
                }
                else if (billingResponse.StatusCode == HttpStatusCode.Forbidden)
                {
                    result.Balance = "N/A (billing access forbidden)";
                }
                else if (billingResponse.StatusCode == HttpStatusCode.Unauthorized)
                {
                    result.Balance = "N/A (billing authentication unavailable)";
                }
                else
                {
                    result.Balance = $"N/A (billing HTTP {(int)billingResponse.StatusCode})";
                }
            }
            catch
            {
                result.Balance = "N/A (failed to parse billing)";
            }
        }

        protected override bool IsValidKeyFormat(string apiKey)
        {
            return !string.IsNullOrWhiteSpace(apiKey) &&
                   apiKey.Length >= 32 &&
                   apiKey.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '-');
        }
    }
}

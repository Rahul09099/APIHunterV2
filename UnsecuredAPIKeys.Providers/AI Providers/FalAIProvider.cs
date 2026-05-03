using Microsoft.Extensions.Logging;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Base;
using UnsecuredAPIKeys.Providers.Common;

namespace UnsecuredAPIKeys.Providers.AI_Providers
{
    /// <summary>
    /// Provider for Fal.ai API keys — fast serverless image and video generation.
    ///
    /// Key format: plain alphanumeric string, stored in FAL_KEY env var.
    /// No fixed prefix — keys are generated from fal.ai/dashboard/keys.
    ///
    /// Auth: Authorization: Key {apiKey}   (NOT Bearer — confirmed from official docs)
    /// Docs: https://fal.ai/docs/reference/platform-apis/authentication
    ///
    /// Verification strategy:
    ///   Primary:  GET https://api.fal.ai/v1/models?limit=1
    ///             Requires API scope key. Returns { "models": [...] } on success.
    ///             Returns 401/403 on invalid key.
    ///
    ///   Balance:  GET https://api.fal.ai/v1/account/billing?expand=credits
    ///             Requires ADMIN scope key. Returns credit balance.
    ///             Falls back gracefully if key only has API scope.
    ///
    /// Note: fal uses a prepaid credit model — credits purchased in advance.
    /// </summary>
    [ApiProvider]
    public class FalAIProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "Fal.ai";
        public override ApiTypeEnum ApiType => ApiTypeEnum.FalAI;

        public override IEnumerable<string> RegexPatterns =>
        [
            // FAL_KEY is the canonical env var name — most common leak pattern
            @"FAL_KEY",
            @"FAL_API_KEY",

            // Keys are alphanumeric, typically 32-64 chars, no fixed prefix
            // Only match when adjacent to fal-related context to reduce false positives
            @"fal[_-]?key\s*[=:]\s*['""]?([A-Za-z0-9_\-]{32,})['""]?",
            @"FAL_KEY\s*[=:]\s*['""]?([A-Za-z0-9_\-]{32,})['""]?"
        ];

        public FalAIProvider() : base() { }
        public FalAIProvider(ILogger<FalAIProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(string apiKey, HttpClient httpClient)
        {
            try
            {
                // Primary verification: GET /v1/models — requires API scope key
                // Returns 401/403 for invalid keys, 200 with model list for valid keys
                using var request = new HttpRequestMessage(HttpMethod.Get,
                    "https://api.fal.ai/v1/models?limit=5");
                // Fal.ai uses "Key" prefix, NOT "Bearer"
                request.Headers.Add("Authorization", $"Key {apiKey}");

                var response = await httpClient.SendAsync(request);
                string responseBody = await response.Content.ReadAsStringAsync();

                _logger?.LogDebug("Fal.ai models response: Status={StatusCode}, Body={Body}",
                    response.StatusCode, TruncateResponse(responseBody));

                if (!IsSuccessStatusCode(response.StatusCode))
                {
                    return response.StatusCode switch
                    {
                        System.Net.HttpStatusCode.Unauthorized or
                        System.Net.HttpStatusCode.Forbidden =>
                            ValidationResult.IsUnauthorized(response.StatusCode),
                        (System.Net.HttpStatusCode)429 =>
                            ValidationResult.Success(response.StatusCode, "Rate limited (key is valid)"),
                        _ => ValidationResult.HasHttpError(response.StatusCode,
                            $"Unexpected status {response.StatusCode}. Body: {TruncateResponse(responseBody)}")
                    };
                }

                var result = ValidationResult.Success(response.StatusCode, "Valid Fal.ai key");

                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(responseBody);
                    // Response: { "models": [...], "next": "..." }
                    if (doc.RootElement.TryGetProperty("models", out var models))
                    {
                        var count = models.GetArrayLength();
                        result.Detail = $"Valid Fal.ai key — {count} model(s) returned";
                    }
                    else
                    {
                        result.Detail = "Valid Fal.ai key";
                    }
                }
                catch { result.Detail = "Valid Fal.ai key"; }

                // Try to get credit balance (requires Admin scope — graceful fallback)
                await TryFetchBalanceAsync(apiKey, httpClient, result);

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
                // Billing endpoint requires Admin scope key
                // If key only has API scope, this returns 403 — we handle gracefully
                using var billingRequest = new HttpRequestMessage(HttpMethod.Get,
                    "https://api.fal.ai/v1/account/billing?expand=credits");
                billingRequest.Headers.Add("Authorization", $"Key {apiKey}");

                var billingResponse = await httpClient.SendAsync(billingRequest);

                if (billingResponse.IsSuccessStatusCode)
                {
                    string billingBody = await billingResponse.Content.ReadAsStringAsync();
                    using var billingDoc = System.Text.Json.JsonDocument.Parse(billingBody);

                    // Response: { "credits": { "balance": 12.50, "currency": "USD" } }
                    if (billingDoc.RootElement.TryGetProperty("credits", out var credits))
                    {
                        if (credits.TryGetProperty("balance", out var balance))
                        {
                            string currency = credits.TryGetProperty("currency", out var curr)
                                ? curr.GetString() ?? "USD" : "USD";
                            result.Balance = $"{balance.GetDouble():N2} {currency} credits";
                        }
                    }
                }
                else
                {
                    // 403 = API scope key (no admin access) — still valid key
                    result.Balance = "N/A (Admin scope required for balance)";
                }
            }
            catch
            {
                result.Balance = "N/A";
            }
        }

        protected override bool IsValidKeyFormat(string apiKey)
        {
            // Fal.ai keys have no fixed prefix — just check reasonable length
            return !string.IsNullOrWhiteSpace(apiKey) && apiKey.Length >= 32;
        }
    }
}

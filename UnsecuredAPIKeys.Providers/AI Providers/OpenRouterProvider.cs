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
    /// Provider for OpenRouter API keys.
    /// OpenRouter is a unified gateway to 400+ models (OpenAI, Anthropic, Google, Meta, etc.)
    /// Keys always start with "sk-or-v1-".
    /// Verification: GET /api/v1/auth/key — returns credits, usage, and key metadata.
    /// Official docs: https://openrouter.ai/docs/api/authentication
    /// </summary>
    [ApiProvider]
    public class OpenRouterProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "OpenRouter";
        public override ApiTypeEnum ApiType => ApiTypeEnum.OpenRouter;

        public override IEnumerable<string> RegexPatterns =>
        [
            @"\bsk-or-v1-[A-Za-z0-9]{40,80}\b",
            @"OPENROUTER_API_KEY",
            @"openrouter[_-]?key",
            @"OPEN_ROUTER_KEY"
        ];

        public OpenRouterProvider() : base() { }
        public OpenRouterProvider(ILogger<OpenRouterProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(
            string apiKey, HttpClient httpClient)
        {
            // GET /api/v1/auth/key — returns key info including credits and usage
            // This is the official lightweight validation endpoint (no generation cost)
            using var request = new HttpRequestMessage(
                HttpMethod.Get, "https://openrouter.ai/api/v1/auth/key");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            var response = await httpClient.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            _logger?.LogDebug("OpenRouter auth/key response: Status={Status}, Body={Body}",
                response.StatusCode, TruncateResponse(body));

            if (response.StatusCode == HttpStatusCode.Unauthorized ||
                response.StatusCode == HttpStatusCode.Forbidden)
            {
                return ValidationResult.IsUnauthorized(response.StatusCode);
            }

            if ((int)response.StatusCode == 429)
            {
                return ValidationResult.Success(response.StatusCode, "Rate limited (key is valid)");
            }

            if (!response.IsSuccessStatusCode)
            {
                return ValidationResult.HasHttpError(response.StatusCode,
                    $"Auth check failed: {TruncateResponse(body)}");
            }

            // Parse credits and usage from response
            // Response shape: { "data": { "label": "...", "usage": 0.0, "limit": null, "limit_remaining": null } }
            var result = ValidationResult.Success(response.StatusCode, "Valid OpenRouter key");
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("data", out var data))
                {
                    // limit_remaining = credits left (null = unlimited)
                    if (data.TryGetProperty("limit_remaining", out var limitRemaining) &&
                        limitRemaining.ValueKind == JsonValueKind.Number)
                    {
                        result.Balance = $"${limitRemaining.GetDouble():F4} credits remaining";
                    }
                    else if (data.TryGetProperty("limit", out var limit) &&
                             limit.ValueKind == JsonValueKind.Null)
                    {
                        result.Balance = "Unlimited (no credit limit set)";
                    }

                    // label = key name set by the user
                    if (data.TryGetProperty("label", out var label) &&
                        label.ValueKind == JsonValueKind.String)
                    {
                        result.AccountTier = label.GetString();
                    }
                }
            }
            catch { /* Best effort parsing */ }

            return result;
        }

        protected override bool IsValidKeyFormat(string apiKey) =>
            !string.IsNullOrWhiteSpace(apiKey) &&
            apiKey.StartsWith("sk-or-v1-", StringComparison.Ordinal) &&
            apiKey.Length >= 49;
    }
}

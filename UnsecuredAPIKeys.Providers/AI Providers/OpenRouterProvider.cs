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
            // Response shape: { "data": { "label": "...", "usage": 0.0, "limit": null, "limit_remaining": null, "is_free_tier": false } }
            var result = ValidationResult.Success(response.StatusCode, "Valid OpenRouter key");
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("data", out var data))
                {
                    double usage = 0;
                    if (data.TryGetProperty("usage", out var usageProp) && usageProp.ValueKind == JsonValueKind.Number)
                    {
                        usage = usageProp.GetDouble();
                    }

                    bool isFreeTier = false;
                    if (data.TryGetProperty("is_free_tier", out var freeProp) && freeProp.ValueKind == JsonValueKind.True)
                    {
                        isFreeTier = true;
                    }

                    double? limit = null;
                    if (data.TryGetProperty("limit", out var limitProp) && limitProp.ValueKind == JsonValueKind.Number)
                    {
                        limit = limitProp.GetDouble();
                    }

                    if (isFreeTier)
                    {
                        result.Balance = $"Free Tier Access (Usage: ${usage:F4})";
                    }
                    else if (data.TryGetProperty("limit_remaining", out var limitRemaining) &&
                        limitRemaining.ValueKind == JsonValueKind.Number)
                    {
                        var remaining = limitRemaining.GetDouble();
                        string limitInfo = limit.HasValue ? $" / ${limit.Value:F4}" : "";
                        result.Balance = $"${remaining:F4}{limitInfo} remaining";
                        
                        if (remaining <= 0)
                        {
                            result.IsQuotaExceeded = true;
                            result.Detail = "Valid key but no credits remaining.";
                        }
                    }
                    else
                    {
                        // limit: null means the key inherits account limits
                        result.Balance = $"No key limit (Used: ${usage:F4})";
                    }

                    // Additional check: If key is valid and paid tier, verify if the account actually has credits
                    if (!isFreeTier && !result.IsQuotaExceeded)
                    {
                        try
                        {
                            using var checkRequest = new HttpRequestMessage(HttpMethod.Post, "https://openrouter.ai/api/v1/chat/completions");
                            checkRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                            
                            var jsonPayload = "{\"model\":\"google/gemini-2.5-flash\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}],\"max_tokens\":1}";
                            checkRequest.Content = new StringContent(jsonPayload, System.Text.Encoding.UTF8, "application/json");
                            
                            using var checkResponse = await httpClient.SendAsync(checkRequest);
                            var checkBody = await checkResponse.Content.ReadAsStringAsync();
                            
                            if ((int)checkResponse.StatusCode == 402 || checkBody.Contains("Insufficient credits", StringComparison.OrdinalIgnoreCase))
                            {
                                result.IsQuotaExceeded = true;
                                result.Detail = "Valid key but insufficient account credits.";
                                result.Balance = $"Insufficient account credits (Used: ${usage:F4})";
                            }
                        }
                        catch { /* Best effort account credit check */ }
                    }

                    // Account Tier
                    string tier = isFreeTier ? "Free Tier" : "Paid Tier";
                    string? labelStr = null;

                    if (data.TryGetProperty("label", out var label) && label.ValueKind == JsonValueKind.String)
                    {
                        labelStr = label.GetString();
                        // If label is NOT the api key itself, include it
                        if (!string.IsNullOrEmpty(labelStr) && !apiKey.Contains(labelStr))
                        {
                            result.AccountTier = $"{tier} (Label: {labelStr})";
                        }
                        else
                        {
                            result.AccountTier = tier;
                        }
                    }
                    else
                    {
                        result.AccountTier = tier;
                    }

                    // Populate Metadata
                    result.Metadata = new Dictionary<string, object>();
                    foreach (var prop in data.EnumerateObject())
                    {
                        switch (prop.Value.ValueKind)
                        {
                            case JsonValueKind.String: result.Metadata[prop.Name] = prop.Value.GetString() ?? ""; break;
                            case JsonValueKind.Number: result.Metadata[prop.Name] = prop.Value.GetDouble(); break;
                            case JsonValueKind.True: result.Metadata[prop.Name] = true; break;
                            case JsonValueKind.False: result.Metadata[prop.Name] = false; break;
                            case JsonValueKind.Null: result.Metadata[prop.Name] = "null"; break;
                        }
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

using Microsoft.Extensions.Logging;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Base;
using UnsecuredAPIKeys.Providers.Common;

namespace UnsecuredAPIKeys.Providers.AI_Providers
{
    /// <summary>
    /// Provider for ElevenLabs API keys - scraper only (no verification implemented yet)
    /// </summary>
    [ApiProvider]
    public class ElevenLabsProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "ElevenLabs";
        public override ApiTypeEnum ApiType => ApiTypeEnum.ElevenLabs;

        public override IEnumerable<string> RegexPatterns =>
        [
            @"[A-Za-z0-9]{32}",  // ElevenLabs uses 32-char alphanumeric tokens
            @"elevenlabs[_-]?[A-Za-z0-9]{32,}",
            @"ELEVEN_API_KEY",
            @"ELEVENLABS_API_KEY"
        ];

        public ElevenLabsProvider() : base() { }
        public ElevenLabsProvider(ILogger<ElevenLabsProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(string apiKey, HttpClient httpClient)
        {
            try
            {
                // GET /v1/user/subscription — confirmed official endpoint (elevenlabs.io/docs/api-reference/user/get-subscription)
                // Header: xi-api-key — confirmed correct authentication method
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.elevenlabs.io/v1/user/subscription");
                request.Headers.Add("xi-api-key", apiKey);

                var response = await httpClient.SendAsync(request);
                string responseBody = await response.Content.ReadAsStringAsync();

                _logger?.LogDebug("ElevenLabs API response: Status={StatusCode}, Body={Body}",
                    response.StatusCode, TruncateResponse(responseBody));

                if (IsSuccessStatusCode(response.StatusCode))
                {
                    var result = ValidationResult.Success(response.StatusCode, "Valid ElevenLabs key");

                    // Parse subscription info for balance/tier display
                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(responseBody);
                        var root = doc.RootElement;

                        // tier: "free", "starter", "creator", "pro", "scale", "business"
                        if (root.TryGetProperty("tier", out var tier))
                            result.AccountTier = tier.GetString();

                        // character_count / character_limit gives remaining quota
                        if (root.TryGetProperty("character_count", out var used) &&
                            root.TryGetProperty("character_limit", out var limit))
                        {
                            var usedVal = used.GetInt64();
                            var limitVal = limit.GetInt64();
                            var remaining = limitVal - usedVal;

                            bool canExtend = false;
                            if (root.TryGetProperty("allowed_to_extend_character_limit", out var extend))
                                canExtend = extend.GetBoolean();

                            if (remaining <= 0 && !canExtend)
                            {
                                result.Balance = "0 (Exhausted)";
                                result.IsQuotaExceeded = true;
                                result.Detail = "Key is valid but character limit reached.";
                            }
                            else
                            {
                                result.Balance = $"{remaining:N0} chars" + (canExtend ? " (Pay-As-You-Go)" : "");
                            }
                        }
                    }
                    catch { /* Best effort parsing */ }

                    return result;
                }
                else if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
                         response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    return ValidationResult.IsUnauthorized(response.StatusCode);
                }
                else if ((int)response.StatusCode == 429)
                {
                    // 429 = rate limited but key is valid
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
                return ValidationResult.HasNetworkError(ex.Message);
            }
        }

        protected override bool IsValidKeyFormat(string apiKey)
        {
            return !string.IsNullOrWhiteSpace(apiKey) && apiKey.Length >= 32;
        }
    }
}

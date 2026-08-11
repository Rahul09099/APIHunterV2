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
    /// Provider for AI21 Labs API keys — Jamba model family.
    ///
    /// Key format: alphanumeric string stored in AI21_API_KEY env var.
    /// Auth: Authorization: Bearer {apiKey}
    /// Base URL: https://api.ai21.com/studio/v1
    ///
    /// Verification strategy:
    ///   - POST https://api.ai21.com/studio/v1/chat/completions
    ///   - Model: jamba-mini (minimal active inference test)
    ///   - {"messages": [{"role": "user", "content": "hi"}], "max_tokens": 1}
    /// Official docs: https://docs.ai21.com/reference/authentication
    /// </summary>
    [ApiProvider]
    public class AI21LabsProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "AI21 Labs";
        public override ApiTypeEnum ApiType => ApiTypeEnum.AI21Labs;

        public override IEnumerable<string> RegexPatterns =>
        [
            @"(?i)\bAI21[\s_-]*API[\s_-]*KEY\s*[:=]\s*['""]?([A-Za-z0-9_-]{20,256})['""]?",
            @"(?i)\bAI21[\s_-]*KEY\s*[:=]\s*['""]?([A-Za-z0-9_-]{20,256})['""]?",
            @"AI21_API_KEY",
            @"AI21LABS_API_KEY"
        ];

        public AI21LabsProvider() : base() { }
        public AI21LabsProvider(ILogger<AI21LabsProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(string apiKey, HttpClient httpClient)
        {
            try
            {
                // POST /studio/v1/chat/completions with jamba-mini — official documented chat API
                const string modelToUse = "jamba-mini";
                using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.ai21.com/studio/v1/chat/completions");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

                var payload = new
                {
                    model = modelToUse,
                    messages = new[] { new { role = "user", content = "hi" } },
                    max_tokens = 1
                };

                request.Content = new StringContent(
                    JsonSerializer.Serialize(payload),
                    System.Text.Encoding.UTF8,
                    "application/json");

                var response = await httpClient.SendAsync(request);
                string responseBody = await response.Content.ReadAsStringAsync();

                _logger?.LogDebug("AI21 Labs API response ({Model}): Status={StatusCode}, Body={Body}",
                    modelToUse, response.StatusCode, TruncateResponse(responseBody));

                if (IsSuccessStatusCode(response.StatusCode))
                {
                    var result = ValidationResult.Success(response.StatusCode, "Valid AI21 Labs key");
                    result.RawResponse = responseBody;
                    result.Balance = "Not available from validation endpoint";
                    result.Detail = $"Valid AI21 Labs key — Chat completions verified with model '{modelToUse}'.";
                    result.Metadata = new Dictionary<string, object>
                    {
                        ["authentication_valid"] = true,
                        ["inference_tested"] = true,
                        ["inference_working"] = true,
                        ["tested_model"] = modelToUse
                    };

                    try
                    {
                        using var doc = JsonDocument.Parse(responseBody);
                        if (doc.RootElement.TryGetProperty("choices", out var choices) &&
                            choices.ValueKind == JsonValueKind.Array &&
                            choices.GetArrayLength() > 0)
                        {
                            var msg = choices[0].GetProperty("message");
                            if (msg.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                            {
                                result.Metadata["test_response"] = content.GetString() ?? "";
                            }
                        }
                    }
                    catch { /* Best effort */ }

                    return result;
                }

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    return ValidationResult.IsUnauthorized(response.StatusCode,
                        "Invalid or expired AI21 Labs API key");
                }

                if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    return ValidationResult.ValidationUnavailable(response.StatusCode,
                        "AI21 Labs API key forbidden (403) — access or permission restriction");
                }

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    return ValidationResult.ValidationUnavailable(response.StatusCode,
                        "AI21 Labs API rate limited (429) — validation unavailable");
                }

                if (response.StatusCode == HttpStatusCode.PaymentRequired || ContainsAny(responseBody, QuotaIndicators))
                {
                    var quotaResult = ValidationResult.Success(response.StatusCode,
                        "Valid AI21 Labs key — 402 Payment Required (insufficient credits/quota)");
                    quotaResult.IsQuotaExceeded = true;
                    quotaResult.RawResponse = responseBody;
                    quotaResult.Metadata = new Dictionary<string, object>
                    {
                        ["authentication_valid"] = true,
                        ["inference_tested"] = true,
                        ["inference_working"] = false,
                        ["quota_exceeded"] = true,
                        ["tested_model"] = modelToUse
                    };
                    return quotaResult;
                }

                if ((int)response.StatusCode >= 500)
                {
                    return ValidationResult.ValidationUnavailable(response.StatusCode,
                        $"AI21 Labs service error ({response.StatusCode}) — validation unavailable");
                }

                return ValidationResult.HasHttpError(response.StatusCode,
                    $"AI21 Labs chat request failed: Status {response.StatusCode}. Body: {TruncateResponse(responseBody)}");
            }
            catch (Exception ex)
            {
                return ValidationResult.HasNetworkError(ex.Message);
            }
        }

        protected override bool IsValidKeyFormat(string apiKey) =>
            !string.IsNullOrWhiteSpace(apiKey) &&
            apiKey.Length >= 20 &&
            apiKey.Length <= 256;
    }
}

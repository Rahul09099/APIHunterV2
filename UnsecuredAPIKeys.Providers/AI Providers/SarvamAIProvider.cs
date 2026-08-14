using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Base;
using UnsecuredAPIKeys.Providers.Common;

namespace UnsecuredAPIKeys.Providers.AI_Providers
{
    /// <summary>
    /// Provider for Sarvam AI API keys — Indic GenAI platform for LLMs, Translation & Speech.
    ///
    /// Auth: api-subscription-key: {apiKey}  (Header, also accepts Authorization: Bearer {apiKey})
    ///
    /// Verification Strategy:
    ///   Primary endpoint: POST https://api.sarvam.ai/translate
    ///   Payload: {"input": "Hi", "source_language_code": "en-IN", "target_language_code": "hi-IN"}
    ///
    /// Error Classification (Sarvam AI Official Auth Spec):
    ///   - 200 OK: Valid key (translation succeeded)
    ///   - 403 Forbidden:
    ///       * error.code == "invalid_api_key_error" -> Invalid/Revoked key
    ///       * other error code -> Authenticated key with restricted resource access
    ///   - 429 Too Many Requests: Rate limited -> ValidationUnavailable
    ///   - 5xx Server Error: ValidationUnavailable
    ///   - 400 / 422 Bad Request: Inspect error payload
    /// </summary>
    [ApiProvider]
    public class SarvamAIProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "Sarvam AI";
        public override ApiTypeEnum ApiType => ApiTypeEnum.SarvamAI;

        public override IEnumerable<string> RegexPatterns =>
        [
            // Primary env var and config variable names
            @"SARVAM_API_KEY",
            @"SARVAM_KEY",
            @"SARVAM_SUBSCRIPTION_KEY",
            @"SHRAVAM_API_KEY",
            @"SHRAVAM_KEY",

            // Context-aware value extraction patterns
            @"SARVAM_API_KEY\s*[=:]\s*['""]?([A-Za-z0-9\-_]{16,})['""]?",
            @"SARVAM_KEY\s*[=:]\s*['""]?([A-Za-z0-9\-_]{16,})['""]?",
            @"SARVAM_SUBSCRIPTION_KEY\s*[=:]\s*['""]?([A-Za-z0-9\-_]{16,})['""]?",
            @"api-subscription-key\s*[=:]\s*['""]?([A-Za-z0-9\-_]{16,})['""]?",
            @"sarvam[._-]?api[._-]?key\s*[=:]\s*['""]?([A-Za-z0-9\-_]{16,})['""]?",
            @"shravam[._-]?api[._-]?key\s*[=:]\s*['""]?([A-Za-z0-9\-_]{16,})['""]?",

            // Sarvam Chat / API subscription keys formatted as sk_xxx
            @"\b(?:sarvam|shravam)[^a-zA-Z0-9]*['""]?(sk_[A-Za-z0-9\-_]{20,})['""]?"
        ];

        public SarvamAIProvider() : base() { }
        public SarvamAIProvider(ILogger<SarvamAIProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(string apiKey, HttpClient httpClient)
        {
            try
            {
                // Primary Verification: POST https://api.sarvam.ai/translate
                using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.sarvam.ai/translate");
                request.Headers.Add("api-subscription-key", apiKey);
                request.Content = new StringContent(
                    JsonSerializer.Serialize(new
                    {
                        input = "Hi",
                        source_language_code = "en-IN",
                        target_language_code = "hi-IN"
                    }),
                    Encoding.UTF8,
                    "application/json"
                );

                var response = await httpClient.SendAsync(request);
                string responseBody = await response.Content.ReadAsStringAsync();

                _logger?.LogDebug("Sarvam AI translate response: Status={StatusCode}, Body={Body}",
                    response.StatusCode, TruncateResponse(responseBody));

                // ── 200 OK: Valid key & translation verified ───────────────────────────
                if (IsSuccessStatusCode(response.StatusCode))
                {
                    var result = ValidationResult.Success(response.StatusCode, "Valid Sarvam AI key (translation verified)");
                    var metadata = new Dictionary<string, object>
                    {
                        ["authentication_valid"] = true,
                        ["tested_endpoint"] = "https://api.sarvam.ai/translate",
                        ["inference_working"] = true
                    };

                    try
                    {
                        using var doc = JsonDocument.Parse(responseBody);
                        if (doc.RootElement.TryGetProperty("translated_text", out var translated))
                        {
                            metadata["sample_translation"] = translated.GetString() ?? "";
                        }
                    }
                    catch { }

                    result.Metadata = metadata;
                    result.RawResponse = responseBody;
                    return result;
                }

                // ── 403 Forbidden: Body-Aware Inspection ─────────────────────────────
                if (response.StatusCode == HttpStatusCode.Forbidden || response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    bool isInvalidKey = false;
                    string errorDetail = "Invalid Sarvam AI API key";

                    try
                    {
                        using var doc = JsonDocument.Parse(responseBody);
                        if (doc.RootElement.TryGetProperty("error", out var errorObj))
                        {
                            string? code = errorObj.TryGetProperty("code", out var codeEl) ? codeEl.GetString() : null;
                            string? message = errorObj.TryGetProperty("message", out var msgEl) ? msgEl.GetString() : null;

                            if (string.Equals(code, "invalid_api_key_error", StringComparison.OrdinalIgnoreCase) ||
                                (message != null && message.Contains("invalid", StringComparison.OrdinalIgnoreCase) && message.Contains("key", StringComparison.OrdinalIgnoreCase)))
                            {
                                isInvalidKey = true;
                                errorDetail = message ?? "Invalid Sarvam AI API key (invalid_api_key_error)";
                            }
                            else if (code != null)
                            {
                                // Authenticated key, but resource or policy restricted
                                isInvalidKey = false;
                                errorDetail = $"Authenticated key with restricted access ({code}: {message})";
                            }
                        }
                        else if (responseBody.Contains("invalid_api_key_error", StringComparison.OrdinalIgnoreCase) ||
                                 responseBody.Contains("Invalid API key", StringComparison.OrdinalIgnoreCase))
                        {
                            isInvalidKey = true;
                        }
                    }
                    catch
                    {
                        if (responseBody.Contains("invalid_api_key", StringComparison.OrdinalIgnoreCase) ||
                            responseBody.Contains("Invalid API key", StringComparison.OrdinalIgnoreCase))
                        {
                            isInvalidKey = true;
                        }
                    }

                    if (isInvalidKey)
                    {
                        var invalidResult = ValidationResult.IsUnauthorized(response.StatusCode, errorDetail);
                        invalidResult.RawResponse = responseBody;
                        return invalidResult;
                    }

                    // Key is authenticated but access to endpoint is restricted
                    var restrictedResult = ValidationResult.Success(response.StatusCode, errorDetail);
                    restrictedResult.Metadata = new Dictionary<string, object>
                    {
                        ["authentication_valid"] = true,
                        ["access_restricted"] = true
                    };
                    restrictedResult.RawResponse = responseBody;
                    return restrictedResult;
                }

                // ── 429 Too Many Requests: Rate limited ──────────────────────────────
                if ((int)response.StatusCode == 429)
                {
                    return new ValidationResult
                    {
                        Status = ValidationAttemptStatus.ValidationUnavailable,
                        HttpStatusCode = response.StatusCode,
                        Detail = "Sarvam AI rate limit exceeded (HTTP 429)",
                        RawResponse = responseBody
                    };
                }

                // ── 5xx Server Error: Temporary service outage ─────────────────────────
                if ((int)response.StatusCode >= 500)
                {
                    return new ValidationResult
                    {
                        Status = ValidationAttemptStatus.ValidationUnavailable,
                        HttpStatusCode = response.StatusCode,
                        Detail = $"Sarvam AI server error (HTTP {(int)response.StatusCode})",
                        RawResponse = responseBody
                    };
                }

                // ── 400 / 422 Client Error: Inspect payload before declaring error ────
                if (responseBody.Contains("invalid_api_key_error", StringComparison.OrdinalIgnoreCase))
                {
                    var invalidResult = ValidationResult.IsUnauthorized(response.StatusCode, "Invalid Sarvam AI API key");
                    invalidResult.RawResponse = responseBody;
                    return invalidResult;
                }

                return ValidationResult.HasHttpError(response.StatusCode,
                    $"Sarvam AI verification returned unexpected status {response.StatusCode}: {TruncateResponse(responseBody)}");
            }
            catch (Exception ex)
            {
                return ValidationResult.HasNetworkError(ex.Message);
            }
        }

        protected override bool IsValidKeyFormat(string apiKey)
        {
            return !string.IsNullOrWhiteSpace(apiKey) &&
                   apiKey.Length >= 16 &&
                   apiKey.Length <= 128 &&
                   !apiKey.Contains(' ');
        }
    }
}

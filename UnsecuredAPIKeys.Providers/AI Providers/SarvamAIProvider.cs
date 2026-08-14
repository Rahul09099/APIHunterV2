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
    /// Provider for Sarvam AI API keys — Indian GenAI platform for Indic LLMs, Speech & Translation.
    ///
    /// Auth: api-subscription-key: {apiKey}  (Header)
    ///
    /// Verification Strategy:
    ///   1. GET https://api.sarvam.ai/v2/models (with api-subscription-key header)
    ///   2. Fallback: POST https://api.sarvam.ai/translate with minimal payload
    ///
    /// Status Codes:
    ///   - 200 OK: Valid key
    ///   - 401/403: Invalid/Revoked key
    ///   - 429: Rate limit / Quota exceeded
    /// </summary>
    [ApiProvider]
    public class SarvamAIProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "Sarvam AI";
        public override ApiTypeEnum ApiType => ApiTypeEnum.SarvamAI;

        public override IEnumerable<string> RegexPatterns =>
        [
            // Primary env var and config names
            @"SARVAM_API_KEY",
            @"SARVAM_KEY",
            @"SARVAM_SUBSCRIPTION_KEY",
            @"SHRAVAM_API_KEY",
            @"SHRAVAM_KEY",

            // Context-aware value extraction patterns
            @"SARVAM_API_KEY\s*[=:]\s*['""]?([A-Za-z0-9\-_]{20,})['""]?",
            @"SARVAM_KEY\s*[=:]\s*['""]?([A-Za-z0-9\-_]{20,})['""]?",
            @"SARVAM_SUBSCRIPTION_KEY\s*[=:]\s*['""]?([A-Za-z0-9\-_]{20,})['""]?",
            @"api-subscription-key\s*[=:]\s*['""]?([A-Za-z0-9\-_]{20,})['""]?",
            @"sarvam[._-]?api[._-]?key\s*[=:]\s*['""]?([A-Za-z0-9\-_]{20,})['""]?",
            @"shravam[._-]?api[._-]?key\s*[=:]\s*['""]?([A-Za-z0-9\-_]{20,})['""]?"
        ];

        public SarvamAIProvider() : base() { }
        public SarvamAIProvider(ILogger<SarvamAIProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(string apiKey, HttpClient httpClient)
        {
            try
            {
                // Step 1: Check models endpoint with api-subscription-key header
                using var modelsRequest = new HttpRequestMessage(HttpMethod.Get, "https://api.sarvam.ai/v2/models");
                modelsRequest.Headers.Add("api-subscription-key", apiKey);

                var modelsResponse = await httpClient.SendAsync(modelsRequest);
                string modelsBody = await modelsResponse.Content.ReadAsStringAsync();

                _logger?.LogDebug("Sarvam AI models response: Status={StatusCode}, Body={Body}",
                    modelsResponse.StatusCode, TruncateResponse(modelsBody));

                if (IsSuccessStatusCode(modelsResponse.StatusCode))
                {
                    var result = ValidationResult.Success(modelsResponse.StatusCode, "Valid Sarvam AI key");
                    var metadata = new Dictionary<string, object>
                    {
                        ["authentication_valid"] = true,
                        ["tested_endpoint"] = "https://api.sarvam.ai/v2/models"
                    };

                    try
                    {
                        using var doc = JsonDocument.Parse(modelsBody);
                        if (doc.RootElement.ValueKind == JsonValueKind.Array)
                        {
                            metadata["models_count"] = doc.RootElement.GetArrayLength();
                        }
                        else if (doc.RootElement.TryGetProperty("data", out var dataArr) && dataArr.ValueKind == JsonValueKind.Array)
                        {
                            metadata["models_count"] = dataArr.GetArrayLength();
                        }
                    }
                    catch { }

                    result.Metadata = metadata;
                    result.RawResponse = modelsBody;
                    return result;
                }

                if (modelsResponse.StatusCode == HttpStatusCode.Unauthorized || modelsResponse.StatusCode == HttpStatusCode.Forbidden)
                {
                    var result = ValidationResult.IsUnauthorized(modelsResponse.StatusCode, "Invalid Sarvam AI API key");
                    result.RawResponse = modelsBody;
                    return result;
                }

                if ((int)modelsResponse.StatusCode == 429)
                {
                    return new ValidationResult
                    {
                        Status = ValidationAttemptStatus.ValidationUnavailable,
                        HttpStatusCode = modelsResponse.StatusCode,
                        Detail = "Sarvam AI rate limit exceeded (HTTP 429)",
                        RawResponse = modelsBody
                    };
                }

                // Step 2: Fallback to lightweight translation check
                using var translateRequest = new HttpRequestMessage(HttpMethod.Post, "https://api.sarvam.ai/translate");
                translateRequest.Headers.Add("api-subscription-key", apiKey);
                translateRequest.Content = new StringContent(
                    JsonSerializer.Serialize(new
                    {
                        input = "Hi",
                        source_language_code = "en-IN",
                        target_language_code = "hi-IN"
                    }),
                    Encoding.UTF8,
                    "application/json"
                );

                var translateResponse = await httpClient.SendAsync(translateRequest);
                string translateBody = await translateResponse.Content.ReadAsStringAsync();

                _logger?.LogDebug("Sarvam AI translate fallback response: Status={StatusCode}, Body={Body}",
                    translateResponse.StatusCode, TruncateResponse(translateBody));

                if (IsSuccessStatusCode(translateResponse.StatusCode))
                {
                    var result = ValidationResult.Success(translateResponse.StatusCode, "Valid Sarvam AI key (tested via translate)");
                    result.Metadata = new Dictionary<string, object>
                    {
                        ["authentication_valid"] = true,
                        ["tested_endpoint"] = "https://api.sarvam.ai/translate"
                    };
                    result.RawResponse = translateBody;
                    return result;
                }

                if (translateResponse.StatusCode == HttpStatusCode.Unauthorized || translateResponse.StatusCode == HttpStatusCode.Forbidden)
                {
                    var result = ValidationResult.IsUnauthorized(translateResponse.StatusCode, "Invalid Sarvam AI API key");
                    result.RawResponse = translateBody;
                    return result;
                }

                if ((int)translateResponse.StatusCode == 429)
                {
                    return new ValidationResult
                    {
                        Status = ValidationAttemptStatus.ValidationUnavailable,
                        HttpStatusCode = translateResponse.StatusCode,
                        Detail = "Sarvam AI rate limit/quota exceeded (HTTP 429)",
                        RawResponse = translateBody
                    };
                }

                return ValidationResult.HasHttpError(translateResponse.StatusCode,
                    $"Sarvam AI verification failed: {TruncateResponse(translateBody)}");
            }
            catch (Exception ex)
            {
                return ValidationResult.HasNetworkError(ex.Message);
            }
        }

        protected override bool IsValidKeyFormat(string apiKey)
        {
            return !string.IsNullOrWhiteSpace(apiKey) &&
                   apiKey.Length >= 20 &&
                   apiKey.Length <= 128 &&
                   !apiKey.Contains(' ');
        }
    }
}

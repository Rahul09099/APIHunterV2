using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Base;
using UnsecuredAPIKeys.Providers.Common;

namespace UnsecuredAPIKeys.Providers.AI_Providers
{
    /// <summary>
    /// Provider for Google AI (Gemini) API keys.
    /// Auth: x-goog-api-key header (recommended by Google AI docs)
    /// 2-step verification strategy:
    ///   1. GET /v1beta/models (authenticates key & lists models supporting generateContent, with pagination)
    ///   2. POST /v1beta/{model}:generateContent (verifies live inference and quota/rate-limit status)
    /// </summary>
    [ApiProvider]
    public class GoogleProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "Google";
        public override ApiTypeEnum ApiType => ApiTypeEnum.GoogleAI;

        public override IEnumerable<string> RegexPatterns =>
        [
            // Classic Google API keys (AIzaSy...)
            @"AIza[0-9A-Za-z\-_]{35,40}",

            // Emerging Google Gemini Developer / Auth keys (AQ.xxx format)
            @"\bAQ\.[0-9A-Za-z\-_]{40,65}\b",

            // Context-aware assignment patterns (only capture real key shapes, not placeholders
            // such as "your_api_key" or "z.string()")
            @"(?:GEMINI_API_KEY|GOOGLE_API_KEY|GEMINI_KEY)\s*[=:]\s*['""]?(AIza[0-9A-Za-z\-_]{35,40}|AQ\.[0-9A-Za-z\-_]{40,65})['""]?"
        ];

        public GoogleProvider() : base() { }
        public GoogleProvider(ILogger<GoogleProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(
            string apiKey,
            HttpClient httpClient)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

                // ── Step 1: GET /v1beta/models with pagination support ─────────────────
                var allModels = new List<ModelInfo>();
                string? pageToken = null;
                int pageCount = 0;
                const int maxPages = 5;

                do
                {
                    string modelsUrl = "https://generativelanguage.googleapis.com/v1beta/models?pageSize=50";
                    if (!string.IsNullOrEmpty(pageToken))
                    {
                        modelsUrl += $"&pageToken={Uri.EscapeDataString(pageToken)}";
                    }

                    using var listRequest = new HttpRequestMessage(HttpMethod.Get, modelsUrl);
                    listRequest.Headers.Add("x-goog-api-key", apiKey);

                    var listResponse = await httpClient.SendAsync(listRequest, cts.Token);
                    var listBody = await listResponse.Content.ReadAsStringAsync(cts.Token);

                    if (!IsSuccessStatusCode(listResponse.StatusCode))
                    {
                        return ParseGoogleErrorResponse(listBody, listResponse.StatusCode, "Model listing failed");
                    }

                    var (models, nextPage) = ParseGoogleModelsWithNextPage(listBody);
                    if (models != null && models.Count > 0)
                    {
                        allModels.AddRange(models);
                    }

                    pageToken = nextPage;
                    pageCount++;
                }
                while (!string.IsNullOrEmpty(pageToken) && pageCount < maxPages);

                var modelsToUse = allModels
                    .Where(m => m.SupportedMethods?.Contains("generateContent") == true)
                    .ToList();

                if (modelsToUse.Count == 0)
                {
                    return ValidationResult.HasHttpError(
                        HttpStatusCode.OK,
                        "Model listing succeeded, but no active models with generateContent support were found.");
                }

                // ── Step 2: Select best inference model ────────────────────────────────
                // Prefer the newest flash model, then any flash, then pro, then anything else.
                var preferredModels = modelsToUse
                    .OrderByDescending(m => m.ModelId.Contains("gemini-3.8-flash", StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(m => m.ModelId.Contains("flash", StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(m => m.ModelId.Contains("pro", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                // Try a few models so a single deprecated/unavailable model cannot make a
                // perfectly valid key look invalid.
                ValidationResult? lastModelError = null;
                foreach (var candidate in preferredModels.Take(3))
                {
                    string modelPath = candidate.ModelId.StartsWith("models/", StringComparison.OrdinalIgnoreCase)
                        ? candidate.ModelId
                        : $"models/{candidate.ModelId}";

                    var generateEndpoint = $"https://generativelanguage.googleapis.com/v1beta/{modelPath}:generateContent";

                    using var generateRequest = new HttpRequestMessage(HttpMethod.Post, generateEndpoint);
                    generateRequest.Headers.Add("x-goog-api-key", apiKey);
                    generateRequest.Content = new StringContent(
                        JsonSerializer.Serialize(new
                        {
                            contents = new[]
                            {
                                new
                                {
                                    parts = new[] { new { text = "Hi" } }
                                }
                            }
                        }),
                        Encoding.UTF8,
                        "application/json");

                    var generateResponse = await httpClient.SendAsync(generateRequest, cts.Token);
                    var generateBody = await generateResponse.Content.ReadAsStringAsync(cts.Token);

                    _logger?.LogDebug(
                        "Google AI generateContent response ({Model}): Status={Status}, Body={Body}",
                        modelPath,
                        generateResponse.StatusCode,
                        TruncateResponse(generateBody));

                    // ── 200 OK: Full inference verified ───────────────────────────────
                    if (IsSuccessStatusCode(generateResponse.StatusCode))
                    {
                        // Check if Google reported leaked key inside 200 response body
                        if (IsLeakedKeyResponse(generateBody))
                        {
                            var leakedResult = ValidationResult.IsUnauthorized(
                                HttpStatusCode.Forbidden,
                                "Key reported as leaked by Google");
                            leakedResult.Metadata = new Dictionary<string, object>
                            {
                                ["is_leaked"] = true,
                                ["security_status"] = "LEAKED_KEY"
                            };
                            leakedResult.RawResponse = TruncateResponse(generateBody);
                            return leakedResult;
                        }

                        var result = ValidationResult.Success(
                            generateResponse.StatusCode,
                            "Content generation successful");
                        result.AvailableModels = modelsToUse;
                        result.Metadata = new Dictionary<string, object>
                        {
                            ["authentication_valid"] = true,
                            ["inference_tested"] = true,
                            ["inference_working"] = true,
                            ["tested_model"] = modelPath,
                            ["api_endpoint_version"] = "v1beta",
                            ["total_models_available"] = allModels.Count,
                            ["models_scan_truncated"] = !string.IsNullOrEmpty(pageToken)
                        };
                        result.RawResponse = TruncateResponse(generateBody);
                        return result;
                    }

                    // A 404 means this particular model is retired/unavailable; try the next one.
                    if (generateResponse.StatusCode == HttpStatusCode.NotFound)
                    {
                        lastModelError = ParseGoogleErrorResponse(generateBody, generateResponse.StatusCode, $"Model unavailable: {modelPath}", modelsToUse, modelPath);
                        continue;
                    }

                    // Any other error (401/403/429/5xx) is decisive for the key itself.
                    return ParseGoogleErrorResponse(generateBody, generateResponse.StatusCode, $"Generation failed on {modelPath}", modelsToUse, modelPath);
                }

                // Every candidate model was retired/unavailable. Step 1 already authenticated
                // the key via the model-listing call, so do not mark a working key invalid.
                var authOnlyResult = ValidationResult.Success(
                    HttpStatusCode.OK,
                    "Key authenticated via model listing (no runnable generateContent model found)");
                authOnlyResult.AvailableModels = modelsToUse;
                authOnlyResult.Metadata = new Dictionary<string, object>
                {
                    ["authentication_valid"] = true,
                    ["inference_tested"] = false,
                    ["total_models_available"] = allModels.Count
                };
                authOnlyResult.RawResponse = lastModelError?.RawResponse;
                return authOnlyResult;
            }
            catch (OperationCanceledException)
            {
                return ValidationResult.HasNetworkError("Google AI request timed out (15s limit reached)");
            }
            catch (Exception ex)
            {
                return ValidationResult.HasNetworkError(ex.Message);
            }
        }

        protected override bool IsValidKeyFormat(string apiKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey)) return false;

            // Flexible candidate validation: allows classic AIza... and new AQ.xxx formats
            return (apiKey.StartsWith("AIza", StringComparison.Ordinal) ||
                    apiKey.StartsWith("AQ.", StringComparison.Ordinal) ||
                    apiKey.StartsWith("AQ-", StringComparison.Ordinal))
                && apiKey.Length >= 30
                && apiKey.Length <= 128
                && !apiKey.Contains(' ');
        }

        private static bool IsLeakedKeyResponse(string body)
        {
            return body.Contains("leaked", StringComparison.OrdinalIgnoreCase) ||
                   body.Contains("reported as leaked", StringComparison.OrdinalIgnoreCase) ||
                   body.Contains("API_KEY_LEAKED", StringComparison.OrdinalIgnoreCase);
        }

        private static ValidationResult ParseGoogleErrorResponse(
            string jsonBody,
            HttpStatusCode statusCode,
            string defaultErrorPrefix,
            List<ModelInfo>? availableModels = null,
            string? testedModel = null)
        {
            string? errorCode = null;
            string? errorMessage = null;
            string? errorStatus = null;
            string? errorReason = null;

            try
            {
                using var doc = JsonDocument.Parse(jsonBody);
                if (doc.RootElement.TryGetProperty("error", out var errorObj))
                {
                    if (errorObj.TryGetProperty("code", out var codeEl)) errorCode = codeEl.ToString();
                    if (errorObj.TryGetProperty("message", out var msgEl) && msgEl.ValueKind == JsonValueKind.String)
                        errorMessage = msgEl.GetString();
                    if (errorObj.TryGetProperty("status", out var statusEl) && statusEl.ValueKind == JsonValueKind.String)
                        errorStatus = statusEl.GetString();

                    if (errorObj.TryGetProperty("details", out var detailsArr) && detailsArr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var detail in detailsArr.EnumerateArray())
                        {
                            if (detail.TryGetProperty("reason", out var reasonEl) && reasonEl.ValueKind == JsonValueKind.String)
                            {
                                errorReason = reasonEl.GetString();
                                break;
                            }
                        }
                    }
                }
            }
            catch { }

            // 1. Leaked key detection
            if (IsLeakedKeyResponse(jsonBody) || errorReason == "API_KEY_LEAKED")
            {
                var result = ValidationResult.IsUnauthorized(
                    statusCode,
                    "Key reported as leaked by Google");
                result.Metadata = new Dictionary<string, object>
                {
                    ["is_leaked"] = true,
                    ["security_status"] = "LEAKED_KEY"
                };
                result.RawResponse = TruncateResponse(jsonBody);
                return result;
            }

            // 2. Authentication failure / invalid key
            if (statusCode == HttpStatusCode.Unauthorized ||
                errorStatus == "UNAUTHENTICATED" ||
                errorReason == "API_KEY_INVALID" ||
                errorReason == "API_KEY_SERVICE_BLOCKED")
            {
                var result = ValidationResult.IsUnauthorized(
                    statusCode,
                    errorMessage ?? "Invalid Google AI API key");
                result.RawResponse = TruncateResponse(jsonBody);
                return result;
            }

            // 3. 403 Forbidden: Differentiate access/policy restrictions from invalid credentials
            if (statusCode == HttpStatusCode.Forbidden)
            {
                if (errorStatus == "PERMISSION_DENIED" && errorReason != "API_KEY_INVALID")
                {
                    var restrictedResult = new ValidationResult
                    {
                        Status = ValidationAttemptStatus.ValidationUnavailable,
                        HttpStatusCode = statusCode,
                        Detail = $"Google AI returned 403 Forbidden ({errorReason ?? "PERMISSION_DENIED"}): {TruncateResponse(errorMessage ?? "Access restricted")}"
                    };
                    restrictedResult.Metadata = new Dictionary<string, object>
                    {
                        ["authentication_valid"] = true,
                        ["access_restricted"] = true,
                        ["permission_reason"] = errorReason ?? "PERMISSION_DENIED"
                    };
                    restrictedResult.RawResponse = TruncateResponse(jsonBody);
                    return restrictedResult;
                }

                var invalidResult = ValidationResult.IsUnauthorized(
                    statusCode,
                    errorMessage ?? "Invalid Google AI API key (403 Forbidden)");
                invalidResult.RawResponse = TruncateResponse(jsonBody);
                return invalidResult;
            }

            // 4. Quota vs Rate Limit handling (429 / RESOURCE_EXHAUSTED)
            if ((int)statusCode == 429 || errorStatus == "RESOURCE_EXHAUSTED")
            {
                var lowerMessage = errorMessage ?? string.Empty;

                // A per-minute throttling error is NOT exhausted quota. Google returns
                // "Quota exceeded for quota metric 'Generate Content API requests per minute'"
                // for ordinary rate limiting, which would otherwise be misreported as
                // ValidNoCredits. Only treat daily/credit exhaustion as a quota failure.
                bool isRateLimit =
                    errorReason == "RATE_LIMIT_EXCEEDED" ||
                    lowerMessage.Contains("per minute", StringComparison.OrdinalIgnoreCase) ||
                    lowerMessage.Contains("per-minute", StringComparison.OrdinalIgnoreCase) ||
                    lowerMessage.Contains("rate limit", StringComparison.OrdinalIgnoreCase);

                bool isQuotaExhausted = !isRateLimit &&
                    (errorReason == "QUOTA_EXCEEDED" ||
                     lowerMessage.Contains("quota", StringComparison.OrdinalIgnoreCase) ||
                     lowerMessage.Contains("insufficient", StringComparison.OrdinalIgnoreCase));

                if (isQuotaExhausted)
                {
                    var result = new ValidationResult
                    {
                        Status = ValidationAttemptStatus.Valid,
                        HttpStatusCode = statusCode,
                        IsQuotaExceeded = true,
                        Detail = "Valid Google AI key — quota exhausted (RESOURCE_EXHAUSTED)"
                    };
                    result.AvailableModels = availableModels;
                    result.Metadata = new Dictionary<string, object>
                    {
                        ["authentication_valid"] = true,
                        ["quota_exceeded"] = true,
                        ["tested_model"] = testedModel ?? "unknown"
                    };
                    result.RawResponse = TruncateResponse(jsonBody);
                    return result;
                }

                // General Rate Limit / Throttling
                var rateLimitResult = new ValidationResult
                {
                    Status = ValidationAttemptStatus.ValidationUnavailable,
                    HttpStatusCode = statusCode,
                    Detail = "Google AI rate limit exceeded (HTTP 429)"
                };
                rateLimitResult.AvailableModels = availableModels;
                rateLimitResult.Metadata = new Dictionary<string, object>
                {
                    ["authentication_valid"] = true,
                    ["inference_limited"] = true,
                    ["tested_model"] = testedModel ?? "unknown"
                };
                rateLimitResult.RawResponse = TruncateResponse(jsonBody);
                return rateLimitResult;
            }

            // 5. Server error (5xx)
            if ((int)statusCode >= 500)
            {
                var result = new ValidationResult
                {
                    Status = ValidationAttemptStatus.ValidationUnavailable,
                    HttpStatusCode = statusCode,
                    Detail = $"Google AI server error (HTTP {(int)statusCode})"
                };
                result.RawResponse = TruncateResponse(jsonBody);
                return result;
            }

            // 6. Generic HTTP Error
            var httpErr = ValidationResult.HasHttpError(
                statusCode,
                $"{defaultErrorPrefix}: {TruncateResponse(errorMessage ?? jsonBody)}");
            httpErr.AvailableModels = availableModels;
            httpErr.RawResponse = TruncateResponse(jsonBody);
            return httpErr;
        }

        private (List<ModelInfo>? Models, string? NextPageToken) ParseGoogleModelsWithNextPage(string jsonResponse)
        {
            try
            {
                using var doc = JsonDocument.Parse(jsonResponse);
                string? nextPage = null;

                if (doc.RootElement.TryGetProperty("nextPageToken", out var nextEl) && nextEl.ValueKind == JsonValueKind.String)
                {
                    nextPage = nextEl.GetString();
                }

                if (!doc.RootElement.TryGetProperty("models", out var modelsArray))
                    return (null, nextPage);

                var models = new List<ModelInfo>();

                foreach (var modelElement in modelsArray.EnumerateArray())
                {
                    var model = new ModelInfo
                    {
                        ModelId = modelElement.GetProperty("name").GetString() ?? ""
                    };

                    if (modelElement.TryGetProperty("supportedGenerationMethods", out var methods))
                    {
                        model.SupportedMethods = methods
                            .EnumerateArray()
                            .Select(m => m.GetString())
                            .Where(s => s != null)
                            .ToList()!;
                    }

                    models.Add(model);
                }

                return (models, nextPage);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error parsing Google models response");
                return (null, null);
            }
        }
    }
}

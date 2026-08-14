using System;
using System.Collections.Generic;
using System.Linq;
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

            // New Google Gemini Developer API keys (AQ.xxx format)
            @"\bAQ\.[0-9A-Za-z\-_]{40,65}\b",

            // Context-aware assignment patterns
            @"(?:GEMINI_API_KEY|GOOGLE_API_KEY|GEMINI_KEY)\s*[=:]\s*['""]?([A-Za-z0-9\-_.]+)['""]?"
        ];

        public GoogleProvider() : base() { }
        public GoogleProvider(ILogger<GoogleProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(
            string apiKey,
            HttpClient httpClient)
        {
            try
            {
                // ── Step 1: GET /v1beta/models with pagination support ─────────────────
                var allModels = new List<ModelInfo>();
                string? pageToken = null;
                int pageCount = 0;
                const int maxPages = 3;

                do
                {
                    string modelsUrl = "https://generativelanguage.googleapis.com/v1beta/models?pageSize=50";
                    if (!string.IsNullOrEmpty(pageToken))
                    {
                        modelsUrl += $"&pageToken={Uri.EscapeDataString(pageToken)}";
                    }

                    using var listRequest = new HttpRequestMessage(HttpMethod.Get, modelsUrl);
                    listRequest.Headers.Add("x-goog-api-key", apiKey);

                    var listResponse = await httpClient.SendAsync(listRequest);
                    var listBody = await listResponse.Content.ReadAsStringAsync();

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

                // ── Step 2: Prioritize best inference model ────────────────────────────
                // Prefer modern fast/flash models, then pro models, then first available
                var selectedModel = modelsToUse.FirstOrDefault(m => m.ModelId.Contains("2.5-flash", StringComparison.OrdinalIgnoreCase))
                                 ?? modelsToUse.FirstOrDefault(m => m.ModelId.Contains("2.0-flash", StringComparison.OrdinalIgnoreCase))
                                 ?? modelsToUse.FirstOrDefault(m => m.ModelId.Contains("1.5-flash", StringComparison.OrdinalIgnoreCase))
                                 ?? modelsToUse.FirstOrDefault(m => m.ModelId.Contains("flash", StringComparison.OrdinalIgnoreCase))
                                 ?? modelsToUse.FirstOrDefault(m => m.ModelId.Contains("pro", StringComparison.OrdinalIgnoreCase))
                                 ?? modelsToUse.First();

                // Build exact endpoint URL: /v1beta/models/{model}:generateContent
                string modelPath = selectedModel.ModelId.StartsWith("models/", StringComparison.OrdinalIgnoreCase)
                    ? selectedModel.ModelId
                    : $"models/{selectedModel.ModelId}";

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

                var generateResponse = await httpClient.SendAsync(generateRequest);
                var generateBody = await generateResponse.Content.ReadAsStringAsync();

                _logger?.LogDebug(
                    "Google AI generateContent response ({Model}): Status={Status}, Body={Body}",
                    modelPath,
                    generateResponse.StatusCode,
                    TruncateResponse(generateBody));

                // ── 200 OK: Full inference verified ───────────────────────────────────
                if (IsSuccessStatusCode(generateResponse.StatusCode))
                {
                    // Check if Google reported leaked key inside 200 response body
                    if (IsLeakedKeyResponse(generateBody))
                    {
                        var leakedResult = ValidationResult.IsUnauthorized(
                            HttpStatusCode.Forbidden,
                            "Key reported as leaked by Google");
                        leakedResult.RawResponse = generateBody;
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
                        ["total_models_available"] = allModels.Count
                    };
                    result.RawResponse = generateBody;
                    return result;
                }

                // ── Error Response Inspection ─────────────────────────────────────────
                return ParseGoogleErrorResponse(generateBody, generateResponse.StatusCode, $"Generation failed on {modelPath}", modelsToUse, modelPath);
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
                    if (errorObj.TryGetProperty("message", out var msgEl)) errorMessage = msgEl.GetString();
                    if (errorObj.TryGetProperty("status", out var statusEl)) errorStatus = statusEl.GetString();

                    if (errorObj.TryGetProperty("details", out var detailsArr) && detailsArr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var detail in detailsArr.EnumerateArray())
                        {
                            if (detail.TryGetProperty("reason", out var reasonEl))
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
                result.RawResponse = jsonBody;
                return result;
            }

            // 2. Authentication failure / invalid key
            if (statusCode == HttpStatusCode.Unauthorized ||
                statusCode == HttpStatusCode.Forbidden ||
                errorStatus == "UNAUTHENTICATED" ||
                errorReason == "API_KEY_INVALID" ||
                errorReason == "API_KEY_SERVICE_BLOCKED")
            {
                var result = ValidationResult.IsUnauthorized(
                    statusCode,
                    errorMessage ?? "Invalid Google AI API key");
                result.RawResponse = jsonBody;
                return result;
            }

            // 3. Quota vs Rate Limit handling (429 / RESOURCE_EXHAUSTED)
            if ((int)statusCode == 429 || errorStatus == "RESOURCE_EXHAUSTED")
            {
                bool isQuotaExhausted = errorReason == "QUOTA_EXCEEDED" ||
                    (errorMessage != null && (errorMessage.Contains("quota", StringComparison.OrdinalIgnoreCase) || errorMessage.Contains("insufficient", StringComparison.OrdinalIgnoreCase)));

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
                    result.RawResponse = jsonBody;
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
                rateLimitResult.RawResponse = jsonBody;
                return rateLimitResult;
            }

            // 4. Server error (5xx)
            if ((int)statusCode >= 500)
            {
                var result = new ValidationResult
                {
                    Status = ValidationAttemptStatus.ValidationUnavailable,
                    HttpStatusCode = statusCode,
                    Detail = $"Google AI server error (HTTP {(int)statusCode})"
                };
                result.RawResponse = jsonBody;
                return result;
            }

            // 5. Generic HTTP Error
            var httpErr = ValidationResult.HasHttpError(
                statusCode,
                $"{defaultErrorPrefix}: {TruncateResponse(errorMessage ?? jsonBody)}");
            httpErr.AvailableModels = availableModels;
            httpErr.RawResponse = jsonBody;
            return httpErr;
        }

        private (List<ModelInfo>? Models, string? NextPageToken) ParseGoogleModelsWithNextPage(string jsonResponse)
        {
            try
            {
                using var doc = JsonDocument.Parse(jsonResponse);
                string? nextPage = null;

                if (doc.RootElement.TryGetProperty("nextPageToken", out var nextEl))
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

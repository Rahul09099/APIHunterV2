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
    ///   1. GET /v1beta/models (authenticates key & lists models supporting generateContent)
    ///   2. POST /v1beta/{model}:generateContent (verifies live inference and quota status)
    /// </summary>
    [ApiProvider]
    public class GoogleProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "Google";
        public override ApiTypeEnum ApiType => ApiTypeEnum.GoogleAI;

        public override IEnumerable<string> RegexPatterns =>
        [
            @"AIza[0-9A-Za-z\-_]{35,40}",
            @"\bAQ\.[0-9A-Za-z\-_]{40,65}\b",
            @"(?:GEMINI_API_KEY|GOOGLE_API_KEY|GEMINI_KEY)\s*[=:]\s*['""]?(AQ\.[0-9A-Za-z\-_]{40,65}|AIza[0-9A-Za-z\-_]{35,40})['""]?"
        ];

        public GoogleProvider() : base() { }
        public GoogleProvider(ILogger<GoogleProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(
            string apiKey,
            HttpClient httpClient)
        {
            try
            {
                // Step 1: GET /v1beta/models with x-goog-api-key header
                using var listBetaRequest = new HttpRequestMessage(HttpMethod.Get, "https://generativelanguage.googleapis.com/v1beta/models");
                listBetaRequest.Headers.Add("x-goog-api-key", apiKey);

                var listBetaResponse = await httpClient.SendAsync(listBetaRequest);
                var listBetaBody = await listBetaResponse.Content.ReadAsStringAsync();

                if (!IsSuccessStatusCode(listBetaResponse.StatusCode))
                {
                    ValidationResult errResult;
                    if (listBetaBody.Contains("leaked", StringComparison.OrdinalIgnoreCase) ||
                        listBetaBody.Contains("reported as leaked", StringComparison.OrdinalIgnoreCase))
                    {
                        errResult = ValidationResult.IsUnauthorized(
                            HttpStatusCode.Forbidden,
                            "Key reported as leaked by Google");
                    }
                    else if (listBetaResponse.StatusCode == HttpStatusCode.Unauthorized ||
                             listBetaResponse.StatusCode == HttpStatusCode.Forbidden)
                    {
                        errResult = ValidationResult.IsUnauthorized(listBetaResponse.StatusCode, "Invalid Google AI API key");
                    }
                    else if ((int)listBetaResponse.StatusCode == 429)
                    {
                        errResult = new ValidationResult
                        {
                            Status = ValidationAttemptStatus.ValidationUnavailable,
                            HttpStatusCode = listBetaResponse.StatusCode,
                            Detail = "Google AI rate limit exceeded (HTTP 429)"
                        };
                    }
                    else
                    {
                        errResult = ValidationResult.HasHttpError(listBetaResponse.StatusCode,
                            $"Model listing failed: {TruncateResponse(listBetaBody)}");
                    }
                    errResult.RawResponse = listBetaBody;
                    return errResult;
                }

                var modelsToUse = ParseGoogleModels(listBetaBody)?
                    .Where(m => m.SupportedMethods?.Contains("generateContent") == true)
                    .ToList();

                if (modelsToUse == null || modelsToUse.Count == 0)
                {
                    var err = ValidationResult.HasHttpError(
                        listBetaResponse.StatusCode,
                        "Model listing succeeded but no models with generateContent support were found.");
                    err.RawResponse = listBetaBody;
                    return err;
                }

                // Prefer fast / flash / pro models, or fallback to first model supporting generateContent
                var selectedModel = modelsToUse.FirstOrDefault(m => m.ModelId.Contains("flash", StringComparison.OrdinalIgnoreCase))
                                 ?? modelsToUse.FirstOrDefault(m => m.ModelId.Contains("pro", StringComparison.OrdinalIgnoreCase))
                                 ?? modelsToUse.First();

                // Step 2: POST /v1beta/{model}:generateContent
                var generateEndpoint = $"https://generativelanguage.googleapis.com/v1beta/{selectedModel.ModelId}:generateContent";

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
                    selectedModel.ModelId,
                    generateResponse.StatusCode,
                    TruncateResponse(generateBody));

                ValidationResult result;

                if (IsSuccessStatusCode(generateResponse.StatusCode))
                {
                    if (generateBody.Contains("leaked", StringComparison.OrdinalIgnoreCase) ||
                        generateBody.Contains("reported as leaked", StringComparison.OrdinalIgnoreCase))
                    {
                        result = ValidationResult.IsUnauthorized(
                            HttpStatusCode.Forbidden,
                            "Key reported as leaked by Google (in success body)");
                        result.RawResponse = generateBody;
                        return result;
                    }

                    result = ValidationResult.Success(
                        generateResponse.StatusCode,
                        "Content generation successful");
                    result.AvailableModels = modelsToUse;
                    result.Metadata = new Dictionary<string, object>
                    {
                        ["authentication_valid"] = true,
                        ["inference_tested"] = true,
                        ["inference_working"] = true,
                        ["tested_model"] = selectedModel.ModelId,
                        ["api_endpoint_version"] = "v1beta"
                    };
                    result.RawResponse = generateBody;
                    return result;
                }

                if (generateResponse.StatusCode == HttpStatusCode.Unauthorized ||
                    generateResponse.StatusCode == HttpStatusCode.Forbidden)
                {
                    if (generateBody.Contains("leaked", StringComparison.OrdinalIgnoreCase))
                    {
                        result = ValidationResult.IsUnauthorized(
                            generateResponse.StatusCode,
                            "Key reported as leaked by Google");
                    }
                    else
                    {
                        result = ValidationResult.IsUnauthorized(generateResponse.StatusCode);
                    }
                    result.Metadata = new Dictionary<string, object>
                    {
                        ["authentication_valid"] = false,
                        ["inference_tested"] = true,
                        ["inference_working"] = false,
                        ["tested_model"] = selectedModel.ModelId
                    };
                    result.RawResponse = generateBody;
                    return result;
                }

                if ((int)generateResponse.StatusCode == 429)
                {
                    result = ValidationResult.Success(
                        generateResponse.StatusCode,
                        "Valid key; generation request rate/quota limited");
                    result.IsQuotaExceeded = true;
                    result.AvailableModels = modelsToUse;
                    result.Metadata = new Dictionary<string, object>
                    {
                        ["authentication_valid"] = true,
                        ["inference_tested"] = true,
                        ["inference_working"] = false,
                        ["inference_limited"] = true,
                        ["tested_model"] = selectedModel.ModelId,
                        ["api_endpoint_version"] = "v1beta"
                    };
                    result.RawResponse = generateBody;
                    return result;
                }

                if (generateBody.Contains("leaked", StringComparison.OrdinalIgnoreCase) ||
                    generateBody.Contains("reported as leaked", StringComparison.OrdinalIgnoreCase))
                {
                    result = ValidationResult.IsUnauthorized(
                        generateResponse.StatusCode,
                        "Key reported as leaked by Google");
                    result.RawResponse = generateBody;
                    return result;
                }

                result = ValidationResult.HasHttpError(
                    generateResponse.StatusCode,
                    $"Generation failed: {TruncateResponse(generateBody)}");
                result.AvailableModels = modelsToUse;
                result.Metadata = new Dictionary<string, object>
                {
                    ["authentication_valid"] = true,
                    ["inference_tested"] = true,
                    ["inference_working"] = false,
                    ["tested_model"] = selectedModel.ModelId,
                    ["api_endpoint_version"] = "v1beta"
                };
                result.RawResponse = generateBody;
                return result;
            }
            catch (Exception ex)
            {
                return ValidationResult.HasNetworkError(ex.Message);
            }
        }

        protected override bool IsValidKeyFormat(string apiKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey)) return false;

            // Classic Google API key: AIzaSy... (39-44 chars)
            if (apiKey.StartsWith("AIza", StringComparison.Ordinal) && apiKey.Length >= 39 && apiKey.Length <= 44)
                return true;

            // New Google Gemini Developer API key: AQ.xxxx (45-65 chars)
            if (apiKey.StartsWith("AQ.", StringComparison.Ordinal) && apiKey.Length >= 45 && apiKey.Length <= 65)
                return true;

            return false;
        }

        private List<ModelInfo>? ParseGoogleModels(string jsonResponse)
        {
            try
            {
                using var doc = JsonDocument.Parse(jsonResponse);

                if (!doc.RootElement.TryGetProperty("models", out var modelsArray))
                    return null;

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

                return models;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error parsing Google models response");
                return null;
            }
        }
    }
}

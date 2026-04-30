using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Base;
using UnsecuredAPIKeys.Providers.Common;

namespace UnsecuredAPIKeys.Providers.AI_Providers
{
    [ApiProvider]
    public class GoogleProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "Google";
        public override ApiTypeEnum ApiType => ApiTypeEnum.GoogleAI;

        public override IEnumerable<string> RegexPatterns =>
        [
            @"AIza[0-9A-Za-z\-_]{35,40}"
        ];

        public GoogleProvider() : base() { }

        public GoogleProvider(ILogger<GoogleProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(
            string apiKey,
            HttpClient httpClient)
        {
            // 1. Check v1 endpoint (stable) - Strictest check for "leaked" status
            var listV1Endpoint = $"https://generativelanguage.googleapis.com/v1/models?key={apiKey}";
            var listV1Response = await httpClient.GetAsync(listV1Endpoint);
            var listV1Body = await listV1Response.Content.ReadAsStringAsync();

            // Console.WriteLine($"DEBUG Google v1 ListModels Status: {listV1Response.StatusCode}");
            
            // Check for leaked in v1
            if (listV1Response.StatusCode == HttpStatusCode.Unauthorized || 
                listV1Response.StatusCode == HttpStatusCode.Forbidden ||
                listV1Body.Contains("leaked", StringComparison.OrdinalIgnoreCase))
            {
                 if (listV1Body.Contains("leaked", StringComparison.OrdinalIgnoreCase) ||
                     listV1Body.Contains("reported as leaked", StringComparison.OrdinalIgnoreCase))
                 {
                     return ValidationResult.IsUnauthorized(
                         HttpStatusCode.Forbidden, 
                         "Key reported as leaked by Google (v1 check)");
                 }
            }

            // Parse v1 models
            var v1Models = ParseGoogleModels(listV1Body)?.Where(m => m.SupportedMethods?.Contains("generateContent") == true).ToList();
            
            // 2. Check v1beta endpoint (if v1 didn't explicitly fail with leaked, or just to get more info)
            var listBetaEndpoint = $"https://generativelanguage.googleapis.com/v1beta/models?key={apiKey}";
            var listBetaResponse = await httpClient.GetAsync(listBetaEndpoint);
            var listBetaBody = await listBetaResponse.Content.ReadAsStringAsync();

             // Check for leaked in v1beta
            if (listBetaBody.Contains("leaked", StringComparison.OrdinalIgnoreCase) ||
                listBetaBody.Contains("reported as leaked", StringComparison.OrdinalIgnoreCase))
            {
                 return ValidationResult.IsUnauthorized(
                     HttpStatusCode.Forbidden, 
                     "Key reported as leaked by Google (v1beta check)");
            }

            var betaModels = ParseGoogleModels(listBetaBody)?.Where(m => m.SupportedMethods?.Contains("generateContent") == true).ToList();
            
            // DECIDE: Use v1 if available, otherwise v1beta
            string endpointBase = "v1beta";
            List<ModelInfo>? modelsToUse = betaModels;

            if (listV1Response.IsSuccessStatusCode && v1Models != null && v1Models.Count > 0)
            {
                endpointBase = "v1";
                modelsToUse = v1Models;
            }
            else if (modelsToUse == null || modelsToUse.Count == 0)
            {
                 // Both failed to find models
                 return ValidationResult.HasHttpError(
                    listBetaResponse.StatusCode,
                    $"Model listing failed or no models found: {TruncateResponse(listBetaBody)}");
            }
            
            // Console.WriteLine($"DEBUG Google: Using {endpointBase} for generation check with {modelsToUse.Count} models");

            // Prefer newer / cheaper models first
            var preferredOrder = new[]
            {
                "gemini-2.0-flash",
                "gemini-1.5-flash",
                "gemini-1.5-pro",
                "gemini-pro"
            };

            var selectedModel =
                modelsToUse.FirstOrDefault(m => preferredOrder.Any(p => m.ModelId.Contains(p)))
                ?? modelsToUse.First();

            // 3. generateContent call (quota-aware)
            var generateEndpoint = $"https://generativelanguage.googleapis.com/{endpointBase}/{selectedModel.ModelId}:generateContent?key={apiKey}";
            
            // Console.WriteLine($"DEBUG Google Generate Endpoint: {generateEndpoint}");

            using var generateRequest = new HttpRequestMessage(
                HttpMethod.Post,
                generateEndpoint);

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
                System.Text.Encoding.UTF8,
                "application/json");

            var generateResponse = await httpClient.SendAsync(generateRequest);
            var generateBody = await generateResponse.Content.ReadAsStringAsync();

            _logger?.LogDebug(
                "Google AI generateContent response ({Model}): Status={Status}, Body={Body}",
                selectedModel.ModelId,
                generateResponse.StatusCode,
                TruncateResponse(generateBody));
            
            // DEBUG: Print full body to find hidden warnings
            // Console.WriteLine($"DEBUG Google Generate Status: {generateResponse.StatusCode}");
            // Console.WriteLine($"DEBUG Google Generate Body: {generateBody}"); // Too verbose?

            // 4. Status-code classification
            if (IsSuccessStatusCode(generateResponse.StatusCode))
            {
                // Even on success, check for leaked message
                if (generateBody.Contains("leaked", StringComparison.OrdinalIgnoreCase) ||
                    generateBody.Contains("reported as leaked", StringComparison.OrdinalIgnoreCase))
                {
                     return ValidationResult.IsUnauthorized(
                        HttpStatusCode.Forbidden, 
                        "Key reported as leaked by Google (in success body)");
                }

                var ok = ValidationResult.Success(
                    generateResponse.StatusCode,
                    "Content generation successful");

                ok.AvailableModels = modelsToUse;
                return ok;
            }

            if (generateResponse.StatusCode == HttpStatusCode.Unauthorized ||
                generateResponse.StatusCode == HttpStatusCode.Forbidden)
            {
                if (generateBody.Contains("leaked", StringComparison.OrdinalIgnoreCase))
                {
                    return ValidationResult.IsUnauthorized(
                        generateResponse.StatusCode, 
                        "Key reported as leaked by Google");
                }
                return ValidationResult.IsUnauthorized(generateResponse.StatusCode);
            }

            if ((int)generateResponse.StatusCode == 429)
            {
                // 429 = quota exhausted — the key IS valid but has no remaining quota
                // Returning IsUnauthorized here was wrong — it would mark the key as Invalid
                var quotaResult = ValidationResult.Success(generateResponse.StatusCode,
                    "quota exhausted");
                quotaResult.AvailableModels = modelsToUse;
                return quotaResult;
            }

            // Check for leaked key message globally
            if (generateBody.Contains("leaked", StringComparison.OrdinalIgnoreCase) ||
                generateBody.Contains("reported as leaked", StringComparison.OrdinalIgnoreCase))
            {
                return ValidationResult.IsUnauthorized(
                    generateResponse.StatusCode, 
                    "Key reported as leaked by Google");
            }

            var error = ValidationResult.HasHttpError(
                generateResponse.StatusCode,
                $"Generation failed: {TruncateResponse(generateBody)}");

            error.AvailableModels = modelsToUse;
            return error;
        }

        protected override bool IsValidKeyFormat(string apiKey)
        {
            return !string.IsNullOrWhiteSpace(apiKey)
                && apiKey.StartsWith("AIza")
                && apiKey.Length >= 39
                && apiKey.Length <= 45;
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

                    if (modelElement.TryGetProperty(
                        "supportedGenerationMethods",
                        out var methods))
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

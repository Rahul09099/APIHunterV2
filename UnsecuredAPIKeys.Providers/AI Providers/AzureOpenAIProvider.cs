using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Base;
using UnsecuredAPIKeys.Providers.Common;

namespace UnsecuredAPIKeys.Providers.AI_Providers
{
    /// <summary>
    /// Provider for Azure OpenAI API keys.
    /// Keys are 32-character hex strings passed via the "api-key" header.
    /// Azure OpenAI endpoints are tenant-specific (e.g., https://{resource}.openai.azure.com).
    /// Verification endpoint: GET {endpoint}/openai/models?api-version=2024-10-21
    /// </summary>
    [ApiProvider]
    public class AzureOpenAIProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "Azure OpenAI";
        public override ApiTypeEnum ApiType => ApiTypeEnum.AzureOpenAI;

        public override IEnumerable<string> RegexPatterns =>
        [
            @"(?i)\bAZURE_OPENAI_API_KEY\s*[:=]\s*['""]?([a-f0-9]{32})['""]?",
            @"(?i)\bAZURE_OPENAI_KEY\s*[:=]\s*['""]?([a-f0-9]{32})['""]?",
            @"(?i)\bAZURE_API_KEY\s*[:=]\s*['""]?([a-f0-9]{32})['""]?"
        ];

        public AzureOpenAIProvider() : base() { }
        public AzureOpenAIProvider(ILogger<AzureOpenAIProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(
            string apiKey, HttpClient httpClient)
        {
            string endpoint = "";
            string actualKey = apiKey;

            if (apiKey.Contains('|'))
            {
                var parts = apiKey.Split('|', 2);
                actualKey = parts[0].Trim();
                endpoint = parts[1].TrimEnd('/');
            }

            if (!string.IsNullOrEmpty(endpoint))
            {
                // SSRF Protection & Endpoint Host Validation
                if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
                    uri.Scheme != Uri.UriSchemeHttps ||
                    !IsValidAzureOpenAiHost(uri.Host))
                {
                    return ValidationResult.HasProviderSpecificError(
                        $"Invalid or untrusted Azure OpenAI endpoint host: {endpoint}");
                }

                var modelsUrl = $"{endpoint}/openai/models?api-version=2024-10-21";

                using var request = new HttpRequestMessage(HttpMethod.Get, modelsUrl);
                request.Headers.Add("api-key", actualKey);

                var response = await httpClient.SendAsync(request);
                var body = await response.Content.ReadAsStringAsync();

                _logger?.LogDebug("Azure OpenAI models response: Status={Status}, Body={Body}",
                    response.StatusCode, TruncateResponse(body));

                ValidationResult result;

                if (IsSuccessStatusCode(response.StatusCode))
                {
                    result = ValidationResult.Success(response.StatusCode, "Valid Azure OpenAI key");
                    result.AccountTier = uri.Host.Split('.')[0];

                    try
                    {
                        using var doc = JsonDocument.Parse(body);
                        if (doc.RootElement.TryGetProperty("data", out var models) && models.ValueKind == JsonValueKind.Array)
                        {
                            var modelList = new List<ModelInfo>();
                            foreach (var el in models.EnumerateArray())
                            {
                                var id = el.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
                                if (!string.IsNullOrEmpty(id))
                                {
                                    modelList.Add(new ModelInfo { ModelId = id, DisplayName = id });
                                }
                            }
                            result.AvailableModels = modelList;
                            result.Detail = $"Valid Azure OpenAI key ({modelList.Count} models available)";
                        }
                    }
                    catch (JsonException ex)
                    {
                        _logger?.LogDebug(ex, "Failed to parse Azure OpenAI models JSON response");
                    }
                }
                else if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    result = ValidationResult.IsUnauthorized(response.StatusCode, "Invalid or unauthorized Azure OpenAI key");
                }
                else if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    result = new ValidationResult
                    {
                        Status = ValidationAttemptStatus.ValidationUnavailable,
                        HttpStatusCode = response.StatusCode,
                        Detail = "Azure OpenAI access forbidden; key validity could not be determined."
                    };
                }
                else if ((int)response.StatusCode == 429)
                {
                    result = new ValidationResult
                    {
                        Status = ValidationAttemptStatus.ValidationUnavailable,
                        HttpStatusCode = response.StatusCode,
                        Detail = "Azure OpenAI rate limit exceeded (HTTP 429)"
                    };
                }
                else if (response.StatusCode == HttpStatusCode.RequestTimeout || (int)response.StatusCode >= 500)
                {
                    result = new ValidationResult
                    {
                        Status = ValidationAttemptStatus.ValidationUnavailable,
                        HttpStatusCode = response.StatusCode,
                        Detail = $"Azure OpenAI service temporarily unavailable (HTTP {(int)response.StatusCode})"
                    };
                }
                else
                {
                    result = ValidationResult.HasHttpError(response.StatusCode,
                        $"Azure OpenAI models request failed: {TruncateResponse(body)}");
                }

                result.RawResponse = body;
                return result;
            }

            return ValidationResult.HasProviderSpecificError(
                "Azure OpenAI key found but no endpoint URL available for verification. " +
                "Key stored as Unverified — manual verification required.");
        }

        protected override bool IsValidKeyFormat(string apiKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey)) return false;

            var key = apiKey.Contains('|') ? apiKey.Split('|')[0].Trim() : apiKey.Trim();
            return key.Length == 32 && key.All(c => char.IsAsciiHexDigit(c));
        }

        private static bool IsValidAzureOpenAiHost(string host)
        {
            if (string.IsNullOrWhiteSpace(host)) return false;

            var hostLower = host.ToLowerInvariant();

            // Block loopback, local IPs, metadata endpoints
            if (hostLower == "localhost" || hostLower.StartsWith("127.") || hostLower.StartsWith("169.254.") ||
                hostLower.StartsWith("10.") || hostLower.StartsWith("192.168."))
            {
                return false;
            }

            var labels = hostLower.Split('.');
            if (labels.Length < 4) return false;

            // Allow official Azure OpenAI and AI service domain suffixes (must have resource subdomain)
            return hostLower.EndsWith(".openai.azure.com", StringComparison.Ordinal) ||
                   hostLower.EndsWith(".cognitiveservices.azure.com", StringComparison.Ordinal) ||
                   hostLower.EndsWith(".ai.azure.com", StringComparison.Ordinal);
        }
    }
}

using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Base;
using UnsecuredAPIKeys.Providers.Common;

namespace UnsecuredAPIKeys.Providers.AI_Providers
{
    /// <summary>
    /// Provider for Azure OpenAI API keys.
    /// Keys are 32-char hex strings, passed via "api-key" header.
    /// Azure OpenAI endpoints are tenant-specific: {resource}.openai.azure.com
    /// 
    /// SCRAPER STRATEGY: Search for keys alongside their resource names in .env files,
    /// config files, and YAML. The regex captures both the key AND the endpoint URL
    /// so we can actually verify them.
    ///
    /// VERIFICATION: When we find a key+endpoint pair, we call
    /// GET {endpoint}/openai/models?api-version=2024-10-21 with api-key header.
    /// If we only find the key without an endpoint, we store it as Unverified
    /// since we can't verify without the resource name.
    ///
    /// Official docs: https://learn.microsoft.com/azure/ai-services/openai/reference
    /// </summary>
    [ApiProvider]
    public class AzureOpenAIProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "Azure OpenAI";
        public override ApiTypeEnum ApiType => ApiTypeEnum.AzureOpenAI;

        public override IEnumerable<string> RegexPatterns =>
        [
            // Capture key alongside Azure-specific env var names
            @"(?i)AZURE_OPENAI_API_KEY[""'\s]*[:=][""'\s]*([a-f0-9]{32})",
            @"(?i)AZURE_OPENAI_KEY[""'\s]*[:=][""'\s]*([a-f0-9]{32})",
            @"(?i)AZURE_API_KEY[""'\s]*[:=][""'\s]*([a-f0-9]{32})",
            // Azure endpoint pattern — captures both key and endpoint together
            @"(?i)openai\.azure\.com.*?([a-f0-9]{32})",
        ];

        public AzureOpenAIProvider() : base() { }
        public AzureOpenAIProvider(ILogger<AzureOpenAIProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(
            string apiKey, HttpClient httpClient)
        {
            // Azure OpenAI keys are tenant-specific — we need the resource endpoint.
            // The key format is stored as "key|endpoint" when we can extract both,
            // or just "key" when we only found the key.
            string endpoint = "";
            string actualKey = apiKey;

            if (apiKey.Contains('|'))
            {
                var parts = apiKey.Split('|', 2);
                actualKey = parts[0];
                endpoint = parts[1].TrimEnd('/');
            }

            // If we have an endpoint, try to verify
            if (!string.IsNullOrEmpty(endpoint))
            {
                var modelsUrl = $"{endpoint}/openai/models?api-version=2024-10-21";

                using var request = new HttpRequestMessage(HttpMethod.Get, modelsUrl);
                request.Headers.Add("api-key", actualKey);

                var response = await httpClient.SendAsync(request);
                var body = await response.Content.ReadAsStringAsync();

                _logger?.LogDebug("Azure OpenAI models response: Status={Status}, Body={Body}",
                    response.StatusCode, TruncateResponse(body));

                if (IsSuccessStatusCode(response.StatusCode))
                {
                    var result = ValidationResult.Success(response.StatusCode, "Valid Azure OpenAI key");
                    result.AccountTier = endpoint.Replace("https://", "").Split('.')[0]; // resource name

                    try
                    {
                        using var doc = JsonDocument.Parse(body);
                        if (doc.RootElement.TryGetProperty("value", out var models))
                        {
                            var modelList = new List<ModelInfo>();
                            foreach (var el in models.EnumerateArray())
                            {
                                var id = el.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
                                modelList.Add(new ModelInfo { ModelId = id, DisplayName = id });
                            }
                            result.AvailableModels = modelList;
                        }
                    }
                    catch { /* Best effort */ }

                    return result;
                }

                if (response.StatusCode == HttpStatusCode.Unauthorized ||
                    response.StatusCode == HttpStatusCode.Forbidden)
                {
                    return ValidationResult.IsUnauthorized(response.StatusCode);
                }

                if ((int)response.StatusCode == 429)
                {
                    return ValidationResult.Success(response.StatusCode, "Rate limited (key is valid)");
                }

                return ValidationResult.HasHttpError(response.StatusCode,
                    $"Azure OpenAI models failed: {TruncateResponse(body)}");
            }

            // No endpoint available — can't verify Azure OpenAI keys without the resource name
            // Return a provider-specific error so the key stays Unverified rather than Invalid
            return ValidationResult.HasProviderSpecificError(
                "Azure OpenAI key found but no endpoint URL available for verification. " +
                "Key stored as Unverified — manual verification required.");
        }

        protected override bool IsValidKeyFormat(string apiKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey)) return false;

            // Handle "key|endpoint" format
            var key = apiKey.Contains('|') ? apiKey.Split('|')[0] : apiKey;

            // Azure OpenAI keys are exactly 32 lowercase hex characters
            return key.Length == 32 && key.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));
        }
    }
}

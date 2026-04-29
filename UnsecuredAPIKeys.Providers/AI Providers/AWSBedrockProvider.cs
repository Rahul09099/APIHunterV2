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
    /// Provider for AWS Bedrock long-term API keys (launched July 2025).
    /// Keys start with "bedrock:" prefix.
    /// These are Bearer tokens used via AWS_BEARER_TOKEN_BEDROCK env var.
    /// Bedrock is OpenAI-compatible — uses bedrock.us-east-1.amazonaws.com/v1/models.
    /// Verification: GET /v1/models (lists available foundation models).
    /// Official docs: https://docs.aws.amazon.com/bedrock/latest/userguide/api-keys-use.html
    /// </summary>
    [ApiProvider]
    public class AWSBedrockProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "AWS Bedrock";
        public override ApiTypeEnum ApiType => ApiTypeEnum.AWSBedrock;

        public override IEnumerable<string> RegexPatterns =>
        [
            // AWS Bedrock long-term API keys start with "bedrock:"
            @"\bbedrock:[A-Za-z0-9+/=]{40,200}\b",
            @"AWS_BEARER_TOKEN_BEDROCK",
            @"BEDROCK_API_KEY",
            @"bedrock[_-]?api[_-]?key"
        ];

        public AWSBedrockProvider() : base() { }
        public AWSBedrockProvider(ILogger<AWSBedrockProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(
            string apiKey, HttpClient httpClient)
        {
            // AWS Bedrock OpenAI-compatible Chat Completions endpoint
            // Correct base URL: https://bedrock-runtime.{region}.amazonaws.com/openai/v1
            // Using us-east-1 (primary region) with fallback to us-west-2
            // Official docs: https://docs.aws.amazon.com/bedrock/latest/userguide/inference-chat-completions.html
            const string primaryEndpoint   = "https://bedrock-runtime.us-east-1.amazonaws.com/openai/v1/models";
            const string fallbackEndpoint  = "https://bedrock-runtime.us-west-2.amazonaws.com/openai/v1/models";

            async Task<(HttpResponseMessage response, string body)> TryEndpoint(string url)
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                var resp = await httpClient.SendAsync(req);
                var b = await resp.Content.ReadAsStringAsync();
                return (resp, b);
            }

            var (response, body) = await TryEndpoint(primaryEndpoint);

            _logger?.LogDebug("AWS Bedrock models response (us-east-1): Status={Status}, Body={Body}",
                response.StatusCode, TruncateResponse(body));

            // If primary region fails with a non-auth error, try fallback region
            if (!response.IsSuccessStatusCode &&
                response.StatusCode != HttpStatusCode.Unauthorized &&
                response.StatusCode != HttpStatusCode.Forbidden)
            {
                var (fallbackResp, fallbackBody) = await TryEndpoint(fallbackEndpoint);
                if (IsSuccessStatusCode(fallbackResp.StatusCode) ||
                    fallbackResp.StatusCode == HttpStatusCode.Unauthorized ||
                    fallbackResp.StatusCode == HttpStatusCode.Forbidden)
                {
                    response = fallbackResp;
                    body = fallbackBody;
                    _logger?.LogDebug("AWS Bedrock fallback (us-west-2): Status={Status}", response.StatusCode);
                }
            }

            if (IsSuccessStatusCode(response.StatusCode))
            {
                var result = ValidationResult.Success(response.StatusCode, "Valid AWS Bedrock key");
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("data", out var data))
                    {
                        var models = new List<ModelInfo>();
                        foreach (var el in data.EnumerateArray())
                        {
                            var id = el.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
                            models.Add(new ModelInfo { ModelId = id, DisplayName = id });
                        }
                        result.AvailableModels = models;
                        result.Detail = $"Valid AWS Bedrock key ({models.Count} models available)";
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

            if (ContainsAny(body, QuotaIndicators))
            {
                return ValidationResult.Success(response.StatusCode, $"Valid key but quota issue: {TruncateResponse(body)}");
            }

            return ValidationResult.HasHttpError(response.StatusCode,
                $"Models listing failed: {TruncateResponse(body)}");
        }

        protected override bool IsValidKeyFormat(string apiKey) =>
            !string.IsNullOrWhiteSpace(apiKey) &&
            apiKey.StartsWith("bedrock:", StringComparison.Ordinal) &&
            apiKey.Length >= 48;
    }
}

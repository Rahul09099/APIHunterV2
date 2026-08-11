using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Base;
using UnsecuredAPIKeys.Providers.Common;

namespace UnsecuredAPIKeys.Providers.AI_Providers
{
    /// <summary>
    /// Provider for AWS Bedrock API keys.
    /// Keys start with "bedrock:" prefix and are used via Bearer authorization.
    /// Verification endpoint: GET https://bedrock-runtime.us-east-1.amazonaws.com/v1/models
    /// Official docs: https://docs.aws.amazon.com/bedrock/latest/userguide/api-keys-use.html
    /// </summary>
    [ApiProvider]
    public class AWSBedrockProvider : BaseApiKeyProvider
    {
        private const string PRIMARY_REGION = "us-east-1";
        private const string PRIMARY_ENDPOINT = "https://bedrock-runtime.us-east-1.amazonaws.com/v1/models";

        public override string ProviderName => "AWS Bedrock";
        public override ApiTypeEnum ApiType => ApiTypeEnum.AWSBedrock;

        public override IEnumerable<string> RegexPatterns =>
        [
            @"\bbedrock:[A-Za-z0-9_\-\+/=]{20,256}\b",
            @"AWS_BEARER_TOKEN_BEDROCK\s*=\s*['""]?(bedrock:[A-Za-z0-9_\-\+/=]{20,256})['""]?",
            @"BEDROCK_API_KEY\s*=\s*['""]?(bedrock:[A-Za-z0-9_\-\+/=]{20,256})['""]?"
        ];

        public AWSBedrockProvider() : base() { }
        public AWSBedrockProvider(ILogger<AWSBedrockProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(
            string apiKey, HttpClient httpClient)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, PRIMARY_ENDPOINT);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var response = await httpClient.SendAsync(req);
            var body = await response.Content.ReadAsStringAsync();

            _logger?.LogDebug("AWS Bedrock response ({Region}): Status={Status}, Body={Body}",
                PRIMARY_REGION, response.StatusCode, TruncateResponse(body));

            ValidationResult result;
            var bodyLower = body.ToLowerInvariant();

            if (IsSuccessStatusCode(response.StatusCode))
            {
                result = ValidationResult.Success(response.StatusCode, "Valid AWS Bedrock key");
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                    {
                        var models = new List<ModelInfo>();
                        foreach (var el in data.EnumerateArray())
                        {
                            var id = el.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
                            if (!string.IsNullOrEmpty(id))
                            {
                                models.Add(new ModelInfo { ModelId = id, DisplayName = id });
                            }
                        }
                        result.AvailableModels = models;
                        result.Detail = $"Valid AWS Bedrock key ({models.Count} models available)";
                    }
                }
                catch (JsonException ex)
                {
                    _logger?.LogDebug(ex, "AWS Bedrock returned a successful response that could not be parsed as JSON.");
                }
            }
            else if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                result = ValidationResult.IsUnauthorized(response.StatusCode, "Invalid AWS Bedrock API key");
            }
            else if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                result = new ValidationResult
                {
                    Status = ValidationAttemptStatus.ValidationUnavailable,
                    HttpStatusCode = response.StatusCode,
                    Detail = "AWS Bedrock access forbidden; key validity could not be determined."
                };
            }
            else if ((int)response.StatusCode == 429)
            {
                result = new ValidationResult
                {
                    Status = ValidationAttemptStatus.ValidationUnavailable,
                    HttpStatusCode = response.StatusCode,
                    Detail = "AWS Bedrock rate limit exceeded (HTTP 429)"
                };
            }
            else if (response.StatusCode == HttpStatusCode.PaymentRequired && ContainsAny(bodyLower, QuotaIndicators))
            {
                result = new ValidationResult
                {
                    Status = ValidationAttemptStatus.Valid,
                    HttpStatusCode = response.StatusCode,
                    IsQuotaExceeded = true,
                    Detail = $"Valid key but billing/quota issue: {TruncateResponse(body)}"
                };
            }
            else
            {
                result = ValidationResult.HasHttpError(response.StatusCode,
                    $"Bedrock request failed ({PRIMARY_REGION}): {TruncateResponse(body)}");
            }

            result.RawResponse = body;
            return result;
        }

        protected override bool IsValidKeyFormat(string apiKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey) || !apiKey.StartsWith("bedrock:", StringComparison.Ordinal))
            {
                return false;
            }

            var value = apiKey["bedrock:".Length..];
            return value.Length >= 20 && value.Length <= 256 &&
                   value.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '+' || c == '/' || c == '=');
        }
    }
}

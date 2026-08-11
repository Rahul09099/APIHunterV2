using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Base;
using UnsecuredAPIKeys.Providers.Common;

namespace UnsecuredAPIKeys.Providers.AI_Providers
{
    /// <summary>
    /// Provider implementation for handling Anthropic (Claude) API keys with enhanced validation.
    /// </summary>
    [ApiProvider]
    public class AnthropicProvider : BaseApiKeyProvider
    {
        private const string API_ENDPOINT = "https://api.anthropic.com/v1/messages";
        private const string ANTHROPIC_VERSION = "2023-06-01";
        private const string DEFAULT_MODEL = "claude-3-5-haiku-latest";
        private const int MAX_RETRIES = 3;
        private const int TIMEOUT_SECONDS = 30;

        // Anthropic-specific response keywords (additional to base class)
        private static readonly HashSet<string> InvalidKeyIndicators = new(StringComparer.OrdinalIgnoreCase)
        {
            "invalid_api_key",
            "authentication_error",
            "invalid x-api-key",
            "unauthorized"
        };

        public override string ProviderName => "Anthropic";

        public override ApiTypeEnum ApiType => ApiTypeEnum.AnthropicClaude;

        // Enhanced regex patterns with compiled regex for better performance
        public override IEnumerable<string> RegexPatterns =>
        [
            @"\bsk-ant-api\d{2}-[a-zA-Z0-9\-_]{40,120}\b",
            @"\bsk-ant-[a-zA-Z0-9\-_]{20,120}\b"
        ];

        public AnthropicProvider() : base()
        {
        }

        public AnthropicProvider(ILogger<AnthropicProvider>? logger) : base(logger)
        {
        }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(string apiKey, HttpClient httpClient)
        {
            using var request = CreateValidationRequest(apiKey);

            var response = await httpClient.SendAsync(request);
            var responseBody = await response.Content.ReadAsStringAsync();

            _logger?.LogDebug("Anthropic API response: Status={StatusCode}, Body={Body}",
                response.StatusCode, responseBody.Length > 200 ? responseBody.Substring(0, 200) + "..." : responseBody);

            return InterpretResponse(response.StatusCode, responseBody);
        }

        private HttpRequestMessage CreateValidationRequest(string apiKey)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, API_ENDPOINT);

            // Set headers
            request.Headers.Add("x-api-key", apiKey);
            request.Headers.Add("anthropic-version", ANTHROPIC_VERSION);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            // Ultra-minimal payload for lowest cost
            var payload = new
            {
                model = DEFAULT_MODEL,
                max_tokens = 1,
                messages = new[]
                {
                    new { role = "user", content = "1" }
                },
                temperature = 0,
                stop_sequences = new[] { "1", "2", "3", "4", "5" }
            };

            var jsonContent = JsonSerializer.Serialize(payload);
            request.Content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            return request;
        }

        private ValidationResult InterpretResponse(HttpStatusCode statusCode, string responseBody)
        {
            // Success cases
            if (IsSuccessStatusCode(statusCode))
            {
                return ValidationResult.Success(statusCode);
            }

            var bodyLower = responseBody.ToLowerInvariant();

            switch (statusCode)
            {
                case HttpStatusCode.Unauthorized: // 401
                    return ValidationResult.IsUnauthorized(statusCode);

                case HttpStatusCode.NotFound: // 404
                    if (bodyLower.Contains("model") || bodyLower.Contains("not_found_error"))
                    {
                        // The key is valid (accepted by gateway), but the model string was invalid.
                        _logger?.LogInformation("API key is valid (404 model error confirms gateway acceptance)");
                        return ValidationResult.Success(statusCode);
                    }
                    return ValidationResult.HasHttpError(statusCode, $"Endpoint not found: {TruncateResponse(responseBody)}");

                case HttpStatusCode.Forbidden: // 403
                    _logger?.LogInformation("API key has permission restrictions but is valid (403)");
                    return ValidationResult.Success(statusCode);

                case HttpStatusCode.BadRequest: // 400
                    // A 400 on a well-formed request means the API rejected it.
                    // Check the body: if it's a model/param error the key is valid;
                    // if it's an auth error the key is bad.
                    if (bodyLower.Contains("invalid_api_key") ||
                        bodyLower.Contains("authentication_error") ||
                        bodyLower.Contains("unauthorized"))
                    {
                        return ValidationResult.IsUnauthorized(statusCode, "Invalid API key (400 auth error)");
                    }

                    if (ContainsAny(bodyLower, QuotaIndicators) || bodyLower.Contains("credit balance") || bodyLower.Contains("credit_balance"))
                    {
                        _logger?.LogInformation("API key is valid but credit balance/quota is depleted (400)");
                        return new ValidationResult
                        {
                            Status = ValidationAttemptStatus.Valid,
                            HttpStatusCode = statusCode,
                            IsQuotaExceeded = true,
                            Detail = "Valid key but credit balance is low / quota exhausted"
                        };
                    }

                    // 400 due to model/param issue = key accepted by gateway = valid
                    _logger?.LogInformation("API key is valid (400 non-auth error confirms gateway acceptance)");
                    return ValidationResult.Success(statusCode, "Valid key (400 non-auth error)");

                case HttpStatusCode.PaymentRequired: // 402
                case HttpStatusCode.TooManyRequests: // 429
                    _logger?.LogInformation("API key is valid but has quota/billing/status issues ({StatusCode})", statusCode);
                    return new ValidationResult
                    {
                        Status = ValidationAttemptStatus.Valid,
                        HttpStatusCode = statusCode,
                        IsQuotaExceeded = true,
                        Detail = "Valid key but quota/billing issue"
                    };

                case HttpStatusCode.ServiceUnavailable: // 503
                case HttpStatusCode.GatewayTimeout: // 504
                    return ValidationResult.HasNetworkError($"Service unavailable: {statusCode}");

                default:
                    if (ContainsAny(bodyLower, QuotaIndicators))
                    {
                        return ValidationResult.Success(statusCode);
                    }

                    return ValidationResult.HasHttpError(statusCode,
                        $"API request failed with status {statusCode}. Response: {TruncateResponse(responseBody)}");
            }
        }

        protected override bool IsValidKeyFormat(string apiKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Length < 20)
                return false;

            if (!apiKey.StartsWith("sk-ant-", StringComparison.Ordinal))
                return false;

            return apiKey.All(c => char.IsLetterOrDigit(c) || c == '-' || c == '_');
        }
    }
}
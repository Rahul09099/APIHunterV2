using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
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
    /// Provider implementation for handling Anthropic (Claude) API keys with enhanced validation.
    /// </summary>
    [ApiProvider]
    public class AnthropicProvider : BaseApiKeyProvider
    {
        private const string API_ENDPOINT = "https://api.anthropic.com/v1/messages";
        private const string ANTHROPIC_VERSION = "2023-06-01";
        private const string DEFAULT_MODEL = "claude-haiku-4-5-20251001";

        public override string ProviderName => "Anthropic";
        public override ApiTypeEnum ApiType => ApiTypeEnum.AnthropicClaude;

        public override IEnumerable<string> RegexPatterns =>
        [
            @"\bsk-ant-api\d{2}-[a-zA-Z0-9\-_]{40,120}\b",
            @"\bsk-ant-[a-zA-Z0-9\-_]{20,120}\b"
        ];

        public AnthropicProvider() : base() { }
        public AnthropicProvider(ILogger<AnthropicProvider>? logger) : base(logger) { }

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

            request.Headers.Add("x-api-key", apiKey);
            request.Headers.Add("anthropic-version", ANTHROPIC_VERSION);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var payload = new
            {
                model = DEFAULT_MODEL,
                max_tokens = 1,
                messages = new[]
                {
                    new { role = "user", content = "1" }
                },
                temperature = 0
            };

            var jsonContent = JsonSerializer.Serialize(payload);
            request.Content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            return request;
        }

        private ValidationResult InterpretResponse(HttpStatusCode statusCode, string responseBody)
        {
            var result = ParseResponseToValidationResult(statusCode, responseBody);
            result.RawResponse = responseBody;
            return result;
        }

        private ValidationResult ParseResponseToValidationResult(HttpStatusCode statusCode, string responseBody)
        {
            if (IsSuccessStatusCode(statusCode))
            {
                return ValidationResult.Success(statusCode, "Valid Anthropic API key");
            }

            var bodyLower = responseBody.ToLowerInvariant();
            string? errorType = null;
            string? errorMessage = null;
            string? requestId = null;

            try
            {
                using var doc = JsonDocument.Parse(responseBody);
                var root = doc.RootElement;
                if (root.TryGetProperty("request_id", out var reqIdProp))
                {
                    requestId = reqIdProp.GetString();
                }

                if (root.TryGetProperty("error", out var errObj) && errObj.ValueKind == JsonValueKind.Object)
                {
                    if (errObj.TryGetProperty("type", out var t)) errorType = t.GetString();
                    if (errObj.TryGetProperty("message", out var m)) errorMessage = m.GetString();
                }
            }
            catch
            {
                // Fall back to string inspection if JSON parsing fails
            }

            switch (statusCode)
            {
                case HttpStatusCode.Unauthorized: // 401
                    return ValidationResult.IsUnauthorized(statusCode, errorMessage ?? "Invalid Anthropic API key");

                case HttpStatusCode.NotFound: // 404
                    if (bodyLower.Contains("model") || bodyLower.Contains("not_found_error") || string.Equals(errorType, "not_found_error", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger?.LogInformation("API key is valid (404 model error confirms gateway acceptance)");
                        return ValidationResult.Success(statusCode, "Valid key (model not found)");
                    }
                    return ValidationResult.HasHttpError(statusCode, $"Endpoint not found: {TruncateResponse(responseBody)}");

                case HttpStatusCode.Forbidden: // 403 (permission_error)
                    if (string.Equals(errorType, "permission_error", StringComparison.OrdinalIgnoreCase) || bodyLower.Contains("permission_error"))
                    {
                        _logger?.LogInformation("API key has permission restrictions but is valid (403)");
                        return ValidationResult.Success(statusCode, "Valid key (permission restricted)");
                    }
                    return ValidationResult.HasHttpError(statusCode, errorMessage ?? "Anthropic access forbidden (403 Forbidden)");

                case HttpStatusCode.BadRequest: // 400
                    if (string.Equals(errorType, "authentication_error", StringComparison.OrdinalIgnoreCase) ||
                        bodyLower.Contains("invalid_api_key") ||
                        bodyLower.Contains("authentication_error") ||
                        bodyLower.Contains("unauthorized"))
                    {
                        return ValidationResult.IsUnauthorized(statusCode, errorMessage ?? "Invalid API key (400 auth error)");
                    }

                    if (ContainsAny(bodyLower, QuotaIndicators) ||
                        bodyLower.Contains("credit balance") ||
                        bodyLower.Contains("credit_balance") ||
                        (errorMessage != null && errorMessage.Contains("credit balance", StringComparison.OrdinalIgnoreCase)))
                    {
                        _logger?.LogInformation("API key is valid but credit balance/quota is depleted (400)");
                        return new ValidationResult
                        {
                            Status = ValidationAttemptStatus.Valid,
                            HttpStatusCode = statusCode,
                            IsQuotaExceeded = true,
                            Detail = errorMessage ?? "Valid key but credit balance is low / quota exhausted"
                        };
                    }

                    // Standard 400 Bad Request (invalid parameter or malformed request)
                    return ValidationResult.HasHttpError(statusCode, errorMessage ?? "Anthropic rejected request (400 Bad Request)");

                case HttpStatusCode.PaymentRequired: // 402
                    return new ValidationResult
                    {
                        Status = ValidationAttemptStatus.Valid,
                        HttpStatusCode = statusCode,
                        IsQuotaExceeded = true,
                        Detail = errorMessage ?? "Valid key but payment required / quota exhausted"
                    };

                case HttpStatusCode.TooManyRequests: // 429 (rate_limit_error)
                    _logger?.LogInformation("API key rate limited by Anthropic (429)");
                    return new ValidationResult
                    {
                        Status = ValidationAttemptStatus.ValidationUnavailable,
                        HttpStatusCode = statusCode,
                        Detail = errorMessage ?? "Anthropic rate limit exceeded (HTTP 429)"
                    };

                case HttpStatusCode.InternalServerError: // 500 (api_error)
                case HttpStatusCode.ServiceUnavailable:   // 503
                case HttpStatusCode.GatewayTimeout:        // 504
                case (HttpStatusCode)529:                  // 529 (overloaded_error)
                    return new ValidationResult
                    {
                        Status = ValidationAttemptStatus.ValidationUnavailable,
                        HttpStatusCode = statusCode,
                        Detail = $"Anthropic service temporarily unavailable ({(int)statusCode})"
                    };

                default:
                    return ValidationResult.HasHttpError(statusCode,
                        $"API request failed with status {(int)statusCode}. {errorMessage ?? TruncateResponse(responseBody)}");
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
using System.Net;
using Microsoft.Extensions.Logging;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Interfaces;
using UnsecuredAPIKeys.Providers.Common;

namespace UnsecuredAPIKeys.Providers._Base
{
    /// <summary>
    /// Base class for API key providers with common functionality and retry logic.
    /// </summary>
    public abstract class BaseApiKeyProvider(ILogger? logger = null) : IApiKeyProvider
    {
        protected const int DEFAULT_MAX_RETRIES = 3;
        protected const int DEFAULT_TIMEOUT_SECONDS = 30;

        protected readonly ILogger? _logger = logger;

        public abstract string ProviderName { get; }
        public abstract ApiTypeEnum ApiType { get; }
        public abstract IEnumerable<string> RegexPatterns { get; }

        /// <summary>
        /// Validates an API key with retry logic and proper resource management.
        /// </summary>
        public async Task<ValidationResult> ValidateKeyAsync(string apiKey, IHttpClientFactory httpClientFactory)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return ValidationResult.HasProviderSpecificError("API key is null or whitespace.");
            }

            // Clean the API key
            apiKey = CleanApiKey(apiKey);

            // Validate format if implemented
            if (!IsValidKeyFormat(apiKey))
            {
                return ValidationResult.HasProviderSpecificError("API key format is invalid.");
            }

            Exception? lastException = null;

            for (int retry = 0; retry < GetMaxRetries(); retry++)
            {
                if (retry > 0)
                {
                    // Exponential backoff with jitter to avoid thundering herd
                    var baseDelay = 500 * Math.Pow(2, retry - 1); // 500ms base, doubles each retry
                    var jitter = Random.Shared.Next(0, 300);       // up to 300ms random jitter
                    var delay = TimeSpan.FromMilliseconds(baseDelay + jitter);

                    _logger?.LogDebug("Retrying {Provider} validation after {Delay}ms (attempt {Retry}/{MaxRetries})",
                        ProviderName, delay.TotalMilliseconds, retry + 1, GetMaxRetries());
                    await Task.Delay(delay);
                }

                try
                {
                    using var httpClient = CreateHttpClient(httpClientFactory);
                    var result = await ValidateKeyWithHttpClientAsync(apiKey, httpClient);

                    // Record latency metric (best-effort, no dependency on Services project)
                    // MetricsService.Instance.RecordProviderLatency is called from VerifierService instead

                    if (result.Status != ValidationAttemptStatus.NetworkError)
                    {
                        return result;
                    }

                    // Continue retrying on network errors
                    lastException = new Exception(result.Detail);
                }
                catch (HttpRequestException ex)
                {
                    lastException = ex;
                    _logger?.LogWarning(ex, "HTTP request failed on attempt {Retry}/{MaxRetries} for {Provider}",
                        retry + 1, GetMaxRetries(), ProviderName);

                    // Check for Retry-After header in the exception (if available via inner exception)
                    if (TryGetRetryAfterDelay(ex, out var retryAfter))
                    {
                        _logger?.LogInformation("Provider {Provider} sent Retry-After: {Delay}s", ProviderName, retryAfter.TotalSeconds);
                        await Task.Delay(retryAfter);
                    }

                    if (retry == GetMaxRetries() - 1)
                    {
                        return ValidationResult.HasNetworkError($"HTTP request failed after {GetMaxRetries()} retries: {ex.Message}");
                    }
                }
                catch (TaskCanceledException ex)
                {
                    lastException = ex;
                    _logger?.LogWarning(ex, "Request timeout on attempt {Retry}/{MaxRetries} for {Provider}",
                        retry + 1, GetMaxRetries(), ProviderName);

                    if (retry == GetMaxRetries() - 1)
                    {
                        return ValidationResult.HasNetworkError($"Request timeout after {GetMaxRetries()} retries: {ex.Message}");
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Unexpected error during {Provider} key validation", ProviderName);
                    return ValidationResult.HasProviderSpecificError($"Unexpected error: {ex.Message}");
                }
            }

            return ValidationResult.HasNetworkError($"Failed after {GetMaxRetries()} retries. Last error: {lastException?.Message ?? "Unknown error"}");
        }

        /// <summary>
        /// Attempts to extract Retry-After delay from an HttpRequestException.
        /// Returns true if found, false otherwise.
        /// </summary>
        private static bool TryGetRetryAfterDelay(HttpRequestException ex, out TimeSpan delay)
        {
            delay = TimeSpan.Zero;

            // Check if the exception message contains "Retry-After" hints
            // (HttpClient doesn't expose response headers in exceptions, so this is best-effort)
            var message = ex.Message.ToLowerInvariant();
            if (message.Contains("retry") && message.Contains("after"))
            {
                // Default to 5 seconds if we detect a retry-after hint but can't parse it
                delay = TimeSpan.FromSeconds(5);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Abstract method for provider-specific validation logic.
        /// </summary>
        protected abstract Task<ValidationResult> ValidateKeyWithHttpClientAsync(string apiKey, HttpClient httpClient);

        /// <summary>
        /// Creates an HttpClient with proper configuration.
        /// </summary>
        protected virtual HttpClient CreateHttpClient(IHttpClientFactory httpClientFactory)
        {
            try
            {
                var client = httpClientFactory.CreateClient(ProviderName.ToLowerInvariant().Replace(" ", ""));
                client.Timeout = TimeSpan.FromSeconds(GetTimeoutSeconds());
                return client;
            }
            catch
            {
                // Fall back to manual creation if factory fails
                return new HttpClient
                {
                    Timeout = TimeSpan.FromSeconds(GetTimeoutSeconds())
                };
            }
        }

        /// <summary>
        /// Cleans the API key by removing common prefixes and whitespace.
        /// </summary>
        protected virtual string CleanApiKey(string apiKey)
        {
            apiKey = apiKey.Trim();

            // Remove common prefixes
            if (apiKey.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                apiKey = apiKey.Substring(7).Trim();
            }
            else if (apiKey.StartsWith("x-api-key:", StringComparison.OrdinalIgnoreCase))
            {
                apiKey = apiKey.Substring(10).Trim();
            }

            return apiKey;
        }

        /// <summary>
        /// Validates the API key format. Override in derived classes for specific validation.
        /// </summary>
        protected virtual bool IsValidKeyFormat(string apiKey)
        {
            return !string.IsNullOrWhiteSpace(apiKey) && apiKey.Length >= 10;
        }

        /// <summary>
        /// Gets the maximum number of retries. Override in derived classes if needed.
        /// </summary>
        protected virtual int GetMaxRetries() => DEFAULT_MAX_RETRIES;

        /// <summary>
        /// Gets the timeout in seconds. Override in derived classes if needed.
        /// </summary>
        protected virtual int GetTimeoutSeconds() => DEFAULT_TIMEOUT_SECONDS;

        /// <summary>
        /// Common method to check if response body contains any of the specified indicators.
        /// </summary>
        protected static bool ContainsAny(string text, HashSet<string> indicators)
        {
            return indicators.Any(indicator => text.Contains(indicator, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Truncates response text for logging purposes.
        /// </summary>
        protected static string TruncateResponse(string response, int maxLength = 200)
        {
            if (string.IsNullOrEmpty(response))
                return string.Empty;

            return response.Length > maxLength
                ? response.Substring(0, maxLength) + "..."
                : response;
        }

        /// <summary>
        /// Checks if the status code indicates success.
        /// </summary>
        protected static bool IsSuccessStatusCode(HttpStatusCode statusCode)
        {
            return (int)statusCode >= 200 && (int)statusCode < 300;
        }

        /// <summary>
        /// Common quota/billing indicators across providers.
        /// </summary>
        protected static readonly HashSet<string> QuotaIndicators = new(StringComparer.OrdinalIgnoreCase)
        {
            "credit", "quota", "billing", "insufficient_funds", "payment", "exceeded", "balance", "limit",
            "insufficient_quota", "exceeded_quota", "rate_limit", "rate_limit_exceeded", "RESOURCE_EXHAUSTED"
        };

        /// <summary>
        /// Common unauthorized indicators across providers.
        /// </summary>
        protected static readonly HashSet<string> UnauthorizedIndicators = new(StringComparer.OrdinalIgnoreCase)
        {
            "invalid_api_key", "authentication_error", "unauthorized", "invalid x-api-key", "API_KEY_INVALID",
            "API key not valid", "API key expired", "invalid token", "authentication failed"
        };

        /// <summary>
        /// Common permission indicators across providers.
        /// </summary>
        protected static readonly HashSet<string> PermissionIndicators = new(StringComparer.OrdinalIgnoreCase)
        {
            "permission", "access", "not_authorized_for_model", "forbidden", "read-only", "Pro service"
        };
    }
}

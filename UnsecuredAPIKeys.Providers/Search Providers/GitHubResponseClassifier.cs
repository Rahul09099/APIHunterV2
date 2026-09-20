using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Interfaces;

namespace UnsecuredAPIKeys.Providers.Search_Providers;

/// <summary>
/// Classified GitHub response outcome with optional delay and sanitized code.
/// Adheres strictly to AC-16.5 through AC-16.15, AC-16.25, and AC-16.26.
/// </summary>
public sealed record GitHubClassificationResult(
    ProviderOutcomeKind Outcome,
    TimeSpan? RetryAfter = null,
    string? SanitizedCode = null);

/// <summary>
/// GitHub-specific response classifier.
/// Bounded allowlisted parsers extract status and rate-limit details without ever disclosing or logging raw bodies.
/// </summary>
public sealed class GitHubResponseClassifier : ISearchProviderOutcomeClassifier
{
    private static readonly TimeSpan DefaultRateLimitFallback = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MinimumSecondaryLimitDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ClockSafetyBuffer = TimeSpan.FromSeconds(5);

    public ProviderOutcomeKind Classify(
        SearchProviderEnum providerKind,
        HttpStatusCode? statusCode,
        Exception? exception = null,
        string? responseBody = null)
    {
        if (exception is not null)
        {
            return exception switch
            {
                OperationCanceledException => ProviderOutcomeKind.Cancellation,
                HttpRequestException or SocketException or TimeoutException => ProviderOutcomeKind.Transient,
                _ => ProviderOutcomeKind.Transient
            };
        }

        if (!statusCode.HasValue)
        {
            return ProviderOutcomeKind.Success;
        }

        var res = ClassifyStatusCode(statusCode.Value, responseBody, null, null);
        return res.Outcome;
    }

    public GitHubClassificationResult ClassifyResponse(
        HttpResponseMessage? response,
        string? responseBody = null,
        Exception? exception = null)
    {
        if (exception is not null)
        {
            if (exception is OperationCanceledException)
                return new GitHubClassificationResult(ProviderOutcomeKind.Cancellation, null, "OperationCanceled");

            if (exception is HttpRequestException or SocketException or TimeoutException)
                return new GitHubClassificationResult(ProviderOutcomeKind.Transient, TimeSpan.FromSeconds(10), "TransportError");

            return new GitHubClassificationResult(ProviderOutcomeKind.Transient, TimeSpan.FromSeconds(10), exception.GetType().Name);
        }

        if (response is null)
            return new GitHubClassificationResult(ProviderOutcomeKind.Success);

        // Header inspection (bounded & trusted)
        TimeSpan? retryAfter = null;
        if (response.Headers.TryGetValues("Retry-After", out var retryValues))
        {
            var rawRetry = retryValues.FirstOrDefault();
            if (int.TryParse(rawRetry, out var seconds) && seconds > 0)
            {
                retryAfter = TimeSpan.FromSeconds(Math.Min(seconds, 3600)); // Cap at 1 hour
            }
        }

        long? rateLimitResetEpoch = null;
        if (response.Headers.TryGetValues("X-RateLimit-Reset", out var resetValues))
        {
            var rawReset = resetValues.FirstOrDefault();
            if (long.TryParse(rawReset, out var epoch))
            {
                rateLimitResetEpoch = epoch;
            }
        }

        int? rateLimitRemaining = null;
        if (response.Headers.TryGetValues("X-RateLimit-Remaining", out var remainingValues))
        {
            var rawRemaining = remainingValues.FirstOrDefault();
            if (int.TryParse(rawRemaining, out var remaining))
            {
                rateLimitRemaining = remaining;
            }
        }

        return ClassifyStatusCode(response.StatusCode, responseBody, retryAfter, rateLimitResetEpoch, rateLimitRemaining);
    }

    private static GitHubClassificationResult ClassifyStatusCode(
        HttpStatusCode code,
        string? responseBody,
        TimeSpan? headerRetryAfter,
        long? rateLimitResetEpoch,
        int? rateLimitRemaining = null)
    {
        // 1. Success codes
        if (code is HttpStatusCode.OK or HttpStatusCode.Created or HttpStatusCode.NoContent)
        {
            return new GitHubClassificationResult(ProviderOutcomeKind.Success);
        }

        // 2. Authentication Invalid (AC-16.9)
        if (code == HttpStatusCode.Unauthorized)
        {
            return new GitHubClassificationResult(ProviderOutcomeKind.AuthInvalid, null, "BadCredentials");
        }

        // 3. Rate Limited / 429 (AC-16.8)
        if (code == HttpStatusCode.TooManyRequests)
        {
            var delay = headerRetryAfter ?? DefaultRateLimitFallback;
            return new GitHubClassificationResult(ProviderOutcomeKind.RateLimited, delay, "TooManyRequests");
        }

        // 4. Forbidden / 403 (AC-16.6, AC-16.7, AC-16.10)
        if (code == HttpStatusCode.Forbidden)
        {
            // Check primary rate limit: X-RateLimit-Remaining=0
            if (rateLimitRemaining.HasValue && rateLimitRemaining.Value == 0 && rateLimitResetEpoch.HasValue)
            {
                var resetUtc = DateTimeOffset.FromUnixTimeSeconds(rateLimitResetEpoch.Value).UtcDateTime;
                var now = DateTime.UtcNow;
                var delay = resetUtc > now ? (resetUtc - now) + ClockSafetyBuffer : TimeSpan.FromSeconds(30);
                return new GitHubClassificationResult(ProviderOutcomeKind.RateLimited, delay, "PrimaryRateLimitExceeded");
            }

            // Check secondary rate limit allowlisted phrases (bounded safe parser, AC-16.7, AC-16.25)
            if (IsSecondaryRateLimitBody(responseBody))
            {
                var delay = headerRetryAfter ?? MinimumSecondaryLimitDelay;
                return new GitHubClassificationResult(ProviderOutcomeKind.RateLimited, delay, "SecondaryRateLimit");
            }

            // Ordinary permission forbidden (AC-16.10)
            return new GitHubClassificationResult(ProviderOutcomeKind.ForbiddenScope, null, "ForbiddenScope");
        }

        // 5. Bad Request / Unprocessable Entity (AC-16.12)
        if (code is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity)
        {
            return new GitHubClassificationResult(ProviderOutcomeKind.RequestInvalid, null, "InvalidQuery");
        }

        // 6. Contextual Resource Missing (AC-16.11)
        if (code == HttpStatusCode.NotFound)
        {
            return new GitHubClassificationResult(ProviderOutcomeKind.ResourceMissing, null, "ResourceNotFound");
        }

        // 7. Transient server errors (AC-16.13)
        if (code is HttpStatusCode.RequestTimeout or
                    HttpStatusCode.InternalServerError or
                    HttpStatusCode.BadGateway or
                    HttpStatusCode.ServiceUnavailable or
                    HttpStatusCode.GatewayTimeout ||
            (int)code >= 500)
        {
            return new GitHubClassificationResult(ProviderOutcomeKind.Transient, TimeSpan.FromSeconds(15), "ServerError");
        }

        return new GitHubClassificationResult(ProviderOutcomeKind.RequestInvalid, null, $"Http_{code}");
    }

    /// <summary>
    /// Bounded allowlisted check for GitHub secondary rate limit messages without exposing body (AC-16.25, AC-16.26).
    /// </summary>
    private static bool IsSecondaryRateLimitBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;

        // Bound inspection to first 512 characters
        var bounded = body.Length > 512 ? body[..512] : body;

        return bounded.Contains("secondary rate limit", StringComparison.OrdinalIgnoreCase) ||
               bounded.Contains("abuse detection mechanism", StringComparison.OrdinalIgnoreCase) ||
               bounded.Contains("wait a few minutes", StringComparison.OrdinalIgnoreCase);
    }
}

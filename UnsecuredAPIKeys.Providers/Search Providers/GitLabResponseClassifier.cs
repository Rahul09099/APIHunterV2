using System.Net;
using System.Net.Sockets;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Interfaces;

namespace UnsecuredAPIKeys.Providers.Search_Providers;

/// <summary>
/// Classified GitLab response outcome with optional delay and sanitized code.
/// Adheres strictly to AC-16.16 through AC-16.26.
/// Raw response bodies are never disclosed; only bounded allowlisted phrases
/// and header evidence (Retry-After, RateLimit-Reset) inform the outcome.
/// </summary>
public sealed record GitLabClassificationResult(
    ProviderOutcomeKind Outcome,
    TimeSpan? RetryAfter = null,
    string? SanitizedCode = null);

/// <summary>
/// GitLab-specific response classifier (Task 11.2).
/// Covers SaaS and self-hosted instances behind the same closed Typed Outcome model:
/// 401 → AuthInvalid, ordinary 403 → ForbiddenScope, 429 (with/without reset
/// evidence) → RateLimited, 400/422 → RequestInvalid, contextual 404 →
/// ResourceMissing, timeout/connectivity/5xx → Transient.
/// </summary>
public sealed class GitLabResponseClassifier : ISearchProviderOutcomeClassifier
{
    private static readonly TimeSpan DefaultRateLimitFallback = TimeSpan.FromMinutes(1);
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

        return ClassifyStatusCode(statusCode.Value, responseBody, null, null).Outcome;
    }

    public GitLabClassificationResult ClassifyResponse(
        HttpResponseMessage? response,
        string? responseBody = null,
        Exception? exception = null)
    {
        if (exception is not null)
        {
            if (exception is OperationCanceledException)
                return new GitLabClassificationResult(ProviderOutcomeKind.Cancellation, null, "OperationCanceled");

            if (exception is HttpRequestException or SocketException or TimeoutException)
                return new GitLabClassificationResult(ProviderOutcomeKind.Transient, TimeSpan.FromSeconds(10), "TransportError");

            return new GitLabClassificationResult(ProviderOutcomeKind.Transient, TimeSpan.FromSeconds(10), exception.GetType().Name);
        }

        if (response is null)
            return new GitLabClassificationResult(ProviderOutcomeKind.Success);

        // Header inspection (bounded & trusted). GitLab uses RateLimit-Reset
        // (epoch seconds) and standard Retry-After on 429.
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
        if (response.Headers.TryGetValues("RateLimit-Reset", out var resetValues))
        {
            var rawReset = resetValues.FirstOrDefault();
            if (long.TryParse(rawReset, out var epoch))
            {
                rateLimitResetEpoch = epoch;
            }
        }
        else if (response.Headers.TryGetValues("X-RateLimit-Reset", out var xResetValues))
        {
            var rawReset = xResetValues.FirstOrDefault();
            if (long.TryParse(rawReset, out var epoch))
            {
                rateLimitResetEpoch = epoch;
            }
        }

        return ClassifyStatusCode(response.StatusCode, responseBody, retryAfter, rateLimitResetEpoch);
    }

    private static GitLabClassificationResult ClassifyStatusCode(
        HttpStatusCode code,
        string? responseBody,
        TimeSpan? headerRetryAfter,
        long? rateLimitResetEpoch)
    {
        // 1. Success codes
        if (code is HttpStatusCode.OK or HttpStatusCode.Created or HttpStatusCode.NoContent)
        {
            return new GitLabClassificationResult(ProviderOutcomeKind.Success);
        }

        // 2. Authentication Invalid (AC-16.17): invalid or revoked PAT.
        if (code == HttpStatusCode.Unauthorized)
        {
            return new GitLabClassificationResult(ProviderOutcomeKind.AuthInvalid, null, "BadCredentials");
        }

        // 3. Rate Limited / 429 (AC-16.18, AC-16.19): with or without reset evidence.
        if (code == HttpStatusCode.TooManyRequests)
        {
            var delay = headerRetryAfter ?? ResolveResetDelay(rateLimitResetEpoch) ?? DefaultRateLimitFallback;
            return new GitLabClassificationResult(ProviderOutcomeKind.RateLimited, delay, "TooManyRequests");
        }

        // 4. Forbidden / 403 (AC-16.17): ordinary permission denial. GitLab also
        // surfaces rate limiting as 403 with allowlisted body text, which maps to
        // RateLimited; everything else is a scope/permission outcome.
        if (code == HttpStatusCode.Forbidden)
        {
            if (IsRateLimitBody(responseBody))
            {
                var delay = headerRetryAfter ?? ResolveResetDelay(rateLimitResetEpoch) ?? DefaultRateLimitFallback;
                return new GitLabClassificationResult(ProviderOutcomeKind.RateLimited, delay, "RateLimited");
            }

            return new GitLabClassificationResult(ProviderOutcomeKind.ForbiddenScope, null, "ForbiddenScope");
        }

        // 5. Bad Request / Unprocessable Entity (AC-16.21)
        if (code is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity)
        {
            return new GitLabClassificationResult(ProviderOutcomeKind.RequestInvalid, null, "InvalidQuery");
        }

        // 6. Contextual Resource Missing (AC-16.20)
        if (code == HttpStatusCode.NotFound)
        {
            return new GitLabClassificationResult(ProviderOutcomeKind.ResourceMissing, null, "ResourceNotFound");
        }

        // 7. Transient server errors (AC-16.22)
        if (code is HttpStatusCode.RequestTimeout or
                    HttpStatusCode.InternalServerError or
                    HttpStatusCode.BadGateway or
                    HttpStatusCode.ServiceUnavailable or
                    HttpStatusCode.GatewayTimeout ||
            (int)code >= 500)
        {
            return new GitLabClassificationResult(ProviderOutcomeKind.Transient, TimeSpan.FromSeconds(15), "ServerError");
        }

        return new GitLabClassificationResult(ProviderOutcomeKind.RequestInvalid, null, $"Http_{code}");
    }

    private static TimeSpan? ResolveResetDelay(long? rateLimitResetEpoch)
    {
        if (!rateLimitResetEpoch.HasValue)
        {
            return null;
        }

        var resetUtc = DateTimeOffset.FromUnixTimeSeconds(rateLimitResetEpoch.Value).UtcDateTime;
        var now = DateTime.UtcNow;
        return resetUtc > now ? (resetUtc - now) + ClockSafetyBuffer : TimeSpan.FromSeconds(30);
    }

    /// <summary>
    /// Bounded allowlisted check for GitLab rate-limit messages without exposing the body (AC-16.25, AC-16.26).
    /// </summary>
    private static bool IsRateLimitBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;

        // Bound inspection to first 512 characters
        var bounded = body.Length > 512 ? body[..512] : body;

        return bounded.Contains("rate limit", StringComparison.OrdinalIgnoreCase) ||
               bounded.Contains("too many requests", StringComparison.OrdinalIgnoreCase) ||
               bounded.Contains("throttled", StringComparison.OrdinalIgnoreCase);
    }
}

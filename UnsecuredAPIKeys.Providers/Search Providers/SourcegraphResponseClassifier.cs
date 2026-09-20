using System.Net;
using System.Net.Sockets;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Interfaces;

namespace UnsecuredAPIKeys.Providers.Search_Providers;

/// <summary>
/// Sourcegraph-specific response classifier (Phase 1, Task 18.2).
/// Covers SaaS (sourcegraph.com/.api) and approved self-hosted instances behind the
/// closed Typed Outcome model: 401 -&gt; AuthInvalid, ordinary 403 -&gt; ForbiddenScope,
/// 429 (with/without reset evidence) -&gt; RateLimited, 400/422 -&gt; RequestInvalid,
/// contextual 404 -&gt; ResourceMissing, timeout/connectivity/5xx -&gt; Transient.
/// Raw bodies are never disclosed; only bounded allowlisted phrases and header
/// evidence (Retry-After, RateLimit-Reset, X-RateLimit-Reset) inform the outcome.
/// </summary>
public sealed record SourcegraphClassificationResult(
    ProviderOutcomeKind Outcome,
    TimeSpan? RetryAfter = null,
    string? SanitizedCode = null);

public sealed class SourcegraphResponseClassifier : ISearchProviderOutcomeClassifier
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

    public SourcegraphClassificationResult ClassifyResponse(
        HttpResponseMessage? response,
        string? responseBody = null,
        Exception? exception = null)
    {
        if (exception is not null)
        {
            if (exception is OperationCanceledException)
                return new SourcegraphClassificationResult(ProviderOutcomeKind.Cancellation, null, "OperationCanceled");

            if (exception is HttpRequestException or SocketException or TimeoutException)
                return new SourcegraphClassificationResult(ProviderOutcomeKind.Transient, TimeSpan.FromSeconds(10), "TransportError");

            return new SourcegraphClassificationResult(ProviderOutcomeKind.Transient, TimeSpan.FromSeconds(10), exception.GetType().Name);
        }

        if (response is null)
            return new SourcegraphClassificationResult(ProviderOutcomeKind.Success);

        TimeSpan? retryAfter = null;
        if (response.Headers.TryGetValues("Retry-After", out var retryValues))
        {
            var rawRetry = retryValues.FirstOrDefault();
            if (int.TryParse(rawRetry, out var seconds) && seconds > 0)
            {
                retryAfter = TimeSpan.FromSeconds(Math.Min(seconds, 3600));
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

    private static SourcegraphClassificationResult ClassifyStatusCode(
        HttpStatusCode code,
        string? responseBody,
        TimeSpan? headerRetryAfter,
        long? rateLimitResetEpoch)
    {
        if (code is HttpStatusCode.OK or HttpStatusCode.Created or HttpStatusCode.NoContent)
        {
            return new SourcegraphClassificationResult(ProviderOutcomeKind.Success);
        }

        if (code == HttpStatusCode.Unauthorized)
        {
            return new SourcegraphClassificationResult(ProviderOutcomeKind.AuthInvalid, null, "BadCredentials");
        }

        if (code == HttpStatusCode.TooManyRequests)
        {
            var delay = headerRetryAfter ?? ResolveResetDelay(rateLimitResetEpoch) ?? DefaultRateLimitFallback;
            return new SourcegraphClassificationResult(ProviderOutcomeKind.RateLimited, delay, "TooManyRequests");
        }

        if (code == HttpStatusCode.Forbidden)
        {
            if (IsRateLimitBody(responseBody))
            {
                var delay = headerRetryAfter ?? ResolveResetDelay(rateLimitResetEpoch) ?? DefaultRateLimitFallback;
                return new SourcegraphClassificationResult(ProviderOutcomeKind.RateLimited, delay, "RateLimited");
            }

            return new SourcegraphClassificationResult(ProviderOutcomeKind.ForbiddenScope, null, "ForbiddenScope");
        }

        if (code is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity)
        {
            return new SourcegraphClassificationResult(ProviderOutcomeKind.RequestInvalid, null, "InvalidQuery");
        }

        if (code == HttpStatusCode.NotFound)
        {
            return new SourcegraphClassificationResult(ProviderOutcomeKind.ResourceMissing, null, "ResourceNotFound");
        }

        if (code is HttpStatusCode.RequestTimeout or
                    HttpStatusCode.InternalServerError or
                    HttpStatusCode.BadGateway or
                    HttpStatusCode.ServiceUnavailable or
                    HttpStatusCode.GatewayTimeout ||
            (int)code >= 500)
        {
            return new SourcegraphClassificationResult(ProviderOutcomeKind.Transient, TimeSpan.FromSeconds(15), "ServerError");
        }

        return new SourcegraphClassificationResult(ProviderOutcomeKind.RequestInvalid, null, $"Http_{code}");
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

    private static bool IsRateLimitBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;

        var bounded = body.Length > 512 ? body[..512] : body;

        return bounded.Contains("rate limit", StringComparison.OrdinalIgnoreCase) ||
               bounded.Contains("too many requests", StringComparison.OrdinalIgnoreCase) ||
               bounded.Contains("throttled", StringComparison.OrdinalIgnoreCase) ||
               bounded.Contains("retry after", StringComparison.OrdinalIgnoreCase);
    }
}

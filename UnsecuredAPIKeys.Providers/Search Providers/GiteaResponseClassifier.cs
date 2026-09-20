using System.Net;
using System.Net.Sockets;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Interfaces;

namespace UnsecuredAPIKeys.Providers.Search_Providers;

public sealed record GiteaClassificationResult(
    ProviderOutcomeKind Outcome,
    TimeSpan? RetryAfter = null,
    string? SanitizedCode = null);

/// <summary>
/// Gitea response classifier (Phase 5, Task 22.2). Flavor/version-validated
/// capabilities only; unsupported versions are rejected before discovery.
/// </summary>
public sealed class GiteaResponseClassifier : ISearchProviderOutcomeClassifier
{
    private static readonly TimeSpan DefaultRateLimitFallback = TimeSpan.FromMinutes(1);

    public ProviderOutcomeKind Classify(SearchProviderEnum providerKind, HttpStatusCode? statusCode, Exception? exception = null, string? responseBody = null)
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
        if (!statusCode.HasValue) return ProviderOutcomeKind.Success;
        return ClassifyStatusCode(statusCode.Value, responseBody, null).Outcome;
    }

    public GiteaClassificationResult ClassifyResponse(HttpResponseMessage? response, string? responseBody = null, Exception? exception = null)
    {
        if (exception is not null)
        {
            if (exception is OperationCanceledException) return new GiteaClassificationResult(ProviderOutcomeKind.Cancellation, null, "OperationCanceled");
            return new GiteaClassificationResult(ProviderOutcomeKind.Transient, TimeSpan.FromSeconds(10), "TransportError");
        }
        if (response is null) return new GiteaClassificationResult(ProviderOutcomeKind.Success);
        TimeSpan? retryAfter = null;
        if (response.Headers.TryGetValues("Retry-After", out var v))
        {
            var raw = v.FirstOrDefault();
            if (int.TryParse(raw, out var s) && s > 0) retryAfter = TimeSpan.FromSeconds(Math.Min(s, 3600));
        }
        return ClassifyStatusCode(response.StatusCode, responseBody, retryAfter);
    }

    private static GiteaClassificationResult ClassifyStatusCode(HttpStatusCode code, string? body, TimeSpan? retryAfter)
    {
        if (code is HttpStatusCode.OK or HttpStatusCode.Created or HttpStatusCode.NoContent) return new GiteaClassificationResult(ProviderOutcomeKind.Success);
        if (code == HttpStatusCode.Unauthorized) return new GiteaClassificationResult(ProviderOutcomeKind.AuthInvalid, null, "BadCredentials");
        if (code == HttpStatusCode.TooManyRequests) return new GiteaClassificationResult(ProviderOutcomeKind.RateLimited, retryAfter ?? DefaultRateLimitFallback, "TooManyRequests");
        if (code == HttpStatusCode.Forbidden)
        {
            if (IsRateLimitBody(body)) return new GiteaClassificationResult(ProviderOutcomeKind.RateLimited, retryAfter ?? DefaultRateLimitFallback, "RateLimited");
            return new GiteaClassificationResult(ProviderOutcomeKind.ForbiddenScope, null, "ForbiddenScope");
        }
        if (code is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity) return new GiteaClassificationResult(ProviderOutcomeKind.RequestInvalid, null, "InvalidQuery");
        if (code == HttpStatusCode.NotFound) return new GiteaClassificationResult(ProviderOutcomeKind.ResourceMissing, null, "ResourceNotFound");
        if (code is HttpStatusCode.RequestTimeout or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout || (int)code >= 500)
            return new GiteaClassificationResult(ProviderOutcomeKind.Transient, TimeSpan.FromSeconds(15), "ServerError");
        return new GiteaClassificationResult(ProviderOutcomeKind.RequestInvalid, null, $"Http_{code}");
    }

    private static bool IsRateLimitBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;
        var b = body.Length > 512 ? body[..512] : body;
        return b.Contains("rate limit", StringComparison.OrdinalIgnoreCase) || b.Contains("too many requests", StringComparison.OrdinalIgnoreCase) || b.Contains("throttled", StringComparison.OrdinalIgnoreCase);
    }
}

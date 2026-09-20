using System.Net;
using System.Net.Sockets;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Interfaces;

namespace UnsecuredAPIKeys.Providers.Search_Providers;

public sealed record AzureDevOpsClassificationResult(
    ProviderOutcomeKind Outcome,
    TimeSpan? RetryAfter = null,
    string? SanitizedCode = null);

/// <summary>
/// Azure DevOps response classifier (Phase 4, Task 21.2). No global-public
/// capability is assumed; scoped org/project/repo search only.
/// </summary>
public sealed class AzureDevOpsResponseClassifier : ISearchProviderOutcomeClassifier
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

    public AzureDevOpsClassificationResult ClassifyResponse(HttpResponseMessage? response, string? responseBody = null, Exception? exception = null)
    {
        if (exception is not null)
        {
            if (exception is OperationCanceledException) return new AzureDevOpsClassificationResult(ProviderOutcomeKind.Cancellation, null, "OperationCanceled");
            return new AzureDevOpsClassificationResult(ProviderOutcomeKind.Transient, TimeSpan.FromSeconds(10), "TransportError");
        }
        if (response is null) return new AzureDevOpsClassificationResult(ProviderOutcomeKind.Success);
        TimeSpan? retryAfter = null;
        if (response.Headers.TryGetValues("Retry-After", out var v))
        {
            var raw = v.FirstOrDefault();
            if (int.TryParse(raw, out var s) && s > 0) retryAfter = TimeSpan.FromSeconds(Math.Min(s, 3600));
        }
        return ClassifyStatusCode(response.StatusCode, responseBody, retryAfter);
    }

    private static AzureDevOpsClassificationResult ClassifyStatusCode(HttpStatusCode code, string? body, TimeSpan? retryAfter)
    {
        if (code is HttpStatusCode.OK or HttpStatusCode.Created or HttpStatusCode.NoContent) return new AzureDevOpsClassificationResult(ProviderOutcomeKind.Success);
        if (code == HttpStatusCode.Unauthorized) return new AzureDevOpsClassificationResult(ProviderOutcomeKind.AuthInvalid, null, "BadCredentials");
        if (code == HttpStatusCode.TooManyRequests) return new AzureDevOpsClassificationResult(ProviderOutcomeKind.RateLimited, retryAfter ?? DefaultRateLimitFallback, "TooManyRequests");
        if (code == HttpStatusCode.Forbidden)
        {
            if (IsRateLimitBody(body)) return new AzureDevOpsClassificationResult(ProviderOutcomeKind.RateLimited, retryAfter ?? DefaultRateLimitFallback, "RateLimited");
            return new AzureDevOpsClassificationResult(ProviderOutcomeKind.ForbiddenScope, null, "ForbiddenScope");
        }
        if (code is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity) return new AzureDevOpsClassificationResult(ProviderOutcomeKind.RequestInvalid, null, "InvalidQuery");
        if (code == HttpStatusCode.NotFound) return new AzureDevOpsClassificationResult(ProviderOutcomeKind.ResourceMissing, null, "ResourceNotFound");
        if (code is HttpStatusCode.RequestTimeout or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout || (int)code >= 500)
            return new AzureDevOpsClassificationResult(ProviderOutcomeKind.Transient, TimeSpan.FromSeconds(15), "ServerError");
        return new AzureDevOpsClassificationResult(ProviderOutcomeKind.RequestInvalid, null, $"Http_{code}");
    }

    private static bool IsRateLimitBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;
        var b = body.Length > 512 ? body[..512] : body;
        return b.Contains("rate limit", StringComparison.OrdinalIgnoreCase) || b.Contains("too many requests", StringComparison.OrdinalIgnoreCase) || b.Contains("throttled", StringComparison.OrdinalIgnoreCase);
    }
}

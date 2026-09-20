using System.Net;
using System.Net.Sockets;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Interfaces;

namespace UnsecuredAPIKeys.Providers.Search_Providers;

public sealed record HuggingFaceClassificationResult(
    ProviderOutcomeKind Outcome,
    TimeSpan? RetryAfter = null,
    string? SanitizedCode = null);

/// <summary>
/// Hugging Face Hub response classifier (Phase 3, Task 20.2).
/// Only supported Hub APIs are used; website scraping is never substituted.
/// </summary>
public sealed class HuggingFaceResponseClassifier : ISearchProviderOutcomeClassifier
{
    private static readonly TimeSpan DefaultRateLimitFallback = TimeSpan.FromMinutes(1);

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

        return ClassifyStatusCode(statusCode.Value, responseBody, null).Outcome;
    }

    public HuggingFaceClassificationResult ClassifyResponse(
        HttpResponseMessage? response,
        string? responseBody = null,
        Exception? exception = null)
    {
        if (exception is not null)
        {
            if (exception is OperationCanceledException)
                return new HuggingFaceClassificationResult(ProviderOutcomeKind.Cancellation, null, "OperationCanceled");
            return new HuggingFaceClassificationResult(ProviderOutcomeKind.Transient, TimeSpan.FromSeconds(10), "TransportError");
        }

        if (response is null)
            return new HuggingFaceClassificationResult(ProviderOutcomeKind.Success);

        TimeSpan? retryAfter = null;
        if (response.Headers.TryGetValues("Retry-After", out var retryValues))
        {
            var raw = retryValues.FirstOrDefault();
            if (int.TryParse(raw, out var seconds) && seconds > 0)
                retryAfter = TimeSpan.FromSeconds(Math.Min(seconds, 3600));
        }

        return ClassifyStatusCode(response.StatusCode, responseBody, retryAfter);
    }

    private static HuggingFaceClassificationResult ClassifyStatusCode(
        HttpStatusCode code,
        string? responseBody,
        TimeSpan? headerRetryAfter)
    {
        if (code is HttpStatusCode.OK or HttpStatusCode.Created or HttpStatusCode.NoContent)
            return new HuggingFaceClassificationResult(ProviderOutcomeKind.Success);
        if (code == HttpStatusCode.Unauthorized)
            return new HuggingFaceClassificationResult(ProviderOutcomeKind.AuthInvalid, null, "BadCredentials");
        if (code == HttpStatusCode.TooManyRequests)
            return new HuggingFaceClassificationResult(ProviderOutcomeKind.RateLimited, headerRetryAfter ?? DefaultRateLimitFallback, "TooManyRequests");
        if (code == HttpStatusCode.Forbidden)
        {
            if (IsRateLimitBody(responseBody))
                return new HuggingFaceClassificationResult(ProviderOutcomeKind.RateLimited, headerRetryAfter ?? DefaultRateLimitFallback, "RateLimited");
            return new HuggingFaceClassificationResult(ProviderOutcomeKind.ForbiddenScope, null, "ForbiddenScope");
        }
        if (code is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity)
            return new HuggingFaceClassificationResult(ProviderOutcomeKind.RequestInvalid, null, "InvalidQuery");
        if (code == HttpStatusCode.NotFound)
            return new HuggingFaceClassificationResult(ProviderOutcomeKind.ResourceMissing, null, "ResourceNotFound");
        if (code is HttpStatusCode.RequestTimeout or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout || (int)code >= 500)
            return new HuggingFaceClassificationResult(ProviderOutcomeKind.Transient, TimeSpan.FromSeconds(15), "ServerError");
        return new HuggingFaceClassificationResult(ProviderOutcomeKind.RequestInvalid, null, $"Http_{code}");
    }

    private static bool IsRateLimitBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;
        var bounded = body.Length > 512 ? body[..512] : body;
        return bounded.Contains("rate limit", StringComparison.OrdinalIgnoreCase) ||
               bounded.Contains("too many requests", StringComparison.OrdinalIgnoreCase) ||
               bounded.Contains("throttled", StringComparison.OrdinalIgnoreCase);
    }
}

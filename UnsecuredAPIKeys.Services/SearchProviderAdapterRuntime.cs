using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Data.Models;
using UnsecuredAPIKeys.Providers;
using UnsecuredAPIKeys.Providers._Interfaces;

namespace UnsecuredAPIKeys.Services;

/// <summary>
/// Default provider outcome classifier adhering to the closed Typed Outcome model (AC-8.20, AC-6.1-AC-6.34).
/// </summary>
public sealed class SearchProviderOutcomeClassifier : ISearchProviderOutcomeClassifier
{
    public ProviderOutcomeKind Classify(
        SearchProviderEnum providerKind,
        HttpStatusCode? statusCode,
        Exception? exception = null,
        string? responseBody = null)
    {
        if (exception is OperationCanceledException)
        {
            return ProviderOutcomeKind.Cancellation;
        }

        if (exception is HttpRequestException or SocketException or TimeoutException)
        {
            return ProviderOutcomeKind.Transient;
        }

        if (statusCode.HasValue)
        {
            return statusCode.Value switch
            {
                HttpStatusCode.OK or HttpStatusCode.Created or HttpStatusCode.NoContent =>
                    ProviderOutcomeKind.Success,

                HttpStatusCode.Unauthorized =>
                    ProviderOutcomeKind.AuthInvalid,

                HttpStatusCode.Forbidden =>
                    ProviderOutcomeKind.ForbiddenScope,

                HttpStatusCode.TooManyRequests =>
                    ProviderOutcomeKind.RateLimited,

                HttpStatusCode.NotFound =>
                    ProviderOutcomeKind.ResourceMissing,

                HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity =>
                    ProviderOutcomeKind.RequestInvalid,

                HttpStatusCode.InternalServerError or
                HttpStatusCode.BadGateway or
                HttpStatusCode.ServiceUnavailable or
                HttpStatusCode.GatewayTimeout =>
                    ProviderOutcomeKind.Transient,

                _ => (int)statusCode.Value >= 500
                    ? ProviderOutcomeKind.Transient
                    : ProviderOutcomeKind.RequestInvalid
            };
        }

        return exception is not null ? ProviderOutcomeKind.Transient : ProviderOutcomeKind.Success;
    }
}

/// <summary>
/// Safe Provider Adapter Runtime interface (Wave 8, Task 8.5).
/// Enforces Operation Slots, credential Claim Leases, pre-traffic kind matching,
/// operation bounds, and provider error classification before provider invocation.
/// </summary>
public interface ISearchProviderAdapterRuntime
{
    IHttpClientFactory HttpClientFactory { get; }
    ISearchProviderOutcomeClassifier OutcomeClassifier { get; }

    Task<ProviderCapabilityResult> DiscoverCapabilitiesAsync(
        ISearchProviderAdapter adapter,
        ValidatedProviderInstance instance,
        Guid slotId,
        CancellationToken cancellationToken = default);

    Task<ProviderOperationResult<CredentialValidationResult, ProviderOutcomeKind>> ValidateCredentialAsync(
        ISearchProviderAdapter adapter,
        ProviderOperationContext context,
        CredentialMaterial credential,
        CancellationToken cancellationToken = default);

    Task<TranslatedProviderQuery> TranslateQueryAsync(
        ISearchProviderAdapter adapter,
        ProviderOperationContext context,
        SearchQuerySnapshot query,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<ProviderOperationResult<NormalizedSearchPage, ProviderOutcomeKind>> SearchAsync(
        ISearchProviderAdapter adapter,
        ProviderOperationContext context,
        CancellationToken cancellationToken = default);

    Task<ProviderOperationResult<NormalizedContent, ProviderOutcomeKind>> FetchContentAsync(
        ISearchProviderAdapter adapter,
        ProviderOperationContext context,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProviderOperationResult<NormalizedContent, ProviderOutcomeKind>>> FetchContentBatchAsync(
        ISearchProviderAdapter adapter,
        ProviderOperationContext context,
        int count,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Safe runtime path implementing Task 8.5 requirements.
/// </summary>
public sealed class SearchProviderAdapterRuntime : ISearchProviderAdapterRuntime
{
    private const string MissingSlotMessage =
        "Provider adapter runtime invocation is disabled until an Operation Slot and, when required, a credential Claim are active.";

    private const string MissingLeaseMessage =
        "This credentialed adapter operation requires an active Lease. Acquire a Claim before invoking credentialed methods.";

    private static readonly HashSet<string> WellKnownPublicSaaSOrigins = new(StringComparer.OrdinalIgnoreCase)
    {
        "api.github.com",
        "github.com",
        "gitlab.com",
        "sourcegraph.com",
        "huggingface.co",
        "dev.azure.com"
    };

    private readonly IHttpClientFactory httpClientFactory;
    private readonly EndpointPolicy endpointPolicy;
    private readonly ISearchProviderOutcomeClassifier outcomeClassifier;
    private readonly ILogger<SearchProviderAdapterRuntime>? logger;
    private readonly SearchPlatformMetrics? metrics;

    public IHttpClientFactory HttpClientFactory => httpClientFactory;
    public ISearchProviderOutcomeClassifier OutcomeClassifier => outcomeClassifier;

    public SearchProviderAdapterRuntime(
        IHttpClientFactory httpClientFactory,
        EndpointPolicy endpointPolicy,
        ISearchProviderOutcomeClassifier outcomeClassifier,
        ILogger<SearchProviderAdapterRuntime>? logger = null,
        SearchPlatformMetrics? metrics = null)
    {
        this.httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        this.endpointPolicy = endpointPolicy ?? throw new ArgumentNullException(nameof(endpointPolicy));
        this.outcomeClassifier = outcomeClassifier ?? throw new ArgumentNullException(nameof(outcomeClassifier));
        this.logger = logger;
        this.metrics = metrics;
    }

    private void RecordOperation(
        SearchProviderEnum providerKind,
        Guid instanceStableId,
        ProviderOutcomeKind outcome,
        TimeSpan elapsed)
    {
        metrics?.RecordOperation(providerKind, instanceStableId, outcome);
        metrics?.ObserveOperationDuration(providerKind, instanceStableId, elapsed);
    }

    public async Task<ProviderCapabilityResult> DiscoverCapabilitiesAsync(
        ISearchProviderAdapter adapter,
        ValidatedProviderInstance instance,
        Guid slotId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(instance);

        // Pre-traffic kind rejection (AC-2.19, AC-8.26)
        AdapterKindGuard.RequireMatchingKind(adapter.ProviderKind, instance.ProviderKind);

        // Slot capacity gating (AC-8.7, AC-8.24, AC-17.45)
        RequireSlot(slotId);

        var timer = Stopwatch.StartNew();
        var capabilities = await adapter.DiscoverCapabilitiesAsync(instance, cancellationToken);
        RecordOperation(adapter.ProviderKind, instance.StableId, capabilities.Outcome, timer.Elapsed);
        return capabilities;
    }

    public async Task<ProviderOperationResult<CredentialValidationResult, ProviderOutcomeKind>> ValidateCredentialAsync(
        ISearchProviderAdapter adapter,
        ProviderOperationContext context,
        CredentialMaterial credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(credential);

        // Pre-traffic kind rejection (AC-2.19, AC-8.26)
        AdapterKindGuard.RequireMatchingKind(adapter.ProviderKind, context.ProviderInstance.ProviderKind);

        // Slot and Claim Lease gating (AC-8.15, AC-17.45, AC-17.46)
        RequireSlot(context.SlotId);
        RequireLease(context.LeaseId);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(context.Bounds.Timeout);

        var timer = Stopwatch.StartNew();
        try
        {
            var validation = await adapter.ValidateCredentialAsync(context, credential, cts.Token);
            RecordOperation(
                adapter.ProviderKind, context.ProviderInstance.StableId,
                validation.Outcome, timer.Elapsed);
            return validation;
        }
        catch (Exception ex)
        {
            var outcome = outcomeClassifier.Classify(adapter.ProviderKind, null, ex);
            logger?.LogWarning(ex, "Credential validation failed with outcome {Outcome}", outcome);
            RecordOperation(
                adapter.ProviderKind, context.ProviderInstance.StableId, outcome, timer.Elapsed);
            return new ProviderOperationResult<CredentialValidationResult, ProviderOutcomeKind>(
                outcome, new CredentialValidationResult(false));
        }
    }

    public async Task<TranslatedProviderQuery> TranslateQueryAsync(
        ISearchProviderAdapter adapter,
        ProviderOperationContext context,
        SearchQuerySnapshot query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(query);

        // Pre-traffic kind rejection (AC-2.19, AC-8.26)
        AdapterKindGuard.RequireMatchingKind(adapter.ProviderKind, context.ProviderInstance.ProviderKind);

        // Operation Slot is required for all operations
        RequireSlot(context.SlotId);

        return await adapter.TranslateQueryAsync(context, query, cancellationToken);
    }

    public async IAsyncEnumerable<ProviderOperationResult<NormalizedSearchPage, ProviderOutcomeKind>> SearchAsync(
        ISearchProviderAdapter adapter,
        ProviderOperationContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(context);

        // Pre-traffic kind rejection (AC-2.19, AC-8.26)
        AdapterKindGuard.RequireMatchingKind(adapter.ProviderKind, context.ProviderInstance.ProviderKind);

        // Slot is always required (AC-17.45)
        RequireSlot(context.SlotId);

        // Self-hosted and private credentialed calls require a Claim (AC-8.17, AC-8.18)
        if (IsSelfHosted(context.ProviderInstance))
        {
            RequireLease(context.LeaseId);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(context.Bounds.Timeout);

        var timer = Stopwatch.StartNew();
        var terminalOutcome = ProviderOutcomeKind.Success;
        try
        {
            var eventCount = 0;
            var enumerator = adapter.SearchAsync(context, cts.Token).GetAsyncEnumerator(cts.Token);

            while (true)
            {
                ProviderOperationResult<NormalizedSearchPage, ProviderOutcomeKind>? current = null;
                ProviderOperationResult<NormalizedSearchPage, ProviderOutcomeKind>? errorResult = null;
                bool hasMore;

                try
                {
                    hasMore = await enumerator.MoveNextAsync();
                    if (hasMore)
                    {
                        current = enumerator.Current;
                    }
                }
                catch (Exception ex)
                {
                    var outcome = outcomeClassifier.Classify(adapter.ProviderKind, null, ex);
                    logger?.LogWarning(ex, "Search stream encountered error classified as {Outcome}", outcome);
                    errorResult = new ProviderOperationResult<NormalizedSearchPage, ProviderOutcomeKind>(
                        outcome, new NormalizedSearchPage([]));
                    hasMore = false;
                }

                if (errorResult is not null)
                {
                    terminalOutcome = errorResult.Outcome;
                    yield return errorResult;
                    yield break;
                }

                if (!hasMore || current is null)
                    break;

                terminalOutcome = current.Outcome;

                // Enforce MaxEventCount bound (AC-8.16, AC-8.21)
                eventCount++;
                yield return current;

                if (eventCount >= context.Bounds.MaxEventCount)
                {
                    logger?.LogDebug("Search reached MaxEventCount bound ({Count})", eventCount);
                    yield break;
                }
            }
        }
        finally
        {
            RecordOperation(
                adapter.ProviderKind, context.ProviderInstance.StableId, terminalOutcome, timer.Elapsed);
        }
    }

    public async Task<ProviderOperationResult<NormalizedContent, ProviderOutcomeKind>> FetchContentAsync(
        ISearchProviderAdapter adapter,
        ProviderOperationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(context);

        // Pre-traffic kind rejection (AC-2.19, AC-8.26)
        AdapterKindGuard.RequireMatchingKind(adapter.ProviderKind, context.ProviderInstance.ProviderKind);

        // Slot and Claim Lease gating (AC-8.15, AC-17.45, AC-17.46)
        RequireSlot(context.SlotId);
        RequireLease(context.LeaseId);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(context.Bounds.Timeout);

        var timer = Stopwatch.StartNew();
        try
        {
            var content = await adapter.FetchContentAsync(context, cts.Token);
            RecordOperation(
                adapter.ProviderKind, context.ProviderInstance.StableId,
                content.Outcome, timer.Elapsed);
            return content;
        }
        catch (Exception ex)
        {
            var outcome = outcomeClassifier.Classify(adapter.ProviderKind, null, ex);
            logger?.LogWarning(ex, "Content fetch failed with outcome {Outcome}", outcome);
            RecordOperation(
                adapter.ProviderKind, context.ProviderInstance.StableId, outcome, timer.Elapsed);
            return new ProviderOperationResult<NormalizedContent, ProviderOutcomeKind>(outcome, null);
        }
    }

    public async Task<IReadOnlyList<ProviderOperationResult<NormalizedContent, ProviderOutcomeKind>>> FetchContentBatchAsync(
        ISearchProviderAdapter adapter,
        ProviderOperationContext context,
        int count,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(context);

        if (count <= 0)
            return [];

        // Concurrency bounded by MaxContentConcurrency (AC-8.16)
        var maxConcurrency = Math.Max(1, context.Bounds.MaxContentConcurrency);
        using var semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);

        var tasks = Enumerable.Range(0, count).Select(async _ =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                return await FetchContentAsync(adapter, context, cancellationToken);
            }
            finally
            {
                semaphore.Release();
            }
        });

        return await Task.WhenAll(tasks);
    }

    private static void RequireSlot(Guid? slotId)
    {
        if (slotId is null || slotId == Guid.Empty)
            throw new InvalidOperationException(MissingSlotMessage);
    }

    private static void RequireLease(Guid? leaseId)
    {
        if (leaseId is null || leaseId == Guid.Empty)
            throw new InvalidOperationException(MissingLeaseMessage);
    }

    private static bool IsSelfHosted(ValidatedProviderInstance instance) =>
        !WellKnownPublicSaaSOrigins.Contains(instance.Host);
}

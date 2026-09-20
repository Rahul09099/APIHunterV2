using System.Net;
using System.Net.Http.Json;

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using UnsecuredAPIKeys.Data;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Data.DTOs;
using UnsecuredAPIKeys.Data.Models;
using UnsecuredAPIKeys.Providers;
using UnsecuredAPIKeys.Providers._Interfaces;

namespace UnsecuredAPIKeys.Services;

/// <summary>One Worker provider-operation attempt.</summary>
public sealed record WorkerOperationResult(
    SearchProviderEnum ProviderKind,
    bool Executed,
    ProviderOutcomeKind Outcome,
    int DiscoveriesReported,
    int Renewals,
    string? FailureReason = null,
    bool Outage = false);

/// <summary>One Worker orchestration cycle across all synced queries.</summary>
public sealed record WorkerCycleResult(
    int QueriesSynced,
    int OperationsExecuted,
    int DiscoveriesReported,
    int Renewals,
    int Failures,
    bool Outage);

/// <summary>
/// Worker orchestration cycle (Wave 13, Task 13.2). Stateless and DB-free: every
/// iteration syncs credential-free configuration, Claims just-in-time before provider
/// use, renews before the Lease threshold, reports actual provenance, and completes in
/// `finally` while the Master is reachable. Master/database outage is surfaced as
/// <see cref="WorkerCycleResult.Outage"/> for bounded backoff; there is no local
/// credential fallback anywhere on this path.
/// </summary>
public sealed class WorkerCycleRunner(
    IMasterApiClient master,
    SearchProviderAdapterRegistry adapters,
    WorkerSecretExtractor extractor,
    WorkerScraperOptions options,
    ILogger<WorkerCycleRunner> logger,
    SearchPlatformMetrics? metrics = null)
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StopBeforeExpiryMargin = TimeSpan.FromSeconds(30);
    private const double RenewalExtensionSeconds = 300;

    /// <summary>Well-known managed-SaaS origins. Self-hosted instances are out of scope and skipped.</summary>
    internal static bool TryGetManagedSaaS(
        SearchProviderEnum kind,
        out Guid defaultStableId,
        out ValidatedProviderInstance instance)
    {
        if (kind == SearchProviderEnum.GitHub)
        {
            defaultStableId = ProviderInstanceSchema.DefaultGitHubStableId;
            instance = new ValidatedProviderInstance(
                defaultStableId, kind, "https", "api.github.com", 443, "/", 1, "{}");
            return true;
        }

        if (kind == SearchProviderEnum.GitLab)
        {
            defaultStableId = ProviderInstanceSchema.DefaultGitLabStableId;
            instance = new ValidatedProviderInstance(
                defaultStableId, kind, "https", "gitlab.com", 443, "/api/v4", 1, "{}");
            return true;
        }

        if (kind == SearchProviderEnum.Sourcegraph)
        {
            defaultStableId = ProviderInstanceSchema.DefaultSourcegraphStableId;
            instance = new ValidatedProviderInstance(
                defaultStableId, kind, "https", "sourcegraph.com", 443, "/.api", 1, "{}");
            return true;
        }

        if (kind == SearchProviderEnum.HuggingFace)
        {
            defaultStableId = ProviderInstanceSchema.DefaultHuggingFaceStableId;
            instance = new ValidatedProviderInstance(
                defaultStableId, kind, "https", "huggingface.co", 443, "/api", 1, "{}");
            return true;
        }

        if (kind == SearchProviderEnum.AzureDevOps)
        {
            defaultStableId = ProviderInstanceSchema.DefaultAzureDevOpsStableId;
            instance = new ValidatedProviderInstance(
                defaultStableId, kind, "https", "dev.azure.com", 443, "/", 1, "{}");
            return true;
        }

        defaultStableId = Guid.Empty;
        instance = null!;
        return false;
    }

    public async Task<WorkerCycleResult> RunCycleAsync(CancellationToken cancellationToken)
    {
        NodeSyncDTO? sync;
        try
        {
            sync = await master.GetSyncAsync(cancellationToken);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning("Worker sync unreachable: {Message}", error.Message);
            return new WorkerCycleResult(0, 0, 0, 0, 0, Outage: true);
        }

        if (sync is null)
        {
            return new WorkerCycleResult(0, 0, 0, 0, 0, Outage: true);
        }

        var queries = sync.Queries.Where(query => query.IsEnabled).ToList();
        var descriptors = sync.ProviderInstances
            .Where(descriptor =>
                descriptor.StableId != Guid.Empty &&
                descriptor.ProviderKind != SearchProviderEnum.Unknown)
            .ToList();

        var executed = 0;
        var reported = 0;
        var renewals = 0;
        var failures = 0;
        var outage = false;

        foreach (var query in queries)
        {
            if (executed >= options.MaxOperationsPerCycle)
            {
                break;
            }

            foreach (var descriptor in descriptors)
            {
                if (executed >= options.MaxOperationsPerCycle)
                {
                    break;
                }

                cancellationToken.ThrowIfCancellationRequested();
                var operation = await RunOperationAsync(descriptor, query.Query, cancellationToken);
                if (operation.Executed)
                {
                    executed++;
                    reported += operation.DiscoveriesReported;
                    renewals += operation.Renewals;
                }
                else if (operation.Outage)
                {
                    outage = true;
                }
                else if (operation.FailureReason is not null)
                {
                    failures++;
                }
            }
        }

        return new WorkerCycleResult(queries.Count, executed, reported, renewals, failures, outage);
    }

    internal async Task<WorkerOperationResult> RunOperationAsync(
        ProviderInstanceDescriptor descriptor,
        string queryText,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(queryText))
        {
            return NotExecuted(descriptor.ProviderKind, "The synced query is empty.");
        }

        if (!TryGetManagedSaaS(descriptor.ProviderKind, out var defaultStableId, out var validated))
        {
            return NotExecuted(descriptor.ProviderKind, "Only managed-SaaS kinds execute on Workers.");
        }

        if (descriptor.StableId != defaultStableId)
        {
            logger.LogWarning(
                "Worker skips self-hosted instance {StableId}: endpoint distribution is out of scope.",
                descriptor.StableId);
            return NotExecuted(descriptor.ProviderKind, "Self-hosted instances are out of scope.");
        }

        ISearchProviderAdapter adapter;
        try
        {
            adapter = adapters.GetRequiredAdapter(descriptor.ProviderKind);
            AdapterKindGuard.RequireMatchingKind(adapter.ProviderKind, descriptor.ProviderKind);
        }
        catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException)
        {
            return NotExecuted(descriptor.ProviderKind, error.Message);
        }

        // Just-in-time Claim immediately before provider use.
        CredentialClaimResponse? claim;
        HttpStatusCode? claimStatus = null;
        try
        {
            using var claimResponse = await master.ClaimAsync(
                new CredentialClaimRequest(
                    descriptor.StableId,
                    Guid.Empty,
                    "default",
                    Guid.NewGuid(),
                    GenericQuery: queryText),
                cancellationToken);
            claimStatus = claimResponse.StatusCode;
            claim = await ReadClaimAsync(claimResponse, cancellationToken);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        {
            metrics?.RecordWorkerClaim(WorkerClaimApiStatus.TransportError);
            return NotExecuted(descriptor.ProviderKind, "Claim unreachable.", outage: true);
        }

        metrics?.RecordWorkerClaim(MapClaimStatus(claimStatus, claim));
        if (claim is null)
        {
            return NotExecuted(descriptor.ProviderKind, "Claim rejected without a typed body.");
        }
        if (claim.Replayed || string.IsNullOrEmpty(claim.CredentialMaterial))
        {
            // A replay carries no material by design; the worker must start new work
            // with a fresh Request ID instead of reusing this lease.
            return NotExecuted(descriptor.ProviderKind, "Claim replayed without material.");
        }
        if (claim.OperationSlotId == Guid.Empty)
        {
            await TryCompleteAsync(
                claim, ProviderOutcomeKind.Transient, cancellationToken);
            return ExecutedResult(descriptor.ProviderKind, ProviderOutcomeKind.Transient, 0, 0,
                "Claim recorded no operation slot.");
        }

        Guid? completedLease = null;
        var overallOutcome = ProviderOutcomeKind.Transient;
        try
        {
            var credential = new CredentialMaterial(claim.CredentialMaterial);
            var bounds = new ProviderOperationBounds(
                4 * 1024 * 1024,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "application/json", "application/octet-stream", "text/plain"
                },
                10_000,
                4,
                OperationTimeout);
            var context = new ProviderOperationContext(
                validated,
                bounds,
                Continuation: null,
                ContinuationAdapterVersion: null,
                SlotId: claim.OperationSlotId,
                LeaseId: claim.LeaseId,
                Credential: credential);

            var translated = await adapter.TranslateQueryAsync(
                context,
                new SearchQuerySnapshot(queryText, null, "{}"),
                cancellationToken);
            var searchContext = context with { SearchQuery = translated.Query };

            var leaseExpiresUtc = claim.LeaseExpiresUtc;
            var renewAtUtc = leaseExpiresUtc.AddSeconds(-claim.RenewalThresholdSeconds);
            var revision = claim.CredentialRevision;
            var renewals = 0;
            var reported = 0;
            overallOutcome = ProviderOutcomeKind.Success;

            await foreach (var page in adapter.SearchAsync(searchContext, cancellationToken))
            {
                if (page.Outcome != ProviderOutcomeKind.Success &&
                    overallOutcome == ProviderOutcomeKind.Success)
                {
                    overallOutcome = page.Outcome;
                }

                if (page.Value is not null)
                {
                    reported += await FetchAndReportAsync(
                        adapter, validated, bounds, claim, searchContext.Credential!,
                        page.Value.Results, cancellationToken);
                }

                if (page.Continuation is null)
                {
                    break;
                }

                // Renew before the threshold; stop before expiry when renewal is impossible.
                if (DateTime.UtcNow >= renewAtUtc)
                {
                    var renewed = await TryRenewAsync(claim, revision, cancellationToken);
                    if (renewed.NewExpiry is null)
                    {
                        overallOutcome = overallOutcome == ProviderOutcomeKind.Success
                            ? ProviderOutcomeKind.Transient
                            : overallOutcome;
                        break;
                    }

                    renewals++;
                    revision = renewed.NewRevision ?? revision;
                    leaseExpiresUtc = renewed.NewExpiry.Value;
                    renewAtUtc = leaseExpiresUtc.AddSeconds(-claim.RenewalThresholdSeconds);
                }

                if (leaseExpiresUtc - DateTime.UtcNow < StopBeforeExpiryMargin)
                {
                    break;
                }
            }

            completedLease = claim.LeaseId;
            await TryCompleteAsync(claim with { CredentialRevision = revision }, overallOutcome, cancellationToken);
            return ExecutedResult(descriptor.ProviderKind, overallOutcome, reported, renewals, null);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        {
            return ExecutedResult(descriptor.ProviderKind, overallOutcome, 0, 0,
                "Provider execution interrupted.", outage: true);
        }
        finally
        {
            if (completedLease is null)
            {
                await TryCompleteAsync(claim, overallOutcome, CancellationToken.None);
            }
        }
    }

    private async Task<int> FetchAndReportAsync(
        ISearchProviderAdapter adapter,
        ValidatedProviderInstance validated,
        ProviderOperationBounds bounds,
        CredentialClaimResponse claim,
        CredentialMaterial credential,
        IReadOnlyList<object> results,
        CancellationToken cancellationToken)
    {
        var discoveries = new List<NodeReportDto>();
        foreach (var input in results.OfType<ProviderResultInput>())
        {
            cancellationToken.ThrowIfCancellationRequested();

            string? content = null;
            var locator = WorkerContentLocator.Resolve(input, validated);
            if (locator is not null)
            {
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(OperationTimeout);
                    var contentContext = new ProviderOperationContext(
                        validated,
                        bounds,
                        Continuation: null,
                        ContinuationAdapterVersion: null,
                        SlotId: claim.OperationSlotId,
                        LeaseId: claim.LeaseId,
                        Credential: credential,
                        ContentApiUrl: locator.ContentApiUrl,
                        ContentPath: locator.ContentPath,
                        ContentRevision: locator.ContentRevision,
                        ContentRepositoryOwner: locator.ContentRepositoryOwner,
                        ContentRepositoryName: locator.ContentRepositoryName);
                    var fetched = await adapter.FetchContentAsync(contentContext, timeout.Token);
                    if (fetched.Outcome == ProviderOutcomeKind.Success && fetched.Value is not null)
                    {
                        content = fetched.Value.Content;
                    }
                }
                catch
                {
                    // Content failure skips one file, never the operation.
                }
            }

            foreach (var secret in extractor.ExtractSecrets(content ?? input.Snippet))
            {
                discoveries.Add(new NodeReportDto
                {
                    ApiKey = secret.ApiKey,
                    ApiType = secret.ApiType,
                    RepoName = input.RepositoryName ?? string.Empty,
                    RepoOwner = input.RepositoryOwner ?? string.Empty,
                    FilePath = input.NormalizedFilePath,
                    FileUrl = input.ProvenanceUrl ?? string.Empty,
                    ProviderKind = input.ProviderKind,
                    ProviderInstanceStableId = claim.ProviderInstanceStableId,
                    WorkItemStableId = claim.WorkItemStableId,
                    PartitionKey = claim.PartitionKey,
                    LeaseId = claim.LeaseId,
                    OperationSlotId = claim.OperationSlotId
                });
            }
        }

        if (discoveries.Count == 0)
        {
            return 0;
        }

        try
        {
            using var response = await master.ReportAsync(
                new NodeBulkReportDto { Discoveries = discoveries }, cancellationToken);
            response.EnsureSuccessStatusCode();
            metrics?.AddWorkerDiscoveries(
                adapter.ProviderKind, claim.ProviderInstanceStableId, discoveries.Count);
            return discoveries.Count;
        }
        catch
        {
            return 0;
        }
    }

    private async Task<(DateTime? NewExpiry, long? NewRevision)> TryRenewAsync(
        CredentialClaimResponse claim,
        long revision,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await master.RenewAsync(
                claim.LeaseId,
                new CredentialLeaseRenewRequest(
                    claim.CredentialStableId,
                    revision,
                    claim.WorkItemStableId ?? Guid.Empty,
                    claim.ProviderInstanceStableId,
                    claim.PartitionKey ?? "default",
                    RenewalExtensionSeconds),
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                metrics?.RecordWorkerRenewal(WorkerRenewalStatus.Failure);
                return (null, null);
            }

            var body = await response.Content.ReadFromJsonAsync<CredentialLeaseRenewResponse>(
                cancellationToken: cancellationToken);
            if (body?.Succeeded == true && body.NewLeaseExpiresUtc.HasValue)
            {
                metrics?.RecordWorkerRenewal(WorkerRenewalStatus.Success);
                return (body.NewLeaseExpiresUtc, body.CurrentRevision);
            }

            metrics?.RecordWorkerRenewal(WorkerRenewalStatus.Failure);
            return (null, null);
        }
        catch
        {
            metrics?.RecordWorkerRenewal(WorkerRenewalStatus.Failure);
            return (null, null);
        }
    }

    private async Task TryCompleteAsync(
        CredentialClaimResponse claim,
        ProviderOutcomeKind outcome,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await master.CompleteAsync(
                claim.LeaseId,
                new CredentialLeaseCompleteRequest(
                    claim.CredentialStableId,
                    claim.CredentialRevision,
                    outcome.ToString(),
                    claim.WorkItemStableId,
                    claim.ProviderInstanceStableId,
                    claim.PartitionKey),
                timeout.Token);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception error)
        {
            logger.LogWarning(
                "Worker completion for lease {LeaseId} did not reach the Master: {Message}",
                claim.LeaseId, error.Message);
        }
    }

    private static async Task<CredentialClaimResponse?> ReadClaimAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            try
            {
                return await response.Content.ReadFromJsonAsync<CredentialClaimResponse>(
                    cancellationToken: cancellationToken);
            }
            catch (Exception error) when (
                error is NotSupportedException or System.Text.Json.JsonException)
            {
                return null;
            }
        }
    }

    private static WorkerClaimApiStatus MapClaimStatus(
        HttpStatusCode? status,
        CredentialClaimResponse? claim) =>
        claim is not null
            ? WorkerClaimApiStatus.Success
            : status switch
            {
                HttpStatusCode.Conflict => WorkerClaimApiStatus.Conflict,
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => WorkerClaimApiStatus.AuthError,
                _ => WorkerClaimApiStatus.TransportError
            };

    private static WorkerOperationResult NotExecuted(
        SearchProviderEnum kind, string reason, bool outage = false) =>
        new(kind, false, ProviderOutcomeKind.Transient, 0, 0, reason, outage);

    private static WorkerOperationResult ExecutedResult(
        SearchProviderEnum kind,
        ProviderOutcomeKind outcome,
        int reported,
        int renewals,
        string? failureReason,
        bool outage = false) =>
        new(kind, outcome == ProviderOutcomeKind.Success, outcome, reported, renewals, failureReason, outage);
}

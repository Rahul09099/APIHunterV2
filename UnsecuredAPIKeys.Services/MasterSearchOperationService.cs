using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using UnsecuredAPIKeys.Data;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Data.Models;
using UnsecuredAPIKeys.Providers;
using UnsecuredAPIKeys.Providers._Interfaces;

namespace UnsecuredAPIKeys.Services;

/// <summary>
/// One durable Master search operation: generic query text plus optional native override.
/// The orchestrator resolves the Provider Instance, Work, Claim, and Slot — never the caller.
/// </summary>
public sealed record MasterOperationQuery(
    Guid ProviderInstanceStableId,
    SearchProviderEnum ProviderKind,
    string GenericQuery,
    string? NativeOverride = null,
    string SettingsJson = "{}",
    int? SearchQueryId = null,
    string PartitionKey = "default");

/// <summary>
/// Outcome of one Master operation attempt.
/// <see cref="MigratedPathTaken"/> is false only when the migrated path is unavailable
/// (flags off, readiness unhealthy, no orchestrator) — the caller must then use the
/// explicit pre-cutover legacy rollback path. Every post-gate failure returns
/// <see cref="MigratedPathTaken"/> true so a query is never executed twice.
/// </summary>
public sealed record MasterOperationResult(
    bool MigratedPathTaken,
    bool Succeeded,
    ProviderOutcomeKind Outcome,
    int PersistedResults,
    int DeduplicatedResults,
    int ContentFetched,
    int ContentFailed,
    string? FailureReason = null,
    string? Continuation = null,
    TimeSpan? RetryAfter = null,
    Guid? LeaseId = null)
{
    public static MasterOperationResult NotAvailable(string reason) => new(
        MigratedPathTaken: false,
        Succeeded: false,
        Outcome: ProviderOutcomeKind.Transient,
        PersistedResults: 0,
        DeduplicatedResults: 0,
        ContentFetched: 0,
        ContentFailed: 0,
        FailureReason: reason);
}

/// <summary>
/// Feature-flagged Master search operation (Wave 10, Task 10.4).
/// Routes one query through the durable platform end to end:
/// Grant preview → Work/query → Claim+Slot → endpoint-validated adapter
/// search/content → result/provenance/checkpoint → scheduler completion.
/// The implementation contains no provider-specific branch: GitHub and GitLab
/// (and later providers) execute through these exact contracts; the adapter
/// registry resolves behavior per Provider Kind and the scheduler, Work,
/// result, and completion services are provider-agnostic.
/// </summary>
public sealed class MasterSearchOperationService(
    DBContext dbContext,
    SearchProviderAdapterRegistry adapterRegistry,
    IWorkService workService,
    ICredentialScheduler scheduler,
    ResultPersistenceService resultPersistence,
    ICredentialGrantEvaluator grantEvaluator,
    CredentialMaterialAccessService materialAccess,
    IConfiguration configuration,
    ISearchPlatformSchedulingReadinessService schedulingReadiness,
    EndpointPolicy? endpointPolicy = null,
    EndpointPolicyOptions? endpointPolicyOptions = null,
    ILogger<MasterSearchOperationService>? logger = null)
{
    /// <summary>Maximum content fetches per operation; content is best-effort enrichment.</summary>
    public const int MaxContentFetchPerOperation = 10;

    private readonly EndpointPolicy endpoint = endpointPolicy ?? new EndpointPolicy();
    private readonly EndpointPolicyOptions bounds = endpointPolicyOptions ?? new EndpointPolicyOptions();

    /// <summary>
    /// True only when the Task 2.4 matrix permits Master scraper Claims and the
    /// scheduling readiness projection is healthy. Missing or invalid flag values
    /// are disabled; the legacy cursor path owns every query while this is false.
    /// </summary>
    public async Task<bool> IsMigratedPathAvailableAsync(
        CancellationToken cancellationToken = default)
    {
        if (!ReadStrictBoolean(configuration, SearchPlatformFeatureFlagNames.MasterScraperClaimsEnabled) ||
            !ReadStrictBoolean(configuration, SearchPlatformFeatureFlagNames.SchedulerEnabled))
        {
            return false;
        }

        var readiness = await schedulingReadiness.EvaluateAsync(cancellationToken);
        return readiness.IsReady;
    }

    public async Task<MasterOperationResult> ExecuteAsync(
        SchedulerPrincipal principal,
        string nodeId,
        Guid requestId,
        MasterOperationQuery operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(operation);

        if (string.IsNullOrWhiteSpace(nodeId))
            throw new ArgumentException("Node ID must be non-empty.", nameof(nodeId));
        if (requestId == Guid.Empty)
            throw new ArgumentException("Request ID must be non-empty.", nameof(requestId));
        if (operation.ProviderInstanceStableId == Guid.Empty ||
            operation.ProviderKind == SearchProviderEnum.Unknown ||
            string.IsNullOrWhiteSpace(operation.GenericQuery))
        {
            return MasterOperationResult.NotAvailable(
                "The migrated operation requires a known Provider Kind, instance, and query.");
        }

        // Gate first: unavailable means the caller falls back to the legacy path.
        // Everything after this point is an executed migrated operation that must
        // never be re-executed by the legacy path.
        if (!await IsMigratedPathAvailableAsync(cancellationToken))
        {
            return MasterOperationResult.NotAvailable(
                "The migrated Master operation path is not available (flags or readiness).");
        }

        // Authoritative Provider Instance (AC-2.3): the persisted ProviderKind wins;
        // a request/instance mismatch fails this operation instead of defaulting.
        var instance = await dbContext.SearchProviderInstances
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.StableId == operation.ProviderInstanceStableId,
                cancellationToken);
        if (instance is null || !instance.IsEnabled)
        {
            return ExecutedFailure(
                ProviderOutcomeKind.RequestInvalid,
                "The Provider Instance is missing or disabled.");
        }
        if (instance.ProviderKind != operation.ProviderKind)
        {
            return ExecutedFailure(
                ProviderOutcomeKind.RequestInvalid,
                "The requested Provider Kind does not match the authoritative instance kind.");
        }

        // Master-local grant preview through the common evaluator (AC-1.12): the same
        // policy that authorizes Worker claims gates Master work before any Claim.
        var authorized = await grantEvaluator.ApplyAuthorization(
                dbContext.SearchProviderTokens
                    .AsNoTracking()
                    .Where(credential =>
                        credential.ProviderInstanceId == instance.Id &&
                        credential.IsEnabled &&
                        !credential.IsArchived &&
                        credential.DisabledAtUtc == null),
                principal)
            .AnyAsync(cancellationToken);
        if (!authorized)
        {
            return ExecutedFailure(
                ProviderOutcomeKind.ForbiddenScope,
                "The principal holds no grant for this Provider Instance.");
        }

        ISearchProviderAdapter adapter;
        try
        {
            adapter = adapterRegistry.GetRequiredAdapter(operation.ProviderKind);
            AdapterKindGuard.RequireMatchingKind(adapter.ProviderKind, instance.ProviderKind);
        }
        catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException)
        {
            return ExecutedFailure(ProviderOutcomeKind.RequestInvalid, error.Message);
        }

        // Durable Work with the initiating Scheduler Principal persisted (AC-1.10/1.11).
        var (scope, telegramId) = MapPrincipal(principal);
        var snapshot = new SearchQuerySnapshot(
            operation.GenericQuery,
            operation.NativeOverride,
            operation.SettingsJson);
        WorkItemResult work;
        try
        {
            work = await workService.CreateWorkItemAsync(
                instance.Id,
                instance.ProviderKind,
                scope,
                telegramId,
                snapshot,
                adapter.AdapterVersion,
                cancellationToken);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            return ExecutedFailure(ProviderOutcomeKind.RequestInvalid, error.Message);
        }

        // Endpoint-validated configuration for this operation. Managed SaaS defaults
        // carry no per-instance approval (readiness treats them as safe); self-hosted
        // instances require current approval and per-operation DNS validation.
        ValidatedProviderInstance validatedInstance;
        ProviderOperationBounds operationBounds;
        try
        {
            (validatedInstance, operationBounds) = await ValidateInstanceForOperationAsync(
                instance,
                cancellationToken);
        }
        catch (EndpointPolicyException error)
        {
            return ExecutedFailure(ProviderOutcomeKind.RequestInvalid, error.Message);
        }

        // Atomic Claim: Lease + Operation Slot all-or-nothing (AC-5.51+).
        var claim = await scheduler.TryClaimAsync(
            operation.ProviderInstanceStableId,
            work.WorkItemStableId,
            operation.PartitionKey,
            nodeId,
            requestId,
            scope,
            telegramId,
            cancellationToken);
        if (!claim.IsSuccess || claim.Success is null)
        {
            return ExecutedFailure(
                ProviderOutcomeKind.Transient,
                claim.Unavailable?.Reason ?? "No credential Claim is available.",
                retryAfter: claim.Unavailable?.RetryAfter);
        }

        var leaseId = claim.Success.LeaseId;
        var credentialStableId = claim.Success.CredentialStableId;
        var credentialRevision = claim.Success.CredentialRevision;
        var completed = false;
        var overallOutcome = ProviderOutcomeKind.Transient;
        var persistedTotal = 0;
        var deduplicatedTotal = 0;
        var operationStartedUtc = DateTime.UtcNow;

        try
        {
            var slotId = await dbContext.OperationSlots
                .Where(slot => slot.RequestId == requestId && !slot.IsTerminal)
                .OrderByDescending(slot => slot.CreatedUtc)
                .Select(slot => (Guid?)slot.SlotId)
                .FirstOrDefaultAsync(cancellationToken);
            if (slotId is null || slotId == Guid.Empty)
            {
                var slotResult = await CompleteAndReturnAsync(
                    ProviderOutcomeKind.Transient,
                    "The Claim succeeded but no Operation Slot was recorded.",
                    leaseId, credentialStableId, credentialRevision,
                    persisted: 0, deduplicated: 0,
                    cancellationToken);
                completed = true;
                return slotResult;
            }

            // Commit-before-decrypt: mint the single-use grant only after the Claim
            // transaction committed, then decrypt exactly one credential.
            CurrentOperationCredentialMaterial material;
            try
            {
                var grant = materialAccess.GrantAfterCommit(
                    new CredentialOperationReference(
                        credentialStableId,
                        instance.ProviderKind,
                        operation.ProviderInstanceStableId,
                        credentialRevision,
                        leaseId),
                    requestId);
                material = await materialAccess.DecryptGrantedAsync(grant, cancellationToken);
            }
            catch (Exception error) when (
                error is CredentialProtectionException or InvalidOperationException)
            {
                // Decrypt outside a selecting transaction; complete Transient so the
                // credential keeps its enabled state for diagnosis (quarantine in Task 15).
                var decryptResult = await CompleteAndReturnAsync(
                    ProviderOutcomeKind.Transient,
                    "Credential decryption failed.",
                    leaseId, credentialStableId, credentialRevision,
                    persisted: 0, deduplicated: 0,
                    cancellationToken);
                completed = true;
                return decryptResult;
            }

            using (material)
            {
                var credential = new CredentialMaterial(material.Value);
                var slotContext = new ProviderOperationContext(
                    validatedInstance,
                    operationBounds,
                    Continuation: null,
                    ContinuationAdapterVersion: null,
                    SlotId: slotId,
                    LeaseId: leaseId,
                    Credential: credential);

                var translated = await adapter.TranslateQueryAsync(slotContext, snapshot, cancellationToken);
                var searchContext = slotContext with { SearchQuery = translated.Query };

                var collected = new List<ProviderResultInput>();
                string? continuation = null;
                overallOutcome = ProviderOutcomeKind.Success;

                await foreach (var page in adapter.SearchAsync(searchContext, cancellationToken))
                {
                    if (page.Outcome != ProviderOutcomeKind.Success && overallOutcome == ProviderOutcomeKind.Success)
                    {
                        overallOutcome = page.Outcome;
                    }

                    if (page.Value is not null)
                    {
                        foreach (var input in page.Value.Results.OfType<ProviderResultInput>())
                        {
                            collected.Add(input with
                            {
                                SearchQueryId = operation.SearchQueryId,
                                WorkItemId = work.WorkItemId,
                                WorkPartitionId = work.WorkPartitionId
                            });
                        }
                    }

                    // Checkpoint-after-persistence ordering (AC-7.15+): persist this
                    // page first, then advance the Last Safe Checkpoint.
                    if (collected.Count > 0)
                    {
                        var persistence = await resultPersistence.PersistResultsAsync(collected, cancellationToken);
                        collected.Clear();
                        persistedTotal += persistence.Persisted;
                        deduplicatedTotal += persistence.Deduplicated;

                        var safeCheckpoint = page.Continuation ?? continuation;
                        if (safeCheckpoint is not null)
                        {
                            await workService.WriteCheckpointAsync(
                                work.PartitionStableId,
                                page.Continuation ?? safeCheckpoint,
                                adapter.AdapterVersion,
                                safeCheckpoint,
                                cancellationToken);
                        }
                    }

                    continuation = page.Continuation;
                    if (continuation is null)
                    {
                        break;
                    }
                }

                // Best-effort content enrichment under the same Claim/Slot: only URLs
                // bound to the approved origin/path are fetched (generic endpoint-policy
                // gate, no provider branch); content failures never fail the operation.
                var (fetched, failed) = await FetchBoundContentAsync(
                    adapter, validatedInstance, operationBounds,
                    slotId, leaseId, credential, operationStartedUtc,
                    cancellationToken);

                var terminal = overallOutcome == ProviderOutcomeKind.Success && continuation is null;
                if (terminal)
                {
                    await workService.TerminatePartitionAsync(
                        work.PartitionStableId, isComplete: true, cancellationToken);
                }

                completed = true;
                CompletionResult completion;
                try
                {
                    completion = await scheduler.CompleteAsync(
                        leaseId, credentialStableId, credentialRevision, overallOutcome, cancellationToken);
                }
                catch (Exception error)
                {
                    logger?.LogWarning(
                        error,
                        "Master operation completion failed for lease {LeaseId}",
                        leaseId);
                    completion = new CompletionResult(false, error.Message);
                }

                return new MasterOperationResult(
                    MigratedPathTaken: true,
                    Succeeded: overallOutcome == ProviderOutcomeKind.Success && completion.Succeeded,
                    Outcome: overallOutcome,
                    PersistedResults: persistedTotal,
                    DeduplicatedResults: deduplicatedTotal,
                    ContentFetched: fetched,
                    ContentFailed: failed,
                    FailureReason: completion.Succeeded
                        ? null
                        : $"Completion reported: {completion.FailureReason}",
                    Continuation: continuation,
                    LeaseId: leaseId);
            }
        }
        finally
        {
            if (!completed)
            {
                try
                {
                    await scheduler.CompleteAsync(
                        leaseId, credentialStableId, credentialRevision, overallOutcome, cancellationToken);
                }
                catch (Exception error)
                {
                    logger?.LogWarning(
                        error,
                        "Master operation cleanup completion failed for lease {LeaseId}",
                        leaseId);
                }
            }
        }
    }

    private async Task<MasterOperationResult> CompleteAndReturnAsync(
        ProviderOutcomeKind outcome,
        string reason,
        Guid leaseId,
        Guid credentialStableId,
        long credentialRevision,
        int persisted,
        int deduplicated,
        CancellationToken cancellationToken)
    {
        try
        {
            await scheduler.CompleteAsync(
                leaseId, credentialStableId, credentialRevision, outcome, cancellationToken);
        }
        catch (Exception error)
        {
            logger?.LogWarning(
                error,
                "Master operation completion failed for lease {LeaseId}",
                leaseId);
        }

        return new MasterOperationResult(
            MigratedPathTaken: true,
            Succeeded: false,
            Outcome: outcome,
            PersistedResults: persisted,
            DeduplicatedResults: deduplicated,
            ContentFetched: 0,
            ContentFailed: 0,
            FailureReason: reason,
            LeaseId: leaseId);
    }

    private static MasterOperationResult ExecutedFailure(
        ProviderOutcomeKind outcome,
        string reason,
        TimeSpan? retryAfter = null) => new(
        MigratedPathTaken: true,
        Succeeded: false,
        Outcome: outcome,
        PersistedResults: 0,
        DeduplicatedResults: 0,
        ContentFetched: 0,
        ContentFailed: 0,
        FailureReason: reason,
        RetryAfter: retryAfter);

    /// <summary>Maps the Scheduler Principal to the persisted (scope, telegram) identity.</summary>
    internal static (CredentialGrantScope Scope, long? TelegramId) MapPrincipal(SchedulerPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (principal.IsSystem)
        {
            return (CredentialGrantScope.Admin, null);
        }
        return principal.IsAdministrator
            ? (CredentialGrantScope.Admin, principal.TelegramPrincipalId)
            : (CredentialGrantScope.User, principal.TelegramPrincipalId);
    }

    private Task<(ValidatedProviderInstance Instance, ProviderOperationBounds Bounds)> ValidateInstanceForOperationAsync(
        SearchProviderInstance instance,
        CancellationToken cancellationToken) =>
        ProviderInstanceOperationValidation.ValidateForOperationAsync(
            instance,
            endpoint,
            bounds,
            EndpointOperationKind.Search,
            cancellationToken);

    private async Task<(int Fetched, int Failed)> FetchBoundContentAsync(
        ISearchProviderAdapter adapter,
        ValidatedProviderInstance validatedInstance,
        ProviderOperationBounds operationBounds,
        Guid? slotId,
        Guid? leaseId,
        CredentialMaterial credential,
        DateTime operationStartedUtc,
        CancellationToken cancellationToken)
    {
        // Content inputs are the normalized results persisted by this operation.
        var inputs = await dbContext.ResultDeduplicationRecords
            .AsNoTracking()
            .Where(record =>
                record.ProviderInstanceStableId == validatedInstance.StableId.ToString("D") &&
                record.FirstDiscoveredUtc >= operationStartedUtc)
            .OrderByDescending(record => record.FirstDiscoveredUtc)
            .Take(MaxContentFetchPerOperation)
            .Join(dbContext.NormalizedResults.AsNoTracking(),
                record => record.NormalizedResultId,
                result => result.Id,
                (record, result) => result.ProvenanceUrl)
            .ToListAsync(cancellationToken);

        if (inputs.Count == 0)
        {
            return (0, 0);
        }

        var approvedOrigin = BuildApprovedOrigin(validatedInstance);
        if (approvedOrigin is null)
        {
            return (0, 0);
        }

        var fetched = 0;
        var failed = 0;
        foreach (var provenanceUrl in inputs)
        {
            if (string.IsNullOrWhiteSpace(provenanceUrl) ||
                !Uri.TryCreate(provenanceUrl, UriKind.Absolute, out var target))
            {
                failed++;
                continue;
            }

            var binding = EndpointPolicy.ValidateRequestBinding(
                approvedOrigin,
                validatedInstance.BasePath,
                target);
            if (!binding.Allowed || !binding.MayAttachCredential)
            {
                continue;
            }

            try
            {
                var context = new ProviderOperationContext(
                    validatedInstance,
                    operationBounds,
                    Continuation: null,
                    ContinuationAdapterVersion: null,
                    SlotId: slotId,
                    LeaseId: leaseId,
                    Credential: credential,
                    ContentApiUrl: provenanceUrl);
                var content = await adapter.FetchContentAsync(context, cancellationToken);
                if (content.Outcome == ProviderOutcomeKind.Success && content.Value is not null)
                {
                    fetched++;
                }
                else
                {
                    failed++;
                }
            }
            catch
            {
                failed++;
            }
        }

        return (fetched, failed);
    }

    private static Uri? BuildApprovedOrigin(ValidatedProviderInstance instance)
    {
        var host = instance.Host.Contains(':') && !instance.Host.StartsWith('[')
            ? $"[{instance.Host}]"
            : instance.Host;
        return Uri.TryCreate(
            $"{instance.Scheme}://{host}:{instance.Port}",
            UriKind.Absolute,
            out var origin)
            ? origin
            : null;
    }

    private static bool ReadStrictBoolean(IConfiguration configuration, string key) =>
        bool.TryParse(configuration[key], out var enabled) && enabled;
}

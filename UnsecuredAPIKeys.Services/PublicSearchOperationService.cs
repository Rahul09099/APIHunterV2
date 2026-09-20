using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using UnsecuredAPIKeys.Data;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Data.Models;
using UnsecuredAPIKeys.Providers;
using UnsecuredAPIKeys.Providers._Interfaces;

namespace UnsecuredAPIKeys.Services;

/// <summary>One credential-free public search operation.</summary>
public sealed record PublicOperationQuery(
    Guid ProviderInstanceStableId,
    SearchProviderEnum ProviderKind,
    string GenericQuery,
    string? NativeOverride = null,
    string SettingsJson = "{}",
    int? SearchQueryId = null,
    string PartitionKey = "default");

/// <summary>Outcome of one public operation. No credential is ever involved.</summary>
public sealed record PublicOperationResult(
    bool Succeeded,
    ProviderOutcomeKind Outcome,
    int PersistedResults,
    int DeduplicatedResults,
    string? FailureReason = null,
    string? Continuation = null,
    Guid? SlotId = null);

/// <summary>
/// Credential-free public search operation (Wave 14, Task 14.2). Mirrors the durable
/// Master chain — Work/query → Slot → adapter search → result/provenance/checkpoint →
/// Slot completion — without selecting, referencing, or decrypting any credential.
/// The dual gate (effective <c>GlobalPublicSearch</c> plus active exact-instance consent)
/// is enforced at Slot issuance inside <see cref="PublicOperationSlotService"/>.
/// </summary>
public sealed class PublicSearchOperationService(
    DBContext dbContext,
    SearchProviderAdapterRegistry adapterRegistry,
    IWorkService workService,
    PublicOperationSlotService slotService,
    ResultPersistenceService resultPersistence,
    EndpointPolicy? endpointPolicy = null,
    EndpointPolicyOptions? endpointPolicyOptions = null,
    ILogger<PublicSearchOperationService>? logger = null)
{
    private readonly EndpointPolicy endpoint = endpointPolicy ?? new EndpointPolicy();
    private readonly EndpointPolicyOptions bounds = endpointPolicyOptions ?? new EndpointPolicyOptions();

    public async Task<PublicOperationResult> ExecuteAsync(
        SchedulerPrincipal principal,
        Guid requestId,
        PublicOperationQuery operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(operation);

        if (requestId == Guid.Empty)
            throw new ArgumentException("Request ID must be non-empty.", nameof(requestId));
        if (operation.ProviderInstanceStableId == Guid.Empty ||
            operation.ProviderKind == SearchProviderEnum.Unknown ||
            string.IsNullOrWhiteSpace(operation.GenericQuery))
        {
            return Failure(
                ProviderOutcomeKind.RequestInvalid,
                "A public operation requires a known Provider Kind, instance, and query.");
        }

        var instance = await dbContext.SearchProviderInstances
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.StableId == operation.ProviderInstanceStableId,
                cancellationToken);
        if (instance is null || !instance.IsEnabled)
        {
            return Failure(ProviderOutcomeKind.RequestInvalid, "The provider instance is unknown or disabled.");
        }
        if (instance.ProviderKind != operation.ProviderKind)
        {
            return Failure(
                ProviderOutcomeKind.RequestInvalid,
                "The requested Provider Kind does not match the authoritative instance kind.");
        }

        var (scope, telegramId) = MasterSearchOperationService.MapPrincipal(principal);
        var snapshot = new SearchQuerySnapshot(
            operation.GenericQuery, operation.NativeOverride, operation.SettingsJson);

        ISearchProviderAdapter adapter;
        try
        {
            adapter = adapterRegistry.GetRequiredAdapter(operation.ProviderKind);
            AdapterKindGuard.RequireMatchingKind(adapter.ProviderKind, instance.ProviderKind);
        }
        catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException)
        {
            return Failure(ProviderOutcomeKind.RequestInvalid, error.Message);
        }

        WorkItemResult work;
        try
        {
            work = await workService.CreateWorkItemAsync(
                instance.Id, instance.ProviderKind, scope, telegramId,
                snapshot, adapter.AdapterVersion, cancellationToken);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            return Failure(ProviderOutcomeKind.RequestInvalid, error.Message);
        }

        var acquisition = await slotService.TryAcquireAsync(
            operation.ProviderInstanceStableId,
            work.WorkItemStableId,
            operation.PartitionKey,
            requestId,
            scope,
            telegramId,
            cancellationToken);
        if (!acquisition.Succeeded ||
            acquisition.SlotId == Guid.Empty ||
            acquisition.ValidatedInstance is null ||
            acquisition.Bounds is null)
        {
            return Failure(
                ProviderOutcomeKind.Transient,
                acquisition.FailureReason ?? "No public operation slot is available.");
        }

        var slotId = acquisition.SlotId;
        var completed = false;
        var overallOutcome = ProviderOutcomeKind.Transient;
        var persistedTotal = 0;
        var deduplicatedTotal = 0;
        string? continuation = null;

        try
        {
            var slotContext = new ProviderOperationContext(
                acquisition.ValidatedInstance,
                acquisition.Bounds,
                Continuation: null,
                ContinuationAdapterVersion: null,
                SlotId: slotId,
                LeaseId: null,
                Credential: null);

            var translated = await adapter.TranslateQueryAsync(slotContext, snapshot, cancellationToken);
            var searchContext = slotContext with { SearchQuery = translated.Query };

            var collected = new List<ProviderResultInput>();
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

            if (overallOutcome == ProviderOutcomeKind.Success && continuation is null)
            {
                await workService.TerminatePartitionAsync(
                    work.PartitionStableId, isComplete: true, cancellationToken);
            }

            completed = true;
            var completion = await slotService.CompleteAsync(
                slotId, scope, telegramId, cancellationToken);

            return new PublicOperationResult(
                overallOutcome == ProviderOutcomeKind.Success && completion.Succeeded,
                overallOutcome,
                persistedTotal,
                deduplicatedTotal,
                completion.Succeeded ? null : $"Slot completion reported: {completion.FailureReason}",
                continuation,
                slotId);
        }
        finally
        {
            if (!completed)
            {
                try
                {
                    await slotService.CompleteAsync(slotId, scope, telegramId, CancellationToken.None);
                }
                catch (Exception error)
                {
                    logger?.LogWarning(
                        error, "Public operation slot cleanup failed for slot {SlotId}", slotId);
                }
            }
        }
    }

    private static PublicOperationResult Failure(ProviderOutcomeKind outcome, string reason) => new(
        false, outcome, 0, 0, reason);
}

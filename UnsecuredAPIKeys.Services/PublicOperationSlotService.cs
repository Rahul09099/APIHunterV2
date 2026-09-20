using Microsoft.EntityFrameworkCore;
using UnsecuredAPIKeys.Data;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Data.Models;
using UnsecuredAPIKeys.Providers;
using UnsecuredAPIKeys.Providers._Interfaces;

namespace UnsecuredAPIKeys.Services;

/// <summary>Outcome of a public Operation Slot acquisition attempt.</summary>
public sealed record PublicSlotAcquisition(
    bool Succeeded,
    Guid SlotId,
    bool IsReplay,
    ValidatedProviderInstance? ValidatedInstance,
    ProviderOperationBounds? Bounds,
    string? FailureReason = null,
    TimeSpan? RetryAfter = null);

/// <summary>Outcome of a public Slot renewal.</summary>
public sealed record PublicSlotRenewal(
    bool Succeeded,
    DateTime? NewExpiresUtc,
    string? FailureReason = null);

/// <summary>Outcome of a public Slot completion.</summary>
public sealed record PublicSlotCompletion(
    bool Succeeded,
    string? FailureReason = null);

/// <summary>
/// Credential-free Operation Slot scheduling for approved Public Operations (Wave 14,
/// Task 14.2). The dual gate — effective <c>GlobalPublicSearch</c> capability plus an
/// active exact-instance consent record, with the instance projection deny-oriented —
/// is enforced here where the Slot is issued, so no caller can bypass it. Slots share
/// instance capacity with credentialed Leases, replay exactly per Request ID, and never
/// select, reference, or decrypt a credential.
/// </summary>
public sealed class PublicOperationSlotService(
    DBContext dbContext,
    IPublicSearchConsentService consentService,
    SearchProviderAdapterRegistry adapterRegistry,
    IDatabaseUtcClock clock,
    EndpointPolicy? endpointPolicy = null,
    EndpointPolicyOptions? endpointPolicyOptions = null)
{
    public static readonly TimeSpan SlotLifetime = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan MaxRenewalExtension = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan CapacityRetryAfter = TimeSpan.FromMinutes(1);

    private readonly EndpointPolicy endpoint = endpointPolicy ?? new EndpointPolicy();
    private readonly EndpointPolicyOptions bounds = endpointPolicyOptions ?? new EndpointPolicyOptions();

    /// <summary>
    /// Pure dual-gate decision used by property tests: a Public Operation is allowed
    /// only when the instance projection, effective capability, and exact-instance
    /// consent all hold. No single input is proof by itself.
    /// </summary>
    public static bool EvaluateGate(
        bool instanceAllowGlobalPublicSearch,
        bool consentActive,
        SearchProviderCapability declaredCapabilities,
        SearchProviderCapability discoveredCapabilities,
        bool discoverySucceeded)
    {
        if (!instanceAllowGlobalPublicSearch || !consentActive)
        {
            return false;
        }

        var effective = SearchProviderCapabilityService.CalculateEffectiveCapabilities(
            declaredCapabilities, discoveredCapabilities, discoverySucceeded);
        return effective.HasFlag(SearchProviderCapability.GlobalPublicSearch);
    }

    public async Task<PublicSlotAcquisition> TryAcquireAsync(
        Guid providerInstanceStableId,
        Guid workItemStableId,
        string partitionKey,
        Guid requestId,
        CredentialGrantScope principalScope,
        long? principalTelegramId,
        CancellationToken cancellationToken = default)
    {
        if (providerInstanceStableId == Guid.Empty ||
            workItemStableId == Guid.Empty ||
            requestId == Guid.Empty)
        {
            throw new ArgumentException("Public slot acquisition requires instance, work, and request identifiers.");
        }
        if (string.IsNullOrWhiteSpace(partitionKey) || partitionKey.Length > 256)
        {
            throw new ArgumentException("A bounded partition key is required.", nameof(partitionKey));
        }

        var instance = await dbContext.SearchProviderInstances
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.StableId == providerInstanceStableId,
                cancellationToken);
        if (instance is null || !instance.IsEnabled)
        {
            return Fail("The provider instance is unknown or disabled.");
        }

        var workItemId = await dbContext.WorkItems
            .AsNoTracking()
            .Where(work => work.StableId == workItemStableId)
            .Select(work => (long?)work.Id)
            .SingleOrDefaultAsync(cancellationToken);
        if (workItemId is null)
        {
            return Fail("The work item is unknown.");
        }

        // Deny-oriented projection first: an explicit false denies without further work.
        if (!instance.AllowGlobalPublicSearch)
        {
            return Fail("Global public search is not enabled for this provider instance.");
        }

        if (!await consentService.IsConsentedAsync(providerInstanceStableId, cancellationToken))
        {
            return Fail("No active public-search consent exists for this provider instance.");
        }

        ValidatedProviderInstance validatedInstance;
        ProviderOperationBounds operationBounds;
        try
        {
            (validatedInstance, operationBounds) =
                await ProviderInstanceOperationValidation.ValidateForOperationAsync(
                    instance, endpoint, bounds, EndpointOperationKind.Search, cancellationToken);
        }
        catch (EndpointPolicyException error)
        {
            return Fail(error.Message);
        }

        ISearchProviderAdapter adapter;
        try
        {
            adapter = adapterRegistry.GetRequiredAdapter(instance.ProviderKind);
            AdapterKindGuard.RequireMatchingKind(adapter.ProviderKind, instance.ProviderKind);
        }
        catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException)
        {
            return Fail(error.Message);
        }

        var discovered = await adapter.DiscoverCapabilitiesAsync(validatedInstance, cancellationToken);
        if (!EvaluateGate(
                instance.AllowGlobalPublicSearch,
                consentActive: true,
                adapter.DeclaredCapabilities,
                discovered.DiscoveredCapabilities,
                discoverySucceeded: discovered.Outcome == ProviderOutcomeKind.Success))
        {
            return Fail("The provider instance has no effective GlobalPublicSearch capability.");
        }

        var now = await clock.GetUtcNowAsync(cancellationToken);
        var executionStrategy = dbContext.Database.CreateExecutionStrategy();
        return await executionStrategy.ExecuteAsync(async () =>
        {
            await using var tx = await dbContext.Database.BeginTransactionAsync(
                System.Data.IsolationLevel.Serializable, cancellationToken);

            var replay = await dbContext.OperationSlots
                .Where(slot =>
                    slot.ProviderInstanceId == instance.Id &&
                    slot.PrincipalScope == principalScope &&
                    slot.PrincipalTelegramId == principalTelegramId &&
                    slot.RequestId == requestId &&
                    !slot.IsTerminal &&
                    slot.ExpiresUtc > now)
                .OrderByDescending(slot => slot.Id)
                .SingleOrDefaultAsync(cancellationToken);
            if (replay is not null)
            {
                await tx.CommitAsync(cancellationToken);
                return new PublicSlotAcquisition(
                    true, replay.SlotId, IsReplay: true, validatedInstance, operationBounds);
            }

            var activeSlots = await dbContext.OperationSlots
                .Where(slot =>
                    slot.ProviderInstanceId == instance.Id &&
                    !slot.IsTerminal &&
                    slot.ExpiresUtc > now)
                .CountAsync(cancellationToken);
            if (activeSlots >= instance.MaxConcurrentOperations)
            {
                await tx.RollbackAsync(cancellationToken);
                return new PublicSlotAcquisition(
                    false, Guid.Empty, IsReplay: false, null, null,
                    "The provider instance has reached its maximum concurrent operation limit.",
                    CapacityRetryAfter);
            }

            var slot = new OperationSlot
            {
                SlotId = Guid.NewGuid(),
                ProviderInstanceId = instance.Id,
                RequestId = requestId,
                PrincipalScope = principalScope,
                PrincipalTelegramId = principalTelegramId,
                WorkItemId = workItemId.Value,
                PartitionKey = partitionKey,
                AcquiredUtc = now,
                ExpiresUtc = now.Add(SlotLifetime),
                Revision = 0,
                CreatedUtc = now,
                UpdatedUtc = now
            };
            dbContext.OperationSlots.Add(slot);

            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                await tx.RollbackAsync(cancellationToken);
                return new PublicSlotAcquisition(
                    false, Guid.Empty, IsReplay: false, null, null,
                    "A conflicting slot was recorded concurrently; retry with the same request.");
            }

            await tx.CommitAsync(cancellationToken);
            return new PublicSlotAcquisition(
                true, slot.SlotId, IsReplay: false, validatedInstance, operationBounds);
        });
    }

    public async Task<PublicSlotRenewal> TryRenewAsync(
        Guid slotId,
        CredentialGrantScope principalScope,
        long? principalTelegramId,
        TimeSpan requestedExtension,
        CancellationToken cancellationToken = default)
    {
        if (slotId == Guid.Empty)
        {
            throw new ArgumentException("Slot ID must be non-empty.", nameof(slotId));
        }
        if (requestedExtension <= TimeSpan.Zero || requestedExtension > MaxRenewalExtension)
        {
            return new PublicSlotRenewal(false, null, "The requested extension is out of bounds.");
        }

        var executionStrategy = dbContext.Database.CreateExecutionStrategy();
        return await executionStrategy.ExecuteAsync(async () =>
        {
            await using var tx = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            var now = await clock.GetUtcNowAsync(cancellationToken);

            var slot = await dbContext.OperationSlots
                .Where(candidate =>
                    candidate.SlotId == slotId &&
                    candidate.PrincipalScope == principalScope &&
                    candidate.PrincipalTelegramId == principalTelegramId &&
                    !candidate.IsTerminal)
                .SingleOrDefaultAsync(cancellationToken);
            if (slot is null)
            {
                return new PublicSlotRenewal(false, null, "The slot is unknown, terminal, or foreign.");
            }

            slot.ExpiresUtc = now.Add(requestedExtension);
            slot.Revision++;
            slot.UpdatedUtc = now;
            await dbContext.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return new PublicSlotRenewal(true, slot.ExpiresUtc, null);
        });
    }

    public async Task<PublicSlotCompletion> CompleteAsync(
        Guid slotId,
        CredentialGrantScope principalScope,
        long? principalTelegramId,
        CancellationToken cancellationToken = default)
    {
        if (slotId == Guid.Empty)
        {
            throw new ArgumentException("Slot ID must be non-empty.", nameof(slotId));
        }

        var slot = await dbContext.OperationSlots
            .Where(candidate =>
                candidate.SlotId == slotId &&
                candidate.PrincipalScope == principalScope &&
                candidate.PrincipalTelegramId == principalTelegramId)
            .SingleOrDefaultAsync(cancellationToken);
        if (slot is null)
        {
            return new PublicSlotCompletion(false, "The slot is unknown or foreign.");
        }
        if (slot.IsTerminal)
        {
            return new PublicSlotCompletion(true, null);
        }

        slot.IsTerminal = true;
        slot.TerminalizedUtc = await clock.GetUtcNowAsync(cancellationToken);
        slot.Revision++;
        slot.UpdatedUtc = slot.TerminalizedUtc.Value;
        await dbContext.SaveChangesAsync(cancellationToken);
        return new PublicSlotCompletion(true, null);
    }

    private static PublicSlotAcquisition Fail(string reason, TimeSpan? retryAfter = null) => new(
        false, Guid.Empty, IsReplay: false, null, null, reason, retryAfter);
}

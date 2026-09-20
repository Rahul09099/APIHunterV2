using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using UnsecuredAPIKeys.Data;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Data.Models;
using UnsecuredAPIKeys.Providers._Interfaces;

namespace UnsecuredAPIKeys.Services;

/// <summary>
/// Configures the Lease lifecycle policy. All time values are injected so tests can
/// control the policy without slow real-time waits.
/// </summary>
public sealed record LeasePolicyOptions
{
    /// <summary>Default Lease duration requested on initial Claim.</summary>
    public TimeSpan DefaultLeaseDuration { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Maximum single Lease duration (capped per renewal).</summary>
    public TimeSpan MaxSingleLeaseDuration { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Per-renewal cap: the maximum cumulative Lease time for a single continuous operation
    /// before the Scheduler considers the credential stuck and terminates the Claim.
    /// Addresses AC-5.63.
    /// </summary>
    public TimeSpan PerRenewalCap { get; init; } = TimeSpan.FromHours(2);

    /// <summary>
    /// Continuous operation cap: the maximum total claimed time across all Claims
    /// for a single credential before a forced rest period is applied.
    /// </summary>
    public TimeSpan ContinuousOperationCap { get; init; } = TimeSpan.FromHours(8);

    /// <summary>
    /// Fraction of the Lease duration at which the Worker should begin renewing.
    /// e.g., 0.7 means renew when 70% of the Lease time has elapsed.
    /// </summary>
    public double RenewalThresholdFraction { get; init; } = 0.7;

    /// <summary>
    /// How long to back off a credential after a ForbiddenScope outcome.
    /// This is a policy configuration value — never a feature flag (per AC-6.16).
    /// </summary>
    public TimeSpan ForbiddenScopeCooldown { get; init; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Base duration for transient failure backoff (exponentially scaled by failure count).
    /// </summary>
    public TimeSpan TransientBackoffBase { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Maximum transient backoff duration.</summary>
    public TimeSpan MaxTransientBackoff { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>Consecutive transient failures before a credential is disabled.</summary>
    public int MaxConsecutiveTransientFailures { get; init; } = 5;

    /// <summary>How long a RateLimited credential must cool down before being re-eligible.</summary>
    public TimeSpan RateLimitCooldown { get; init; } = TimeSpan.FromMinutes(15);
}

/// <summary>
/// Injectable jitter source for scheduler timing. The production implementation
/// uses <see cref="System.Random"/> seeded from <see cref="System.Security.Cryptography.RandomNumberGenerator"/>.
/// Tests inject a deterministic implementation. Addresses AC-6.19.
/// </summary>
public interface ISchedulerJitterSource
{
    /// <summary>Returns a jitter duration in the range [0, maxJitter].</summary>
    TimeSpan Next(TimeSpan maxJitter);
}

public sealed class CryptographicSchedulerJitterSource : ISchedulerJitterSource
{
    public TimeSpan Next(TimeSpan maxJitter)
    {
        if (maxJitter <= TimeSpan.Zero) return TimeSpan.Zero;
        var fraction = System.Security.Cryptography.RandomNumberGenerator.GetInt32(1000) / 1000.0;
        return TimeSpan.FromTicks((long)(maxJitter.Ticks * fraction));
    }
}

/// <summary>
/// Result of a successful Claim operation.
/// </summary>
public sealed record ClaimResult(
    Guid LeaseId,
    Guid CredentialStableId,
    Guid ProviderInstanceStableId,
    DateTime LeaseExpiresUtc,
    long CredentialRevision,
    TimeSpan RenewalThreshold);

/// <summary>
/// Outcome of a Claim attempt when no credential is available.
/// </summary>
public sealed record ClaimUnavailableResult(
    string Reason,
    TimeSpan? RetryAfter = null);

/// <summary>
/// Discriminated union result of a Claim attempt.
/// </summary>
public sealed class ClaimOutcome
{
    private ClaimOutcome() { }

    public ClaimResult? Success { get; private init; }
    public ClaimUnavailableResult? Unavailable { get; private init; }
    public bool IsSuccess => Success is not null;

    public static ClaimOutcome Succeeded(ClaimResult result) => new() { Success = result };
    public static ClaimOutcome NotAvailable(string reason, TimeSpan? retryAfter = null) =>
        new() { Unavailable = new ClaimUnavailableResult(reason, retryAfter) };
}

/// <summary>
/// Result of a Lease renewal.
/// </summary>
public sealed record RenewalResult(
    bool Succeeded,
    DateTime? NewLeaseExpiresUtc,
    string? FailureReason = null);

/// <summary>
/// Result of completing a Lease (with outcome reporting).
/// </summary>
public sealed record CompletionResult(
    bool Succeeded,
    string? FailureReason = null);

public interface ICredentialScheduler
{
    /// <summary>
    /// Attempts to acquire an exclusive Lease on an eligible credential for the given
    /// Provider Instance and Work Item. Uses BEGIN IMMEDIATE (SQLite) or SELECT FOR UPDATE
    /// SKIP LOCKED (PostgreSQL) to ensure only one claimer wins per credential per request.
    /// </summary>
    Task<ClaimOutcome> TryClaimAsync(
        Guid providerInstanceStableId,
        Guid workItemStableId,
        string partitionKey,
        string nodeId,
        Guid requestId,
        CredentialGrantScope principalScope,
        long? principalTelegramId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Renews an active Lease by extending its expiry. Validates:
    /// - Lease ID matches the caller's context
    /// - Credential Revision has not changed
    /// - Per-renewal cap (AC-5.63) is not exceeded
    /// </summary>
    Task<RenewalResult> TryRenewAsync(
        Guid leaseId,
        Guid credentialStableId,
        long expectedRevision,
        TimeSpan requestedExtension,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Completes a Lease with a terminal outcome. Applies health state transitions
    /// (cooldown, transient failure increment, disable) based on the outcome kind.
    /// </summary>
    Task<CompletionResult> CompleteAsync(
        Guid leaseId,
        Guid credentialStableId,
        long expectedRevision,
        ProviderOutcomeKind outcomeKind,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// SQLite-optimized credential scheduler. Uses a process-local gate to prevent
/// concurrent claim attempts from the same process, then uses BEGIN IMMEDIATE to
/// serialize at the database level. Addresses Tasks 8.2–8.4.
/// </summary>
public sealed class SqliteCredentialScheduler(
    DBContext dbContext,
    LeasePolicyOptions policy,
    ISchedulerJitterSource jitterSource,
    IDatabaseUtcClock clock,
    ILogger<SqliteCredentialScheduler>? logger = null,
    SearchPlatformMetrics? metrics = null)
    : ICredentialScheduler
{
    // Process-local gate: prevents concurrent SQLite write contention within one process.
    private static readonly SemaphoreSlim ProcessGate = new(1, 1);

    public async Task<ClaimOutcome> TryClaimAsync(
        Guid providerInstanceStableId,
        Guid workItemStableId,
        string partitionKey,
        string nodeId,
        Guid requestId,
        CredentialGrantScope principalScope,
        long? principalTelegramId,
        CancellationToken cancellationToken = default)
    {
        if (providerInstanceStableId == Guid.Empty)
            throw new ArgumentException("Provider Instance Stable ID must be non-empty.", nameof(providerInstanceStableId));
        if (workItemStableId == Guid.Empty)
            throw new ArgumentException("Work Item Stable ID must be non-empty.", nameof(workItemStableId));
        if (requestId == Guid.Empty)
            throw new ArgumentException("Request ID must be non-empty.", nameof(requestId));
        if (string.IsNullOrWhiteSpace(nodeId))
            throw new ArgumentException("Node ID must be non-empty.", nameof(nodeId));
        if (string.IsNullOrWhiteSpace(partitionKey))
            throw new ArgumentException("Partition key must be non-empty.", nameof(partitionKey));

        await ProcessGate.WaitAsync(cancellationToken);
        try
        {
            return await TryClaimInternalAsync(
                providerInstanceStableId, workItemStableId, partitionKey,
                nodeId, requestId, principalScope, principalTelegramId, cancellationToken);
        }
        finally
        {
            ProcessGate.Release();
        }
    }

    private async Task<ClaimOutcome> TryClaimInternalAsync(
        Guid providerInstanceStableId,
        Guid workItemStableId,
        string partitionKey,
        string nodeId,
        Guid requestId,
        CredentialGrantScope principalScope,
        long? principalTelegramId,
        CancellationToken cancellationToken)
    {
        var waitTimer = Stopwatch.StartNew();
        var executionStrategy = dbContext.Database.CreateExecutionStrategy();
        return await executionStrategy.ExecuteAsync(async () =>
        {
            await using var tx = await dbContext.Database.BeginTransactionAsync(
                System.Data.IsolationLevel.Serializable, cancellationToken);

            var now = await clock.GetUtcNowAsync(cancellationToken);

            void RecordClaim(SearchProviderEnum kind, Guid instanceStableId, ClaimOutcomeLabel outcome)
            {
                metrics?.RecordClaim(kind, instanceStableId, outcome);
                metrics?.ObserveClaimWait(kind, instanceStableId, waitTimer.Elapsed);
            }

            // Idempotency check: if an active claim already exists for (principalScope, principalTelegramId, requestId),
            // replay the winning record without allocating another lease or consuming instance slot capacity (AC-13.46, AC-17.29).
            var existingClaim = await dbContext.CredentialClaimRecords
                .Where(c => c.PrincipalScope == principalScope &&
                            c.PrincipalTelegramId == principalTelegramId &&
                            c.RequestId == requestId &&
                            !c.IsTerminal &&
                            c.LeaseExpiresUtc > now)
                .SingleOrDefaultAsync(cancellationToken);

            var renewalThreshold = TimeSpan.FromTicks(
                (long)(policy.DefaultLeaseDuration.Ticks * policy.RenewalThresholdFraction));

            if (existingClaim is not null)
            {
                var replayKind = await dbContext.SearchProviderInstances
                    .Where(i => i.StableId == existingClaim.ProviderInstanceStableId)
                    .Select(i => (SearchProviderEnum?)i.ProviderKind)
                    .SingleOrDefaultAsync(cancellationToken);
                if (replayKind.HasValue)
                {
                    RecordClaim(replayKind.Value, existingClaim.ProviderInstanceStableId, ClaimOutcomeLabel.Success);
                }

                return ClaimOutcome.Succeeded(new ClaimResult(
                    existingClaim.LeaseId,
                    existingClaim.CredentialStableId,
                    existingClaim.ProviderInstanceStableId,
                    existingClaim.LeaseExpiresUtc,
                    existingClaim.CredentialRevision,
                    renewalThreshold));
            }

            // Resolve Provider Instance
            var instance = await dbContext.SearchProviderInstances
                .Where(i => i.StableId == providerInstanceStableId && i.IsEnabled)
                .Select(i => new { i.Id, i.ProviderKind, i.MaxConcurrentOperations })
                .SingleOrDefaultAsync(cancellationToken);

            if (instance is null)
                return ClaimOutcome.NotAvailable("Provider Instance is not found or not enabled.");

            // Count active slots to check concurrency
            var activeSlots = await dbContext.OperationSlots
                .Where(s => s.ProviderInstanceId == instance.Id && !s.IsTerminal && s.ExpiresUtc > now)
                .CountAsync(cancellationToken);

            if (activeSlots >= instance.MaxConcurrentOperations)
            {
                RecordClaim(instance.ProviderKind, providerInstanceStableId, ClaimOutcomeLabel.Unavailable);
                return ClaimOutcome.NotAvailable(
                    "Provider Instance has reached its maximum concurrent operation limit.",
                    policy.DefaultLeaseDuration);
            }

            // Find an eligible credential (observed eligibility — not speculative)
            var candidate = await dbContext.SearchProviderTokens
                .Where(t =>
                    t.ProviderInstanceId == instance.Id &&
                    t.IsEnabled &&
                    !t.IsArchived &&
                    t.LeaseId == null &&
                    (t.CooldownUntilUtc == null || t.CooldownUntilUtc <= now))
                .OrderBy(t => t.LastClaimedUtc == null ? 0 : 1)
                .ThenBy(t => t.LastClaimedUtc)
                .ThenBy(t => t.StableId)
                .FirstOrDefaultAsync(cancellationToken);

            if (candidate is null)
            {
                RecordClaim(instance.ProviderKind, providerInstanceStableId, ClaimOutcomeLabel.Unavailable);
                var retryAfter = await ResolveEarliestCredentialRetryAsync(
                    dbContext, instance.Id, now, cancellationToken);
                return ClaimOutcome.NotAvailable("No eligible credential is available for this Provider Instance.", retryAfter);
            }

            // Revision-conditional update (optimistic write)
            var leaseId = Guid.NewGuid();
            var leaseExpires = now.Add(policy.DefaultLeaseDuration);
            var previousRevision = candidate.Revision;

            candidate.LeaseId = leaseId;
            candidate.LeaseOwnerNodeId = nodeId;
            candidate.LeaseRequestId = requestId;
            candidate.LeaseAcquiredUtc = now;
            candidate.LeaseExpiresUtc = leaseExpires;
            candidate.LastClaimedUtc = now;
            candidate.Revision = previousRevision + 1;
            candidate.UpdatedUtc = now < candidate.CreatedUtc ? candidate.CreatedUtc : now;

            // Resolve Work Item ID
            var workItemId = await dbContext.WorkItems
                .Where(w => w.StableId == workItemStableId)
                .Select(w => (long?)w.Id)
                .SingleOrDefaultAsync(cancellationToken)
                ?? throw new InvalidOperationException($"Work Item '{workItemStableId}' not found.");

            // Write Operation Slot
            var slot = new OperationSlot
            {
                SlotId = Guid.NewGuid(),
                ProviderInstanceId = instance.Id,
                RequestId = requestId,
                PrincipalScope = principalScope,
                PrincipalTelegramId = principalTelegramId,
                WorkItemId = workItemId,
                PartitionKey = partitionKey,
                AcquiredUtc = now,
                ExpiresUtc = leaseExpires,
                Revision = 0,
                CreatedUtc = now,
                UpdatedUtc = now
            };
            dbContext.OperationSlots.Add(slot);

            // Write Claim Record
            var claimRecord = new CredentialClaimRecord
            {
                PrincipalScope = principalScope,
                PrincipalTelegramId = principalTelegramId,
                RequestId = requestId,
                CredentialStableId = candidate.StableId,
                ProviderInstanceStableId = providerInstanceStableId,
                WorkItemId = workItemId,
                LeaseId = leaseId,
                LeaseOwnerNodeId = nodeId,
                LeaseAcquiredUtc = now,
                LeaseExpiresUtc = leaseExpires,
                CredentialRevision = candidate.Revision,
                CreatedUtc = now,
                UpdatedUtc = now
            };
            dbContext.CredentialClaimRecords.Add(claimRecord);

            await dbContext.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            logger?.LogDebug(
                "Claimed credential {StableId} for instance {InstanceId} with lease {LeaseId}",
                candidate.StableId, providerInstanceStableId, leaseId);

            RecordClaim(instance.ProviderKind, providerInstanceStableId, ClaimOutcomeLabel.Success);
            metrics?.LeaseAcquired(instance.ProviderKind, providerInstanceStableId);

            return ClaimOutcome.Succeeded(new ClaimResult(
                leaseId,
                candidate.StableId,
                providerInstanceStableId,
                leaseExpires,
                candidate.Revision,
                renewalThreshold));
        });
    }

    public async Task<RenewalResult> TryRenewAsync(
        Guid leaseId,
        Guid credentialStableId,
        long expectedRevision,
        TimeSpan requestedExtension,
        CancellationToken cancellationToken = default)
    {
        if (leaseId == Guid.Empty)
            throw new ArgumentException("Lease ID must be non-empty.", nameof(leaseId));
        if (credentialStableId == Guid.Empty)
            throw new ArgumentException("Credential Stable ID must be non-empty.", nameof(credentialStableId));

        var clampedExtension = requestedExtension > policy.MaxSingleLeaseDuration
            ? policy.MaxSingleLeaseDuration
            : requestedExtension < TimeSpan.Zero
                ? policy.DefaultLeaseDuration
                : requestedExtension;

        var executionStrategy = dbContext.Database.CreateExecutionStrategy();
        return await executionStrategy.ExecuteAsync(async () =>
        {
            await using var tx = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            var now = await clock.GetUtcNowAsync(cancellationToken);

            var credential = await dbContext.SearchProviderTokens
                .Where(t =>
                    t.StableId == credentialStableId &&
                    t.LeaseId == leaseId &&
                    t.Revision == expectedRevision &&
                    t.LeaseExpiresUtc > now)
                .SingleOrDefaultAsync(cancellationToken);

            if (credential is null)
                return new RenewalResult(false, null, "Lease not found, expired, or revision mismatch.");

            // Per-renewal cap check (AC-5.63)
            var acquiredAt = credential.LeaseAcquiredUtc!.Value;
            var totalElapsed = now - acquiredAt + clampedExtension;
            if (totalElapsed > policy.PerRenewalCap)
            {
                logger?.LogWarning(
                    "Credential {StableId} renewal rejected: per-renewal cap exceeded ({Elapsed} > {Cap})",
                    credentialStableId, totalElapsed, policy.PerRenewalCap);
                return new RenewalResult(false, null, "Per-renewal cap exceeded.");
            }

            var newExpiry = now.Add(clampedExtension);
            credential.LeaseExpiresUtc = newExpiry;
            credential.Revision++;
            credential.UpdatedUtc = now < credential.CreatedUtc ? credential.CreatedUtc : now;

            // Update the Claim Record
            var claimRecord = await dbContext.CredentialClaimRecords
                .Where(c => c.LeaseId == leaseId && !c.IsTerminal)
                .SingleOrDefaultAsync(cancellationToken);

            if (claimRecord is not null)
            {
                claimRecord.LeaseExpiresUtc = newExpiry;
                claimRecord.CredentialRevision = credential.Revision;
                claimRecord.UpdatedUtc = now;
            }

            // Update the Operation Slot expiry
            var slot = await dbContext.OperationSlots
                .Where(s => s.RequestId == credential.LeaseRequestId && !s.IsTerminal)
                .SingleOrDefaultAsync(cancellationToken);

            if (slot is not null)
            {
                slot.ExpiresUtc = newExpiry;
                slot.Revision++;
                slot.UpdatedUtc = now;
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            return new RenewalResult(true, newExpiry);
        });
    }

    public async Task<CompletionResult> CompleteAsync(
        Guid leaseId,
        Guid credentialStableId,
        long expectedRevision,
        ProviderOutcomeKind outcomeKind,
        CancellationToken cancellationToken = default)
    {
        if (leaseId == Guid.Empty)
            throw new ArgumentException("Lease ID must be non-empty.", nameof(leaseId));
        if (credentialStableId == Guid.Empty)
            throw new ArgumentException("Credential Stable ID must be non-empty.", nameof(credentialStableId));

        var executionStrategy = dbContext.Database.CreateExecutionStrategy();
        return await executionStrategy.ExecuteAsync(async () =>
        {
            await using var tx = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            var now = await clock.GetUtcNowAsync(cancellationToken);

            var credential = await dbContext.SearchProviderTokens
                .Where(t =>
                    t.StableId == credentialStableId &&
                    t.LeaseId == leaseId &&
                    t.Revision == expectedRevision)
                .SingleOrDefaultAsync(cancellationToken);

            if (credential is null)
                return new CompletionResult(false, "Lease not found or revision mismatch.");

            // Apply health state transitions per outcome kind
            ApplyHealthTransition(credential, outcomeKind, now);

            // Release the Lease
            credential.LastUsedUTC = now;
            credential.LastOutcome = outcomeKind.ToString();
            credential.Revision++;
            credential.UpdatedUtc = now < credential.CreatedUtc ? credential.CreatedUtc : now;

            // Terminate Claim Record
            var claimRecord = await dbContext.CredentialClaimRecords
                .Where(c => c.LeaseId == leaseId && !c.IsTerminal)
                .SingleOrDefaultAsync(cancellationToken);

            if (claimRecord is not null)
            {
                claimRecord.IsTerminal = true;
                claimRecord.TerminalOutcome = outcomeKind.ToString();
                claimRecord.TerminalizedUtc = now;
                claimRecord.CredentialRevision = credential.Revision;
                claimRecord.UpdatedUtc = now;
            }

            // Terminate Operation Slot
            var slot = await dbContext.OperationSlots
                .Where(s => s.RequestId == credential.LeaseRequestId && !s.IsTerminal)
                .SingleOrDefaultAsync(cancellationToken);

            if (slot is not null)
            {
                slot.IsTerminal = true;
                slot.TerminalizedUtc = now;
                slot.Revision++;
                slot.UpdatedUtc = now;
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            logger?.LogDebug(
                "Completed credential {StableId} lease {LeaseId} with outcome {Outcome}",
                credentialStableId, leaseId, outcomeKind);

            if (claimRecord is not null)
            {
                metrics?.LeaseReleased(
                    credential.SearchProvider, claimRecord.ProviderInstanceStableId);
                var cooldown = outcomeKind switch
                {
                    ProviderOutcomeKind.RateLimited => (CooldownReason?)CooldownReason.RateLimited,
                    ProviderOutcomeKind.ForbiddenScope => CooldownReason.ForbiddenScope,
                    ProviderOutcomeKind.Transient or ProviderOutcomeKind.Cancellation => CooldownReason.Transient,
                    _ => null
                };
                if (cooldown.HasValue)
                {
                    metrics?.RecordCooldown(
                        credential.SearchProvider, claimRecord.ProviderInstanceStableId, cooldown.Value);
                }
            }

            return new CompletionResult(true);
        });
    }

    /// <summary>
    /// Applies fail-closed health state transitions based on the operation outcome.
    /// Per AC-6.16: ForbiddenScope cooldown is a policy configuration value, not a feature flag.
    /// Per AC-6.19: Transient backoff uses the injectable jitter source.
    /// </summary>
    private void ApplyHealthTransition(
        SearchProviderToken credential,
        ProviderOutcomeKind outcome,
        DateTime now)
    {
        switch (outcome)
        {
            case ProviderOutcomeKind.Success:
                credential.ConsecutiveTransientFailures = 0;
                credential.CooldownUntilUtc = null;
                // Clear the lease fields
                credential.LeaseId = null;
                credential.LeaseOwnerNodeId = null;
                credential.LeaseRequestId = null;
                credential.LeaseAcquiredUtc = null;
                credential.LeaseExpiresUtc = null;
                break;

            case ProviderOutcomeKind.RateLimited:
                credential.CooldownUntilUtc = now.Add(policy.RateLimitCooldown);
                credential.LeaseId = null;
                credential.LeaseOwnerNodeId = null;
                credential.LeaseRequestId = null;
                credential.LeaseAcquiredUtc = null;
                credential.LeaseExpiresUtc = null;
                break;

            case ProviderOutcomeKind.ForbiddenScope:
                // AC-6.16: cooldown is policy-configured, not a feature flag
                credential.CooldownUntilUtc = now.Add(policy.ForbiddenScopeCooldown);
                credential.LeaseId = null;
                credential.LeaseOwnerNodeId = null;
                credential.LeaseRequestId = null;
                credential.LeaseAcquiredUtc = null;
                credential.LeaseExpiresUtc = null;
                break;

            case ProviderOutcomeKind.AuthInvalid:
                credential.IsEnabled = false;
                credential.DisabledReason = "AuthInvalid";
                credential.DisabledAtUtc = now;
                credential.LeaseId = null;
                credential.LeaseOwnerNodeId = null;
                credential.LeaseRequestId = null;
                credential.LeaseAcquiredUtc = null;
                credential.LeaseExpiresUtc = null;
                break;

            case ProviderOutcomeKind.Transient:
            case ProviderOutcomeKind.Cancellation:
                credential.ConsecutiveTransientFailures++;
                if (credential.ConsecutiveTransientFailures >= policy.MaxConsecutiveTransientFailures)
                {
                    credential.IsEnabled = false;
                    credential.DisabledReason = "MaxTransientFailures";
                    credential.DisabledAtUtc = now;
                }
                else
                {
                    // Exponential backoff with injectable jitter (AC-6.19)
                    var baseBackoff = TimeSpan.FromTicks(
                        policy.TransientBackoffBase.Ticks *
                        (long)Math.Pow(2, credential.ConsecutiveTransientFailures - 1));
                    var backoff = baseBackoff > policy.MaxTransientBackoff
                        ? policy.MaxTransientBackoff
                        : baseBackoff;
                    var jitter = jitterSource.Next(TimeSpan.FromSeconds(30));
                    credential.CooldownUntilUtc = now.Add(backoff + jitter);
                }
                credential.LeaseId = null;
                credential.LeaseOwnerNodeId = null;
                credential.LeaseRequestId = null;
                credential.LeaseAcquiredUtc = null;
                credential.LeaseExpiresUtc = null;
                break;

            case ProviderOutcomeKind.RequestInvalid:
            case ProviderOutcomeKind.ResourceMissing:
                // These are caller errors, not credential errors — clear the lease
                credential.LeaseId = null;
                credential.LeaseOwnerNodeId = null;
                credential.LeaseRequestId = null;
                credential.LeaseAcquiredUtc = null;
                credential.LeaseExpiresUtc = null;
                break;

            default:
                // Unknown outcome kind is fail-closed
                credential.IsEnabled = false;
                credential.DisabledReason = $"UnknownOutcome:{outcome}";
                credential.DisabledAtUtc = now;
                credential.LeaseId = null;
                credential.LeaseOwnerNodeId = null;
                credential.LeaseRequestId = null;
                credential.LeaseAcquiredUtc = null;
                credential.LeaseExpiresUtc = null;
                break;
        }
    }

    /// <summary>
    /// Earliest retry hint when no credential is eligible: the soonest
    /// CooldownUntilUtc or LeaseExpiresUtc in the future, or null when no
    /// credential exists (admin action required, not a retryable wait).
    /// </summary>
    public static async Task<TimeSpan?> ResolveEarliestCredentialRetryAsync(
        DBContext dbContext,
        long providerInstanceId,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var cooldown = await dbContext.SearchProviderTokens
            .Where(t => t.ProviderInstanceId == providerInstanceId && t.IsEnabled && !t.IsArchived && t.CooldownUntilUtc != null && t.CooldownUntilUtc > now)
            .OrderBy(t => t.CooldownUntilUtc)
            .Select(t => t.CooldownUntilUtc)
            .FirstOrDefaultAsync(cancellationToken);
        var leaseExpiry = await dbContext.SearchProviderTokens
            .Where(t => t.ProviderInstanceId == providerInstanceId && t.IsEnabled && !t.IsArchived && t.LeaseId != null && t.LeaseExpiresUtc != null && t.LeaseExpiresUtc > now)
            .OrderBy(t => t.LeaseExpiresUtc)
            .Select(t => t.LeaseExpiresUtc)
            .FirstOrDefaultAsync(cancellationToken);

        DateTime? earliest = null;
        if (cooldown.HasValue) earliest = cooldown.Value;
        if (leaseExpiry.HasValue && (!earliest.HasValue || leaseExpiry.Value < earliest.Value)) earliest = leaseExpiry.Value;
        if (!earliest.HasValue) return null;
        var delay = earliest.Value - now;
        if (delay <= TimeSpan.Zero) return TimeSpan.FromSeconds(1);
        return delay > TimeSpan.FromHours(8) ? TimeSpan.FromHours(8) : delay;
    }
}

using System.Data;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using UnsecuredAPIKeys.Data;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Data.Models;
using UnsecuredAPIKeys.Providers._Interfaces;

namespace UnsecuredAPIKeys.Services;

/// <summary>
/// PostgreSQL-optimized credential scheduler.
/// Implements AC-13.32 through AC-13.38, AC-13.46, AC-17.24 through AC-17.28:
/// - Uses explicit EF Core execution strategy with short ACID transactions (AC-13.32, AC-13.36)
/// - Uses SELECT ... FOR UPDATE SKIP LOCKED for concurrent row-level candidate locking (AC-13.34, AC-17.24)
/// - Uses database CURRENT_TIMESTAMP for time authority (AC-13.33, AC-17.26)
/// - Idempotent replay: checks existing claim records and replays winning record without leaking leases or slots (AC-13.35, AC-17.25)
/// - Atomic OperationSlot concurrency enforcement (AC-13.48)
/// - Strictly leaves credential decryption and provider traffic outside row locks (AC-13.37, AC-13.38, AC-17.44)
/// - Compatible with PgBouncer / Supabase transaction pooling mode (AC-13.46, AC-17.28)
/// </summary>
public sealed class PostgresCredentialScheduler(
    DBContext dbContext,
    LeasePolicyOptions policy,
    ISchedulerJitterSource jitterSource,
    IDatabaseUtcClock clock,
    ILogger<PostgresCredentialScheduler>? logger = null,
    SearchPlatformMetrics? metrics = null)
    : ICredentialScheduler
{
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

        var waitTimer = Stopwatch.StartNew();
        var executionStrategy = dbContext.Database.CreateExecutionStrategy();
        return await executionStrategy.ExecuteAsync(async () =>
        {
            // PgBouncer transaction-mode compatible: explicit short transaction
            await using var tx = await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted, cancellationToken);

            // Authoritative database clock (AC-13.33, AC-17.26)
            var now = await clock.GetUtcNowAsync(cancellationToken);

            void RecordClaim(SearchProviderEnum kind, Guid instanceStableId, ClaimOutcomeLabel outcome)
            {
                metrics?.RecordClaim(kind, instanceStableId, outcome);
                metrics?.ObserveClaimWait(kind, instanceStableId, waitTimer.Elapsed);
            }

            // Idempotency check: if an active claim already exists for (principalScope, principalTelegramId, requestId),
            // replay the winning record without allocating another lease or consuming instance slot capacity (AC-13.35, AC-17.25).
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

            // Count active slots to check concurrency capacity (AC-13.48)
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

            // Candidate selection using FOR UPDATE SKIP LOCKED (AC-13.34, AC-17.24)
            SearchProviderToken? candidate;
            if (dbContext.Database.IsNpgsql())
            {
                candidate = await dbContext.SearchProviderTokens
                    .FromSqlInterpolated($"""
                        SELECT *
                          FROM "SearchProviderTokens"
                         WHERE "ProviderInstanceId" = {instance.Id}
                           AND "IsEnabled" = TRUE
                           AND "IsArchived" = FALSE
                           AND "LeaseId" IS NULL
                           AND ("CooldownUntilUtc" IS NULL OR "CooldownUntilUtc" <= {now})
                         ORDER BY CASE WHEN "LastClaimedUtc" IS NULL THEN 0 ELSE 1 END,
                                  "LastClaimedUtc",
                                  "StableId"
                         LIMIT 1
                         FOR UPDATE SKIP LOCKED
                    """)
                    .SingleOrDefaultAsync(cancellationToken);
            }
            else
            {
                // Fallback for non-Postgres testing
                candidate = await dbContext.SearchProviderTokens
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
            }

            if (candidate is null)
            {
                RecordClaim(instance.ProviderKind, providerInstanceStableId, ClaimOutcomeLabel.Unavailable);
                var retryAfter = await SqliteCredentialScheduler.ResolveEarliestCredentialRetryAsync(
                    dbContext, instance.Id, now, cancellationToken);
                return ClaimOutcome.NotAvailable("No eligible credential is available for this Provider Instance.", retryAfter);
            }

            // Apply lease metadata
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

            // Write Operation Slot (AC-13.48)
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

            // Write Claim Record (AC-13.47)
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
                "Postgres claimed credential {StableId} for instance {InstanceId} with lease {LeaseId}",
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
            await using var tx = await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted, cancellationToken);
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
            await using var tx = await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted, cancellationToken);
            var now = await clock.GetUtcNowAsync(cancellationToken);

            // Find credential strictly matching LeaseId and Revision to isolate against replacement leases (AC-17.27)
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
                "Postgres completed credential {StableId} lease {LeaseId} with outcome {Outcome}",
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
                credential.LeaseId = null;
                credential.LeaseOwnerNodeId = null;
                credential.LeaseRequestId = null;
                credential.LeaseAcquiredUtc = null;
                credential.LeaseExpiresUtc = null;
                break;

            default:
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
}

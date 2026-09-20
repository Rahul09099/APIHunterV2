using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using UnsecuredAPIKeys.Data;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Data.Models;
using UnsecuredAPIKeys.Providers._Interfaces;

namespace UnsecuredAPIKeys.Services;

public enum WorkerClaimsCutoverStatus
{
    Succeeded,
    NotReady,
    AlreadyComplete
}

public sealed record WorkerClaimsCutoverResult(
    WorkerClaimsCutoverStatus Status,
    IReadOnlyList<string> Failures);

public sealed record RollbackDrainResult(
    int LeasesDrained,
    int LeasesSkipped);

/// <summary>
/// Durable Worker-Claims cutover and rollback-drain boundary (Wave 16, Tasks 16.2/16.5).
/// Cutover is one-way: once the marker is complete, only a separately approved migration
/// may change scheduling behavior — there is no un-cutover. Rollback preparation drains
/// active Leases through the Scheduler without touching credential rows.
/// </summary>
public sealed class WorkerClaimsCutoverService(
    DBContext dbContext,
    ISearchPlatformSchedulingReadinessService schedulingReadiness,
    ICredentialScheduler scheduler,
    IConfiguration configuration,
    IDatabaseUtcClock clock)
{
    /// <summary>
    /// Records the durable cutover after verifying pre-cutover readiness and explicit
    /// Worker-Claims enablement. Safe to retry: a complete marker returns
    /// <see cref="WorkerClaimsCutoverStatus.AlreadyComplete"/> without further writes.
    /// </summary>
    public async Task<WorkerClaimsCutoverResult> ExecuteCutoverAsync(
        CancellationToken cancellationToken = default)
    {
        var readiness = await schedulingReadiness.EvaluateAsync(cancellationToken);
        if (!readiness.IsReady)
        {
            return new WorkerClaimsCutoverResult(
                WorkerClaimsCutoverStatus.NotReady,
                readiness.Failures);
        }

        if (!ReadStrictBoolean(configuration, SearchPlatformFeatureFlagNames.WorkerClaimsEnabled))
        {
            return new WorkerClaimsCutoverResult(
                WorkerClaimsCutoverStatus.NotReady,
                ["Worker Claims must be explicitly enabled before cutover."]);
        }

        var executionStrategy = dbContext.Database.CreateExecutionStrategy();
        return await executionStrategy.ExecuteAsync(async () =>
        {
            await using var tx = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            var now = await clock.GetUtcNowAsync(cancellationToken);

            var marker = await dbContext.CutoverMarkers
                .SingleOrDefaultAsync(
                    candidate =>
                        candidate.Id == ProviderInstanceSchema.MarkerRecordId &&
                        candidate.Name == ProviderInstanceSchema.WorkerClaimsCutoverMarkerName,
                    cancellationToken);
            if (marker is null)
            {
                marker = new CutoverMarker
                {
                    Id = ProviderInstanceSchema.MarkerRecordId,
                    Name = ProviderInstanceSchema.WorkerClaimsCutoverMarkerName,
                    Version = ProviderInstanceSchema.CurrentVersion,
                    IsComplete = false,
                    UpdatedUtc = now
                };
                dbContext.CutoverMarkers.Add(marker);
            }

            if (marker.Version != ProviderInstanceSchema.CurrentVersion)
            {
                await tx.RollbackAsync(cancellationToken);
                return new WorkerClaimsCutoverResult(
                    WorkerClaimsCutoverStatus.NotReady,
                    ["The Worker Claims cutover marker version is incompatible."]);
            }

            if (marker.IsComplete)
            {
                await tx.RollbackAsync(cancellationToken);
                return new WorkerClaimsCutoverResult(
                    WorkerClaimsCutoverStatus.AlreadyComplete, []);
            }

            marker.IsComplete = true;
            marker.Version = ProviderInstanceSchema.CurrentVersion;
            marker.UpdatedUtc = now;
            await dbContext.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return new WorkerClaimsCutoverResult(WorkerClaimsCutoverStatus.Succeeded, []);
        });
    }

    /// <summary>
    /// Drains every non-terminal Lease as Transient for rollback preparation (Task 16.5).
    /// Never mutates credential rows beyond the Scheduler's own release transitions, and
    /// never deletes state: skipped Leases are reported, not forced.
    /// </summary>
    public async Task<RollbackDrainResult> DrainActiveLeasesForRollbackAsync(
        CancellationToken cancellationToken = default)
    {
        var actives = await dbContext.CredentialClaimRecords
            .AsNoTracking()
            .Where(record => !record.IsTerminal)
            .OrderBy(record => record.Id)
            .Select(record => new
            {
                record.LeaseId,
                record.CredentialStableId,
                record.CredentialRevision
            })
            .ToListAsync(cancellationToken);

        var drained = 0;
        var skipped = 0;
        foreach (var active in actives)
        {
            try
            {
                var completion = await scheduler.CompleteAsync(
                    active.LeaseId,
                    active.CredentialStableId,
                    active.CredentialRevision,
                    ProviderOutcomeKind.Transient,
                    cancellationToken);
                if (completion.Succeeded)
                {
                    drained++;
                }
                else
                {
                    skipped++;
                }
            }
            catch
            {
                skipped++;
            }
        }

        return new RollbackDrainResult(drained, skipped);
    }

    private static bool ReadStrictBoolean(IConfiguration configuration, string key) =>
        bool.TryParse(configuration[key], out var enabled) && enabled;
}

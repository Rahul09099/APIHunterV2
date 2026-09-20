using Microsoft.EntityFrameworkCore;
using UnsecuredAPIKeys.Data;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Data.Models;

namespace UnsecuredAPIKeys.Services;

/// <summary>
/// Serializes every credential identity/state migration with administrator and bootstrap
/// mutations. Database transactions remain the durability boundary; this gate prevents two
/// in-process mutation workflows from deriving changes from different credential snapshots.
/// </summary>
public sealed class CredentialMutationGate
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<T> ExecuteAsync<T>(
        Func<Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await operation();
        }
        finally
        {
            gate.Release();
        }
    }
}

public sealed record CredentialDuplicateReconciliationResult(
    bool IsDryRun,
    int DuplicateGroupCount,
    int DuplicateCredentialCount,
    IReadOnlyList<Guid> CanonicalStableIds,
    IReadOnlyList<Guid> DuplicateStableIds);

public sealed class CredentialDuplicateLeaseConflictException(
    IReadOnlyList<Guid> credentialStableIds)
    : InvalidOperationException("Duplicate credential reconciliation is blocked by conflicting active Leases.")
{
    public IReadOnlyList<Guid> CredentialStableIds { get; } = credentialStableIds;
}

/// <summary>
/// Provider-specific DDL for the post-reconciliation active credential identity constraint.
/// Archived identities retain their historical fingerprint and are intentionally excluded.
/// </summary>
public static class CredentialFingerprintIdentityIndex
{
    public const string Name = "UX_SearchProviderTokens_ProviderInstance_Fingerprint";

    public static Task DropAsync(DBContext dbContext, CancellationToken cancellationToken = default) =>
        dbContext.Database.ExecuteSqlRawAsync(
            $"DROP INDEX IF EXISTS \"{Name}\";",
            cancellationToken);

    public static async Task EnsureAsync(
        DBContext dbContext,
        CancellationToken cancellationToken = default)
    {
        var activePredicate = dbContext.Database.IsNpgsql()
            ? "\"FingerprintKeyVersion\" > 0 AND NOT \"IsArchived\""
            : "\"FingerprintKeyVersion\" > 0 AND \"IsArchived\" = 0";
        await dbContext.Database.ExecuteSqlRawAsync(
            $"CREATE UNIQUE INDEX IF NOT EXISTS \"{Name}\" " +
            "ON \"SearchProviderTokens\" (\"ProviderInstanceId\", \"Fingerprint\") " +
            $"WHERE {activePredicate};",
            cancellationToken);
    }
}

/// <summary>
/// Atomically merges active protected credential identities that have the same Provider
/// Instance and versioned fingerprint. The canonical row retains history and becomes the
/// sole active identity; duplicate rows remain as archived stable references.
/// </summary>
public sealed class CredentialDuplicateReconciliationService(
    DBContext dbContext,
    IDatabaseUtcClock databaseUtcClock,
    CredentialMutationGate mutationGate)
{
    private const string DuplicateArchivedReason = "DuplicateReconciled";

    public Task<CredentialDuplicateReconciliationResult> ReconcileAsync(
        bool dryRun = false,
        CancellationToken cancellationToken = default) =>
        mutationGate.ExecuteAsync(
            () => ReconcileUnderGateAsync(dryRun, cancellationToken),
            cancellationToken);

    private async Task<CredentialDuplicateReconciliationResult> ReconcileUnderGateAsync(
        bool dryRun,
        CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();
        var activeCredentials = await dbContext.SearchProviderTokens
            .Include(credential => credential.ProviderInstance)
            .Include(credential => credential.CredentialGrants)
            .Where(credential => !credential.IsArchived)
            .OrderBy(credential => credential.Id)
            .ToListAsync(cancellationToken);

        ValidateMigrationPrerequisites(activeCredentials);
        var groups = activeCredentials
            .GroupBy(credential => new FingerprintIdentity(
                credential.ProviderInstanceId,
                Convert.ToHexString(credential.Fingerprint)))
            .Where(group => group.Count() > 1)
            .Select(group => group
                .OrderBy(credential => credential.CreatedUtc)
                .ThenBy(credential => credential.Id)
                .ThenBy(credential => credential.StableId)
                .ToArray())
            .OrderBy(group => group[0].StableId)
            .ToArray();

        var canonicalStableIds = groups.Select(group => group[0].StableId).OrderBy(id => id).ToArray();
        var duplicateStableIds = groups.SelectMany(group => group.Skip(1))
            .Select(credential => credential.StableId)
            .OrderBy(id => id)
            .ToArray();
        var result = new CredentialDuplicateReconciliationResult(
            dryRun,
            groups.Length,
            duplicateStableIds.Length,
            canonicalStableIds,
            duplicateStableIds);

        if (dryRun)
        {
            dbContext.ChangeTracker.Clear();
            return result;
        }

        var executionStrategy = dbContext.Database.CreateExecutionStrategy();
        return await executionStrategy.ExecuteAsync(async () =>
        {
            // Retries must derive the complete merge from durable state.
            dbContext.ChangeTracker.Clear();
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            try
            {
                var now = await databaseUtcClock.GetUtcNowAsync(cancellationToken);
                var credentials = await dbContext.SearchProviderTokens
                    .Include(credential => credential.ProviderInstance)
                    .Include(credential => credential.CredentialGrants)
                    .Where(credential => !credential.IsArchived)
                    .OrderBy(credential => credential.Id)
                    .ToListAsync(cancellationToken);
                ValidateMigrationPrerequisites(credentials);
                var transactionGroups = credentials
                    .GroupBy(credential => new FingerprintIdentity(
                        credential.ProviderInstanceId,
                        Convert.ToHexString(credential.Fingerprint)))
                    .Where(group => group.Count() > 1)
                    .Select(group => group
                        .OrderBy(credential => credential.CreatedUtc)
                        .ThenBy(credential => credential.Id)
                        .ThenBy(credential => credential.StableId)
                        .ToArray())
                    .ToArray();

                foreach (var group in transactionGroups)
                {
                    MergeGroup(group, now);
                    var duplicateIds = group.Skip(1).Select(item => item.StableId).ToArray();
                    if (duplicateIds.Length > 0)
                    {
                        await dbContext.PrivilegedAuditRecords
                            .Where(record => duplicateIds.Contains(record.TargetStableId))
                            .ExecuteUpdateAsync(
                                update => update.SetProperty(
                                    record => record.TargetStableId,
                                    group[0].StableId),
                                cancellationToken);
                    }
                }

                await dbContext.SaveChangesAsync(cancellationToken);
                await CredentialFingerprintIdentityIndex.DropAsync(dbContext, cancellationToken);
                await CredentialFingerprintIdentityIndex.EnsureAsync(dbContext, cancellationToken);
                await transaction.CommitAsync(cancellationToken);

                return new CredentialDuplicateReconciliationResult(
                    false,
                    transactionGroups.Length,
                    transactionGroups.Sum(group => group.Length - 1),
                    transactionGroups.Select(group => group[0].StableId).OrderBy(id => id).ToArray(),
                    transactionGroups.SelectMany(group => group.Skip(1))
                        .Select(credential => credential.StableId)
                        .OrderBy(id => id)
                        .ToArray());
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                dbContext.ChangeTracker.Clear();
                throw;
            }
        });
    }

    private void MergeGroup(IReadOnlyList<SearchProviderToken> orderedGroup, DateTime now)
    {
        var activeLeases = orderedGroup
            .Where(credential => credential.LeaseId is not null && credential.LeaseExpiresUtc > now)
            .ToArray();
        if (activeLeases.Length > 1)
        {
            throw new CredentialDuplicateLeaseConflictException(
                activeLeases.Select(credential => credential.StableId).OrderBy(id => id).ToArray());
        }

        var canonical = orderedGroup[0];
        var duplicates = orderedGroup.Skip(1).ToArray();
        UnionGrants(canonical, duplicates);

        canonical.CooldownUntilUtc = Latest(orderedGroup.Select(credential => credential.CooldownUntilUtc));
        canonical.LastClaimedUtc = Latest(orderedGroup.Select(credential => credential.LastClaimedUtc));
        canonical.LastUsedUTC = Latest(orderedGroup.Select(credential => credential.LastUsedUTC));
        canonical.LastSeenUtc = Latest(orderedGroup.Select(credential => credential.LastSeenUtc));
        canonical.ConsecutiveTransientFailures = orderedGroup.Max(credential => credential.ConsecutiveTransientFailures);

        var disabled = orderedGroup.Where(credential => !credential.IsEnabled).ToArray();
        if (disabled.Length > 0)
        {
            canonical.IsEnabled = false;
            canonical.DisabledReason = disabled
                .Select(credential => credential.DisabledReason!)
                .OrderBy(DisabledReasonRank)
                .ThenBy(reason => reason, StringComparer.Ordinal)
                .First();
            canonical.DisabledAtUtc = Latest(disabled.Select(credential => credential.DisabledAtUtc)) ?? now;
        }
        else
        {
            canonical.IsEnabled = true;
            canonical.DisabledReason = null;
            canonical.DisabledAtUtc = null;
        }

        CopyLease(activeLeases.SingleOrDefault(), canonical);
        canonical.Revision = checked(orderedGroup.Max(credential => credential.Revision) + 1);
        canonical.UpdatedUtc = now;

        foreach (var duplicate in duplicates)
        {
            ClearLease(duplicate);
            duplicate.IsEnabled = false;
            duplicate.DisabledReason = DuplicateArchivedReason;
            duplicate.DisabledAtUtc = now;
            duplicate.IsArchived = true;
            duplicate.ReplacedByStableId = canonical.StableId;
            duplicate.Revision = checked(duplicate.Revision + 1);
            duplicate.UpdatedUtc = now;
        }
    }

    private void UnionGrants(
        SearchProviderToken canonical,
        IReadOnlyCollection<SearchProviderToken> duplicates)
    {
        var keys = canonical.CredentialGrants
            .Select(grant => new GrantIdentity(grant.Scope, grant.TelegramPrincipalId))
            .ToHashSet();
        foreach (var duplicate in duplicates)
        {
            foreach (var grant in duplicate.CredentialGrants.ToArray())
            {
                var key = new GrantIdentity(grant.Scope, grant.TelegramPrincipalId);
                if (keys.Add(key))
                {
                    canonical.CredentialGrants.Add(new CredentialGrant
                    {
                        Scope = grant.Scope,
                        TelegramPrincipalId = grant.TelegramPrincipalId,
                        CreatedUtc = grant.CreatedUtc
                    });
                }
                dbContext.CredentialGrants.Remove(grant);
            }
        }
    }

    private static void CopyLease(SearchProviderToken? source, SearchProviderToken target)
    {
        if (source is null)
        {
            ClearLease(target);
            return;
        }

        target.LeaseId = source.LeaseId;
        target.LeaseOwnerNodeId = source.LeaseOwnerNodeId;
        target.LeaseRequestId = source.LeaseRequestId;
        target.LeaseAcquiredUtc = source.LeaseAcquiredUtc;
        target.LeaseExpiresUtc = source.LeaseExpiresUtc;
        if (!ReferenceEquals(source, target))
        {
            ClearLease(source);
        }
    }

    private static void ClearLease(SearchProviderToken credential)
    {
        credential.LeaseId = null;
        credential.LeaseOwnerNodeId = null;
        credential.LeaseRequestId = null;
        credential.LeaseAcquiredUtc = null;
        credential.LeaseExpiresUtc = null;
    }

    private static DateTime? Latest(IEnumerable<DateTime?> values) =>
        values.Where(value => value.HasValue).Select(value => value!.Value).DefaultIfEmpty().Max() is var value &&
        value != default
            ? value
            : null;

    private static int DisabledReasonRank(string reason) => reason switch
    {
        "AdministratorDisabled" => 0,
        "AuthInvalid" => 1,
        "ProtectionFailure" => 2,
        "RemovedFromEnvironment" => 3,
        _ => 4
    };

    private static void ValidateMigrationPrerequisites(IEnumerable<SearchProviderToken> credentials)
    {
        foreach (var credential in credentials)
        {
            if (credential.ProviderInstanceId <= 0 || credential.ProviderInstance is null ||
                credential.ProviderInstance.ProviderKind != credential.SearchProvider)
            {
                throw new InvalidOperationException(
                    "Duplicate reconciliation requires completed Provider Instance linking.");
            }
            if (!CredentialStorageService.IsProtected(credential))
            {
                throw new InvalidOperationException(
                    "Duplicate reconciliation requires completed protected credential backfill.");
            }
        }
    }

    private readonly record struct FingerprintIdentity(long ProviderInstanceId, string Fingerprint);
    private readonly record struct GrantIdentity(CredentialGrantScope Scope, long? TelegramPrincipalId);
}

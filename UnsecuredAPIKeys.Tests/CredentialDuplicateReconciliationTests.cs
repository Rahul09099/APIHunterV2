using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using UnsecuredAPIKeys.Data;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Data.Models;
using UnsecuredAPIKeys.Services;
using Xunit;

namespace UnsecuredAPIKeys.Tests;

public sealed class CredentialDuplicateReconciliationTests
{
    private static readonly DateTime BaseUtc =
        new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task ReconcileAsync_MergesIdentityStateHistoryGrantsAuditAndEnforcesActiveUniqueness()
    {
        await using var store = await ReconciliationStore.CreateAsync();
        var laterIdButEarlierCreated = store.CreateCredential(
            "ghp_duplicate_material_abcdefghijklmnopqrstuvwxyz",
            store.GitHub,
            BaseUtc.AddDays(-3));
        var firstInserted = store.CreateCredential(
            "ghp_duplicate_material_abcdefghijklmnopqrstuvwxyz",
            store.GitHub,
            BaseUtc.AddDays(-2));
        var restrictive = store.CreateCredential(
            "ghp_duplicate_material_abcdefghijklmnopqrstuvwxyz",
            store.GitHub,
            BaseUtc.AddDays(-1));
        var sameFingerprintOtherInstance = store.CreateCredential(
            "glpat-other-instance-material-abcdefghijklmnopqrstuvwxyz",
            store.GitLab,
            BaseUtc.AddDays(-4));
        sameFingerprintOtherInstance.FingerprintKeyVersion = laterIdButEarlierCreated.FingerprintKeyVersion;
        sameFingerprintOtherInstance.Fingerprint = laterIdButEarlierCreated.Fingerprint.ToArray();

        // Insert order deliberately disagrees with CreatedUtc order. An identical fingerprint
        // on another instance remains a distinct physical identity.
        store.Context.AddRange(
            firstInserted,
            laterIdButEarlierCreated,
            restrictive,
            sameFingerprintOtherInstance);
        await store.Context.SaveChangesAsync();

        laterIdButEarlierCreated.CredentialGrants.Add(UserGrant(1001, BaseUtc.AddDays(-3)));
        firstInserted.CredentialGrants.Add(new CredentialGrant
        {
            Scope = CredentialGrantScope.Global,
            CreatedUtc = BaseUtc.AddDays(-2)
        });
        restrictive.CredentialGrants.Add(UserGrant(2002, BaseUtc.AddDays(-1)));
        restrictive.IsEnabled = false;
        restrictive.DisabledReason = "AdministratorDisabled";
        restrictive.DisabledAtUtc = BaseUtc.AddHours(3);
        restrictive.CooldownUntilUtc = BaseUtc.AddDays(8);
        restrictive.LastClaimedUtc = BaseUtc.AddHours(8);
        restrictive.LastUsedUTC = BaseUtc.AddHours(7);
        restrictive.ConsecutiveTransientFailures = 4;
        firstInserted.CooldownUntilUtc = BaseUtc.AddDays(5);
        firstInserted.LastClaimedUtc = BaseUtc.AddHours(2);
        firstInserted.LastUsedUTC = BaseUtc.AddHours(4);

        var activeLeaseId = Guid.NewGuid();
        var leaseRequestId = Guid.NewGuid();
        firstInserted.LeaseId = activeLeaseId;
        firstInserted.LeaseOwnerNodeId = "node-merge";
        firstInserted.LeaseRequestId = leaseRequestId;
        firstInserted.LeaseAcquiredUtc = DateTime.UtcNow.AddMinutes(-1);
        firstInserted.LeaseExpiresUtc = DateTime.UtcNow.AddMinutes(20);

        store.Context.PrivilegedAuditRecords.Add(new PrivilegedAuditRecord
        {
            ActorTelegramId = 77,
            Action = PrivilegedActionKind.CredentialDisable,
            TargetStableId = restrictive.StableId,
            OccurredUtc = BaseUtc.AddHours(4),
            Outcome = PrivilegedActionOutcome.Succeeded,
            SanitizedReason = "migration evidence"
        });
        await store.Context.SaveChangesAsync();
        var expectedCanonical = laterIdButEarlierCreated.StableId;

        var result = await store.Reconciler.ReconcileAsync();

        Assert.False(result.IsDryRun);
        Assert.Equal(1, result.DuplicateGroupCount);
        Assert.Equal(2, result.DuplicateCredentialCount);
        Assert.Equal([expectedCanonical], result.CanonicalStableIds);
        Assert.DoesNotContain(
            Convert.ToHexString(laterIdButEarlierCreated.Fingerprint),
            JsonSerializer.Serialize(result),
            StringComparison.OrdinalIgnoreCase);

        store.Context.ChangeTracker.Clear();
        var credentials = await store.Context.SearchProviderTokens
            .Include(credential => credential.CredentialGrants)
            .OrderBy(credential => credential.Id)
            .ToListAsync();
        var canonical = Assert.Single(credentials, credential =>
            !credential.IsArchived && credential.ProviderInstanceId == store.GitHub.Id);
        Assert.Contains(credentials, credential =>
            !credential.IsArchived &&
            credential.ProviderInstanceId == store.GitLab.Id &&
            credential.StableId == sameFingerprintOtherInstance.StableId);
        Assert.Equal(expectedCanonical, canonical.StableId);
        Assert.False(canonical.IsEnabled);
        Assert.Equal("AdministratorDisabled", canonical.DisabledReason);
        Assert.Equal(BaseUtc.AddHours(3), canonical.DisabledAtUtc);
        Assert.Equal(BaseUtc.AddDays(8), canonical.CooldownUntilUtc);
        Assert.Equal(BaseUtc.AddHours(8), canonical.LastClaimedUtc);
        Assert.Equal(BaseUtc.AddHours(7), canonical.LastUsedUTC);
        Assert.Equal(4, canonical.ConsecutiveTransientFailures);
        Assert.Equal(activeLeaseId, canonical.LeaseId);
        Assert.Equal("node-merge", canonical.LeaseOwnerNodeId);
        Assert.Equal(leaseRequestId, canonical.LeaseRequestId);
        Assert.Collection(
            canonical.CredentialGrants.OrderBy(grant => grant.Scope).ThenBy(grant => grant.TelegramPrincipalId),
            grant => Assert.Equal(1001, grant.TelegramPrincipalId),
            grant => Assert.Equal(2002, grant.TelegramPrincipalId),
            grant => Assert.Equal(CredentialGrantScope.Global, grant.Scope));

        var archived = credentials.Where(credential => credential.IsArchived).ToArray();
        Assert.Equal(2, archived.Length);
        Assert.All(archived, duplicate =>
        {
            Assert.Equal(expectedCanonical, duplicate.ReplacedByStableId);
            Assert.Equal("DuplicateReconciled", duplicate.DisabledReason);
            Assert.Null(duplicate.LeaseId);
            Assert.Empty(duplicate.CredentialGrants);
        });
        Assert.Equal(expectedCanonical, await store.Context.PrivilegedAuditRecords
            .Select(record => record.TargetStableId)
            .SingleAsync());

        var anotherActiveDuplicate = store.CreateCredential(
            "ghp_duplicate_material_abcdefghijklmnopqrstuvwxyz",
            store.GitHub,
            BaseUtc);
        // Reconciliation intentionally clears the tracker; attach by FK rather than the
        // fixture's now-detached Provider Instance object.
        anotherActiveDuplicate.ProviderInstance = null;
        store.Context.SearchProviderTokens.Add(anotherActiveDuplicate);
        await Assert.ThrowsAsync<DbUpdateException>(() => store.Context.SaveChangesAsync());
    }

    public static IEnumerable<object[]> RestrictiveReasonCases()
    {
        yield return ["AdministratorDisabled", "AuthInvalid", "AdministratorDisabled"];
        yield return ["AuthInvalid", "ProtectionFailure", "AuthInvalid"];
        yield return ["ProtectionFailure", "RemovedFromEnvironment", "ProtectionFailure"];
        yield return ["RemovedFromEnvironment", "OtherReason", "RemovedFromEnvironment"];
        yield return ["ZuluReason", "AlphaReason", "AlphaReason"];
    }

    [Theory]
    [MemberData(nameof(RestrictiveReasonCases))]
    public async Task ReconcileAsync_SelectsRequiredDisabledReasonPrecedence(
        string firstReason,
        string secondReason,
        string expectedReason)
    {
        await using var store = await ReconciliationStore.CreateAsync();
        var canonical = store.CreateCredential(
            "ghp_reason_precedence_abcdefghijklmnopqrstuvwxyz",
            store.GitHub,
            BaseUtc.AddDays(-2));
        var first = store.CreateCredential(
            "ghp_reason_precedence_abcdefghijklmnopqrstuvwxyz",
            store.GitHub,
            BaseUtc.AddDays(-1));
        var second = store.CreateCredential(
            "ghp_reason_precedence_abcdefghijklmnopqrstuvwxyz",
            store.GitHub,
            BaseUtc);
        Disable(first, firstReason, BaseUtc.AddHours(1));
        Disable(second, secondReason, BaseUtc.AddHours(2));
        store.Context.AddRange(canonical, first, second);
        await store.Context.SaveChangesAsync();

        await store.Reconciler.ReconcileAsync();

        store.Context.ChangeTracker.Clear();
        var merged = await store.Context.SearchProviderTokens.SingleAsync(item => !item.IsArchived);
        Assert.Equal(expectedReason, merged.DisabledReason);
        Assert.Equal(BaseUtc.AddHours(2), merged.DisabledAtUtc);
    }

    [Fact]
    public async Task ReconcileAsync_ConflictingActiveLeasesAbortEveryMutation()
    {
        await using var store = await ReconciliationStore.CreateAsync();
        var first = store.CreateCredential(
            "ghp_conflicting_leases_abcdefghijklmnopqrstuvwxyz",
            store.GitHub,
            BaseUtc.AddDays(-2));
        var second = store.CreateCredential(
            "ghp_conflicting_leases_abcdefghijklmnopqrstuvwxyz",
            store.GitHub,
            BaseUtc.AddDays(-1));
        AssignLease(first, "node-a");
        AssignLease(second, "node-b");
        first.CredentialGrants.Add(UserGrant(111, BaseUtc));
        second.CredentialGrants.Add(UserGrant(222, BaseUtc));
        store.Context.AddRange(first, second);
        await store.Context.SaveChangesAsync();
        var before = await SnapshotAsync(store.Context);

        var exception = await Assert.ThrowsAsync<CredentialDuplicateLeaseConflictException>(
            () => store.Reconciler.ReconcileAsync());

        Assert.Equal(2, exception.CredentialStableIds.Count);
        store.Context.ChangeTracker.Clear();
        Assert.Equal(before, await SnapshotAsync(store.Context));
        Assert.False(await IndexExistsAsync(store.Context));
    }

    [Fact]
    public async Task ReconcileAsync_DryRunReportsOnlyCountsAndStableReferencesWithoutMutation()
    {
        await using var store = await ReconciliationStore.CreateAsync();
        const string material = "ghp_dry_run_secret_material_abcdefghijklmnopqrstuvwxyz";
        var first = store.CreateCredential(material, store.GitHub, BaseUtc.AddDays(-2));
        var second = store.CreateCredential(material, store.GitHub, BaseUtc.AddDays(-1));
        store.Context.AddRange(first, second);
        await store.Context.SaveChangesAsync();
        var fingerprintHex = Convert.ToHexString(first.Fingerprint);
        var before = await SnapshotAsync(store.Context);

        var result = await store.Reconciler.ReconcileAsync(dryRun: true);

        Assert.True(result.IsDryRun);
        Assert.Equal(1, result.DuplicateGroupCount);
        Assert.Equal(1, result.DuplicateCredentialCount);
        var serialized = JsonSerializer.Serialize(result);
        Assert.DoesNotContain(material, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(fingerprintHex, serialized, StringComparison.OrdinalIgnoreCase);
        store.Context.ChangeTracker.Clear();
        Assert.Equal(before, await SnapshotAsync(store.Context));
        Assert.False(await IndexExistsAsync(store.Context));
    }

    [Fact]
    public async Task ReconcileAsync_RejectsUnprotectedRowsBeforeChangingIdentity()
    {
        await using var store = await ReconciliationStore.CreateAsync();
        var legacy = new SearchProviderToken
        {
            StableId = Guid.NewGuid(),
            Token = "legacy-only-secret",
            SearchProvider = SearchProviderEnum.GitHub,
            ProviderInstanceId = store.GitHub.Id,
            ProviderInstance = store.GitHub,
            CreatedUtc = BaseUtc,
            UpdatedUtc = BaseUtc
        };
        store.Context.SearchProviderTokens.Add(legacy);
        await store.Context.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.Reconciler.ReconcileAsync());

        Assert.Contains("protected credential backfill", error.Message, StringComparison.Ordinal);
        store.Context.ChangeTracker.Clear();
        Assert.False((await store.Context.SearchProviderTokens.SingleAsync()).IsArchived);
    }

    private static CredentialGrant UserGrant(long principal, DateTime createdUtc) => new()
    {
        Scope = CredentialGrantScope.User,
        TelegramPrincipalId = principal,
        CreatedUtc = createdUtc
    };

    private static void Disable(SearchProviderToken credential, string reason, DateTime disabledAtUtc)
    {
        credential.IsEnabled = false;
        credential.DisabledReason = reason;
        credential.DisabledAtUtc = disabledAtUtc;
        credential.UpdatedUtc = disabledAtUtc;
    }

    private static void AssignLease(SearchProviderToken credential, string owner)
    {
        credential.LeaseId = Guid.NewGuid();
        credential.LeaseOwnerNodeId = owner;
        credential.LeaseRequestId = Guid.NewGuid();
        credential.LeaseAcquiredUtc = DateTime.UtcNow.AddMinutes(-1);
        credential.LeaseExpiresUtc = DateTime.UtcNow.AddMinutes(20);
    }

    private static async Task<string> SnapshotAsync(DBContext context)
    {
        var rows = await context.SearchProviderTokens
            .AsNoTracking()
            .OrderBy(credential => credential.Id)
            .Select(credential => new
            {
                credential.Id,
                credential.StableId,
                credential.IsEnabled,
                credential.DisabledReason,
                credential.DisabledAtUtc,
                credential.IsArchived,
                credential.ReplacedByStableId,
                credential.LeaseId,
                credential.Revision,
                Grants = credential.CredentialGrants
                    .OrderBy(grant => grant.Id)
                    .Select(grant => new { grant.Scope, grant.TelegramPrincipalId })
                    .ToArray()
            })
            .ToArrayAsync();
        return JsonSerializer.Serialize(rows);
    }

    private static async Task<bool> IndexExistsAsync(DBContext context)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = $name;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$name";
        parameter.Value = CredentialFingerprintIdentityIndex.Name;
        command.Parameters.Add(parameter);
        return Convert.ToInt64(await command.ExecuteScalarAsync()) == 1;
    }

    private sealed class ReconciliationStore : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private ReconciliationStore(
            SqliteConnection connection,
            DBContext context,
            CredentialStorageService storage,
            SearchProviderInstance gitHub,
            SearchProviderInstance gitLab,
            CredentialDuplicateReconciliationService reconciler)
        {
            this.connection = connection;
            Context = context;
            Storage = storage;
            GitHub = gitHub;
            GitLab = gitLab;
            Reconciler = reconciler;
        }

        public DBContext Context { get; }
        public CredentialStorageService Storage { get; }
        public SearchProviderInstance GitHub { get; }
        public SearchProviderInstance GitLab { get; }
        public CredentialDuplicateReconciliationService Reconciler { get; }

        public static async Task<ReconciliationStore> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
            await connection.OpenAsync();
            var context = new DBContext(
                new DbContextOptionsBuilder<DBContext>().UseSqlite(connection).Options);
            await context.Database.EnsureCreatedAsync();
            await CredentialFingerprintIdentityIndex.DropAsync(context);

            var gitHub = Instance(
                ProviderInstanceSchema.DefaultGitHubStableId,
                SearchProviderEnum.GitHub,
                "GitHub",
                "api.github.com",
                "/");
            var gitLab = Instance(
                ProviderInstanceSchema.DefaultGitLabStableId,
                SearchProviderEnum.GitLab,
                "GitLab",
                "gitlab.com",
                "/api/v4");
            context.SearchProviderInstances.AddRange(gitHub, gitLab);
            await context.SaveChangesAsync();

            var protection = new CredentialProtectionService(
                new Dictionary<int, byte[]> { [7] = Enumerable.Repeat((byte)0x37, 32).ToArray() },
                7);
            var fingerprint = new CredentialFingerprintService(
                new Dictionary<int, byte[]> { [11] = Enumerable.Repeat((byte)0x5B, 32).ToArray() },
                11);
            var guard = new CredentialStorageMigrationGuard(new ConfigurationBuilder().Build());
            var storage = new CredentialStorageService(protection, fingerprint, guard);
            var reconciler = new CredentialDuplicateReconciliationService(
                context,
                new DatabaseUtcClock(context),
                new CredentialMutationGate());
            return new ReconciliationStore(connection, context, storage, gitHub, gitLab, reconciler);
        }

        public SearchProviderToken CreateCredential(
            string material,
            SearchProviderInstance instance,
            DateTime createdUtc) =>
            Storage.CreateProtectedCredential(
                material,
                instance,
                CredentialSource.Manual,
                nowUtc: createdUtc);

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
        }

        private static SearchProviderInstance Instance(
            Guid stableId,
            SearchProviderEnum kind,
            string displayName,
            string host,
            string basePath) => new()
        {
            StableId = stableId,
            ProviderKind = kind,
            DisplayName = displayName,
            NormalizedScheme = "https",
            NormalizedHost = host,
            NormalizedPort = 443,
            NormalizedBasePath = basePath,
            IsEnabled = true,
            MaxConcurrentOperations = 4,
            SettingsVersion = 1,
            SettingsJson = "{}",
            CreatedUtc = BaseUtc.AddYears(-1),
            UpdatedUtc = BaseUtc.AddYears(-1)
        };
    }
}

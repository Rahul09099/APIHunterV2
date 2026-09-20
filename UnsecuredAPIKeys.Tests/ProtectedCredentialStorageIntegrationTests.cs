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

/// <summary>
/// Task 4.2 integration coverage for protected writes, bounded protected-first reads,
/// duplicate identity, and explicit pre-scrub verification readiness.
/// </summary>
public sealed class ProtectedCredentialStorageIntegrationTests
{
    [Fact]
    public async Task ProtectedWrite_PersistsNoPlaintextAndReturnsSecretFreeDuplicateSummary()
    {
        await using var store = await CredentialStore.CreateAsync();
        const string material = "ghp_task_4_2_protected_write_canary";

        var first = await store.DatabaseService.AddGitHubTokenAsync(store.Context, material);
        var duplicate = await store.DatabaseService.AddGitHubTokenAsync(store.Context, material);

        Assert.True(first.Created);
        Assert.False(duplicate.Created);
        Assert.Equal(first.Id, duplicate.Id);
        Assert.Equal(first.StableId, duplicate.StableId);

        store.Context.ChangeTracker.Clear();
        var persisted = await store.Context.SearchProviderTokens
            .AsNoTracking()
            .SingleAsync();
        Assert.Equal(string.Empty, persisted.Token);
        Assert.True(CredentialStorageService.IsProtected(persisted));
        Assert.NotEqual(Guid.Empty, persisted.StableId);
        Assert.Equal(store.Instance.Id, persisted.ProviderInstanceId);

        var summaries = await store.DatabaseService.GetGitHubTokensAsync(store.Context);
        var serialized = JsonSerializer.Serialize(summaries);
        Assert.DoesNotContain(material, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToHexString(persisted.Fingerprint), serialized, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(first.Alias, Assert.Single(summaries).Alias);
    }

    [Fact]
    public async Task CurrentOperationGrant_IsPostCommitSingleUseAndNeverFallsBackAfterEnvelopeFailure()
    {
        await using var store = await CredentialStore.CreateAsync(allowLegacyRead: true);
        const string material = "glpat-task-4-2-current-operation-canary";
        var created = await store.DatabaseService.AddGitLabTokenAsync(store.Context, material);

        var grant = await store.MaterialAccess.GrantStoredCredentialAfterCommitAsync(
            created.StableId,
            Guid.NewGuid());
        using (var plaintext = await store.MaterialAccess.DecryptGrantedAsync(grant))
        {
            Assert.Equal(material, plaintext.Value);
        }

        await Assert.ThrowsAsync<CredentialProtectionException>(
            () => store.MaterialAccess.DecryptGrantedAsync(grant));

        var credential = await store.Context.SearchProviderTokens
            .SingleAsync(candidate => candidate.StableId == created.StableId);
        credential.Token = "legacy-fallback-must-not-be-used";
        var tamperedTag = credential.AuthenticationTag.ToArray();
        tamperedTag[0] ^= 0x40;
        credential.AuthenticationTag = tamperedTag;
        await store.Context.SaveChangesAsync();
        store.Context.ChangeTracker.Clear();

        var tamperedGrant = await store.MaterialAccess.GrantStoredCredentialAfterCommitAsync(
            created.StableId,
            Guid.NewGuid());
        var failure = await Assert.ThrowsAsync<CredentialProtectionException>(
            () => store.MaterialAccess.DecryptGrantedAsync(tamperedGrant));
        Assert.DoesNotContain(material, failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("legacy-fallback-must-not-be-used", failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProtectedReadiness_RequiresExplicitSuccessfulVerificationMarker()
    {
        await using var store = await CredentialStore.CreateAsync(protectedStorageRequired: true);
        await store.DatabaseService.AddGitHubTokenAsync(
            store.Context,
            "ghp_task_4_2_readiness_canary");

        var readinessService = new CredentialProtectionReadinessService(
            store.Context,
            store.Protection,
            store.Fingerprint,
            store.Guard,
            store.Configuration);
        var beforeVerification = await readinessService.EvaluateAsync();
        Assert.False(beforeVerification.IsReady);
        Assert.False(beforeVerification.ProtectedReadVerificationReady);

        var migration = new CredentialPlaintextMigrationService(
            store.Context,
            store.Storage,
            store.Guard,
            store.MaterialAccess,
            new DatabaseUtcClock(store.Context));
        var verification = await migration.VerifyEnabledProtectedReadsAsync();
        Assert.True(verification.AllEnabledCredentialsUseProtectedReads);
        Assert.Empty(verification.FailedCredentialStableIds);

        var afterVerification = await readinessService.EvaluateAsync();
        Assert.True(afterVerification.IsReady);
        Assert.True(afterVerification.RequiredKeysReady);
        Assert.True(afterVerification.UsableCredentialsReady);
        Assert.True(afterVerification.ProtectedReadVerificationReady);
    }

    [Fact]
    public async Task LegacyPlaintextRead_RequiresExplicitDualReadGuard()
    {
        await using var allowed = await CredentialStore.CreateAsync(allowLegacyRead: true);
        const string material = "ghp_task_4_2_guarded_legacy_canary";
        var legacy = new SearchProviderToken
        {
            StableId = Guid.NewGuid(),
            Token = material,
            SearchProvider = SearchProviderEnum.GitHub,
            ProviderInstanceId = allowed.Instance.Id,
            ProviderInstance = allowed.Instance,
            IsEnabled = true,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };
        allowed.Context.SearchProviderTokens.Add(legacy);
        await allowed.Context.SaveChangesAsync();
        allowed.Context.ChangeTracker.Clear();

        var allowedGrant = await allowed.MaterialAccess.GrantStoredCredentialAfterCommitAsync(
            legacy.StableId,
            Guid.NewGuid());
        using (var plaintext = await allowed.MaterialAccess.DecryptGrantedAsync(allowedGrant))
        {
            Assert.Equal(material, plaintext.Value);
        }

        await using var denied = await CredentialStore.CreateAsync();
        var deniedLegacy = new SearchProviderToken
        {
            StableId = Guid.NewGuid(),
            Token = material,
            SearchProvider = SearchProviderEnum.GitHub,
            ProviderInstanceId = denied.Instance.Id,
            ProviderInstance = denied.Instance,
            IsEnabled = true,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };
        denied.Context.SearchProviderTokens.Add(deniedLegacy);
        await denied.Context.SaveChangesAsync();
        denied.Context.ChangeTracker.Clear();

        var deniedGrant = await denied.MaterialAccess.GrantStoredCredentialAfterCommitAsync(
            deniedLegacy.StableId,
            Guid.NewGuid());
        var failure = await Assert.ThrowsAsync<CredentialProtectionException>(
            () => denied.MaterialAccess.DecryptGrantedAsync(deniedGrant));
        Assert.DoesNotContain(material, failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void MigrationGuard_RejectsPlaintextWriteOutsideDualReadOrDuringVerification()
    {
        static IConfiguration Configuration(bool read, bool write, bool verify) =>
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [CredentialStorageMigrationGuard.PreScrubGuardEnabled] = "true",
                    [CredentialStorageMigrationGuard.LegacyPlaintextReadEnabled] = read.ToString(),
                    [CredentialStorageMigrationGuard.LegacyPlaintextWriteEnabled] = write.ToString(),
                    [CredentialStorageMigrationGuard.VerifyProtectedReadsOnStartup] = verify.ToString()
                })
                .Build();

        Assert.Throws<CredentialProtectionException>(() =>
            new CredentialStorageMigrationGuard(Configuration(read: false, write: true, verify: false)));
        Assert.Throws<CredentialProtectionException>(() =>
            new CredentialStorageMigrationGuard(Configuration(read: true, write: true, verify: true)));

        var guardedWrite = new CredentialStorageMigrationGuard(
            Configuration(read: true, write: true, verify: false));
        Assert.True(guardedWrite.CanReadLegacyPlaintext);
        Assert.True(guardedWrite.CanRetainLegacyPlaintextOnProtectedWrite);
    }

    [Fact]
    public async Task ProtectedReadiness_RejectsCredentialsOnDisabledProviderInstances()
    {
        await using var store = await CredentialStore.CreateAsync(protectedStorageRequired: true);
        await store.DatabaseService.AddGitHubTokenAsync(
            store.Context,
            "ghp_task_4_2_disabled_instance_canary");

        var migration = new CredentialPlaintextMigrationService(
            store.Context,
            store.Storage,
            store.Guard,
            store.MaterialAccess,
            new DatabaseUtcClock(store.Context));
        Assert.True((await migration.VerifyEnabledProtectedReadsAsync())
            .AllEnabledCredentialsUseProtectedReads);

        store.Instance.IsEnabled = false;
        store.Instance.UpdatedUtc = DateTime.UtcNow;
        await store.Context.SaveChangesAsync();

        var readiness = await new CredentialProtectionReadinessService(
            store.Context,
            store.Protection,
            store.Fingerprint,
            store.Guard,
            store.Configuration).EvaluateAsync();

        Assert.False(readiness.IsReady);
        Assert.False(readiness.UsableCredentialsReady);
    }

    private sealed class CredentialStore : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private CredentialStore(
            SqliteConnection connection,
            DBContext context,
            IConfiguration configuration,
            SearchProviderInstance instance,
            CredentialProtectionService protection,
            CredentialFingerprintService fingerprint,
            CredentialStorageMigrationGuard guard,
            CredentialStorageService storage,
            CredentialMaterialAccessService materialAccess,
            DatabaseService databaseService)
        {
            this.connection = connection;
            Context = context;
            Configuration = configuration;
            Instance = instance;
            Protection = protection;
            Fingerprint = fingerprint;
            Guard = guard;
            Storage = storage;
            MaterialAccess = materialAccess;
            DatabaseService = databaseService;
        }

        public DBContext Context { get; }
        public IConfiguration Configuration { get; }
        public SearchProviderInstance Instance { get; }
        public CredentialProtectionService Protection { get; }
        public CredentialFingerprintService Fingerprint { get; }
        public CredentialStorageMigrationGuard Guard { get; }
        public CredentialStorageService Storage { get; }
        public CredentialMaterialAccessService MaterialAccess { get; }
        public DatabaseService DatabaseService { get; }

        public static async Task<CredentialStore> CreateAsync(
            bool allowLegacyRead = false,
            bool protectedStorageRequired = false)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var context = new DBContext(
                new DbContextOptionsBuilder<DBContext>()
                    .UseSqlite(connection)
                    .Options);
            await context.Database.EnsureCreatedAsync();

            var values = new Dictionary<string, string?>
            {
                ["SearchCredentials:Protection:ActiveKeyVersion"] = "7",
                ["SearchCredentials:Protection:Keys:7"] = Convert.ToBase64String(
                    Enumerable.Repeat((byte)0x37, 32).ToArray()),
                ["SearchCredentials:Fingerprint:ActiveKeyVersion"] = "11",
                ["SearchCredentials:Fingerprint:Keys:11"] = Convert.ToBase64String(
                    Enumerable.Repeat((byte)0x5B, 32).ToArray()),
                [CredentialStorageMigrationGuard.PreScrubGuardEnabled] = allowLegacyRead.ToString(),
                [CredentialStorageMigrationGuard.LegacyPlaintextReadEnabled] = allowLegacyRead.ToString(),
                [CredentialStorageMigrationGuard.LegacyPlaintextWriteEnabled] = "false",
                [SearchPlatformFeatureFlagNames.ProtectedStorageEnabled] = protectedStorageRequired.ToString()
            };
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(values)
                .Build();
            var protection = new CredentialProtectionService(configuration);
            var fingerprint = new CredentialFingerprintService(configuration);
            var guard = new CredentialStorageMigrationGuard(configuration);
            var storage = new CredentialStorageService(protection, fingerprint, guard);
            var materialAccess = new CredentialMaterialAccessService(context, protection, guard);
            var databaseService = new DatabaseService(context, storage);

            var github = CreateInstance(SearchProviderEnum.GitHub);
            var gitlab = CreateInstance(SearchProviderEnum.GitLab);
            context.SearchProviderInstances.AddRange(github, gitlab);
            await context.SaveChangesAsync();

            return new CredentialStore(
                connection,
                context,
                configuration,
                github,
                protection,
                fingerprint,
                guard,
                storage,
                materialAccess,
                databaseService);
        }

        private static SearchProviderInstance CreateInstance(SearchProviderEnum provider)
        {
            var now = DateTime.UtcNow;
            return provider == SearchProviderEnum.GitHub
                ? new SearchProviderInstance
                {
                    StableId = ProviderInstanceSchema.DefaultGitHubStableId,
                    ProviderKind = provider,
                    DisplayName = "GitHub",
                    NormalizedScheme = "https",
                    NormalizedHost = "api.github.com",
                    NormalizedPort = 443,
                    NormalizedBasePath = "/",
                    IsEnabled = true,
                    MaxConcurrentOperations = 4,
                    SettingsVersion = 1,
                    SettingsJson = "{}",
                    CreatedUtc = now,
                    UpdatedUtc = now
                }
                : new SearchProviderInstance
                {
                    StableId = ProviderInstanceSchema.DefaultGitLabStableId,
                    ProviderKind = provider,
                    DisplayName = "GitLab",
                    NormalizedScheme = "https",
                    NormalizedHost = "gitlab.com",
                    NormalizedPort = 443,
                    NormalizedBasePath = "/api/v4",
                    IsEnabled = true,
                    MaxConcurrentOperations = 4,
                    SettingsVersion = 1,
                    SettingsJson = "{}",
                    CreatedUtc = now,
                    UpdatedUtc = now
                };
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}

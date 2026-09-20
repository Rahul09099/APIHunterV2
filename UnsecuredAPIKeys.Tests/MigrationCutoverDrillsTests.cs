using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using UnsecuredAPIKeys.Data;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Data.DTOs;
using UnsecuredAPIKeys.Data.Models;
using UnsecuredAPIKeys.Providers._Interfaces;
using UnsecuredAPIKeys.Services;
using Xunit;

namespace UnsecuredAPIKeys.Tests;

/// <summary>
/// Tasks 16.2–16.5 — Migration stages, fleet cutover, plaintext scrub, and rollback drills.
/// Covers staged backfill/reconcile/bootstrap/readiness order with legacy-kind gating,
/// the durable Worker-Claims cutover, WORKER_* variable removal, protected-read
/// verification with plaintext scrub, and rollback on both sides of the scrub.
/// </summary>
public sealed class MigrationCutoverDrillsTests
{
    private sealed class FixedClock(DateTime utcNow) : IDatabaseUtcClock
    {
        public Task<DateTime> GetUtcNowAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(utcNow);
    }

    private sealed class ZeroJitterSource : ISchedulerJitterSource
    {
        public TimeSpan Next(TimeSpan maxJitter) => TimeSpan.Zero;
    }

    private sealed class FixedReadiness(bool ready) : ISearchPlatformSchedulingReadinessService
    {
        public Task<ProviderInstanceReadinessReport> EvaluateAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ready
                ? new ProviderInstanceReadinessReport(true, true, true, true,
                    DatabaseCoordinationMode.SingleMaster, false, [])
                : ProviderInstanceReadinessReport.NotEvaluated);
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        public Harness(
            SqliteConnection connection,
            DBContext context,
            IConfiguration configuration,
            CredentialProtectionService protection,
            CredentialFingerprintService fingerprint,
            CredentialStorageMigrationGuard guard,
            CredentialStorageService storage,
            CredentialMaterialAccessService materialAccess,
            DatabaseService databaseService,
            FixedClock clock)
        {
            this.connection = connection;
            Context = context;
            Configuration = configuration;
            Protection = protection;
            Fingerprint = fingerprint;
            Guard = guard;
            Storage = storage;
            MaterialAccess = materialAccess;
            DatabaseService = databaseService;
            Clock = clock;
        }

        public DBContext Context { get; }
        public IConfiguration Configuration { get; }
        public CredentialProtectionService Protection { get; }
        public CredentialFingerprintService Fingerprint { get; }
        public CredentialStorageMigrationGuard Guard { get; }
        public CredentialStorageService Storage { get; }
        public CredentialMaterialAccessService MaterialAccess { get; }
        public DatabaseService DatabaseService { get; }
        public FixedClock Clock { get; }

        public static Harness Create(
            SqliteConnection connection,
            DBContext context,
            IDictionary<string, string?> values)
        {
            values["SearchCredentials:Protection:ActiveKeyVersion"] = "7";
            values["SearchCredentials:Protection:Keys:7"] = Convert.ToBase64String(
                Enumerable.Repeat((byte)0x37, 32).ToArray());
            values["SearchCredentials:Fingerprint:ActiveKeyVersion"] = "11";
            values["SearchCredentials:Fingerprint:Keys:11"] = Convert.ToBase64String(
                Enumerable.Repeat((byte)0x5B, 32).ToArray());
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(values)
                .Build();
            var protection = new CredentialProtectionService(configuration);
            var fingerprint = new CredentialFingerprintService(configuration);
            var guard = new CredentialStorageMigrationGuard(configuration);
            var storage = new CredentialStorageService(protection, fingerprint, guard);
            var clock = new FixedClock(DateTime.UtcNow);
            return new Harness(
                connection, context, configuration, protection, fingerprint, guard, storage,
                new CredentialMaterialAccessService(context, protection, guard),
                new DatabaseService(context, storage), clock);
        }

        public static async Task<Harness> CreateWithDefaultsAsync(
            IDictionary<string, string?> values)
        {
            var sqlConnection = new SqliteConnection("Data Source=:memory:");
            await sqlConnection.OpenAsync();
            var context = new DBContext(
                new DbContextOptionsBuilder<DBContext>()
                    .UseSqlite(sqlConnection)
                    .Options);
            await context.Database.EnsureCreatedAsync();

            var now = DateTime.UtcNow;
            context.SearchProviderInstances.AddRange(
                NewInstance(
                    ProviderInstanceSchema.DefaultGitHubStableId, SearchProviderEnum.GitHub,
                    "GitHub SaaS", "api.github.com", 443, "/", now),
                NewInstance(
                    ProviderInstanceSchema.DefaultGitLabStableId, SearchProviderEnum.GitLab,
                    "GitLab SaaS", "gitlab.com", 443, "/api/v4", now));
            await context.SaveChangesAsync();
            return Create(sqlConnection, context, values);
        }

        private static SearchProviderInstance NewInstance(
            Guid stableId, SearchProviderEnum kind, string displayName,
            string host, int port, string basePath, DateTime now) => new()
        {
            StableId = stableId,
            ProviderKind = kind,
            DisplayName = displayName,
            NormalizedScheme = "https",
            NormalizedHost = host,
            NormalizedPort = port,
            NormalizedBasePath = basePath,
            IsEnabled = true,
            AllowGlobalPublicSearch = false,
            MaxConcurrentOperations = 4,
            SettingsVersion = 1,
            SettingsJson = "{}",
            PrivateNetworkAllowlistJson = "[]",
            CreatedUtc = now,
            UpdatedUtc = now
        };

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private static string ReadServiceSource(string fileName)
    {
        var directory = AppContext.BaseDirectory;
        while (directory is not null && !Directory.Exists(Path.Combine(directory, "UnsecuredAPIKeys.Services")))
        {
            directory = Directory.GetParent(directory)?.FullName;
        }

        Assert.True(directory is not null, "Repository root not found from test output.");
        return File.ReadAllText(Path.Combine(directory, "UnsecuredAPIKeys.Services", fileName));
    }

    // ── 16.2: staged migration order + cutover ─────────────────────────────────

    [Fact]
    public async Task Stages_BackfillReconcileBootstrapProgressInOrder()
    {
        await using var harness = await Harness.CreateWithDefaultsAsync(new Dictionary<string, string?>
        {
            [SearchPlatformFeatureFlagNames.EnvironmentBootstrapEnabled] = "true",
            ["MASTER_GITHUB_TOKENS"] = "ghp_stage_canary_001"
        });

        var gitHub = await harness.Context.SearchProviderInstances
            .SingleAsync(i => i.StableId == ProviderInstanceSchema.DefaultGitHubStableId);
        var legacy = harness.Storage.CreateProtectedCredential(
            "ghp_stage_legacy_canary_002", gitHub,
            CredentialSource.Manual, addedByTelegramId: 77001);
        harness.Context.SearchProviderTokens.Add(legacy);
        await harness.Context.SaveChangesAsync();

        var backfill = await new CredentialGrantBackfillService(
            harness.Context, harness.Configuration, new CredentialGrantBackfillState())
            .BackfillAsync();
        Assert.True(backfill.UserGrantsAdded >= 1);

        var reconcile = await new CredentialDuplicateReconciliationService(
            harness.Context, harness.Clock, new CredentialMutationGate())
            .ReconcileAsync();
        Assert.False(reconcile.IsDryRun);

        var bootstrap = await new EnvironmentBootstrapService(
            harness.Context, harness.Configuration, harness.Storage,
            new CredentialStateService(), harness.Clock,
            new EnvironmentBootstrapReadinessState(), new CredentialMutationGate())
            .RunAsync();
        Assert.True(bootstrap.IsEnabled);
        Assert.True(bootstrap.ImportedCount >= 1);

        var imported = await harness.Context.SearchProviderTokens
            .Where(t => t.Source == CredentialSource.Environment)
            .ToListAsync();
        Assert.All(imported, credential =>
        {
            Assert.Equal(string.Empty, credential.Token);
            Assert.True(CredentialStorageService.IsProtected(credential));
        });

        var readiness = await new ProviderInstanceReadinessService(harness.Context).EvaluateAsync();
        Assert.True(readiness.SchemaReady);
        Assert.True(readiness.ProviderInstancesReady);
        Assert.False(readiness.MarkersReady);
    }

    [Fact]
    public async Task LegacyKindCompatibility_IsEnforcedBeforeAuthorityChanges()
    {
        // The EF validation gate rejects kind/instance mismatch at write time, so no
        // legacy row can reach the scheduler with a conflicting authority.
        await using var harness = await Harness.CreateWithDefaultsAsync(
            new Dictionary<string, string?>());
        var gitLab = await harness.Context.SearchProviderInstances
            .SingleAsync(i => i.StableId == ProviderInstanceSchema.DefaultGitLabStableId);

        harness.Context.SearchProviderTokens.Add(new SearchProviderToken
        {
            StableId = Guid.NewGuid(),
            Token = string.Empty,
            SearchProvider = SearchProviderEnum.GitHub,
            ProviderInstanceId = gitLab.Id,
            Source = CredentialSource.Manual,
            IsEnabled = true,
            Revision = 0,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        });

        var error = await Assert.ThrowsAsync<System.ComponentModel.DataAnnotations.ValidationException>(
            () => harness.Context.SaveChangesAsync());
        Assert.Contains("match", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cutover_RequiresReadinessAndFlag_ThenRecordsDurably()
    {
        await using var harness = await Harness.CreateWithDefaultsAsync(new Dictionary<string, string?>
        {
            [SearchPlatformFeatureFlagNames.WorkerClaimsEnabled] = "true"
        });
        var cutover = new WorkerClaimsCutoverService(
            harness.Context, new FixedReadiness(false),
            NewScheduler(harness), harness.Configuration, harness.Clock);

        var notReady = await cutover.ExecuteCutoverAsync();
        Assert.Equal(WorkerClaimsCutoverStatus.NotReady, notReady.Status);

        var flagOff = new WorkerClaimsCutoverService(
            harness.Context, new FixedReadiness(true),
            NewScheduler(harness),
            new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>()).Build(),
            harness.Clock);
        Assert.Equal(
            WorkerClaimsCutoverStatus.NotReady,
            (await flagOff.ExecuteCutoverAsync()).Status);

        var ready = new WorkerClaimsCutoverService(
            harness.Context, new FixedReadiness(true),
            NewScheduler(harness), harness.Configuration, harness.Clock);
        Assert.Equal(
            WorkerClaimsCutoverStatus.Succeeded,
            (await ready.ExecuteCutoverAsync()).Status);
        Assert.Equal(
            WorkerClaimsCutoverStatus.AlreadyComplete,
            (await ready.ExecuteCutoverAsync()).Status);

        var markers = await harness.Context.CutoverMarkers
            .Where(marker =>
                marker.Id == ProviderInstanceSchema.MarkerRecordId &&
                marker.Name == ProviderInstanceSchema.WorkerClaimsCutoverMarkerName)
            .ToListAsync();
        var marker = Assert.Single(markers);
        Assert.True(marker.IsComplete);
        Assert.Equal(ProviderInstanceSchema.CurrentVersion, marker.Version);
    }

    [Fact]
    public async Task Cutover_RejectsIncompatibleMarkerVersion()
    {
        await using var harness = await Harness.CreateWithDefaultsAsync(new Dictionary<string, string?>
        {
            [SearchPlatformFeatureFlagNames.WorkerClaimsEnabled] = "true"
        });
        harness.Context.CutoverMarkers.Add(new CutoverMarker
        {
            Id = ProviderInstanceSchema.MarkerRecordId,
            Name = ProviderInstanceSchema.WorkerClaimsCutoverMarkerName,
            Version = ProviderInstanceSchema.CurrentVersion - 1,
            IsComplete = false,
            UpdatedUtc = DateTime.UtcNow
        });
        await harness.Context.SaveChangesAsync();

        var cutover = new WorkerClaimsCutoverService(
            harness.Context, new FixedReadiness(true),
            NewScheduler(harness), harness.Configuration, harness.Clock);

        Assert.Equal(
            WorkerClaimsCutoverStatus.NotReady,
            (await cutover.ExecuteCutoverAsync()).Status);
    }

    private static SqliteCredentialScheduler NewScheduler(Harness harness) =>
        new(
            harness.Context,
            new LeasePolicyOptions(),
            new ZeroJitterSource(),
            harness.Clock,
            NullLogger<SqliteCredentialScheduler>.Instance);

    // ── 16.3: fleet cutover ────────────────────────────────────────────────────

    [Fact]
    public async Task Bootstrap_IgnoresWorkerCredentialVariables()
    {
        await using var harness = await Harness.CreateWithDefaultsAsync(new Dictionary<string, string?>
        {
            [SearchPlatformFeatureFlagNames.EnvironmentBootstrapEnabled] = "true",
            ["MASTER_GITHUB_TOKENS"] = "ghp_fleet_master_canary_001",
            ["WORKER_GITHUB_TOKENS"] = "ghp_fleet_worker_canary_002",
            ["WORKER_GITLAB_TOKENS"] = "glpat-fleet-worker-canary-003"
        });

        var bootstrap = await new EnvironmentBootstrapService(
            harness.Context, harness.Configuration, harness.Storage,
            new CredentialStateService(), harness.Clock,
            new EnvironmentBootstrapReadinessState(), new CredentialMutationGate())
            .RunAsync();

        Assert.True(bootstrap.IsEnabled);
        var entryIds = await harness.Context.SearchProviderTokens
            .AsNoTracking()
            .Select(credential => credential.SourceEntryId!)
            .ToListAsync();
        Assert.Equal("master-github-0001", Assert.Single(entryIds));
    }

    [Fact]
    public void WorkerSources_ReadNoCredentialVariables()
    {
        foreach (var fileName in new[]
                 {
                     "WorkerCycleRunner.cs",
                     "MasterApiClient.cs",
                     "WorkerScraperHostedService.cs",
                     "WorkerSecretExtractor.cs",
                     "EnvironmentBootstrapService.cs"
                 })
        {
            var source = ReadServiceSource(fileName);
            Assert.DoesNotContain("configuration[\"WORKER_", source, StringComparison.Ordinal);
            Assert.DoesNotContain("GetEnvironmentVariable(\"WORKER_", source, StringComparison.Ordinal);
        }
    }

    // ── 16.4: protected reads + scrub ──────────────────────────────────────────

    [Fact]
    public async Task Scrub_ClearsPlaintextOnlyAfterProtection()
    {
        await using var harness = await Harness.CreateWithDefaultsAsync(
            new Dictionary<string, string?>());
        var gitHub = await harness.Context.SearchProviderInstances
            .SingleAsync(i => i.StableId == ProviderInstanceSchema.DefaultGitHubStableId);

        var credential = harness.Storage.CreateProtectedCredential(
            "ghp_scrub_canary_001", gitHub, CredentialSource.Manual);
        harness.Context.SearchProviderTokens.Add(credential);
        await harness.Context.SaveChangesAsync();
        credential.Token = "ghp_scrub_legacy_plaintext_001";
        await harness.Context.SaveChangesAsync();

        var migration = new CredentialPlaintextMigrationService(
            harness.Context, harness.Storage, harness.Guard,
            harness.MaterialAccess, harness.Clock, new CredentialMutationGate());

        var dirty = await migration.VerifyNoPlaintextRetainedAsync();
        Assert.False(dirty.Clean);
        Assert.Equal(1, dirty.RowsWithPlaintext);

        var scrubbed = await migration.ScrubLegacyPlaintextAsync();
        Assert.Equal(1, scrubbed.RowsScrubbed);

        harness.Context.ChangeTracker.Clear();
        Assert.True((await migration.VerifyNoPlaintextRetainedAsync()).Clean);

        var grant = await harness.MaterialAccess.GrantStoredCredentialAfterCommitAsync(
            credential.StableId, Guid.NewGuid());
        using var material = await harness.MaterialAccess.DecryptGrantedAsync(grant);
        Assert.Equal("ghp_scrub_canary_001", material.Value);
    }

    [Fact]
    public async Task Scrub_BlockedWhileUnprotectedPlaintextRemains()
    {
        await using var harness = await Harness.CreateWithDefaultsAsync(
            new Dictionary<string, string?>());
        var gitHub = await harness.Context.SearchProviderInstances
            .SingleAsync(i => i.StableId == ProviderInstanceSchema.DefaultGitHubStableId);

        harness.Context.SearchProviderTokens.Add(new SearchProviderToken
        {
            StableId = Guid.NewGuid(),
            Token = "ghp_unprotected_plaintext_001",
            SearchProvider = SearchProviderEnum.GitHub,
            ProviderInstanceId = gitHub.Id,
            Source = CredentialSource.Manual,
            IsEnabled = true,
            Revision = 0,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        });
        await harness.Context.SaveChangesAsync();

        var migration = new CredentialPlaintextMigrationService(
            harness.Context, harness.Storage, harness.Guard,
            harness.MaterialAccess, harness.Clock, new CredentialMutationGate());

        await Assert.ThrowsAsync<CredentialProtectionException>(
            () => migration.ScrubLegacyPlaintextAsync());
    }

    // ── 16.5: rollback on both sides ───────────────────────────────────────────

    [Fact]
    public void PreScrubRollback_RetainsAdditiveSchemaAndLegacyColumn()
    {
        // Rollback to a pre-scrub release keeps every additive surface (new tables plus
        // the retained legacy Token column) without restoring credential sync/fallback.
        var model = HarnessModel.Create();
        Assert.NotNull(model.FindEntityType(typeof(PublicSearchConsent)));
        Assert.NotNull(model.FindEntityType(typeof(CredentialGrant)));
        Assert.NotNull(model.FindEntityType(typeof(CredentialClaimRecord)));

        var tokenProperty = model.FindEntityType(typeof(SearchProviderToken))!
            .FindProperty(nameof(SearchProviderToken.Token));
        Assert.NotNull(tokenProperty);

        foreach (var type in new[] { typeof(NodeSyncDTO), typeof(CredentialClaimRequest) })
        {
            foreach (var property in type.GetProperties())
            {
                Assert.DoesNotContain("credential", property.Name, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("secret", property.Name, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    private static class HarnessModel
    {
        public static Microsoft.EntityFrameworkCore.Metadata.IModel Create()
        {
            using var context = new DBContext(
                new DbContextOptionsBuilder<DBContext>()
                    .UseSqlite("Data Source=:memory:")
                    .Options);
            return context.Model;
        }
    }

    [Fact]
    public async Task PostScrubRollback_DrainsLeasesWithoutDestroyingState()
    {
        await using var harness = await Harness.CreateWithDefaultsAsync(
            new Dictionary<string, string?>());
        var gitHub = await harness.Context.SearchProviderInstances
            .SingleAsync(i => i.StableId == ProviderInstanceSchema.DefaultGitHubStableId);

        foreach (var material in new[] { "ghp_rollback_canary_001", "ghp_rollback_canary_002" })
        {
            var created = harness.Storage.CreateProtectedCredential(
                material, gitHub, CredentialSource.Manual);
            harness.Context.SearchProviderTokens.Add(created);
        }
        await harness.Context.SaveChangesAsync();

        var scheduler = NewScheduler(harness);
        var workService = new WorkService(harness.Context);
        var requestIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        for (var i = 0; i < 2; i++)
        {
            var work = await workService.CreateWorkItemAsync(
                gitHub.Id, SearchProviderEnum.GitHub, CredentialGrantScope.Admin, null,
                new SearchQuerySnapshot("SECRET", null, "{}"), "test-v1");
            var claim = await scheduler.TryClaimAsync(
                gitHub.StableId, work.WorkItemStableId, "default",
                "rollback-node", requestIds[i], CredentialGrantScope.Admin, null);
            Assert.True(claim.IsSuccess);
        }

        var cutover = new WorkerClaimsCutoverService(
            harness.Context, new FixedReadiness(true),
            scheduler, harness.Configuration, harness.Clock);
        var drained = await cutover.DrainActiveLeasesForRollbackAsync();
        Assert.Equal(2, drained.LeasesDrained);
        Assert.Equal(0, drained.LeasesSkipped);
        Assert.Equal(0, await harness.Context.CredentialClaimRecords.CountAsync(r => !r.IsTerminal));

        // No destructive downgrade: credential rows survive enabled with history intact.
        Assert.Equal(2, await harness.Context.SearchProviderTokens.CountAsync());
        Assert.All(
            await harness.Context.SearchProviderTokens.ToListAsync(),
            credential => Assert.True(credential.IsEnabled));

        var repeat = await cutover.DrainActiveLeasesForRollbackAsync();
        Assert.Equal(0, repeat.LeasesDrained);
        Assert.Equal(0, repeat.LeasesSkipped);
    }
}

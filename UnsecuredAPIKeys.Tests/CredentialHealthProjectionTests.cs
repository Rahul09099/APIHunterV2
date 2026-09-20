using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using UnsecuredAPIKeys.Data;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Data.Models;
using UnsecuredAPIKeys.Providers._Interfaces;
using UnsecuredAPIKeys.Services;
using Xunit;

namespace UnsecuredAPIKeys.Tests;

/// <summary>
/// Tasks 15.1/15.2 — Role-filtered credential/provider health and audited command tests.
/// Proves every health field, admin-only Lease ownership, aggregate provider health,
/// atomic re-enable/replacement, non-admin principal filtering, fleet-wide admin
/// projection, and secret-free views.
/// </summary>
public sealed class CredentialHealthProjectionTests
{
    private const long UserTelegramId = 77001;
    private const long AdminTelegramId = 999;

    private static readonly SchedulerPrincipal User =
        SchedulerPrincipal.ForTelegram(UserTelegramId, isAdministrator: false);

    private static readonly SchedulerPrincipal Admin =
        SchedulerPrincipal.ForTelegram(AdminTelegramId, isAdministrator: true);

    private sealed class FixedClock(DateTime utcNow) : IDatabaseUtcClock
    {
        public Task<DateTime> GetUtcNowAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(utcNow);
    }

    private sealed class ZeroJitterSource : ISchedulerJitterSource
    {
        public TimeSpan Next(TimeSpan maxJitter) => TimeSpan.Zero;
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        public Harness(
            SqliteConnection connection,
            DBContext context,
            DatabaseService databaseService,
            CredentialStorageService storage)
        {
            this.connection = connection;
            Context = context;
            DatabaseService = databaseService;
            Clock = new FixedClock(DateTime.UtcNow);
            Health = new CredentialHealthProjectionService(
                context, new CredentialGrantEvaluator(context));
            Management = new CredentialManagementService(
                context,
                new PrivilegePolicy(),
                new AuditReasonSanitizer(),
                Clock,
                new CredentialStateService(),
                storage,
                new CredentialMutationGate());
        }

        public DBContext Context { get; }
        public DatabaseService DatabaseService { get; }
        public FixedClock Clock { get; }
        public CredentialHealthProjectionService Health { get; }
        public CredentialManagementService Management { get; }
        public Guid GitHubToken { get; set; }
        public Guid GitLabToken { get; set; }
        public Guid AdminGitHubToken { get; set; }

        public static async Task<Harness> CreateAsync()
        {
            var sqlConnection = new SqliteConnection("Data Source=:memory:");
            await sqlConnection.OpenAsync();
            var context = new DBContext(
                new DbContextOptionsBuilder<DBContext>()
                    .UseSqlite(sqlConnection)
                    .Options);
            await context.Database.EnsureCreatedAsync();

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["SearchCredentials:Protection:ActiveKeyVersion"] = "7",
                    ["SearchCredentials:Protection:Keys:7"] = Convert.ToBase64String(
                        Enumerable.Repeat((byte)0x37, 32).ToArray()),
                    ["SearchCredentials:Fingerprint:ActiveKeyVersion"] = "11",
                    ["SearchCredentials:Fingerprint:Keys:11"] = Convert.ToBase64String(
                        Enumerable.Repeat((byte)0x5B, 32).ToArray())
                })
                .Build();
            var protection = new CredentialProtectionService(configuration);
            var fingerprint = new CredentialFingerprintService(configuration);
            var guard = new CredentialStorageMigrationGuard(configuration);
            var storage = new CredentialStorageService(protection, fingerprint, guard);
            var databaseService = new DatabaseService(context, storage);

            var now = DateTime.UtcNow;
            context.SearchProviderInstances.AddRange(
                NewInstance(
                    ProviderInstanceSchema.DefaultGitHubStableId, SearchProviderEnum.GitHub,
                    "GitHub SaaS", "api.github.com", 443, "/", now),
                NewInstance(
                    ProviderInstanceSchema.DefaultGitLabStableId, SearchProviderEnum.GitLab,
                    "GitLab SaaS", "gitlab.com", 443, "/api/v4", now));
            await context.SaveChangesAsync();

            var harness = new Harness(sqlConnection, context, databaseService, storage);
            harness.GitHubToken = (await databaseService.AddGitHubTokenAsync(
                context, "ghp_health_user_canary_001", addedBy: UserTelegramId)).StableId;
            harness.GitLabToken = (await databaseService.AddGitLabTokenAsync(
                context, "glpat-health-admin-canary-001")).StableId;
            harness.AdminGitHubToken = (await databaseService.AddGitHubTokenAsync(
                context, "ghp_health_admin_canary_002")).StableId;
            return harness;
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

    [Fact]
    public async Task NonAdmin_SeesOnlyGrantedCredentialsWithoutLeaseOwners()
    {
        await using var harness = await Harness.CreateAsync();

        var items = await harness.Health.GetCredentialsAsync(
            isAdmin: false, telegramPrincipalId: UserTelegramId);

        var item = Assert.Single(items);
        Assert.Equal(harness.GitHubToken, item.StableId);
        Assert.Equal(SearchProviderEnum.GitHub, item.ProviderKind);
        Assert.Equal(item.StableId.ToString("N")[..12], item.Alias);
        Assert.True(item.IsEnabled);
        Assert.Null(item.LeaseOwnerNodeId);
        Assert.Null(item.LeaseExpiresUtc);
    }

    [Fact]
    public async Task NonAdmin_WithoutTelegram_SeesNothing()
    {
        await using var harness = await Harness.CreateAsync();

        Assert.Empty(await harness.Health.GetCredentialsAsync(isAdmin: false, telegramPrincipalId: null));
        Assert.Empty(await harness.Health.GetProvidersAsync(isAdmin: false, telegramPrincipalId: null));
    }

    [Fact]
    public async Task Admin_SeesFleetWithLeaseOwners()
    {
        await using var harness = await Harness.CreateAsync();

        var instance = await harness.Context.SearchProviderInstances
            .SingleAsync(i => i.StableId == ProviderInstanceSchema.DefaultGitHubStableId);
        var work = await new WorkService(harness.Context).CreateWorkItemAsync(
            instance.Id, SearchProviderEnum.GitHub, CredentialGrantScope.Admin, null,
            new SearchQuerySnapshot("SECRET", null, "{}"), "test-v1");
        var scheduler = new SqliteCredentialScheduler(
            harness.Context, new LeasePolicyOptions(), new ZeroJitterSource(),
            harness.Clock, NullLogger<SqliteCredentialScheduler>.Instance);
        var claim = await scheduler.TryClaimAsync(
            instance.StableId, work.WorkItemStableId, "default",
            "health-owner-node", Guid.NewGuid(), CredentialGrantScope.Admin, null);
        Assert.True(claim.IsSuccess);

        var items = await harness.Health.GetCredentialsAsync(isAdmin: true, telegramPrincipalId: null);

        Assert.Equal(3, items.Count);
        var leased = items.Single(item => item.LeaseOwnerNodeId is not null);
        Assert.Equal("health-owner-node", leased.LeaseOwnerNodeId);
        Assert.NotNull(leased.LeaseExpiresUtc);
        Assert.All(items, item => Assert.Equal(
            item.StableId.ToString("N")[..12], item.Alias));
    }

    [Fact]
    public async Task Providers_NonAdmin_FiltersToVisibleInstances()
    {
        await using var harness = await Harness.CreateAsync();

        var items = await harness.Health.GetProvidersAsync(
            isAdmin: false, telegramPrincipalId: UserTelegramId);

        var item = Assert.Single(items);
        Assert.Equal(ProviderInstanceSchema.DefaultGitHubStableId, item.InstanceStableId);
        Assert.Equal(2, item.TotalCredentials);
        Assert.Empty(item.ActiveLeaseOwners);
    }

    [Fact]
    public async Task Providers_Admin_AggregatesFleetHealth()
    {
        await using var harness = await Harness.CreateAsync();

        var items = await harness.Health.GetProvidersAsync(isAdmin: true, telegramPrincipalId: null);

        Assert.Equal(2, items.Count);
        var gitHub = items.Single(item =>
            item.InstanceStableId == ProviderInstanceSchema.DefaultGitHubStableId);
        Assert.Equal(2, gitHub.TotalCredentials);
        Assert.Equal(2, gitHub.EnabledCredentials);
        Assert.Equal(0, gitHub.ActiveLeases);
        Assert.Equal(0, gitHub.CooldownCredentials);
        Assert.True(gitHub.IsEnabled);
    }

    [Fact]
    public async Task Reenable_ClearsReasonAndTimeAtomicallyWithAudit()
    {
        await using var harness = await Harness.CreateAsync();

        var disabled = await harness.Management.DisableAsync(
            harness.GitHubToken, Admin, "Compromise suspected.");
        Assert.Equal(CredentialManagementStatus.Succeeded, disabled.Status);

        harness.Context.ChangeTracker.Clear();
        var mid = await harness.Context.SearchProviderTokens
            .SingleAsync(t => t.StableId == harness.GitHubToken);
        Assert.False(mid.IsEnabled);
        Assert.NotNull(mid.DisabledReason);
        Assert.NotNull(mid.DisabledAtUtc);

        var reenabled = await harness.Management.ReenableAsync(
            harness.GitHubToken, Admin, "Cleared after rotation.");
        Assert.Equal(CredentialManagementStatus.Succeeded, reenabled.Status);

        harness.Context.ChangeTracker.Clear();
        var after = await harness.Context.SearchProviderTokens
            .SingleAsync(t => t.StableId == harness.GitHubToken);
        Assert.True(after.IsEnabled);
        Assert.Null(after.DisabledReason);
        Assert.Null(after.DisabledAtUtc);

        Assert.Equal(2, await harness.Context.PrivilegedAuditRecords.CountAsync(
            audit => audit.TargetStableId == harness.GitHubToken));
    }

    [Fact]
    public async Task Replace_CreatesNewIdentityAndArchivesOld()
    {
        await using var harness = await Harness.CreateAsync();

        var replaced = await harness.Management.ReplaceAsync(
            harness.GitHubToken, "ghp_health_replacement_canary_003",
            Admin, "Scheduled rotation.");
        Assert.Equal(CredentialManagementStatus.Succeeded, replaced.Status);
        var replacementId = Assert.NotNull(replaced.ReplacementStableId);
        Assert.NotEqual(harness.GitHubToken, replacementId);

        harness.Context.ChangeTracker.Clear();
        var old = await harness.Context.SearchProviderTokens
            .SingleAsync(t => t.StableId == harness.GitHubToken);
        Assert.True(old.IsArchived);
        var fresh = await harness.Context.SearchProviderTokens
            .SingleAsync(t => t.StableId == replacementId);
        Assert.True(fresh.IsEnabled);
        Assert.False(fresh.IsArchived);
    }

    [Fact]
    public void HealthAndReadinessViews_CarryNoSecretMembers()
    {
        var forbiddenFragments = new[] { "token", "secret", "password", "fingerprint", "material" };

        foreach (var type in new[]
                 {
                     typeof(CredentialHealthItem),
                     typeof(ProviderHealthItem),
                     typeof(SchedulingReadinessProjection),
                     typeof(MetricSample)
                 })
        {
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                foreach (var fragment in forbiddenFragments)
                {
                    // LeaseOwnerNodeId names node ownership, never node authentication material.
                    if (property.Name == nameof(CredentialHealthItem.LeaseOwnerNodeId))
                    {
                        continue;
                    }

                    Assert.DoesNotContain(fragment, property.Name, StringComparison.OrdinalIgnoreCase);
                }
            }
        }
    }

    [Fact]
    public void HealthSnapshot_Serialization_ContainsNoCredentialMaterial()
    {
        var item = new CredentialHealthItem(
            Guid.NewGuid(), SearchProviderEnum.GitHub, Guid.NewGuid(),
            "deadbeefcafe", CredentialSource.Manual, true, null, null,
            DateTime.UtcNow, null, null, 0, "Success", null, null);
        var serialized = JsonSerializer.Serialize(new[] { item });

        Assert.DoesNotContain("ghp_health_user_canary_001", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('a', 64), serialized, StringComparison.Ordinal);
    }
}

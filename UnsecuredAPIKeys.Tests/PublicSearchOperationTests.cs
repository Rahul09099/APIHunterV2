using System.Net;
using System.Runtime.CompilerServices;
using System.Text;

using FsCheck.Xunit;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using UnsecuredAPIKeys.Data;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Data.DTOs;
using UnsecuredAPIKeys.Data.Models;
using UnsecuredAPIKeys.Providers;
using UnsecuredAPIKeys.Providers._Interfaces;
using UnsecuredAPIKeys.Providers.Search_Providers;
using UnsecuredAPIKeys.Services;
using Xunit;

namespace UnsecuredAPIKeys.Tests;

/// <summary>
/// Tasks 14.1/14.2 — Consent and credential-free public-operation tests.
/// Proves the dual gate (deny-oriented projection + effective capability + exact-instance
/// consent), revocation blocking, zero credential involvement, exact Slot replay, shared
/// capacity, and full Work/checkpoint/provenance parity.
/// Validates AC-1.21–AC-1.26, AC-2.10, AC-2.15–AC-2.22, AC-5.55–AC-5.62, AC-8.17–AC-8.19.
/// </summary>
public sealed class PublicSearchOperationTests
{
    private static readonly SchedulerPrincipal Admin =
        SchedulerPrincipal.ForTelegram(999, isAdministrator: true);

    private static readonly SchedulerPrincipal SystemPrincipal = SchedulerPrincipal.System;

    private sealed class FixedClock(DateTime utcNow) : IDatabaseUtcClock
    {
        public Task<DateTime> GetUtcNowAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(utcNow);
    }

    private sealed class ZeroJitterSource : ISchedulerJitterSource
    {
        public TimeSpan Next(TimeSpan maxJitter) => TimeSpan.Zero;
    }

    private sealed class PublicFakeAdapter(
        SearchProviderEnum kind,
        SearchProviderCapability declared) : ISearchProviderAdapter
    {
        public SearchProviderEnum ProviderKind => kind;
        public string AdapterVersion => "public-fake-v1";
        public SearchProviderCapability DeclaredCapabilities => declared;
        public ProviderSettingsSchema SettingsSchema => ProviderSettingsSchema.Empty;

        public Task<ProviderCapabilityResult> DiscoverCapabilitiesAsync(
            ValidatedProviderInstance instance, CancellationToken cancellationToken) =>
            Task.FromResult(new ProviderCapabilityResult(ProviderOutcomeKind.Success, declared));

        public Task<ProviderOperationResult<CredentialValidationResult, ProviderOutcomeKind>> ValidateCredentialAsync(
            ProviderOperationContext context, CredentialMaterial credential, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Public fake never validates credentials.");

        public Task<TranslatedProviderQuery> TranslateQueryAsync(
            ProviderOperationContext context, SearchQuerySnapshot query, CancellationToken cancellationToken) =>
            Task.FromResult(new TranslatedProviderQuery(query.GenericQuery, query.SettingsJson));

        public async IAsyncEnumerable<ProviderOperationResult<NormalizedSearchPage, ProviderOutcomeKind>> SearchAsync(
            ProviderOperationContext context,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            var instanceStable = kind == SearchProviderEnum.GitHub
                ? ProviderInstanceSchema.DefaultGitHubStableId
                : ProviderInstanceSchema.DefaultGitLabStableId;
            var input = new ProviderResultInput(
                ProviderKind: kind,
                ProviderInstanceStableId: instanceStable,
                RepositoryStableId: "repo-1",
                ImmutableRevisionOrEquivalentVersion: "sha-1",
                NormalizedFilePath: "/public/file.txt",
                RepositoryOwner: "octo",
                RepositoryName: "demo",
                Snippet: "public snippet",
                ProvenanceUrl: "https://example.test/public/file.txt",
                Branch: "main");
            yield return new ProviderOperationResult<NormalizedSearchPage, ProviderOutcomeKind>(
                ProviderOutcomeKind.Success,
                new NormalizedSearchPage(new object[] { input }),
                Continuation: null);
        }

        public Task<ProviderOperationResult<NormalizedContent, ProviderOutcomeKind>> FetchContentAsync(
            ProviderOperationContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Public fake never fetches content.");
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        public Harness(SqliteConnection connection, DBContext context)
        {
            this.connection = connection;
            Context = context;
            Clock = new FixedClock(DateTime.UtcNow);
            Consent = new PublicSearchConsentService(
                context,
                new PrivilegePolicy(),
                new AuditReasonSanitizer(),
                Clock);
        }

        public DBContext Context { get; }
        public FixedClock Clock { get; }
        public PublicSearchConsentService Consent { get; }
        public SearchProviderInstance GitHub { get; set; } = null!;
        public SearchProviderInstance GitLab { get; set; } = null!;

        public static async Task<Harness> CreateAsync()
        {
            var sqlConnection = new SqliteConnection("Data Source=:memory:");
            await sqlConnection.OpenAsync();
            var context = new DBContext(
                new DbContextOptionsBuilder<DBContext>()
                    .UseSqlite(sqlConnection)
                    .Options);
            await context.Database.EnsureCreatedAsync();

            var now = DateTime.UtcNow;
            var harness = new Harness(sqlConnection, context)
            {
                GitHub = AddInstance(
                    context, ProviderInstanceSchema.DefaultGitHubStableId,
                    SearchProviderEnum.GitHub, "GitHub SaaS",
                    "api.github.com", 443, "/", now),
                GitLab = AddInstance(
                    context, ProviderInstanceSchema.DefaultGitLabStableId,
                    SearchProviderEnum.GitLab, "GitLab SaaS",
                    "gitlab.com", 443, "/api/v4", now)
            };
            await context.SaveChangesAsync();
            return harness;
        }

        private static SearchProviderInstance AddInstance(
            DBContext context,
            Guid stableId,
            SearchProviderEnum kind,
            string displayName,
            string host,
            int port,
            string basePath,
            DateTime now)
        {
            var instance = new SearchProviderInstance
            {
                StableId = stableId,
                ProviderKind = kind,
                DisplayName = displayName,
                NormalizedScheme = "https",
                NormalizedHost = host,
                NormalizedPort = port,
                NormalizedBasePath = basePath,
                IsEnabled = true,
                AllowGlobalPublicSearch = true,
                MaxConcurrentOperations = 4,
                SettingsVersion = 1,
                SettingsJson = "{}",
                PrivateNetworkAllowlistJson = "[]",
                CreatedUtc = now,
                UpdatedUtc = now
            };
            context.SearchProviderInstances.Add(instance);
            return instance;
        }

        public SearchProviderAdapterRegistry RegistryFor(
            SearchProviderEnum kind,
            SearchProviderCapability declared) =>
            new([new PublicFakeAdapter(kind, declared)]);

        public PublicOperationSlotService SlotsFor(SearchProviderEnum kind, SearchProviderCapability declared) =>
            new(Context, Consent, RegistryFor(kind, declared), Clock);

        public PublicSearchOperationService OperationsFor(
            SearchProviderEnum kind, SearchProviderCapability declared) =>
            new(
                Context,
                RegistryFor(kind, declared),
                new WorkService(Context),
                SlotsFor(kind, declared),
                new ResultPersistenceService(Context));

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private static SearchProviderCapability PublicDeclared =>
        SearchProviderCapability.CodeSearch |
        SearchProviderCapability.PaginatedSearch |
        SearchProviderCapability.ContentRetrieval |
        SearchProviderCapability.GlobalPublicSearch;

    // ── 14.1: dual-gate property ───────────────────────────────────────────────

    /// <summary>
    /// **Validates: AC-1.25–AC-1.26, AC-2.15–AC-2.22, AC-12.28.**
    /// The gate holds exactly when all three independent inputs hold.
    /// </summary>
    [Property(MaxTest = 256)]
    public bool GateHoldsExactlyWhenProjectionCapabilityAndConsentHold(
        bool allowGlobal,
        bool consentActive,
        int declaredBits,
        int discoveredBits,
        bool discoverySucceeded)
    {
        var declared = (SearchProviderCapability)(declaredBits & 0x1FF);
        var discovered = (SearchProviderCapability)(discoveredBits & 0x1FF);

        var actual = PublicOperationSlotService.EvaluateGate(
            allowGlobal, consentActive, declared, discovered, discoverySucceeded);

        var expected = allowGlobal &&
                       consentActive &&
                       discoverySucceeded &&
                       declared.HasFlag(SearchProviderCapability.GlobalPublicSearch) &&
                       discovered.HasFlag(SearchProviderCapability.GlobalPublicSearch);

        return actual == expected;
    }

    // ── Consent lifecycle ──────────────────────────────────────────────────────

    [Fact]
    public async Task Consent_GrantAndRevoke_PersistStateAndAudit()
    {
        await using var harness = await Harness.CreateAsync();

        var granted = await harness.Consent.GrantAsync(
            harness.GitHub.StableId, Admin, "Enable public research access.");
        Assert.Equal(PublicSearchConsentStatus.Succeeded, granted.Status);
        Assert.True(granted.IsActive);
        Assert.True(await harness.Consent.IsConsentedAsync(harness.GitHub.StableId));

        var regranted = await harness.Consent.GrantAsync(
            harness.GitHub.StableId, Admin, "Re-affirm public research access.");
        Assert.Equal(PublicSearchConsentStatus.Succeeded, regranted.Status);
        Assert.Equal(1, await harness.Context.PublicSearchConsents.CountAsync());

        var revoked = await harness.Consent.RevokeAsync(
            harness.GitHub.StableId, Admin, "Revoke after incident review.");
        Assert.False(revoked.IsActive);
        Assert.False(await harness.Consent.IsConsentedAsync(harness.GitHub.StableId));

        var audits = await harness.Context.PrivilegedAuditRecords
            .Where(audit =>
                audit.Action == PrivilegedActionKind.PublicSearchConsent &&
                audit.TargetStableId == harness.GitHub.StableId)
            .ToListAsync();
        Assert.Equal(3, audits.Count);
        Assert.All(audits, audit => Assert.Equal(PrivilegedActionOutcome.Succeeded, audit.Outcome));
        Assert.All(audits, audit => Assert.Equal(999, audit.ActorTelegramId));
    }

    [Fact]
    public async Task Consent_NonAdministrator_IsRejected()
    {
        await using var harness = await Harness.CreateAsync();

        await Assert.ThrowsAsync<PrivilegeAuthorizationException>(() =>
            harness.Consent.GrantAsync(
                harness.GitHub.StableId,
                SchedulerPrincipal.ForTelegram(77001, isAdministrator: false),
                "Non-admin attempt."));

        Assert.False(await harness.Consent.IsConsentedAsync(harness.GitHub.StableId));
    }

    [Fact]
    public async Task Consent_UnknownInstance_ReturnsNotFound()
    {
        await using var harness = await Harness.CreateAsync();

        var result = await harness.Consent.GrantAsync(Guid.NewGuid(), Admin, "Unknown instance.");

        Assert.Equal(PublicSearchConsentStatus.NotFound, result.Status);
    }

    // ── Public slot acquisition ────────────────────────────────────────────────

    private static async Task<Guid> CreateWorkStableAsync(Harness harness, SearchProviderEnum kind)
    {
        var instance = kind == SearchProviderEnum.GitHub ? harness.GitHub : harness.GitLab;
        var work = await new WorkService(harness.Context).CreateWorkItemAsync(
            instance.Id, kind, CredentialGrantScope.Admin, null,
            new SearchQuerySnapshot("SECRET", null, "{}"), "public-fake-v1");
        return work.WorkItemStableId;
    }

    [Fact]
    public async Task Acquire_WithoutConsent_IsDenied()
    {
        await using var harness = await Harness.CreateAsync();
        var slots = harness.SlotsFor(SearchProviderEnum.GitHub, PublicDeclared);
        var workStableId = await CreateWorkStableAsync(harness, SearchProviderEnum.GitHub);

        var result = await slots.TryAcquireAsync(
            harness.GitHub.StableId, workStableId, "default",
            Guid.NewGuid(), CredentialGrantScope.Admin, null);

        Assert.False(result.Succeeded);
        Assert.Contains("consent", result.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(harness.Context.OperationSlots);
    }

    [Fact]
    public async Task Acquire_WithoutCapability_IsDenied()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.Consent.GrantAsync(harness.GitHub.StableId, Admin, "Consent without capability.");
        var slots = harness.SlotsFor(
            SearchProviderEnum.GitHub,
            SearchProviderCapability.CodeSearch | SearchProviderCapability.PaginatedSearch);
        var workStableId = await CreateWorkStableAsync(harness, SearchProviderEnum.GitHub);

        var result = await slots.TryAcquireAsync(
            harness.GitHub.StableId, workStableId, "default",
            Guid.NewGuid(), CredentialGrantScope.Admin, null);

        Assert.False(result.Succeeded);
        Assert.Contains("capability", result.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(harness.Context.OperationSlots);
    }

    [Fact]
    public async Task Acquire_ProjectionDenied_IsDeniedDespiteConsent()
    {
        await using var harness = await Harness.CreateAsync();
        harness.GitHub.AllowGlobalPublicSearch = false;
        await harness.Context.SaveChangesAsync();
        await harness.Consent.GrantAsync(harness.GitHub.StableId, Admin, "Consent with projection denied.");
        var slots = harness.SlotsFor(SearchProviderEnum.GitHub, PublicDeclared);
        var workStableId = await CreateWorkStableAsync(harness, SearchProviderEnum.GitHub);

        var result = await slots.TryAcquireAsync(
            harness.GitHub.StableId, workStableId, "default",
            Guid.NewGuid(), CredentialGrantScope.Admin, null);

        Assert.False(result.Succeeded);
        Assert.Empty(harness.Context.OperationSlots);
    }

    [Fact]
    public async Task Acquire_RevocationBlocksNewWork()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.Consent.GrantAsync(harness.GitHub.StableId, Admin, "Temporary consent.");
        var slots = harness.SlotsFor(SearchProviderEnum.GitHub, PublicDeclared);
        var workStableId = await CreateWorkStableAsync(harness, SearchProviderEnum.GitHub);
        var requestId = Guid.NewGuid();

        var first = await slots.TryAcquireAsync(
            harness.GitHub.StableId, workStableId, "default",
            requestId, CredentialGrantScope.Admin, null);
        Assert.True(first.Succeeded);

        await harness.Consent.RevokeAsync(harness.GitHub.StableId, Admin, "Revoked.");

        var second = await slots.TryAcquireAsync(
            harness.GitHub.StableId, workStableId, "default",
            Guid.NewGuid(), CredentialGrantScope.Admin, null);
        Assert.False(second.Succeeded);
    }

    [Fact]
    public async Task Acquire_UsesNoCredential_SelectsNoLease()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.Consent.GrantAsync(harness.GitHub.StableId, Admin, "Public research.");
        var slots = harness.SlotsFor(SearchProviderEnum.GitHub, PublicDeclared);
        var workStableId = await CreateWorkStableAsync(harness, SearchProviderEnum.GitHub);

        var result = await slots.TryAcquireAsync(
            harness.GitHub.StableId, workStableId, "default",
            Guid.NewGuid(), CredentialGrantScope.Admin, null);

        Assert.True(result.Succeeded);
        Assert.NotEqual(Guid.Empty, result.SlotId);
        Assert.False(result.IsReplay);
        Assert.NotNull(result.ValidatedInstance);
        Assert.NotNull(result.Bounds);
        Assert.Empty(harness.Context.SearchProviderTokens);
        Assert.Single(harness.Context.OperationSlots);
    }

    [Fact]
    public async Task Acquire_ExactReplay_ReturnsSameSlot()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.Consent.GrantAsync(harness.GitHub.StableId, Admin, "Public research.");
        var slots = harness.SlotsFor(SearchProviderEnum.GitHub, PublicDeclared);
        var workStableId = await CreateWorkStableAsync(harness, SearchProviderEnum.GitHub);
        var requestId = Guid.NewGuid();

        var first = await slots.TryAcquireAsync(
            harness.GitHub.StableId, workStableId, "default",
            requestId, CredentialGrantScope.Admin, null);
        var second = await slots.TryAcquireAsync(
            harness.GitHub.StableId, workStableId, "default",
            requestId, CredentialGrantScope.Admin, null);

        Assert.True(first.Succeeded);
        Assert.False(first.IsReplay);
        Assert.True(second.Succeeded);
        Assert.True(second.IsReplay);
        Assert.Equal(first.SlotId, second.SlotId);
        Assert.Equal(1, await harness.Context.OperationSlots.CountAsync());
    }

    [Fact]
    public async Task Acquire_SharesCapacityWithCredentialedLeases()
    {
        await using var harness = await Harness.CreateAsync();
        harness.GitHub.MaxConcurrentOperations = 1;
        await harness.Context.SaveChangesAsync();

        // Occupy the single shared slot through a credentialed scheduler Claim.
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
        var databaseService = new DatabaseService(harness.Context, storage);
        await databaseService.AddGitHubTokenAsync(harness.Context, "ghp_public_capacity_canary_001");

        var work = await new WorkService(harness.Context).CreateWorkItemAsync(
            harness.GitHub.Id, SearchProviderEnum.GitHub, CredentialGrantScope.Admin, null,
            new SearchQuerySnapshot("SECRET", null, "{}"), "github-metadata-v1");
        var scheduler = new SqliteCredentialScheduler(
            harness.Context,
            new LeasePolicyOptions(),
            new ZeroJitterSource(),
            harness.Clock,
            NullLogger<SqliteCredentialScheduler>.Instance);
        var claim = await scheduler.TryClaimAsync(
            harness.GitHub.StableId, work.WorkItemStableId, "default",
            "capacity-holder", Guid.NewGuid(), CredentialGrantScope.Admin, null);
        Assert.True(claim.IsSuccess);

        await harness.Consent.GrantAsync(harness.GitHub.StableId, Admin, "Public research.");
        var slots = harness.SlotsFor(SearchProviderEnum.GitHub, PublicDeclared);
        var publicWorkStableId = await CreateWorkStableAsync(harness, SearchProviderEnum.GitHub);

        var result = await slots.TryAcquireAsync(
            harness.GitHub.StableId, publicWorkStableId, "default",
            Guid.NewGuid(), CredentialGrantScope.Admin, null);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.RetryAfter);
    }

    // ── 14.2: parity slice ─────────────────────────────────────────────────────

    [Fact]
    public async Task PublicOperation_PersistsResultsCheckpointsAndCompletesSlot()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.Consent.GrantAsync(harness.GitHub.StableId, Admin, "Public research.");
        var operations = harness.OperationsFor(SearchProviderEnum.GitHub, PublicDeclared);

        var result = await operations.ExecuteAsync(
            SystemPrincipal,
            Guid.NewGuid(),
            new PublicOperationQuery(
                harness.GitHub.StableId, SearchProviderEnum.GitHub, "SECRET"));

        Assert.True(result.Succeeded);
        Assert.Equal(ProviderOutcomeKind.Success, result.Outcome);
        Assert.Equal(1, result.PersistedResults);
        Assert.NotNull(result.SlotId);

        harness.Context.ChangeTracker.Clear();
        Assert.Equal(1, await harness.Context.NormalizedResults.CountAsync());
        Assert.Equal(1, await harness.Context.ResultOutboxRecords.CountAsync(o => !o.IsProcessed));

        var partition = await harness.Context.WorkPartitions.SingleAsync();
        Assert.True(partition.IsTerminal);
        Assert.True(partition.IsComplete);

        var slot = await harness.Context.OperationSlots.SingleAsync();
        Assert.True(slot.IsTerminal);
        Assert.Empty(harness.Context.SearchProviderTokens);
    }

    [Fact]
    public async Task PublicOperation_SlotRenewAndComplete_Lifecycle()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.Consent.GrantAsync(harness.GitHub.StableId, Admin, "Public research.");
        var slots = harness.SlotsFor(SearchProviderEnum.GitHub, PublicDeclared);
        var workStableId = await CreateWorkStableAsync(harness, SearchProviderEnum.GitHub);
        var requestId = Guid.NewGuid();

        var acquired = await slots.TryAcquireAsync(
            harness.GitHub.StableId, workStableId, "default",
            requestId, CredentialGrantScope.Admin, null);
        Assert.True(acquired.Succeeded);

        var renewed = await slots.TryRenewAsync(
            acquired.SlotId, CredentialGrantScope.Admin, null, TimeSpan.FromMinutes(2));
        Assert.True(renewed.Succeeded);
        Assert.NotNull(renewed.NewExpiresUtc);

        var foreignRenew = await slots.TryRenewAsync(
            acquired.SlotId, CredentialGrantScope.User, 77001, TimeSpan.FromMinutes(2));
        Assert.False(foreignRenew.Succeeded);

        var completed = await slots.CompleteAsync(
            acquired.SlotId, CredentialGrantScope.Admin, null);
        Assert.True(completed.Succeeded);

        var replayed = await slots.CompleteAsync(
            acquired.SlotId, CredentialGrantScope.Admin, null);
        Assert.True(replayed.Succeeded);
    }
}

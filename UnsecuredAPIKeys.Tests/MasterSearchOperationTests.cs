using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using UnsecuredAPIKeys.Data;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Data.Models;
using UnsecuredAPIKeys.Providers;
using UnsecuredAPIKeys.Providers._Interfaces;
using UnsecuredAPIKeys.Providers.Search_Providers;
using UnsecuredAPIKeys.Services;
using Xunit;

namespace UnsecuredAPIKeys.Tests;

/// <summary>
/// Tasks 10.4 / 11.3 — Feature-flagged Master operation vertical slice.
/// Proves Grant → Work/query → Claim+Slot → adapter search/content →
/// result/provenance/checkpoint → completion through the durable services,
/// with no provider-specific branch (GitHub and GitLab execute the same method)
/// and the legacy path retained as the explicit pre-cutover fallback.
/// Validates AC-5.4–AC-5.6, AC-8.24–AC-8.26, AC-15.3–AC-15.4, AC-15.9–AC-15.10,
/// AC-15.22–AC-15.23, AC-16.3–AC-16.26.
/// </summary>
public sealed class MasterSearchOperationTests
{
    private const string GitHubSearchBody = """
        {"total_count":1,"items":[{"repository":{"id":123,"name":"demo","owner":{"login":"octo"}},"path":"app/.env","name":".env","sha":"abc123def456","html_url":"https://github.com/octo/demo/blob/abc123def456/app/.env","text_matches":[{"fragment":"SECRET=x"}]}]}
        """;

    private const string GitLabSearchBody = """
        [{"basename":".env","data":"SECRET=x","path":"config/.env","filename":".env","ref":"main","startline":3,"project_id":9999}]
        """;

    private sealed class DelegatingHandlerStub(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(responder(request));
        }
    }

    private sealed class ReadySchedulingReadiness : ISearchPlatformSchedulingReadinessService
    {
        public Task<ProviderInstanceReadinessReport> EvaluateAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProviderInstanceReadinessReport(
                DatabaseReady: true,
                SchemaReady: true,
                ProviderInstancesReady: true,
                MarkersReady: true,
                CoordinationMode: DatabaseCoordinationMode.SingleMaster,
                DistributedCoordinationReady: false,
                Failures: []));
    }

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
            IConfiguration configuration,
            MasterSearchOperationService operations)
        {
            this.connection = connection;
            Context = context;
            Configuration = configuration;
            Operations = operations;
        }

        public DBContext Context { get; }
        public IConfiguration Configuration { get; }
        public MasterSearchOperationService Operations { get; }

        public static async Task<Harness> CreateAsync(
            Func<HttpRequestMessage, HttpResponseMessage> gitHubResponder,
            Func<HttpRequestMessage, HttpResponseMessage> gitLabResponder,
            bool enableMigratedFlags = true,
            string? gitHubMaterial = "ghp_master_operation_canary_001",
            string? gitLabMaterial = "glpat-master-operation-canary-001")
        {
            var sqlConnection = new SqliteConnection("Data Source=:memory:");
            await sqlConnection.OpenAsync();
            var context = new DBContext(
                new DbContextOptionsBuilder<DBContext>()
                    .UseSqlite(sqlConnection)
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
                [SearchPlatformFeatureFlagNames.MasterScraperClaimsEnabled] =
                    enableMigratedFlags.ToString(),
                [SearchPlatformFeatureFlagNames.SchedulerEnabled] =
                    enableMigratedFlags.ToString()
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

            AddDefaultInstance(context, SearchProviderEnum.GitHub);
            AddDefaultInstance(context, SearchProviderEnum.GitLab);
            await context.SaveChangesAsync();

            if (gitHubMaterial is not null)
            {
                await databaseService.AddGitHubTokenAsync(context, gitHubMaterial);
            }
            if (gitLabMaterial is not null)
            {
                await databaseService.AddGitLabTokenAsync(context, gitLabMaterial);
            }

            var gitHubClient = new HttpClient(new DelegatingHandlerStub(gitHubResponder))
            {
                BaseAddress = new Uri("https://api.github.com/")
            };
            var gitLabClient = new HttpClient(new DelegatingHandlerStub(gitLabResponder))
            {
                BaseAddress = new Uri("https://gitlab.com/api/v4/")
            };
            var registry = new SearchProviderAdapterRegistry(
            [
                new GitHubSearchProviderAdapter(gitHubClient),
                new GitLabSearchProviderAdapter(gitLabClient)
            ]);

            var now = DateTime.UtcNow;
            var operations = new MasterSearchOperationService(
                context,
                registry,
                new WorkService(context),
                new SqliteCredentialScheduler(
                    context,
                    new LeasePolicyOptions(),
                    new ZeroJitterSource(),
                    new FixedClock(now),
                    NullLogger<SqliteCredentialScheduler>.Instance),
                new ResultPersistenceService(context),
                new CredentialGrantEvaluator(context),
                materialAccess,
                configuration,
                new ReadySchedulingReadiness());

            return new Harness(sqlConnection, context, configuration, operations);
        }

        private static void AddDefaultInstance(DBContext context, SearchProviderEnum kind)
        {
            var now = DateTime.UtcNow;
            context.SearchProviderInstances.Add(kind == SearchProviderEnum.GitHub
                ? new SearchProviderInstance
                {
                    StableId = ProviderInstanceSchema.DefaultGitHubStableId,
                    ProviderKind = kind,
                    DisplayName = "GitHub SaaS",
                    NormalizedScheme = "https",
                    NormalizedHost = "api.github.com",
                    NormalizedPort = 443,
                    NormalizedBasePath = "/",
                    IsEnabled = true,
                    MaxConcurrentOperations = 4,
                    SettingsVersion = 1,
                    SettingsJson = "{}",
                    PrivateNetworkAllowlistJson = "[]",
                    CreatedUtc = now,
                    UpdatedUtc = now
                }
                : new SearchProviderInstance
                {
                    StableId = ProviderInstanceSchema.DefaultGitLabStableId,
                    ProviderKind = kind,
                    DisplayName = "GitLab SaaS",
                    NormalizedScheme = "https",
                    NormalizedHost = "gitlab.com",
                    NormalizedPort = 443,
                    NormalizedBasePath = "/api/v4",
                    IsEnabled = true,
                    MaxConcurrentOperations = 4,
                    SettingsVersion = 1,
                    SettingsJson = "{}",
                    PrivateNetworkAllowlistJson = "[]",
                    CreatedUtc = now,
                    UpdatedUtc = now
                });
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private static HttpResponseMessage JsonResponse(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

    // ── 10.4: Master GitHub operation ────────────────────────────────────────────

    [Fact]
    public async Task GitHub_MigratedOperation_PersistsResultsCheckpointsAndCompletes()
    {
        await using var harness = await Harness.CreateAsync(
            _ => JsonResponse(GitHubSearchBody),
            _ => JsonResponse("[]"));

        var result = await harness.Operations.ExecuteAsync(
            SchedulerPrincipal.System,
            "test-master",
            Guid.NewGuid(),
            new MasterOperationQuery(
                ProviderInstanceSchema.DefaultGitHubStableId,
                SearchProviderEnum.GitHub,
                "SECRET"),
            CancellationToken.None);

        Assert.True(result.MigratedPathTaken);
        Assert.True(result.Succeeded);
        Assert.Equal(ProviderOutcomeKind.Success, result.Outcome);
        Assert.Equal(1, result.PersistedResults);
        Assert.Equal(0, result.DeduplicatedResults);
        Assert.NotNull(result.LeaseId);
        Assert.Null(result.Continuation);
        // github.com provenance is off the approved api.github.com origin: skipped, not failed.
        Assert.Equal(0, result.ContentFetched);
        Assert.Equal(0, result.ContentFailed);

        harness.Context.ChangeTracker.Clear();
        var stored = await harness.Context.NormalizedResults.SingleAsync();
        Assert.Equal("123", stored.RepositoryStableId);
        Assert.Equal("app/.env", stored.NormalizedFilePath);
        Assert.NotNull(stored.WorkItemId);
        Assert.Equal(1, await harness.Context.ResultOutboxRecords.CountAsync(o => !o.IsProcessed));

        var partition = await harness.Context.WorkPartitions.SingleAsync();
        Assert.True(partition.IsTerminal);
        Assert.True(partition.IsComplete);

        var credential = await harness.Context.SearchProviderTokens.SingleAsync(
            t => t.SearchProvider == SearchProviderEnum.GitHub);
        Assert.Null(credential.LeaseId);
        Assert.True(credential.IsEnabled);
        Assert.Equal("Success", credential.LastOutcome);

        var claim = await harness.Context.CredentialClaimRecords.SingleAsync();
        Assert.True(claim.IsTerminal);
        Assert.Equal("Success", claim.TerminalOutcome);
        Assert.True((await harness.Context.OperationSlots.SingleAsync()).IsTerminal);
    }

    // ── 11.3: Master GitLab operation through the same contracts ─────────────────

    [Fact]
    public async Task GitLab_MigratedOperation_ReusesSameContractsAndFetchesBoundContent()
    {
        await using var harness = await Harness.CreateAsync(
            _ => JsonResponse("[]"),
            request => request.RequestUri!.AbsolutePath.Contains("/repository/files/", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("line1\nline2\n", Encoding.UTF8, "text/plain")
                }
                : JsonResponse(GitLabSearchBody));

        var result = await harness.Operations.ExecuteAsync(
            SchedulerPrincipal.System,
            "test-master",
            Guid.NewGuid(),
            new MasterOperationQuery(
                ProviderInstanceSchema.DefaultGitLabStableId,
                SearchProviderEnum.GitLab,
                "SECRET"),
            CancellationToken.None);

        Assert.True(result.MigratedPathTaken);
        Assert.True(result.Succeeded);
        Assert.Equal(ProviderOutcomeKind.Success, result.Outcome);
        Assert.Equal(1, result.PersistedResults);

        harness.Context.ChangeTracker.Clear();
        var stored = await harness.Context.NormalizedResults.SingleAsync();
        Assert.Equal("9999", stored.RepositoryStableId);
        Assert.Equal("config/.env", stored.NormalizedFilePath);
        Assert.Equal("main", stored.Branch);
        Assert.NotNull(stored.ProvenanceUrl);
        Assert.StartsWith("https://gitlab.com/api/v4/projects/9999", stored.ProvenanceUrl, StringComparison.Ordinal);

        // The raw-file provenance URL is bound to the approved origin/path, so the
        // same Claim/Slot also serves content retrieval through the same classifier.
        Assert.Equal(1, result.ContentFetched);
        Assert.Equal(0, result.ContentFailed);
    }

    // ── Flag gate: legacy owns the query while unavailable ───────────────────────

    [Fact]
    public async Task FlagsOff_MigratedPathNotTaken_LegacyRetainsQuery()
    {
        await using var harness = await Harness.CreateAsync(
            _ => JsonResponse(GitHubSearchBody),
            _ => JsonResponse("[]"),
            enableMigratedFlags: false);

        var result = await harness.Operations.ExecuteAsync(
            SchedulerPrincipal.System,
            "test-master",
            Guid.NewGuid(),
            new MasterOperationQuery(
                ProviderInstanceSchema.DefaultGitHubStableId,
                SearchProviderEnum.GitHub,
                "SECRET"),
            CancellationToken.None);

        Assert.False(result.MigratedPathTaken);
        Assert.False(result.Succeeded);
        Assert.Empty(harness.Context.WorkItems);
        Assert.Empty(harness.Context.CredentialClaimRecords);
        Assert.Empty(harness.Context.NormalizedResults);
    }

    // ── Grant preview precedes any Claim ─────────────────────────────────────────

    [Fact]
    public async Task PrincipalWithoutGrant_ReturnsForbiddenScopeWithoutClaiming()
    {
        await using var harness = await Harness.CreateAsync(
            _ => JsonResponse(GitHubSearchBody),
            _ => JsonResponse("[]"));

        // Tokens carry Admin grants only; a non-admin user principal holds nothing.
        var result = await harness.Operations.ExecuteAsync(
            SchedulerPrincipal.ForTelegram(424242, isAdministrator: false),
            "test-master",
            Guid.NewGuid(),
            new MasterOperationQuery(
                ProviderInstanceSchema.DefaultGitHubStableId,
                SearchProviderEnum.GitHub,
                "SECRET"),
            CancellationToken.None);

        Assert.True(result.MigratedPathTaken);
        Assert.False(result.Succeeded);
        Assert.Equal(ProviderOutcomeKind.ForbiddenScope, result.Outcome);
        Assert.Empty(harness.Context.CredentialClaimRecords);
        Assert.Empty(harness.Context.WorkItems);
    }

    // ── Scheduler exhaustion surfaces as a typed retryable outcome ───────────────

    [Fact]
    public async Task NoEligibleCredential_ReturnsTransientWithoutDisabling()
    {
        await using var harness = await Harness.CreateAsync(
            _ => JsonResponse(GitHubSearchBody),
            _ => JsonResponse("[]"));

        var credential = await harness.Context.SearchProviderTokens.SingleAsync(
            t => t.SearchProvider == SearchProviderEnum.GitHub);
        credential.CooldownUntilUtc = DateTime.UtcNow.AddHours(1);
        await harness.Context.SaveChangesAsync();
        harness.Context.ChangeTracker.Clear();

        var result = await harness.Operations.ExecuteAsync(
            SchedulerPrincipal.System,
            "test-master",
            Guid.NewGuid(),
            new MasterOperationQuery(
                ProviderInstanceSchema.DefaultGitHubStableId,
                SearchProviderEnum.GitHub,
                "SECRET"),
            CancellationToken.None);

        Assert.True(result.MigratedPathTaken);
        Assert.False(result.Succeeded);
        Assert.Equal(ProviderOutcomeKind.Transient, result.Outcome);

        harness.Context.ChangeTracker.Clear();
        var after = await harness.Context.SearchProviderTokens.SingleAsync(
            t => t.SearchProvider == SearchProviderEnum.GitHub);
        Assert.True(after.IsEnabled);
    }

    // ── AuthInvalid disables only the failing credential ─────────────────────────

    [Fact]
    public async Task SearchAuthInvalid_DisablesCredentialAndLeavesPartitionResumable()
    {
        await using var harness = await Harness.CreateAsync(
            _ => JsonResponse("""{"message":"Bad credentials"}""", HttpStatusCode.Unauthorized),
            _ => JsonResponse("[]"));

        var result = await harness.Operations.ExecuteAsync(
            SchedulerPrincipal.System,
            "test-master",
            Guid.NewGuid(),
            new MasterOperationQuery(
                ProviderInstanceSchema.DefaultGitHubStableId,
                SearchProviderEnum.GitHub,
                "SECRET"),
            CancellationToken.None);

        Assert.True(result.MigratedPathTaken);
        Assert.False(result.Succeeded);
        Assert.Equal(ProviderOutcomeKind.AuthInvalid, result.Outcome);
        Assert.Equal(0, result.PersistedResults);

        harness.Context.ChangeTracker.Clear();
        var credential = await harness.Context.SearchProviderTokens.SingleAsync(
            t => t.SearchProvider == SearchProviderEnum.GitHub);
        Assert.False(credential.IsEnabled);
        Assert.Equal("AuthInvalid", credential.DisabledReason);
        Assert.Null(credential.LeaseId);

        var claim = await harness.Context.CredentialClaimRecords.SingleAsync();
        Assert.True(claim.IsTerminal);
        Assert.Equal("AuthInvalid", claim.TerminalOutcome);

        var partition = await harness.Context.WorkPartitions.SingleAsync();
        Assert.False(partition.IsTerminal);
    }

    // ── No provider-specific branch in the durable path ──────────────────────────

    [Fact]
    public void DurablePath_ContainsNoProviderSpecificBranch()
    {
        var types = new[]
        {
            typeof(MasterSearchOperationService),
            typeof(SqliteCredentialScheduler),
            typeof(WorkService),
            typeof(ResultPersistenceService)
        };

        var violations = types
            .SelectMany(type => type.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Instance | BindingFlags.Static |
                BindingFlags.DeclaredOnly))
            .Where(method =>
                method.Name.Contains("GitHub", StringComparison.OrdinalIgnoreCase) ||
                method.Name.Contains("GitLab", StringComparison.OrdinalIgnoreCase) ||
                method.Name.Contains("Sourcegraph", StringComparison.OrdinalIgnoreCase))
            .Select(method => $"{method.DeclaringType!.Name}.{method.Name}")
            .ToArray();

        Assert.Empty(violations);
    }
}

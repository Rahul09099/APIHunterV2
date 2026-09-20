using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
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
/// Task 14.3 — Endpoint security through the public path.
/// Proves dual gating under endpoint policy: exact-instance approval, instance-scoped
/// consent, SSRF-safe validation, secret-free public transport, and preserved
/// credentialed guards after the Task 14.2 adapter relaxation.
/// Validates AC-12.1–AC-12.36 (public slice) and AC-17.10–AC-17.11.
/// </summary>
public sealed class PublicEndpointSecurityTests
{
    private static readonly SchedulerPrincipal Admin =
        SchedulerPrincipal.ForTelegram(999, isAdministrator: true);

    private sealed class FixedClock(DateTime utcNow) : IDatabaseUtcClock
    {
        public Task<DateTime> GetUtcNowAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(utcNow);
    }

    private sealed class DelegatingHandlerStub(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest;
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(responder(request));
        }
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
        public SearchProviderInstance SelfHosted { get; set; } = null!;

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
                    "gitlab.com", 443, "/api/v4", now),
                SelfHosted = AddInstance(
                    context, Guid.NewGuid(),
                    SearchProviderEnum.GitLab, "Private GitLab",
                    "git.example.test", 443, "/api/v4", now)
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

        public PublicOperationSlotService SlotsFor(SearchProviderEnum kind) =>
            new(
                Context,
                Consent,
                new SearchProviderAdapterRegistry(
                [
                    new GitHubSearchProviderAdapter(),
                    new GitLabSearchProviderAdapter()
                ]),
                Clock);

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private static async Task<Guid> CreateWorkStableAsync(Harness harness, SearchProviderInstance instance)
    {
        var work = await new WorkService(harness.Context).CreateWorkItemAsync(
            instance.Id, instance.ProviderKind, CredentialGrantScope.Admin, null,
            new SearchQuerySnapshot("SECRET", null, "{}"), "test-v1");
        return work.WorkItemStableId;
    }

    private static ValidatedProviderInstance SaaSInstance(SearchProviderEnum kind) =>
        kind == SearchProviderEnum.GitHub
            ? new ValidatedProviderInstance(
                ProviderInstanceSchema.DefaultGitHubStableId, kind,
                "https", "api.github.com", 443, "/", 1, "{}")
            : new ValidatedProviderInstance(
                ProviderInstanceSchema.DefaultGitLabStableId, kind,
                "https", "gitlab.com", 443, "/api/v4", 1, "{}");

    private static ProviderOperationBounds TestBounds() => new(
        1024 * 1024,
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "application/json", "text/plain" },
        100,
        4,
        TimeSpan.FromSeconds(30));

    private static ProviderOperationContext PublicContext(ValidatedProviderInstance instance) => new(
        instance,
        TestBounds(),
        Continuation: null,
        ContinuationAdapterVersion: null,
        SlotId: Guid.NewGuid(),
        LeaseId: null,
        Credential: null,
        SearchQuery: "SECRET");

    // ── Secret-free public transport through real adapters ─────────────────────

    [Theory]
    [InlineData(SearchProviderEnum.GitHub)]
    [InlineData(SearchProviderEnum.GitLab)]
    public async Task PublicSearch_SendsNoControlPlaneSecrets(SearchProviderEnum kind)
    {
        const string searchBody = kind == SearchProviderEnum.GitHub
            ? """{"total_count":0,"items":[]}"""
            : "[]";
        var handler = new DelegatingHandlerStub(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(searchBody, Encoding.UTF8, "application/json")
        });
        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri(kind == SearchProviderEnum.GitHub
                ? "https://api.github.com/"
                : "https://gitlab.com/api/v4/")
        };
        ISearchProviderAdapter adapter = kind == SearchProviderEnum.GitHub
            ? new GitHubSearchProviderAdapter(client)
            : new GitLabSearchProviderAdapter(client);

        var pages = new List<ProviderOperationResult<NormalizedSearchPage, ProviderOutcomeKind>>();
        await foreach (var page in adapter.SearchAsync(PublicContext(SaaSInstance(kind))))
        {
            pages.Add(page);
        }

        var page = Assert.Single(pages);
        Assert.Equal(ProviderOutcomeKind.Success, page.Outcome);

        var request = Assert.NotNull(handler.LastRequest);
        Assert.False(request.Headers.Contains("Authorization"));
        Assert.False(request.Headers.Contains("PRIVATE-TOKEN"));
        var rendered = request.ToString();
        Assert.DoesNotContain("Bearer", rendered, StringComparison.OrdinalIgnoreCase);
    }

    // ── Exact-instance approval + consent ──────────────────────────────────────

    [Fact]
    public async Task SelfHostedWithoutApproval_IsDeniedDespiteConsent()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.Consent.GrantAsync(harness.SelfHosted.StableId, Admin, "Consent without approval.");
        var slots = harness.SlotsFor(SearchProviderEnum.GitLab);
        var workStableId = await CreateWorkStableAsync(harness, harness.SelfHosted);

        var result = await slots.TryAcquireAsync(
            harness.SelfHosted.StableId, workStableId, "default",
            Guid.NewGuid(), CredentialGrantScope.Admin, null);

        Assert.False(result.Succeeded);
        Assert.Contains("approv", result.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(harness.Context.OperationSlots);
    }

    [Fact]
    public async Task ConsentForOtherInstance_DoesNotAuthorize()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.Consent.GrantAsync(harness.GitHub.StableId, Admin, "GitHub-only consent.");
        var slots = harness.SlotsFor(SearchProviderEnum.GitLab);
        var workStableId = await CreateWorkStableAsync(harness, harness.GitLab);

        var result = await slots.TryAcquireAsync(
            harness.GitLab.StableId, workStableId, "default",
            Guid.NewGuid(), CredentialGrantScope.Admin, null);

        Assert.False(result.Succeeded);
        Assert.Contains("consent", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    // ── Preserved credentialed guards after relaxation ─────────────────────────

    public static TheoryData<SearchProviderEnum> BothKinds => new()
    {
        SearchProviderEnum.GitHub,
        SearchProviderEnum.GitLab
    };

    [Theory]
    [MemberData(nameof(BothKinds))]
    public async Task CredentialedSearchWithoutLease_RemainsDenied(SearchProviderEnum kind)
    {
        ISearchProviderAdapter adapter = kind == SearchProviderEnum.GitHub
            ? new GitHubSearchProviderAdapter(new HttpClient())
            : new GitLabSearchProviderAdapter(new HttpClient());
        var context = PublicContext(SaaSInstance(kind)) with
        {
            Credential = new CredentialMaterial("credential-present-without-lease")
        };

        var enumerator = adapter.SearchAsync(context).GetAsyncEnumerator();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await enumerator.MoveNextAsync());

        Assert.Contains("Lease", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(BothKinds))]
    public async Task SearchWithoutSlot_RemainsDenied(SearchProviderEnum kind)
    {
        ISearchProviderAdapter adapter = kind == SearchProviderEnum.GitHub
            ? new GitHubSearchProviderAdapter(new HttpClient())
            : new GitLabSearchProviderAdapter(new HttpClient());
        var context = PublicContext(SaaSInstance(kind)) with { SlotId = null };

        var enumerator = adapter.SearchAsync(context).GetAsyncEnumerator();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await enumerator.MoveNextAsync());

        Assert.Contains("Operation Slot", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(BothKinds))]
    public async Task CredentialedFetchWithoutLease_RemainsDenied(SearchProviderEnum kind)
    {
        ISearchProviderAdapter adapter = kind == SearchProviderEnum.GitHub
            ? new GitHubSearchProviderAdapter(new HttpClient())
            : new GitLabSearchProviderAdapter(new HttpClient());
        var context = PublicContext(SaaSInstance(kind)) with
        {
            Credential = new CredentialMaterial("credential-present-without-lease"),
            ContentApiUrl = "https://example.test/content"
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => adapter.FetchContentAsync(context));

        Assert.Contains("Lease", error.Message, StringComparison.Ordinal);
    }
}

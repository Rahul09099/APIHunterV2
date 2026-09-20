using System.Net;
using System.Text;
using System.Text.Json;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Interfaces;
using UnsecuredAPIKeys.Providers.Search_Providers;
using Xunit;

namespace UnsecuredAPIKeys.Tests;

/// <summary>
/// Task 11.2/11.3 — Versioned GitLab adapter contract tests.
/// Validates AC-8.4–AC-8.26, AC-16.16–AC-16.26: kind gating, Slot/Lease gating,
/// continuation versioning, Typed Outcomes, pagination, deterministic content version.
/// </summary>
public sealed class GitLabSearchProviderAdapterTests
{
    private static readonly ValidatedProviderInstance DefaultInstance = new(
        StableId: Guid.NewGuid(),
        ProviderKind: SearchProviderEnum.GitLab,
        Scheme: "https",
        Host: "gitlab.com",
        Port: 443,
        BasePath: "/api/v4",
        SettingsVersion: 1,
        SettingsJson: "{}");

    private static readonly ProviderOperationBounds DefaultBounds = new(
        MaxResponseSizeBytes: 10 * 1024 * 1024,
        AllowedContentTypes: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "application/json", "text/plain" },
        MaxEventCount: 100,
        MaxContentConcurrency: 4,
        Timeout: TimeSpan.FromSeconds(30));

    private sealed class DelegatingHandlerStub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handlerFunc)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handlerFunc(request, cancellationToken);
    }

    private static HttpClient CreateMockClient(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new DelegatingHandlerStub((req, _) => Task.FromResult(responder(req)));
        return new HttpClient(handler) { BaseAddress = new Uri("https://gitlab.com/api/v4/") };
    }

    private static ProviderOperationContext CreateContext(
        Guid? slotId = null,
        Guid? leaseId = null,
        CredentialMaterial? credential = null,
        string? continuation = null,
        string? continuationAdapterVersion = null,
        string? searchQuery = null,
        string? contentPath = null,
        string? contentRepo = null,
        string? contentRevision = null,
        string? contentApiUrl = null) =>
        new(
            ProviderInstance: DefaultInstance,
            Bounds: DefaultBounds,
            Continuation: continuation,
            ContinuationAdapterVersion: continuationAdapterVersion,
            SlotId: slotId,
            LeaseId: leaseId,
            Credential: credential,
            SearchQuery: searchQuery,
            ContentPath: contentPath,
            ContentRepositoryName: contentRepo,
            ContentRevision: contentRevision,
            ContentApiUrl: contentApiUrl);

    [Fact]
    public async Task DiscoverCapabilities_ReturnsExpectedDeclaredCapabilities()
    {
        var adapter = new GitLabSearchProviderAdapter();

        Assert.Equal(SearchProviderEnum.GitLab, adapter.ProviderKind);
        Assert.Equal("gitlab-metadata-v1", adapter.AdapterVersion);
        Assert.True(adapter.DeclaredCapabilities.HasFlag(SearchProviderCapability.CodeSearch));
        Assert.True(adapter.DeclaredCapabilities.HasFlag(SearchProviderCapability.SelfHosted));
        Assert.True(adapter.DeclaredCapabilities.HasFlag(SearchProviderCapability.ContentRetrieval));

        var result = await adapter.DiscoverCapabilitiesAsync(DefaultInstance, CancellationToken.None);
        Assert.Equal(ProviderOutcomeKind.Success, result.Outcome);
        Assert.Equal(adapter.DeclaredCapabilities, result.DiscoveredCapabilities);
    }

    [Fact]
    public async Task DiscoverCapabilities_KindMismatch_ReturnsRequestInvalid()
    {
        var adapter = new GitLabSearchProviderAdapter();
        var githubInstance = DefaultInstance with { ProviderKind = SearchProviderEnum.GitHub };
        var result = await adapter.DiscoverCapabilitiesAsync(githubInstance, CancellationToken.None);
        Assert.Equal(ProviderOutcomeKind.RequestInvalid, result.Outcome);
    }

    [Fact]
    public async Task ValidateCredential_MissingSlotOrLease_ThrowsInvalidOperationException()
    {
        var adapter = new GitLabSearchProviderAdapter();
        var context = CreateContext(slotId: null, leaseId: null);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.ValidateCredentialAsync(context, new CredentialMaterial("glpat-x"), CancellationToken.None));
    }

    [Fact]
    public async Task ValidateCredential_Unauthorized401_ReturnsAuthInvalidAndNotValid()
    {
        var client = CreateMockClient(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("""{"message":"401 Unauthorized"}""", Encoding.UTF8, "application/json")
        });
        var adapter = new GitLabSearchProviderAdapter(client);
        var context = CreateContext(slotId: Guid.NewGuid(), leaseId: Guid.NewGuid());

        var result = await adapter.ValidateCredentialAsync(context, new CredentialMaterial("glpat-bad"), CancellationToken.None);

        Assert.Equal(ProviderOutcomeKind.AuthInvalid, result.Outcome);
        Assert.NotNull(result.Value);
        Assert.False(result.Value.IsValid);
    }

    [Fact]
    public async Task ValidateCredential_Ok200_ReturnsSuccessAndValid()
    {
        var client = CreateMockClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"id":1,"username":"tester"}""", Encoding.UTF8, "application/json")
        });
        var adapter = new GitLabSearchProviderAdapter(client);
        var context = CreateContext(slotId: Guid.NewGuid(), leaseId: Guid.NewGuid());

        var result = await adapter.ValidateCredentialAsync(context, new CredentialMaterial("glpat-good"), CancellationToken.None);

        Assert.Equal(ProviderOutcomeKind.Success, result.Outcome);
        Assert.NotNull(result.Value);
        Assert.True(result.Value.IsValid);
    }

    [Fact]
    public async Task Search_IncompatibleContinuationVersion_ReturnsRequestInvalid()
    {
        var adapter = new GitLabSearchProviderAdapter();
        var context = CreateContext(
            slotId: Guid.NewGuid(),
            leaseId: Guid.NewGuid(),
            continuation: """{"page":2,"query":"q"}""",
            continuationAdapterVersion: "github-metadata-v1",
            searchQuery: "q");

        var results = new List<ProviderOperationResult<NormalizedSearchPage, ProviderOutcomeKind>>();
        await foreach (var page in adapter.SearchAsync(context))
        {
            results.Add(page);
        }

        Assert.Single(results);
        Assert.Equal(ProviderOutcomeKind.RequestInvalid, results[0].Outcome);
        Assert.Equal("IncompatibleContinuation", results[0].SanitizedCode);
    }

    [Fact]
    public async Task Search_MalformedContinuation_ReturnsRequestInvalid()
    {
        var adapter = new GitLabSearchProviderAdapter();
        var context = CreateContext(
            slotId: Guid.NewGuid(),
            leaseId: Guid.NewGuid(),
            continuation: "not-json{{{",
            searchQuery: "q");

        var results = new List<ProviderOperationResult<NormalizedSearchPage, ProviderOutcomeKind>>();
        await foreach (var page in adapter.SearchAsync(context))
        {
            results.Add(page);
        }

        Assert.Single(results);
        Assert.Equal(ProviderOutcomeKind.RequestInvalid, results[0].Outcome);
        Assert.Equal("MalformedContinuation", results[0].SanitizedCode);
    }

    [Fact]
    public async Task Search_SuccessPage_ParsesBlobItemsAndNextPageContinuation()
    {
        const string body = """[{"basename":".env","data":"SECRET=x","path":"config/.env","filename":".env","ref":"main","startline":3,"project_id":9999}]""";
        var client = CreateMockClient(request =>
        {
            Assert.Contains("scope=blobs", request.RequestUri!.Query);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            response.Headers.TryAddWithoutValidation("X-Next-Page", "2");
            return response;
        });
        var adapter = new GitLabSearchProviderAdapter(client);
        var context = CreateContext(
            slotId: Guid.NewGuid(),
            leaseId: Guid.NewGuid(),
            credential: new CredentialMaterial("glpat-x"),
            searchQuery: "SECRET");

        var pages = new List<ProviderOperationResult<NormalizedSearchPage, ProviderOutcomeKind>>();
        await foreach (var page in adapter.SearchAsync(context))
        {
            pages.Add(page);
        }

        Assert.Single(pages);
        Assert.Equal(ProviderOutcomeKind.Success, pages[0].Outcome);
        Assert.NotNull(pages[0].Continuation);
        using var doc = JsonDocument.Parse(pages[0].Continuation!);
        Assert.Equal(2, doc.RootElement.GetProperty("page").GetInt32());

        Assert.NotNull(pages[0].Value);
        Assert.Single(pages[0].Value.Results);
        var input = Assert.IsType<ProviderResultInput>(pages[0].Value.Results[0]);
        Assert.Equal(SearchProviderEnum.GitLab, input.ProviderKind);
        Assert.Equal("9999", input.RepositoryStableId);
        Assert.Equal("config/.env", input.NormalizedFilePath);
        Assert.Equal("main", input.Branch);
        Assert.Equal(3, input.LineNumber);
    }

    [Fact]
    public async Task Search_RateLimited429_PropagatesTypedOutcome()
    {
        var client = CreateMockClient(_ => new HttpResponseMessage((HttpStatusCode)429));
        var adapter = new GitLabSearchProviderAdapter(client);
        var context = CreateContext(
            slotId: Guid.NewGuid(),
            leaseId: Guid.NewGuid(),
            credential: new CredentialMaterial("glpat-x"),
            searchQuery: "SECRET");

        var pages = new List<ProviderOperationResult<NormalizedSearchPage, ProviderOutcomeKind>>();
        await foreach (var page in adapter.SearchAsync(context))
        {
            pages.Add(page);
        }

        Assert.Single(pages);
        Assert.Equal(ProviderOutcomeKind.RateLimited, pages[0].Outcome);
    }

    [Fact]
    public async Task FetchContent_MissingLocation_ReturnsRequestInvalid()
    {
        var adapter = new GitLabSearchProviderAdapter();
        var context = CreateContext(slotId: Guid.NewGuid(), leaseId: Guid.NewGuid());

        var result = await adapter.FetchContentAsync(context, CancellationToken.None);

        Assert.Equal(ProviderOutcomeKind.RequestInvalid, result.Outcome);
        Assert.Equal("MissingContentLocation", result.SanitizedCode);
    }

    [Fact]
    public async Task FetchContent_RawBytes_ProduceDeterministicSha256Version()
    {
        const string content = "line1\nline2\n";
        var client = CreateMockClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(content, Encoding.UTF8, "text/plain")
        });
        var adapter = new GitLabSearchProviderAdapter(client);
        var context = CreateContext(
            slotId: Guid.NewGuid(),
            leaseId: Guid.NewGuid(),
            credential: new CredentialMaterial("glpat-x"),
            contentApiUrl: "https://gitlab.com/api/v4/projects/9999/repository/files/config%2F.env/raw?ref=main");

        var first = await adapter.FetchContentAsync(context, CancellationToken.None);
        var second = await adapter.FetchContentAsync(context, CancellationToken.None);

        Assert.Equal(ProviderOutcomeKind.Success, first.Outcome);
        Assert.NotNull(first.Value);
        Assert.Equal(64, first.Value.ContentVersion.Length);
        Assert.Matches("^[0-9a-f]{64}$", first.Value.ContentVersion);
        Assert.Equal(first.Value.ContentVersion, second.Value!.ContentVersion);
        Assert.Equal(content, first.Value.Content);
    }

    [Fact]
    public void TranslateQuery_PrefersNativeOverride()
    {
        var adapter = new GitLabSearchProviderAdapter();
        var context = CreateContext(slotId: Guid.NewGuid(), leaseId: Guid.NewGuid());

        var translated = adapter.TranslateQueryAsync(
            context,
            new SearchQuerySnapshot("generic", "native-override", "{}"),
            CancellationToken.None).GetAwaiter().GetResult();

        Assert.Equal("native-override", translated.Query);
    }
}

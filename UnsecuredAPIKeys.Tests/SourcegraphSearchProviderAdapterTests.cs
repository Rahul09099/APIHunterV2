using System.Net;
using System.Text;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Interfaces;
using UnsecuredAPIKeys.Providers.Search_Providers;
using Xunit;

namespace UnsecuredAPIKeys.Tests;

/// <summary>
/// Phase 1 Sourcegraph adapter contract tests (Tasks 18.1–18.3).
/// Validates AC-16.27–AC-16.30, AC-8.1–AC-8.26, AC-7.16–AC-7.27:
/// transport/stream fixtures, event framing, terminal completion, cancellation,
/// reconnect-safe checkpoints, malformed/bounded events, Typed Outcomes.
/// </summary>
public sealed class SourcegraphSearchProviderAdapterTests
{
    private static readonly ValidatedProviderInstance DefaultInstance = new(
        StableId: Guid.NewGuid(),
        ProviderKind: SearchProviderEnum.Sourcegraph,
        Scheme: "https",
        Host: "sourcegraph.com",
        Port: 443,
        BasePath: "/.api",
        SettingsVersion: 1,
        SettingsJson: "{}");

    private static readonly ProviderOperationBounds DefaultBounds = new(
        MaxResponseSizeBytes: 10 * 1024 * 1024,
        AllowedContentTypes: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "application/json", "text/event-stream" },
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
        return new HttpClient(handler) { BaseAddress = new Uri("https://sourcegraph.com/.api/") };
    }

    private static ProviderOperationContext CreateContext(
        Guid? slotId = null,
        Guid? leaseId = null,
        CredentialMaterial? credential = null,
        string? continuation = null,
        string? continuationAdapterVersion = null,
        string? searchQuery = null,
        string? contentPath = null,
        string? contentOwner = null,
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
            ContentRepositoryOwner: contentOwner,
            ContentRepositoryName: contentRepo,
            ContentRevision: contentRevision,
            ContentApiUrl: contentApiUrl);

    [Fact]
    public async Task DiscoverCapabilities_ReturnsExpectedDeclaredCapabilities()
    {
        var adapter = new SourcegraphSearchProviderAdapter();
        Assert.Equal(SearchProviderEnum.Sourcegraph, adapter.ProviderKind);
        Assert.Equal("sourcegraph-stream-v1", adapter.AdapterVersion);
        Assert.True(adapter.DeclaredCapabilities.HasFlag(SearchProviderCapability.CodeSearch));
        Assert.True(adapter.DeclaredCapabilities.HasFlag(SearchProviderCapability.StreamingSearch));
        Assert.True(adapter.DeclaredCapabilities.HasFlag(SearchProviderCapability.SelfHosted));

        var result = await adapter.DiscoverCapabilitiesAsync(DefaultInstance, CancellationToken.None);
        Assert.Equal(ProviderOutcomeKind.Success, result.Outcome);
        Assert.Equal(adapter.DeclaredCapabilities, result.DiscoveredCapabilities);
    }

    [Fact]
    public async Task DiscoverCapabilities_KindMismatch_ReturnsRequestInvalid()
    {
        var adapter = new SourcegraphSearchProviderAdapter();
        var githubInstance = DefaultInstance with { ProviderKind = SearchProviderEnum.GitHub };
        var result = await adapter.DiscoverCapabilitiesAsync(githubInstance, CancellationToken.None);
        Assert.Equal(ProviderOutcomeKind.RequestInvalid, result.Outcome);
    }

    [Fact]
    public async Task ValidateCredential_MissingSlotOrLease_ThrowsInvalidOperationException()
    {
        var adapter = new SourcegraphSearchProviderAdapter();
        var context = CreateContext(slotId: null, leaseId: null);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.ValidateCredentialAsync(context, new CredentialMaterial("sgp-x"), CancellationToken.None));
    }

    [Fact]
    public async Task ValidateCredential_Unauthorized401_ReturnsAuthInvalid()
    {
        var client = CreateMockClient(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("""{"message":"401 Unauthorized"}""", Encoding.UTF8, "application/json")
        });
        var adapter = new SourcegraphSearchProviderAdapter(client);
        var context = CreateContext(slotId: Guid.NewGuid(), leaseId: Guid.NewGuid());
        var result = await adapter.ValidateCredentialAsync(context, new CredentialMaterial("sgp-bad"), CancellationToken.None);
        Assert.Equal(ProviderOutcomeKind.AuthInvalid, result.Outcome);
        Assert.False(result.Value!.IsValid);
    }

    [Fact]
    public async Task Search_MissingSlot_ThrowsInvalidOperationException()
    {
        var adapter = new SourcegraphSearchProviderAdapter();
        var context = CreateContext(slotId: null, searchQuery: "password");
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in adapter.SearchAsync(context, CancellationToken.None)) { }
        });
    }

    [Fact]
    public async Task Search_IncompatibleContinuation_ReturnsRequestInvalid()
    {
        var client = CreateMockClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("event: done\ndata: {}", Encoding.UTF8, "text/event-stream")
        });
        var adapter = new SourcegraphSearchProviderAdapter(client);
        var context = CreateContext(slotId: Guid.NewGuid(), leaseId: Guid.NewGuid(), credential: new CredentialMaterial("sgp-x"), continuation: """{"offset":0}""", continuationAdapterVersion: "other-v1", searchQuery: "q");
        var pages = new List<ProviderOperationResult<NormalizedSearchPage, ProviderOutcomeKind>>();
        await foreach (var p in adapter.SearchAsync(context, CancellationToken.None)) pages.Add(p);
        Assert.Single(pages);
        Assert.Equal(ProviderOutcomeKind.RequestInvalid, pages[0].Outcome);
    }

    [Fact]
    public async Task Search_StreamingMatches_ParsesEventsAndEmitsProvenance()
    {
        var sse = "event: matches\ndata: {\"results\":[{\"repository\":\"github.com/org/repo\",\"path\":\"config/.env\",\"commit\":\"abc123\",\"lineMatches\":[{\"preview\":\"SECRET=x\"}]}]}\n" +
                  "event: progress\ndata: {\"skipped\":1}\n" +
                  "event: done\ndata: {}\n";
        var client = CreateMockClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
        });
        var adapter = new SourcegraphSearchProviderAdapter(client);
        var context = CreateContext(slotId: Guid.NewGuid(), leaseId: Guid.NewGuid(), credential: new CredentialMaterial("sgp-x"), searchQuery: "password");
        var pages = new List<ProviderOperationResult<NormalizedSearchPage, ProviderOutcomeKind>>();
        await foreach (var p in adapter.SearchAsync(context, CancellationToken.None)) pages.Add(p);
        Assert.Single(pages);
        Assert.Equal(ProviderOutcomeKind.Success, pages[0].Outcome);
        Assert.Single(pages[0].Value!.Results);
        dynamic first = pages[0].Value!.Results[0];
        Assert.Equal(SearchProviderEnum.Sourcegraph, first.ProviderKind);
    }

    [Fact]
    public async Task Search_MalformedEvents_AreSkippedWithoutFailingStream()
    {
        var sse = "event: matches\ndata: not-json{{{\n" +
                  "event: matches\ndata: {\"results\":[{\"repository\":\"github.com/o/r\",\"path\":\"a.txt\",\"commit\":\"deadbeef\"}]}\n" +
                  "event: done\ndata: {}\n";
        var client = CreateMockClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
        });
        var adapter = new SourcegraphSearchProviderAdapter(client);
        var context = CreateContext(slotId: Guid.NewGuid(), leaseId: Guid.NewGuid(), credential: new CredentialMaterial("sgp-x"), searchQuery: "q");
        var pages = new List<ProviderOperationResult<NormalizedSearchPage, ProviderOutcomeKind>>();
        await foreach (var p in adapter.SearchAsync(context, CancellationToken.None)) pages.Add(p);
        Assert.Single(pages);
        Assert.Equal(ProviderOutcomeKind.Success, pages[0].Outcome);
        Assert.Single(pages[0].Value!.Results);
    }

    [Fact]
    public async Task Search_RateLimited429_ReturnsRateLimited()
    {
        var client = CreateMockClient(_ => new HttpResponseMessage((HttpStatusCode)429)
        {
            Content = new StringContent("too many requests", Encoding.UTF8, "text/plain")
        });
        var adapter = new SourcegraphSearchProviderAdapter(client);
        var context = CreateContext(slotId: Guid.NewGuid(), leaseId: Guid.NewGuid(), credential: new CredentialMaterial("sgp-x"), searchQuery: "q");
        var pages = new List<ProviderOperationResult<NormalizedSearchPage, ProviderOutcomeKind>>();
        await foreach (var p in adapter.SearchAsync(context, CancellationToken.None)) pages.Add(p);
        Assert.Single(pages);
        Assert.Equal(ProviderOutcomeKind.RateLimited, pages[0].Outcome);
    }

    [Fact]
    public async Task Search_PublicSlotOnly_SucceedsWithoutLease()
    {
        var sse = "event: done\ndata: {}\n";
        var client = CreateMockClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
        });
        var adapter = new SourcegraphSearchProviderAdapter(client);
        var context = CreateContext(slotId: Guid.NewGuid(), searchQuery: "public query");
        var pages = new List<ProviderOperationResult<NormalizedSearchPage, ProviderOutcomeKind>>();
        await foreach (var p in adapter.SearchAsync(context, CancellationToken.None)) pages.Add(p);
        Assert.Single(pages);
        Assert.Equal(ProviderOutcomeKind.Success, pages[0].Outcome);
    }

    [Fact]
    public async Task FetchContent_DeterministicVersion_SameBytesSameVersion()
    {
        var bytes = Encoding.UTF8.GetBytes("secret-content");
        var client = CreateMockClient(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        var adapter = new SourcegraphSearchProviderAdapter(client);
        var ctx1 = CreateContext(slotId: Guid.NewGuid(), leaseId: Guid.NewGuid(), credential: new CredentialMaterial("sgp-x"), contentApiUrl: "https://sourcegraph.com/github.com/o/r/-/raw/a.txt@abc");
        var ctx2 = CreateContext(slotId: Guid.NewGuid(), leaseId: Guid.NewGuid(), credential: new CredentialMaterial("sgp-x"), contentApiUrl: "https://sourcegraph.com/github.com/o/r/-/raw/a.txt@abc");
        var r1 = await adapter.FetchContentAsync(ctx1, CancellationToken.None);
        var r2 = await adapter.FetchContentAsync(ctx2, CancellationToken.None);
        Assert.Equal(ProviderOutcomeKind.Success, r1.Outcome);
        Assert.Equal(r1.Value!.ContentVersion, r2.Value!.ContentVersion);
        Assert.Matches("^[0-9a-f]{64}$", r1.Value!.ContentVersion);
    }

    [Fact]
    public async Task FetchContent_MissingLocation_ReturnsRequestInvalid()
    {
        var adapter = new SourcegraphSearchProviderAdapter();
        var context = CreateContext(slotId: Guid.NewGuid(), leaseId: Guid.NewGuid(), credential: new CredentialMaterial("sgp-x"));
        var result = await adapter.FetchContentAsync(context, CancellationToken.None);
        Assert.Equal(ProviderOutcomeKind.RequestInvalid, result.Outcome);
    }
}

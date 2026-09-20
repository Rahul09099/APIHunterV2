using System.Net;
using System.Text;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Interfaces;
using UnsecuredAPIKeys.Providers.Search_Providers;
using Xunit;

namespace UnsecuredAPIKeys.Tests;

/// <summary>
/// Phase 5 Gitea/Forgejo adapter tests (Tasks 22.1–22.2).
/// AC-16.41–AC-16.44: endpoint-first, flavor/version detection, validated caps only.
/// </summary>
public sealed class GiteaSearchProviderAdapterTests
{
    private static ValidatedProviderInstance Instance(string settings) => new(
        Guid.NewGuid(), SearchProviderEnum.Gitea, "https", "gitea.example.com", 443, "/", 1, settings);
    private static readonly ProviderOperationBounds Bounds = new(
        10 * 1024 * 1024, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "application/json" }, 100, 4, TimeSpan.FromSeconds(30));

    private sealed class Stub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> f) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken c) => f(r, c);
    }
    private static HttpClient Mock(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var h = new Stub((req, _) => Task.FromResult(responder(req)));
        return new HttpClient(h) { BaseAddress = new Uri("https://gitea.example.com/") };
    }

    [Fact]
    public async Task Discover_SupportedVersion_Succeeds()
    {
        var a = new GiteaSearchProviderAdapter();
        var r = await a.DiscoverCapabilitiesAsync(Instance("""{"ApiFlavor":"gitea","ServerVersion":"1.24.0"}"""), CancellationToken.None);
        Assert.Equal(ProviderOutcomeKind.Success, r.Outcome);
    }

    [Fact]
    public async Task Discover_UnsupportedVersion_Rejected()
    {
        var a = new GiteaSearchProviderAdapter();
        var r = await a.DiscoverCapabilitiesAsync(Instance("""{"ApiFlavor":"gitea","ServerVersion":"1.10.0"}"""), CancellationToken.None);
        Assert.Equal(ProviderOutcomeKind.RequestInvalid, r.Outcome);
    }

    [Fact]
    public async Task Discover_WrongFlavor_Rejected()
    {
        var a = new GiteaSearchProviderAdapter();
        var r = await a.DiscoverCapabilitiesAsync(Instance("""{"ApiFlavor":"forgejo","ServerVersion":"1.24.0"}"""), CancellationToken.None);
        Assert.Equal(ProviderOutcomeKind.RequestInvalid, r.Outcome);
    }

    [Fact]
    public async Task Search_UnsupportedVersion_BlockedBeforeTraffic()
    {
        var calls = 0;
        var c = Mock(_ => { calls++; return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]", Encoding.UTF8, "application/json") }; });
        var a = new GiteaSearchProviderAdapter(c);
        var inst = Instance("""{"ApiFlavor":"gitea","ServerVersion":"1.9.0"}""");
        var ctx = new ProviderOperationContext(inst, Bounds, null, null, Guid.NewGuid(), Guid.NewGuid(), new CredentialMaterial("t"), SearchQuery: "q");
        var pages = new List<ProviderOperationResult<NormalizedSearchPage, ProviderOutcomeKind>>();
        await foreach (var p in a.SearchAsync(ctx, CancellationToken.None)) pages.Add(p);
        Assert.Equal(ProviderOutcomeKind.RequestInvalid, pages[0].Outcome);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Search_SupportedVersion_ParsesCodeResults()
    {
        var body = """[{"repo":{"full_name":"owner/repo"},"path":"config.env","sha":"abc"}]""";
        var c = Mock(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        var a = new GiteaSearchProviderAdapter(c);
        var inst = Instance("""{"ApiFlavor":"gitea","ServerVersion":"1.24.0"}""");
        var ctx = new ProviderOperationContext(inst, Bounds, null, null, Guid.NewGuid(), Guid.NewGuid(), new CredentialMaterial("t"), SearchQuery: "q");
        var pages = new List<ProviderOperationResult<NormalizedSearchPage, ProviderOutcomeKind>>();
        await foreach (var p in a.SearchAsync(ctx, CancellationToken.None)) pages.Add(p);
        Assert.Equal(ProviderOutcomeKind.Success, pages[0].Outcome);
        Assert.Single(pages[0].Value!.Results);
    }
}

public sealed class ForgejoSearchProviderAdapterTests
{
    private static ValidatedProviderInstance Instance(string settings) => new(
        Guid.NewGuid(), SearchProviderEnum.Forgejo, "https", "forgejo.example.com", 443, "/", 1, settings);
    private static readonly ProviderOperationBounds Bounds = new(
        10 * 1024 * 1024, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "application/json" }, 100, 4, TimeSpan.FromSeconds(30));

    private sealed class Stub2(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> f) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken c) => f(r, c);
    }
    private static HttpClient Mock(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var h = new Stub2((req, _) => Task.FromResult(responder(req)));
        return new HttpClient(h) { BaseAddress = new Uri("https://forgejo.example.com/") };
    }

    [Fact]
    public async Task Discover_SupportedVersion_Succeeds()
    {
        var a = new ForgejoSearchProviderAdapter();
        var r = await a.DiscoverCapabilitiesAsync(Instance("""{"ApiFlavor":"forgejo","ServerVersion":"1.21.0"}"""), CancellationToken.None);
        Assert.Equal(ProviderOutcomeKind.Success, r.Outcome);
        Assert.False(a.DeclaredCapabilities.HasFlag(SearchProviderCapability.GlobalPublicSearch));
    }

    [Fact]
    public async Task Discover_WrongFlavor_Rejected()
    {
        var a = new ForgejoSearchProviderAdapter();
        var r = await a.DiscoverCapabilitiesAsync(Instance("""{"ApiFlavor":"gitea","ServerVersion":"1.24.0"}"""), CancellationToken.None);
        Assert.Equal(ProviderOutcomeKind.RequestInvalid, r.Outcome);
    }

    [Fact]
    public async Task Search_MissingSlot_Throws()
    {
        var a = new ForgejoSearchProviderAdapter();
        var inst = Instance("""{"ApiFlavor":"forgejo","ServerVersion":"7.0.0"}""");
        var ctx = new ProviderOperationContext(inst, Bounds, null, null, null, null, SearchQuery: "q");
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in a.SearchAsync(ctx, CancellationToken.None)) { }
        });
    }
}

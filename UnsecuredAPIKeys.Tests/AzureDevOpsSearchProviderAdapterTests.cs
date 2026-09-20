using System.Net;
using System.Text;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Interfaces;
using UnsecuredAPIKeys.Providers.Search_Providers;
using Xunit;

namespace UnsecuredAPIKeys.Tests;

/// <summary>
/// Phase 4 Azure DevOps adapter tests (Tasks 21.1–21.3).
/// AC-16.38–AC-16.40: explicit org/project scope, no global-public.
/// </summary>
public sealed class AzureDevOpsSearchProviderAdapterTests
{
    private static readonly ValidatedProviderInstance DefaultInstance = new(
        Guid.NewGuid(), SearchProviderEnum.AzureDevOps, "https", "dev.azure.com", 443, "/", 1,
        """{"Organization":"contoso","Project":"proj"}""");
    private static readonly ProviderOperationBounds Bounds = new(
        10 * 1024 * 1024, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "application/json" }, 100, 4, TimeSpan.FromSeconds(30));

    private sealed class Stub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> f) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken c) => f(r, c);
    }
    private static HttpClient Mock(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var h = new Stub((req, _) => Task.FromResult(responder(req)));
        return new HttpClient(h) { BaseAddress = new Uri("https://dev.azure.com/") };
    }
    private static ProviderOperationContext Ctx(Guid? slot = null, Guid? lease = null, CredentialMaterial? cred = null, string? cont = null, string? contVer = null, string? q = null, string? apiUrl = null) =>
        new(DefaultInstance, Bounds, cont, contVer, slot, lease, cred, SearchQuery: q, ContentApiUrl: apiUrl);

    [Fact]
    public async Task Discover_NoGlobalPublicCapability()
    {
        var a = new AzureDevOpsSearchProviderAdapter();
        Assert.Equal("azuredevops-search-v1", a.AdapterVersion);
        Assert.False(a.DeclaredCapabilities.HasFlag(SearchProviderCapability.GlobalPublicSearch));
        var r = await a.DiscoverCapabilitiesAsync(DefaultInstance, CancellationToken.None);
        Assert.Equal(ProviderOutcomeKind.Success, r.Outcome);
    }

    [Fact]
    public async Task Validate_401_AuthInvalid()
    {
        var c = Mock(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{}", Encoding.UTF8, "application/json") });
        var a = new AzureDevOpsSearchProviderAdapter(c);
        var r = await a.ValidateCredentialAsync(Ctx(slot: Guid.NewGuid(), lease: Guid.NewGuid()), new CredentialMaterial("az-bad"), CancellationToken.None);
        Assert.Equal(ProviderOutcomeKind.AuthInvalid, r.Outcome);
    }

    [Fact]
    public async Task Search_ScopedResults_PreserveProjectRepoVersionProvenance()
    {
        var body = """{"results":[{"project":{"name":"proj"},"repository":{"name":"repo","id":"r1"},"path":"/src/secret.txt","version":"main","content":"key"}]}""";
        var c = Mock(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        var a = new AzureDevOpsSearchProviderAdapter(c);
        var pages = new List<ProviderOperationResult<NormalizedSearchPage, ProviderOutcomeKind>>();
        await foreach (var p in a.SearchAsync(Ctx(slot: Guid.NewGuid(), lease: Guid.NewGuid(), cred: new CredentialMaterial("az-x"), q: "password"), CancellationToken.None)) pages.Add(p);
        Assert.Single(pages);
        Assert.Equal(ProviderOutcomeKind.Success, pages[0].Outcome);
        Assert.Single(pages[0].Value!.Results);
        dynamic first = pages[0].Value!.Results[0];
        Assert.Equal("proj", first.RepositoryOwner);
        Assert.Equal("repo", first.RepositoryName);
    }

    [Fact]
    public async Task Search_403Forbidden_OrdinaryIsForbiddenScope()
    {
        var c = Mock(_ => new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("access denied", Encoding.UTF8, "text/plain") });
        var a = new AzureDevOpsSearchProviderAdapter(c);
        var pages = new List<ProviderOperationResult<NormalizedSearchPage, ProviderOutcomeKind>>();
        await foreach (var p in a.SearchAsync(Ctx(slot: Guid.NewGuid(), lease: Guid.NewGuid(), cred: new CredentialMaterial("az-x"), q: "q"), CancellationToken.None)) pages.Add(p);
        Assert.Equal(ProviderOutcomeKind.ForbiddenScope, pages[0].Outcome);
    }

    [Fact]
    public async Task FetchContent_MissingLocation_RequestInvalid()
    {
        var a = new AzureDevOpsSearchProviderAdapter();
        var r = await a.FetchContentAsync(Ctx(slot: Guid.NewGuid(), lease: Guid.NewGuid(), cred: new CredentialMaterial("az-x")), CancellationToken.None);
        Assert.Equal(ProviderOutcomeKind.RequestInvalid, r.Outcome);
    }
}

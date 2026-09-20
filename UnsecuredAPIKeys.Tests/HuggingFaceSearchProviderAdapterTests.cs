using System.Net;
using System.Text;
using System.Text.Json;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Interfaces;
using UnsecuredAPIKeys.Providers.Search_Providers;
using Xunit;

namespace UnsecuredAPIKeys.Tests;

/// <summary>
/// Phase 3 Hugging Face Hub adapter tests (Tasks 20.1–20.3).
/// AC-16.35–AC-16.37: supported Hub APIs only, no scraping, dual-gated public.
/// </summary>
public sealed class HuggingFaceSearchProviderAdapterTests
{
    private static readonly ValidatedProviderInstance DefaultInstance = new(
        Guid.NewGuid(), SearchProviderEnum.HuggingFace, "https", "huggingface.co", 443, "/api", 1, "{}");
    private static readonly ProviderOperationBounds Bounds = new(
        10 * 1024 * 1024, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "application/json" }, 100, 4, TimeSpan.FromSeconds(30));

    private sealed class Stub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> f) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken c) => f(r, c);
    }
    private static HttpClient Mock(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var h = new Stub((req, _) => Task.FromResult(responder(req)));
        return new HttpClient(h) { BaseAddress = new Uri("https://huggingface.co/api/") };
    }
    private static ProviderOperationContext Ctx(Guid? slot = null, Guid? lease = null, CredentialMaterial? cred = null, string? cont = null, string? contVer = null, string? q = null, string? apiUrl = null) =>
        new(DefaultInstance, Bounds, cont, contVer, slot, lease, cred, SearchQuery: q, ContentApiUrl: apiUrl);

    [Fact]
    public async Task Discover_ReturnsDeclared()
    {
        var a = new HuggingFaceSearchProviderAdapter();
        Assert.Equal("huggingface-hub-v1", a.AdapterVersion);
        var r = await a.DiscoverCapabilitiesAsync(DefaultInstance, CancellationToken.None);
        Assert.Equal(ProviderOutcomeKind.Success, r.Outcome);
    }

    [Fact]
    public async Task Discover_KindMismatch_RequestInvalid()
    {
        var a = new HuggingFaceSearchProviderAdapter();
        var r = await a.DiscoverCapabilitiesAsync(DefaultInstance with { ProviderKind = SearchProviderEnum.GitHub }, CancellationToken.None);
        Assert.Equal(ProviderOutcomeKind.RequestInvalid, r.Outcome);
    }

    [Fact]
    public async Task Validate_401_AuthInvalid()
    {
        var c = Mock(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{}", Encoding.UTF8, "application/json") });
        var a = new HuggingFaceSearchProviderAdapter(c);
        var r = await a.ValidateCredentialAsync(Ctx(slot: Guid.NewGuid(), lease: Guid.NewGuid()), new CredentialMaterial("hf-bad"), CancellationToken.None);
        Assert.Equal(ProviderOutcomeKind.AuthInvalid, r.Outcome);
    }

    [Fact]
    public async Task Search_EnumeratesModelsAndDatasets_WithContinuation()
    {
        var models = """[{"id":"org/model","sha":"aaa","pipeline_tag":"text-generation"}]""";
        var call = 0;
        var c = Mock(_ =>
        {
            call++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(models, Encoding.UTF8, "application/json") };
        });
        var a = new HuggingFaceSearchProviderAdapter(c);
        var ctx = Ctx(slot: Guid.NewGuid(), lease: Guid.NewGuid(), cred: new CredentialMaterial("hf-x"), q: "llm");
        var pages = new List<ProviderOperationResult<NormalizedSearchPage, ProviderOutcomeKind>>();
        await foreach (var p in a.SearchAsync(ctx, CancellationToken.None)) pages.Add(p);
        Assert.Single(pages);
        Assert.Equal(ProviderOutcomeKind.Success, pages[0].Outcome);
        Assert.Equal(2, pages[0].Value!.Results.Count);
    }

    [Fact]
    public async Task Search_429_RateLimited()
    {
        var c = Mock(_ => new HttpResponseMessage((HttpStatusCode)429) { Content = new StringContent("rate limit", Encoding.UTF8, "text/plain") });
        var a = new HuggingFaceSearchProviderAdapter(c);
        var pages = new List<ProviderOperationResult<NormalizedSearchPage, ProviderOutcomeKind>>();
        await foreach (var p in a.SearchAsync(Ctx(slot: Guid.NewGuid(), lease: Guid.NewGuid(), cred: new CredentialMaterial("hf-x"), q: "q"), CancellationToken.None)) pages.Add(p);
        Assert.Equal(ProviderOutcomeKind.RateLimited, pages[0].Outcome);
    }

    [Fact]
    public async Task FetchContent_VersionDeterministic()
    {
        var bytes = Encoding.UTF8.GetBytes("model-card");
        var c = Mock(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        var a = new HuggingFaceSearchProviderAdapter(c);
        var r1 = await a.FetchContentAsync(Ctx(slot: Guid.NewGuid(), lease: Guid.NewGuid(), cred: new CredentialMaterial("hf-x"), apiUrl: "https://huggingface.co/org/m/raw/main/README.md"), CancellationToken.None);
        var r2 = await a.FetchContentAsync(Ctx(slot: Guid.NewGuid(), lease: Guid.NewGuid(), cred: new CredentialMaterial("hf-x"), apiUrl: "https://huggingface.co/org/m/raw/main/README.md"), CancellationToken.None);
        Assert.Equal(r1.Value!.ContentVersion, r2.Value!.ContentVersion);
        Assert.Matches("^[0-9a-f]{64}$", r1.Value!.ContentVersion);
    }

    [Fact]
    public void Translate_PrefersNativeOverride()
    {
        var a = new HuggingFaceSearchProviderAdapter();
        var t = a.TranslateQueryAsync(Ctx(slot: Guid.NewGuid()), new SearchQuerySnapshot("generic", "model:llama", "{}"), CancellationToken.None).Result;
        Assert.Equal("model:llama", t.Query);
    }
}

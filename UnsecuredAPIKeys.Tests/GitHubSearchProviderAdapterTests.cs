using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Data.Models;
using UnsecuredAPIKeys.Providers._Interfaces;
using UnsecuredAPIKeys.Providers.Search_Providers;
using UnsecuredAPIKeys.Services;
using Xunit;

namespace UnsecuredAPIKeys.Tests;

public sealed class GitHubSearchProviderAdapterTests
{
    private static readonly ValidatedProviderInstance DefaultInstance = new(
        StableId: Guid.NewGuid(),
        ProviderKind: SearchProviderEnum.GitHub,
        Scheme: "https",
        Host: "api.github.com",
        Port: 443,
        BasePath: "/",
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
        return new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com") };
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
        string? contentRevision = null) =>
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
            ContentRevision: contentRevision);

    // ── 1. Metadata and Capabilities (AC-8.4 - AC-8.7) ───────────────────────────

    [Fact]
    public async Task DiscoverCapabilities_ReturnsExpectedDeclaredCapabilities()
    {
        var adapter = new GitHubSearchProviderAdapter();

        Assert.Equal(SearchProviderEnum.GitHub, adapter.ProviderKind);
        Assert.Equal("github-metadata-v1", adapter.AdapterVersion);
        Assert.True(adapter.DeclaredCapabilities.HasFlag(SearchProviderCapability.CodeSearch));
        Assert.True(adapter.DeclaredCapabilities.HasFlag(SearchProviderCapability.PaginatedSearch));
        Assert.True(adapter.DeclaredCapabilities.HasFlag(SearchProviderCapability.ContentRetrieval));
        Assert.True(adapter.DeclaredCapabilities.HasFlag(SearchProviderCapability.PrivateRepositories));
        Assert.True(adapter.DeclaredCapabilities.HasFlag(SearchProviderCapability.GlobalPublicSearch));

        var result = await adapter.DiscoverCapabilitiesAsync(DefaultInstance, CancellationToken.None);
        Assert.Equal(ProviderOutcomeKind.Success, result.Outcome);
        Assert.Equal(adapter.DeclaredCapabilities, result.DiscoveredCapabilities);
    }

    // ── 2. ValidateCredentialAsync (AC-8.8, AC-16.9) ─────────────────────────────

    [Fact]
    public async Task ValidateCredential_MissingSlotOrLease_ThrowsInvalidOperationException()
    {
        var adapter = new GitHubSearchProviderAdapter();
        var context = CreateContext(slotId: null, leaseId: null);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.ValidateCredentialAsync(context, new CredentialMaterial("ghp_test"), CancellationToken.None));
    }

    [Fact]
    public async Task ValidateCredential_200OK_ReturnsSuccessAndValidResult()
    {
        var client = CreateMockClient(req =>
        {
            Assert.Equal("/user", req.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", req.Headers.Authorization?.Scheme);
            Assert.Equal("ghp_valid_token", req.Headers.Authorization?.Parameter);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"login\":\"octocat\",\"id\":1}", Encoding.UTF8, "application/json")
            };
        });

        var adapter = new GitHubSearchProviderAdapter(client);
        var context = CreateContext(slotId: Guid.NewGuid(), leaseId: Guid.NewGuid());
        var result = await adapter.ValidateCredentialAsync(context, new CredentialMaterial("ghp_valid_token"), CancellationToken.None);

        Assert.Equal(ProviderOutcomeKind.Success, result.Outcome);
        Assert.NotNull(result.Value);
        Assert.True(result.Value.IsValid);
    }

    [Fact]
    public async Task ValidateCredential_401Unauthorized_ReturnsAuthInvalid()
    {
        var client = CreateMockClient(req => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("{\"message\":\"Bad credentials\"}", Encoding.UTF8, "application/json")
        });

        var adapter = new GitHubSearchProviderAdapter(client);
        var context = CreateContext(slotId: Guid.NewGuid(), leaseId: Guid.NewGuid());
        var result = await adapter.ValidateCredentialAsync(context, new CredentialMaterial("ghp_bad_token"), CancellationToken.None);

        Assert.Equal(ProviderOutcomeKind.AuthInvalid, result.Outcome);
        Assert.NotNull(result.Value);
        Assert.False(result.Value.IsValid);
    }

    [Fact]
    public async Task ValidateCredential_403ForbiddenScope_ReturnsForbiddenScope()
    {
        var client = CreateMockClient(req => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("{\"message\":\"Resource not accessible by personal access token\"}", Encoding.UTF8, "application/json")
        });

        var adapter = new GitHubSearchProviderAdapter(client);
        var context = CreateContext(slotId: Guid.NewGuid(), leaseId: Guid.NewGuid());
        var result = await adapter.ValidateCredentialAsync(context, new CredentialMaterial("ghp_forbidden"), CancellationToken.None);

        Assert.Equal(ProviderOutcomeKind.ForbiddenScope, result.Outcome);
        Assert.NotNull(result.Value);
        Assert.False(result.Value.IsValid);
    }

    // ── 3. TranslateQueryAsync (AC-8.9, AC-9.5, AC-9.6) ──────────────────────────

    [Fact]
    public async Task TranslateQuery_WithNativeOverride_UsesNativeOverride()
    {
        var adapter = new GitHubSearchProviderAdapter();
        var context = CreateContext();
        var snapshot = new SearchQuerySnapshot(
            GenericQuery: "AKIAIOSFODNN7EXAMPLE",
            NativeOverride: "repo:org/repo filename:.env AKIAIOSFODNN7EXAMPLE",
            SettingsJson: "{}");

        var translated = await adapter.TranslateQueryAsync(context, snapshot, CancellationToken.None);
        Assert.Equal("repo:org/repo filename:.env AKIAIOSFODNN7EXAMPLE", translated.Query);
    }

    [Fact]
    public async Task TranslateQuery_WithoutNativeOverride_UsesGenericQuery()
    {
        var adapter = new GitHubSearchProviderAdapter();
        var context = CreateContext();
        var snapshot = new SearchQuerySnapshot(
            GenericQuery: "AKIAIOSFODNN7EXAMPLE",
            NativeOverride: null,
            SettingsJson: "{}");

        var translated = await adapter.TranslateQueryAsync(context, snapshot, CancellationToken.None);
        Assert.Equal("AKIAIOSFODNN7EXAMPLE", translated.Query);
    }

    // ── 4. SearchAsync (AC-8.10, AC-10.1 - AC-10.10, AC-16.4 - AC-16.15) ────────

    [Fact]
    public async Task SearchAsync_MissingSlotOrLease_ThrowsInvalidOperationException()
    {
        var adapter = new GitHubSearchProviderAdapter();
        var context = CreateContext(slotId: null, leaseId: null);

        await using var enumerator = adapter.SearchAsync(context, CancellationToken.None).GetAsyncEnumerator();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await enumerator.MoveNextAsync());
    }

    [Fact]
    public async Task SearchAsync_IncompatibleContinuationVersion_ReturnsRequestInvalid()
    {
        var adapter = new GitHubSearchProviderAdapter();
        var context = CreateContext(
            slotId: Guid.NewGuid(),
            leaseId: Guid.NewGuid(),
            credential: new CredentialMaterial("ghp_test"),
            continuation: "{\"page\": 2}",
            continuationAdapterVersion: "incompatible-version-99");

        var results = new List<ProviderOperationResult<NormalizedSearchPage, ProviderOutcomeKind>>();
        await foreach (var page in adapter.SearchAsync(context, CancellationToken.None))
        {
            results.Add(page);
        }

        Assert.Single(results);
        Assert.Equal(ProviderOutcomeKind.RequestInvalid, results[0].Outcome);
    }

    [Fact]
    public async Task SearchAsync_SuccessfulResponse_ReturnsNormalizedResultsAndContinuation()
    {
        var searchJson = JsonSerializer.Serialize(new
        {
            total_count = 150,
            incomplete_results = false,
            items = new[]
            {
                new
                {
                    name = "credentials.env",
                    path = "config/credentials.env",
                    sha = "blobsha_1234567890abcdef",
                    html_url = "https://github.com/acme/app/blob/main/config/credentials.env",
                    repository = new
                    {
                        id = 987654321,
                        name = "app",
                        full_name = "acme/app",
                        owner = new { login = "acme" }
                    },
                    text_matches = new[]
                    {
                        new { fragment = "AWS_SECRET_ACCESS_KEY=example..." }
                    }
                }
            }
        });

        var client = CreateMockClient(req =>
        {
            Assert.Contains("/search/code", req.RequestUri!.AbsolutePath);
            Assert.Contains("q=test_query", req.RequestUri.Query);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(searchJson, Encoding.UTF8, "application/json")
            };
        });

        var adapter = new GitHubSearchProviderAdapter(client);
        var context = CreateContext(
            slotId: Guid.NewGuid(),
            leaseId: Guid.NewGuid(),
            credential: new CredentialMaterial("ghp_valid"),
            searchQuery: "test_query");

        var pages = new List<ProviderOperationResult<NormalizedSearchPage, ProviderOutcomeKind>>();
        await foreach (var page in adapter.SearchAsync(context, CancellationToken.None))
        {
            pages.Add(page);
        }

        Assert.Single(pages);
        var firstPage = pages[0];
        Assert.Equal(ProviderOutcomeKind.Success, firstPage.Outcome);
        Assert.NotNull(firstPage.Value);
        Assert.Single(firstPage.Value.Results);

        var item = Assert.IsType<ProviderResultInput>(firstPage.Value.Results[0]);
        Assert.Equal(SearchProviderEnum.GitHub, item.ProviderKind);
        Assert.Equal(DefaultInstance.StableId, item.ProviderInstanceStableId);
        Assert.Equal("987654321", item.RepositoryStableId);
        Assert.Equal("acme", item.RepositoryOwner);
        Assert.Equal("app", item.RepositoryName);
        Assert.Equal("blobsha_1234567890abcdef", item.ImmutableRevisionOrEquivalentVersion);
        Assert.Equal("config/credentials.env", item.NormalizedFilePath);
        Assert.Equal("credentials.env", item.FileName);
        Assert.Equal("https://github.com/acme/app/blob/main/config/credentials.env", item.ProvenanceUrl);
        Assert.NotNull(firstPage.Continuation);
    }

    [Fact]
    public async Task SearchAsync_WhenGitHubReturns429_PropagatesRateLimitedOutcome()
    {
        var client = CreateMockClient(req =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            resp.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(45));
            return resp;
        });

        var adapter = new GitHubSearchProviderAdapter(client);
        var context = CreateContext(
            slotId: Guid.NewGuid(),
            leaseId: Guid.NewGuid(),
            credential: new CredentialMaterial("ghp_valid"),
            searchQuery: "rate_limited_query");

        var pages = new List<ProviderOperationResult<NormalizedSearchPage, ProviderOutcomeKind>>();
        await foreach (var page in adapter.SearchAsync(context, CancellationToken.None))
        {
            pages.Add(page);
        }

        Assert.Single(pages);
        Assert.Equal(ProviderOutcomeKind.RateLimited, pages[0].Outcome);
    }

    // ── 5. FetchContentAsync (AC-8.11, AC-10.24, AC-16.14) ───────────────────────

    [Fact]
    public async Task FetchContent_MissingSlotOrLease_ThrowsInvalidOperationException()
    {
        var adapter = new GitHubSearchProviderAdapter();
        var context = CreateContext(slotId: null, leaseId: null);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.FetchContentAsync(context, CancellationToken.None));
    }

    [Fact]
    public async Task FetchContent_ContentsApi_Base64Encoded_DecodesAndUsesBlobSha()
    {
        var rawContent = "AWS_SECRET_ACCESS_KEY=wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY";
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(rawContent));
        var responseJson = JsonSerializer.Serialize(new
        {
            type = "file",
            encoding = "base64",
            size = rawContent.Length,
            name = ".env",
            path = "src/.env",
            content = base64,
            sha = "3d2110d065839ff61803623ff2f1ef1e8b2b9186"
        });

        var client = CreateMockClient(req =>
        {
            Assert.Contains("/repos/acme/my-repo/contents/src/.env", req.RequestUri!.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            };
        });

        var adapter = new GitHubSearchProviderAdapter(client);
        var context = CreateContext(
            slotId: Guid.NewGuid(),
            leaseId: Guid.NewGuid(),
            credential: new CredentialMaterial("ghp_valid"),
            contentOwner: "acme",
            contentRepo: "my-repo",
            contentPath: "src/.env",
            contentRevision: "main");

        var result = await adapter.FetchContentAsync(context, CancellationToken.None);

        Assert.Equal(ProviderOutcomeKind.Success, result.Outcome);
        Assert.NotNull(result.Value);
        Assert.Equal(rawContent, result.Value.Content);
        Assert.Equal("3d2110d065839ff61803623ff2f1ef1e8b2b9186", result.Value.ContentVersion);
    }

    [Fact]
    public async Task FetchContent_WhenNoBlobSha_CalculatesDeterministicSha256ContentVersion()
    {
        var rawContent = "DATABASE_URL=postgres://user:pass@localhost:5432/db";
        var expectedSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawContent))).ToLowerInvariant();

        var client = CreateMockClient(req => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(rawContent, Encoding.UTF8, "text/plain")
        });

        var adapter = new GitHubSearchProviderAdapter(client);
        var context = CreateContext(
            slotId: Guid.NewGuid(),
            leaseId: Guid.NewGuid(),
            credential: new CredentialMaterial("ghp_valid"),
            contentOwner: "acme",
            contentRepo: "my-repo",
            contentPath: "src/.env",
            contentRevision: "main");

        var result = await adapter.FetchContentAsync(context, CancellationToken.None);

        Assert.Equal(ProviderOutcomeKind.Success, result.Outcome);
        Assert.NotNull(result.Value);
        Assert.Equal(rawContent, result.Value.Content);
        Assert.Equal(expectedSha256, result.Value.ContentVersion);
    }

    [Fact]
    public async Task FetchContent_When404_PropagatesResourceMissing()
    {
        var client = CreateMockClient(req => new HttpResponseMessage(HttpStatusCode.NotFound));

        var adapter = new GitHubSearchProviderAdapter(client);
        var context = CreateContext(
            slotId: Guid.NewGuid(),
            leaseId: Guid.NewGuid(),
            credential: new CredentialMaterial("ghp_valid"),
            contentOwner: "acme",
            contentRepo: "my-repo",
            contentPath: "deleted-file.txt");

        var result = await adapter.FetchContentAsync(context, CancellationToken.None);

        Assert.Equal(ProviderOutcomeKind.ResourceMissing, result.Outcome);
        Assert.Null(result.Value);
    }
}

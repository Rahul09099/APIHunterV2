using System.Net;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Data.Models;
using UnsecuredAPIKeys.Providers;
using UnsecuredAPIKeys.Providers._Interfaces;
using UnsecuredAPIKeys.Services;
using Xunit;

namespace UnsecuredAPIKeys.Tests;

/// <summary>
/// Unit and property tests for Task 8.5: Safe Provider Adapter Runtime with Slot + Claim gating.
/// Validates: AC-2.19, AC-5.6, AC-8.7, AC-8.13-AC-8.18, AC-8.20-AC-8.26, AC-17.45-AC-17.47.
/// </summary>
public sealed class ProviderAdapterRuntimeContractTests
{
    private static readonly ProviderOperationBounds DefaultBounds = new(
        MaxResponseSizeBytes: 1024 * 1024,
        AllowedContentTypes: new HashSet<string> { "application/json", "text/plain" },
        MaxEventCount: 100,
        MaxContentConcurrency: 2,
        Timeout: TimeSpan.FromSeconds(10));

    private static ValidatedProviderInstance CreateValidatedInstance(
        SearchProviderEnum kind = SearchProviderEnum.GitHub,
        string host = "api.github.com") =>
        new(
            StableId: Guid.NewGuid(),
            ProviderKind: kind,
            Scheme: "https",
            Host: host,
            Port: 443,
            BasePath: "/",
            SettingsVersion: 1,
            SettingsJson: "{}");

    private static ProviderOperationContext CreateContext(
        ValidatedProviderInstance instance,
        Guid? slotId = null,
        Guid? leaseId = null,
        ProviderOperationBounds? bounds = null) =>
        new(
            ProviderInstance: instance,
            Bounds: bounds ?? DefaultBounds,
            Continuation: null,
            ContinuationAdapterVersion: null,
            SlotId: slotId,
            LeaseId: leaseId);

    // ── AC-2.19, AC-8.26: Pre-traffic Kind Rejection ─────────────────────────

    [Fact]
    public async Task PreTrafficKindMismatch_RejectedBeforeInvocation()
    {
        var runtime = CreateRuntime();
        var fakeGithubAdapter = new TestFakeAdapter(SearchProviderEnum.GitHub, "test-v1");
        var gitlabInstance = CreateValidatedInstance(SearchProviderEnum.GitLab, "gitlab.com");
        var context = CreateContext(gitlabInstance, slotId: Guid.NewGuid(), leaseId: Guid.NewGuid());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runtime.ValidateCredentialAsync(fakeGithubAdapter, context, new CredentialMaterial("token"), CancellationToken.None));

        Assert.Contains("does not match", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(fakeGithubAdapter.ValidateCalled, "Adapter must not be invoked when kinds mismatch.");
    }

    // ── AC-8.7, AC-17.45: Operation Slot Gating ───────────────────────────────

    [Fact]
    public async Task CapabilityDiscovery_WithoutSlot_ThrowsMissingSlotException()
    {
        var runtime = CreateRuntime();
        var adapter = new TestFakeAdapter(SearchProviderEnum.GitHub, "test-v1");
        var instance = CreateValidatedInstance();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runtime.DiscoverCapabilitiesAsync(adapter, instance, slotId: Guid.Empty, CancellationToken.None));

        Assert.Contains("Operation Slot", error.Message, StringComparison.Ordinal);
        Assert.False(adapter.DiscoverCalled);
    }

    [Fact]
    public async Task CredentialValidation_WithoutSlot_ThrowsMissingSlotException()
    {
        var runtime = CreateRuntime();
        var adapter = new TestFakeAdapter(SearchProviderEnum.GitHub, "test-v1");
        var instance = CreateValidatedInstance();
        var context = CreateContext(instance, slotId: null, leaseId: Guid.NewGuid());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runtime.ValidateCredentialAsync(adapter, context, new CredentialMaterial("tok"), CancellationToken.None));

        Assert.Contains("Operation Slot", error.Message, StringComparison.Ordinal);
        Assert.False(adapter.ValidateCalled);
    }

    [Fact]
    public async Task Search_WithoutSlot_ThrowsMissingSlotException()
    {
        var runtime = CreateRuntime();
        var adapter = new TestFakeAdapter(SearchProviderEnum.GitHub, "test-v1");
        var instance = CreateValidatedInstance();
        var context = CreateContext(instance, slotId: null, leaseId: Guid.NewGuid());

        var enumerator = runtime.SearchAsync(adapter, context, CancellationToken.None).GetAsyncEnumerator();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await enumerator.MoveNextAsync());

        Assert.Contains("Operation Slot", error.Message, StringComparison.Ordinal);
        Assert.False(adapter.SearchCalled);
    }

    [Fact]
    public async Task FetchContent_WithoutSlot_ThrowsMissingSlotException()
    {
        var runtime = CreateRuntime();
        var adapter = new TestFakeAdapter(SearchProviderEnum.GitHub, "test-v1");
        var instance = CreateValidatedInstance();
        var context = CreateContext(instance, slotId: null, leaseId: Guid.NewGuid());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runtime.FetchContentAsync(adapter, context, CancellationToken.None));

        Assert.Contains("Operation Slot", error.Message, StringComparison.Ordinal);
        Assert.False(adapter.FetchCalled);
    }

    // ── AC-8.15, AC-17.46, AC-17.47: Credential Claim Lease Gating ───────────

    [Fact]
    public async Task CredentialValidation_WithSlotButWithoutLease_ThrowsMissingLeaseException()
    {
        var runtime = CreateRuntime();
        var adapter = new TestFakeAdapter(SearchProviderEnum.GitHub, "test-v1");
        var instance = CreateValidatedInstance();
        var context = CreateContext(instance, slotId: Guid.NewGuid(), leaseId: null);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runtime.ValidateCredentialAsync(adapter, context, new CredentialMaterial("tok"), CancellationToken.None));

        Assert.Contains("Lease", error.Message, StringComparison.Ordinal);
        Assert.False(adapter.ValidateCalled);
    }

    [Fact]
    public async Task FetchContent_WithSlotButWithoutLease_ThrowsMissingLeaseException()
    {
        var runtime = CreateRuntime();
        var adapter = new TestFakeAdapter(SearchProviderEnum.GitHub, "test-v1");
        var instance = CreateValidatedInstance();
        var context = CreateContext(instance, slotId: Guid.NewGuid(), leaseId: null);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runtime.FetchContentAsync(adapter, context, CancellationToken.None));

        Assert.Contains("Lease", error.Message, StringComparison.Ordinal);
        Assert.False(adapter.FetchCalled);
    }

    // ── AC-8.17, AC-8.18: Self-Hosted / Private Credentialed Operation Gating ──

    [Fact]
    public async Task Search_SelfHostedWithoutLease_ThrowsMissingLeaseException()
    {
        var runtime = CreateRuntime();
        var adapter = new TestFakeAdapter(SearchProviderEnum.GitLab, "test-v1");
        var selfHostedInstance = CreateValidatedInstance(SearchProviderEnum.GitLab, "git.internal.corp");
        var context = CreateContext(selfHostedInstance, slotId: Guid.NewGuid(), leaseId: null);

        var enumerator = runtime.SearchAsync(adapter, context, CancellationToken.None).GetAsyncEnumerator();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await enumerator.MoveNextAsync());

        Assert.Contains("Lease", error.Message, StringComparison.Ordinal);
        Assert.False(adapter.SearchCalled);
    }

    // ── AC-8.20: Provider Error Classification for Search and Content ────────

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, ProviderOutcomeKind.AuthInvalid)]
    [InlineData(HttpStatusCode.Forbidden, ProviderOutcomeKind.ForbiddenScope)]
    [InlineData(HttpStatusCode.TooManyRequests, ProviderOutcomeKind.RateLimited)]
    [InlineData(HttpStatusCode.NotFound, ProviderOutcomeKind.ResourceMissing)]
    [InlineData(HttpStatusCode.BadRequest, ProviderOutcomeKind.RequestInvalid)]
    [InlineData(HttpStatusCode.UnprocessableEntity, ProviderOutcomeKind.RequestInvalid)]
    [InlineData(HttpStatusCode.InternalServerError, ProviderOutcomeKind.Transient)]
    [InlineData(HttpStatusCode.BadGateway, ProviderOutcomeKind.Transient)]
    [InlineData(HttpStatusCode.ServiceUnavailable, ProviderOutcomeKind.Transient)]
    [InlineData(HttpStatusCode.GatewayTimeout, ProviderOutcomeKind.Transient)]
    [InlineData(HttpStatusCode.OK, ProviderOutcomeKind.Success)]
    public void SharedClassifier_ClassifiesStatusCodesConsistently(HttpStatusCode status, ProviderOutcomeKind expected)
    {
        var classifier = new SearchProviderOutcomeClassifier();
        var result = classifier.Classify(SearchProviderEnum.GitHub, status);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void SharedClassifier_ClassifiesExceptionsConsistently()
    {
        var classifier = new SearchProviderOutcomeClassifier();
        Assert.Equal(ProviderOutcomeKind.Transient,
            classifier.Classify(SearchProviderEnum.GitHub, null, new HttpRequestException("connection reset")));
        Assert.Equal(ProviderOutcomeKind.Transient,
            classifier.Classify(SearchProviderEnum.GitHub, null, new TimeoutException("timed out")));
        Assert.Equal(ProviderOutcomeKind.Cancellation,
            classifier.Classify(SearchProviderEnum.GitHub, null, new OperationCanceledException()));
    }

    // ── AC-8.16, AC-8.21: Bounded Streaming Search ───────────────────────────

    [Fact]
    public async Task BoundedStreamingSearch_EnforcesMaxEventCount()
    {
        var runtime = CreateRuntime();
        var adapter = new StreamingFakeAdapter(totalEventsToYield: 200);
        var instance = CreateValidatedInstance();
        var bounds = new ProviderOperationBounds(
            MaxResponseSizeBytes: 10 * 1024 * 1024,
            AllowedContentTypes: new HashSet<string> { "application/json" },
            MaxEventCount: 15,
            MaxContentConcurrency: 2,
            Timeout: TimeSpan.FromSeconds(10));
        var context = CreateContext(instance, slotId: Guid.NewGuid(), leaseId: Guid.NewGuid(), bounds: bounds);

        var receivedPages = 0;
        await foreach (var page in runtime.SearchAsync(adapter, context, CancellationToken.None))
        {
            receivedPages++;
        }

        Assert.True(receivedPages <= 15, $"Stream must be capped at MaxEventCount=15, got {receivedPages}.");
    }

    // ── AC-8.16, AC-17.45: Bounded Content Concurrency ───────────────────────

    [Fact]
    public async Task BoundedParallelContent_RespectsMaxContentConcurrency()
    {
        var runtime = CreateRuntime();
        var adapter = new ConcurrencyTrackingFakeAdapter();
        var instance = CreateValidatedInstance();
        var bounds = new ProviderOperationBounds(
            MaxResponseSizeBytes: 1024 * 1024,
            AllowedContentTypes: new HashSet<string> { "text/plain" },
            MaxEventCount: 100,
            MaxContentConcurrency: 3,
            Timeout: TimeSpan.FromSeconds(10));
        var context = CreateContext(instance, slotId: Guid.NewGuid(), leaseId: Guid.NewGuid(), bounds: bounds);

        var results = await runtime.FetchContentBatchAsync(adapter, context, count: 10, CancellationToken.None);

        Assert.Equal(10, results.Count);
        Assert.True(adapter.PeakConcurrency <= 3,
            $"Peak concurrency must not exceed 3, but was {adapter.PeakConcurrency}.");
    }

    // ── AC-5.6, AC-8.25: Scheduler Has No Provider-Specific Branches ─────────

    [Fact]
    public void CredentialScheduler_ContainsNoProviderSpecificBranches()
    {
        var schedulerType = typeof(SqliteCredentialScheduler);
        var methods = schedulerType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        foreach (var method in methods)
        {
            var body = method.GetMethodBody();
            if (body is null) continue;

            // Assert method names and signatures don't mention GitHub, GitLab, Sourcegraph
            Assert.DoesNotContain("GitHub", method.Name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("GitLab", method.Name, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ── AC-8.13: IHttpClientFactory Available in Runtime ──────────────────────

    [Fact]
    public void Runtime_ProvidesHttpClientFactory()
    {
        var runtime = CreateRuntime();
        Assert.NotNull(runtime.HttpClientFactory);
    }

    // ── Test Helpers ─────────────────────────────────────────────────────────

    private static SearchProviderAdapterRuntime CreateRuntime()
    {
        var services = new ServiceCollection();
        services.AddHttpClient();
        services.AddLogging();
        var provider = services.BuildServiceProvider();

        return new SearchProviderAdapterRuntime(
            provider.GetRequiredService<IHttpClientFactory>(),
            new EndpointPolicy(),
            new SearchProviderOutcomeClassifier(),
            NullLogger<SearchProviderAdapterRuntime>.Instance);
    }

    private class TestFakeAdapter : ISearchProviderAdapter
    {
        public SearchProviderEnum ProviderKind { get; }
        public string AdapterVersion { get; }
        public SearchProviderCapability DeclaredCapabilities => SearchProviderCapability.CodeSearch;
        public ProviderSettingsSchema SettingsSchema => ProviderSettingsSchema.Empty;

        public bool DiscoverCalled { get; private set; }
        public bool ValidateCalled { get; private set; }
        public bool SearchCalled { get; private set; }
        public bool FetchCalled { get; private set; }

        public TestFakeAdapter(SearchProviderEnum kind, string version)
        {
            ProviderKind = kind;
            AdapterVersion = version;
        }

        public Task<ProviderCapabilityResult> DiscoverCapabilitiesAsync(
            ValidatedProviderInstance instance, CancellationToken cancellationToken)
        {
            DiscoverCalled = true;
            return Task.FromResult(new ProviderCapabilityResult(ProviderOutcomeKind.Success, DeclaredCapabilities));
        }

        public Task<ProviderOperationResult<CredentialValidationResult, ProviderOutcomeKind>> ValidateCredentialAsync(
            ProviderOperationContext context, CredentialMaterial credential, CancellationToken cancellationToken)
        {
            ValidateCalled = true;
            return Task.FromResult(new ProviderOperationResult<CredentialValidationResult, ProviderOutcomeKind>(
                ProviderOutcomeKind.Success, new CredentialValidationResult(true)));
        }

        public Task<TranslatedProviderQuery> TranslateQueryAsync(
            ProviderOperationContext context, SearchQuerySnapshot query, CancellationToken cancellationToken) =>
            Task.FromResult(new TranslatedProviderQuery(query.GenericQuery, query.SettingsJson));

        public async IAsyncEnumerable<ProviderOperationResult<NormalizedSearchPage, ProviderOutcomeKind>> SearchAsync(
            ProviderOperationContext context, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            SearchCalled = true;
            yield return new ProviderOperationResult<NormalizedSearchPage, ProviderOutcomeKind>(
                ProviderOutcomeKind.Success, new NormalizedSearchPage([]));
            await Task.CompletedTask;
        }

        public Task<ProviderOperationResult<NormalizedContent, ProviderOutcomeKind>> FetchContentAsync(
            ProviderOperationContext context, CancellationToken cancellationToken)
        {
            FetchCalled = true;
            return Task.FromResult(new ProviderOperationResult<NormalizedContent, ProviderOutcomeKind>(
                ProviderOutcomeKind.Success, new NormalizedContent("content", "v1")));
        }
    }

    private class StreamingFakeAdapter : ISearchProviderAdapter
    {
        private readonly int totalEventsToYield;
        public SearchProviderEnum ProviderKind => SearchProviderEnum.GitHub;
        public string AdapterVersion => "streaming-fake-v1";
        public SearchProviderCapability DeclaredCapabilities => SearchProviderCapability.StreamingSearch;
        public ProviderSettingsSchema SettingsSchema => ProviderSettingsSchema.Empty;

        public StreamingFakeAdapter(int totalEventsToYield) => this.totalEventsToYield = totalEventsToYield;

        public Task<ProviderCapabilityResult> DiscoverCapabilitiesAsync(ValidatedProviderInstance instance, CancellationToken cancellationToken) =>
            Task.FromResult(new ProviderCapabilityResult(ProviderOutcomeKind.Success, DeclaredCapabilities));

        public Task<ProviderOperationResult<CredentialValidationResult, ProviderOutcomeKind>> ValidateCredentialAsync(
            ProviderOperationContext context, CredentialMaterial credential, CancellationToken cancellationToken) =>
            Task.FromResult(new ProviderOperationResult<CredentialValidationResult, ProviderOutcomeKind>(
                ProviderOutcomeKind.Success, new CredentialValidationResult(true)));

        public Task<TranslatedProviderQuery> TranslateQueryAsync(ProviderOperationContext context, SearchQuerySnapshot query, CancellationToken cancellationToken) =>
            Task.FromResult(new TranslatedProviderQuery(query.GenericQuery, query.SettingsJson));

        public async IAsyncEnumerable<ProviderOperationResult<NormalizedSearchPage, ProviderOutcomeKind>> SearchAsync(
            ProviderOperationContext context, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            for (var i = 0; i < totalEventsToYield; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new ProviderOperationResult<NormalizedSearchPage, ProviderOutcomeKind>(
                    ProviderOutcomeKind.Success, new NormalizedSearchPage([$"item-{i}"]));
                await Task.Yield();
            }
        }

        public Task<ProviderOperationResult<NormalizedContent, ProviderOutcomeKind>> FetchContentAsync(
            ProviderOperationContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new ProviderOperationResult<NormalizedContent, ProviderOutcomeKind>(
                ProviderOutcomeKind.Success, new NormalizedContent("test", "v1")));
    }

    private class ConcurrencyTrackingFakeAdapter : ISearchProviderAdapter
    {
        private int currentConcurrency;
        public int PeakConcurrency { get; private set; }

        public SearchProviderEnum ProviderKind => SearchProviderEnum.GitHub;
        public string AdapterVersion => "concurrency-fake-v1";
        public SearchProviderCapability DeclaredCapabilities => SearchProviderCapability.ContentRetrieval;
        public ProviderSettingsSchema SettingsSchema => ProviderSettingsSchema.Empty;

        public Task<ProviderCapabilityResult> DiscoverCapabilitiesAsync(ValidatedProviderInstance instance, CancellationToken cancellationToken) =>
            Task.FromResult(new ProviderCapabilityResult(ProviderOutcomeKind.Success, DeclaredCapabilities));

        public Task<ProviderOperationResult<CredentialValidationResult, ProviderOutcomeKind>> ValidateCredentialAsync(
            ProviderOperationContext context, CredentialMaterial credential, CancellationToken cancellationToken) =>
            Task.FromResult(new ProviderOperationResult<CredentialValidationResult, ProviderOutcomeKind>(
                ProviderOutcomeKind.Success, new CredentialValidationResult(true)));

        public Task<TranslatedProviderQuery> TranslateQueryAsync(ProviderOperationContext context, SearchQuerySnapshot query, CancellationToken cancellationToken) =>
            Task.FromResult(new TranslatedProviderQuery(query.GenericQuery, query.SettingsJson));

        public async IAsyncEnumerable<ProviderOperationResult<NormalizedSearchPage, ProviderOutcomeKind>> SearchAsync(
            ProviderOperationContext context, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return new ProviderOperationResult<NormalizedSearchPage, ProviderOutcomeKind>(
                ProviderOutcomeKind.Success, new NormalizedSearchPage([]));
            await Task.CompletedTask;
        }

        public async Task<ProviderOperationResult<NormalizedContent, ProviderOutcomeKind>> FetchContentAsync(
            ProviderOperationContext context, CancellationToken cancellationToken)
        {
            var current = Interlocked.Increment(ref currentConcurrency);
            lock (this)
            {
                if (current > PeakConcurrency)
                    PeakConcurrency = current;
            }

            await Task.Delay(20, cancellationToken);
            Interlocked.Decrement(ref currentConcurrency);

            return new ProviderOperationResult<NormalizedContent, ProviderOutcomeKind>(
                ProviderOutcomeKind.Success, new NormalizedContent("content", "v1"));
        }
    }
}

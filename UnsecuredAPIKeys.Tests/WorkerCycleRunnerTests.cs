using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using UnsecuredAPIKeys.Data;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Data.DTOs;
using UnsecuredAPIKeys.Providers._Interfaces;
using UnsecuredAPIKeys.Services;
using Xunit;

namespace UnsecuredAPIKeys.Tests;

/// <summary>
/// Task 13.4 — One-Worker canary path under flags, per provider kind.
/// Drives <see cref="WorkerCycleRunner"/> against a fake Master and a fake streaming
/// adapter: sync → Claim → provider execution → provenance report → complete, with a
/// renewal-threshold crossing, lost-response tolerance, and no credential pools.
/// </summary>
public sealed class WorkerCycleRunnerTests
{
    private const string PageKey = "AKIAIOSFODNN7EXAMPLE";

    private sealed class FakeMasterApiClient : IMasterApiClient
    {
        public NodeSyncDTO SyncPayload = new();
        public CredentialClaimResponse? ClaimPayload;
        public HttpStatusCode ClaimStatus = HttpStatusCode.OK;
        public bool ThrowOnSync;
        public CredentialLeaseRenewResponse RenewPayload =
            new(true, DateTime.UtcNow.AddMinutes(5), null, 6);

        public readonly List<CredentialClaimRequest> Claims = [];
        public readonly List<(Guid Lease, CredentialLeaseRenewRequest Body)> Renews = [];
        public readonly List<(Guid Lease, CredentialLeaseCompleteRequest Body)> Completes = [];
        public readonly List<NodeBulkReportDto> Reports = [];

        public Task<NodeSyncDTO?> GetSyncAsync(CancellationToken cancellationToken)
        {
            if (ThrowOnSync)
            {
                throw new HttpRequestException("Master unreachable.");
            }

            return Task.FromResult<NodeSyncDTO?>(SyncPayload);
        }

        public Task<HttpResponseMessage> ClaimAsync(
            CredentialClaimRequest request, CancellationToken cancellationToken)
        {
            Claims.Add(request);
            if (ClaimStatus != HttpStatusCode.OK || ClaimPayload is null)
            {
                return Task.FromResult(new HttpResponseMessage(ClaimStatus));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(ClaimPayload), Encoding.UTF8, "application/json")
            });
        }

        public Task<HttpResponseMessage> RenewAsync(
            Guid leaseId, CredentialLeaseRenewRequest request, CancellationToken cancellationToken)
        {
            Renews.Add((leaseId, request));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(RenewPayload), Encoding.UTF8, "application/json")
            });
        }

        public Task<HttpResponseMessage> CompleteAsync(
            Guid leaseId, CredentialLeaseCompleteRequest request, CancellationToken cancellationToken)
        {
            Completes.Add((leaseId, request));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });
        }

        public Task<HttpResponseMessage> ReportAsync(
            NodeBulkReportDto report, CancellationToken cancellationToken)
        {
            Reports.Add(report);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"status":"success","addedCount":1,"acceptedCount":1,"rejectedCount":0,"rejected":[]}""",
                    Encoding.UTF8, "application/json")
            });
        }

        public Task<bool> SendHeartbeatAsync(CancellationToken cancellationToken) =>
            Task.FromResult(true);
    }

    private sealed class CanaryAdapter(SearchProviderEnum kind) : ISearchProviderAdapter
    {
        public SearchProviderEnum ProviderKind => kind;
        public string AdapterVersion => "canary-v1";
        public SearchProviderCapability DeclaredCapabilities =>
            SearchProviderCapability.CodeSearch |
            SearchProviderCapability.PaginatedSearch |
            SearchProviderCapability.ContentRetrieval;
        public ProviderSettingsSchema SettingsSchema => ProviderSettingsSchema.Empty;
        public readonly List<ProviderOperationContext> SeenContexts = [];
        public int ContentFetches;

        public Task<ProviderCapabilityResult> DiscoverCapabilitiesAsync(
            ValidatedProviderInstance instance, CancellationToken cancellationToken) =>
            Task.FromResult(new ProviderCapabilityResult(ProviderOutcomeKind.Success, DeclaredCapabilities));

        public Task<ProviderOperationResult<CredentialValidationResult, ProviderOutcomeKind>> ValidateCredentialAsync(
            ProviderOperationContext context, CredentialMaterial credential, CancellationToken cancellationToken) =>
            Task.FromResult(new ProviderOperationResult<CredentialValidationResult, ProviderOutcomeKind>(
                ProviderOutcomeKind.Success, new CredentialValidationResult(true)));

        public Task<TranslatedProviderQuery> TranslateQueryAsync(
            ProviderOperationContext context, SearchQuerySnapshot query, CancellationToken cancellationToken) =>
            Task.FromResult(new TranslatedProviderQuery(query.GenericQuery, query.SettingsJson));

        public async IAsyncEnumerable<ProviderOperationResult<NormalizedSearchPage, ProviderOutcomeKind>> SearchAsync(
            ProviderOperationContext context,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            SeenContexts.Add(context);
            await Task.Yield();
            yield return Page(context, "p2", PageKey);
            await Task.Yield();
            yield return Page(context, null, PageKey);
        }

        private ProviderOperationResult<NormalizedSearchPage, ProviderOutcomeKind> Page(
            ProviderOperationContext context, string? continuation, string key)
        {
            var instanceStable = kind == SearchProviderEnum.GitHub
                ? ProviderInstanceSchema.DefaultGitHubStableId
                : ProviderInstanceSchema.DefaultGitLabStableId;
            var provenance = kind == SearchProviderEnum.GitHub
                ? "https://github.com/octo/demo/blob/abc123/app/.env"
                : "https://gitlab.com/api/v4/projects/9999/repository/files/config%2F.env/raw?ref=main";
            var input = new ProviderResultInput(
                ProviderKind: kind,
                ProviderInstanceStableId: instanceStable,
                RepositoryStableId: "repo-1",
                ImmutableRevisionOrEquivalentVersion: "abc123",
                NormalizedFilePath: kind == SearchProviderEnum.GitHub ? "app/.env" : "config/.env",
                RepositoryOwner: "octo",
                RepositoryName: "demo",
                Snippet: $"leaked {key}",
                ProvenanceUrl: provenance,
                Branch: "main");
            return new ProviderOperationResult<NormalizedSearchPage, ProviderOutcomeKind>(
                ProviderOutcomeKind.Success,
                new NormalizedSearchPage(new object[] { input }),
                continuation);
        }

        public Task<ProviderOperationResult<NormalizedContent, ProviderOutcomeKind>> FetchContentAsync(
            ProviderOperationContext context, CancellationToken cancellationToken)
        {
            ContentFetches++;
            return Task.FromResult(new ProviderOperationResult<NormalizedContent, ProviderOutcomeKind>(
                ProviderOutcomeKind.Success,
                new NormalizedContent($"config {PageKey}", "content-v1")));
        }
    }

    private static (WorkerCycleRunner Runner, FakeMasterApiClient Master, CanaryAdapter Adapter) CreateRunner(
        SearchProviderEnum kind,
        TimeSpan leaseLifetime,
        double renewalThresholdSeconds,
        HttpStatusCode claimStatus = HttpStatusCode.OK)
    {
        var leaseId = Guid.NewGuid();
        var slotId = Guid.NewGuid();
        var credentialStableId = Guid.NewGuid();
        var workStableId = Guid.NewGuid();
        var instanceStable = kind == SearchProviderEnum.GitHub
            ? ProviderInstanceSchema.DefaultGitHubStableId
            : ProviderInstanceSchema.DefaultGitLabStableId;

        var master = new FakeMasterApiClient
        {
            SyncPayload = new NodeSyncDTO
            {
                Queries =
                [
                    new SearchQueryDTO { Id = 7, Query = "SECRET", IsEnabled = true }
                ],
                ProviderInstances =
                [
                    new ProviderInstanceDescriptor
                    {
                        StableId = instanceStable,
                        ProviderKind = kind,
                        DisplayName = $"{kind} SaaS"
                    }
                ]
            },
            ClaimStatus = claimStatus,
            ClaimPayload = new CredentialClaimResponse(
                leaseId,
                credentialStableId,
                instanceStable,
                DateTime.UtcNow.Add(leaseLifetime),
                5,
                renewalThresholdSeconds,
                "worker-claimed-material",
                Replayed: false,
                OperationSlotId: slotId,
                WorkItemStableId: workStableId,
                PartitionKey: "default"),
            RenewPayload = new CredentialLeaseRenewResponse(
                true, DateTime.UtcNow.AddMinutes(5), null, 6)
        };

        var adapter = new CanaryAdapter(kind);
        var runner = new WorkerCycleRunner(
            master,
            new SearchProviderAdapterRegistry([adapter]),
            new WorkerSecretExtractor(),
            new WorkerScraperOptions { MaxOperationsPerCycle = 10 },
            NullLogger<WorkerCycleRunner>.Instance);

        return (runner, master, adapter);
    }

    [Theory]
    [InlineData(SearchProviderEnum.GitHub)]
    [InlineData(SearchProviderEnum.GitLab)]
    public async Task CanaryOperation_RunsSyncClaimExecuteReportComplete(SearchProviderEnum kind)
    {
        var (runner, master, adapter) = CreateRunner(
            kind, TimeSpan.FromMinutes(5), renewalThresholdSeconds: 210);

        var result = await runner.RunCycleAsync(CancellationToken.None);

        Assert.False(result.Outage);
        Assert.Equal(1, result.QueriesSynced);
        Assert.Equal(1, result.OperationsExecuted);
        Assert.Empty(master.Renews);

        // Claim carried the synced query for a fresh Request ID.
        var claim = Assert.Single(master.Claims);
        Assert.Equal("SECRET", claim.GenericQuery);
        Assert.NotEqual(Guid.Empty, claim.RequestId);

        // Execution ran under the claimed Slot, Lease, and material.
        var context = Assert.Single(adapter.SeenContexts);
        var expected = master.ClaimPayload!;
        Assert.Equal(expected.OperationSlotId, context.SlotId);
        Assert.Equal(expected.LeaseId, context.LeaseId);
        Assert.Equal("worker-claimed-material", context.Credential!.Value);
        Assert.True(adapter.ContentFetches > 0);

        // Report carries immutable normalized provenance for every discovery.
        var bulk = Assert.Single(master.Reports);
        Assert.Equal(2, bulk.Discoveries.Count);
        foreach (var discovery in bulk.Discoveries)
        {
            Assert.Equal(PageKey, discovery.ApiKey);
            Assert.Equal(kind, discovery.ProviderKind);
            Assert.Equal(expected.ProviderInstanceStableId, discovery.ProviderInstanceStableId);
            Assert.Equal(expected.LeaseId, discovery.LeaseId);
            Assert.Equal(expected.OperationSlotId, discovery.OperationSlotId);
            Assert.Equal(expected.WorkItemStableId, discovery.WorkItemStableId);
        }

        // Completion reports the provider outcome with tracked identity.
        var (leaseId, complete) = Assert.Single(master.Completes);
        Assert.Equal(expected.LeaseId, leaseId);
        Assert.Equal("Success", complete.Outcome);
    }

    [Fact]
    public async Task CanaryOperation_RenewsAcrossThresholdWithTrackedRevision()
    {
        // A 2-second lease with a 60-second threshold is already renewable: renewal must
        // fire between pages and completion must carry the post-renewal revision.
        var (runner, master, _) = CreateRunner(
            SearchProviderEnum.GitHub, TimeSpan.FromSeconds(2), renewalThresholdSeconds: 60);

        var result = await runner.RunCycleAsync(CancellationToken.None);

        Assert.False(result.Outage);
        Assert.Equal(1, result.Renewals);

        var (renewLease, renew) = Assert.Single(master.Renews);
        Assert.Equal(master.ClaimPayload!.LeaseId, renewLease);
        Assert.Equal(5, renew.ExpectedRevision);

        var (_, complete) = Assert.Single(master.Completes);
        Assert.Equal(6, complete.ExpectedRevision);
        Assert.Equal("Success", complete.Outcome);
    }

    [Fact]
    public async Task Cycle_MasterOutage_SurfacesOutageWithoutFallback()
    {
        var (runner, master, adapter) = CreateRunner(
            SearchProviderEnum.GitHub, TimeSpan.FromMinutes(5), renewalThresholdSeconds: 210);
        master.ThrowOnSync = true;

        var result = await runner.RunCycleAsync(CancellationToken.None);

        Assert.True(result.Outage);
        Assert.Equal(0, result.OperationsExecuted);
        Assert.Empty(master.Claims);
        Assert.Empty(adapter.SeenContexts);
        Assert.Empty(master.Reports);
        Assert.Empty(master.Completes);
    }

    [Fact]
    public async Task Operation_ClaimRejected_DoesNotExecute()
    {
        var (runner, master, adapter) = CreateRunner(
            SearchProviderEnum.GitHub,
            TimeSpan.FromMinutes(5),
            renewalThresholdSeconds: 210,
            claimStatus: HttpStatusCode.Unauthorized);

        var result = await runner.RunCycleAsync(CancellationToken.None);

        Assert.False(result.Outage);
        Assert.Equal(0, result.OperationsExecuted);
        Assert.Equal(1, result.Failures);
        Assert.Empty(adapter.SeenContexts);
        Assert.Empty(master.Reports);
        Assert.Empty(master.Completes);
    }
}

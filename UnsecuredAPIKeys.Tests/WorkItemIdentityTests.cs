using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using UnsecuredAPIKeys.Data;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Data.Models;
using UnsecuredAPIKeys.Providers._Interfaces;
using UnsecuredAPIKeys.Services;
using UnsecuredAPIKeys.Tests.Infrastructure;
using Xunit;

namespace UnsecuredAPIKeys.Tests;

/// <summary>
/// Task 7.1/7.2 — Work Item identity, query hash derivation, and content version.
/// Task 7.3/7.4 — Result deduplication and checkpoint transaction tests.
/// Tests are written first; implementation in WorkService and ResultPersistenceService satisfies them.
/// </summary>
public sealed class WorkItemIdentityTests(SharedMemorySqliteWebApiFixture fixture)
    : IClassFixture<SharedMemorySqliteWebApiFixture>
{
    // ── AC-10.24: Content version derivation ────────────────────────────────────

    /// <summary>
    /// **Validates: AC-10.24** — Content version must be derived from content bytes,
    /// not a branch name or URL.
    /// </summary>
    [Fact]
    public void ContentVersion_IsSha256HexOverNormalizedUtf8Bytes()
    {
        var content = "line1\nline2\n";
        var version = WorkService.DeriveContentVersionFromText(content);

        Assert.Equal(64, version.Length);
        Assert.Matches("^[0-9a-f]{64}$", version);

        // Verify it's deterministic
        Assert.Equal(version, WorkService.DeriveContentVersionFromText(content));
    }

    [Fact]
    public void ContentVersion_NormalizesCrLfToLf()
    {
        var unix = "line1\nline2\n";
        var windows = "line1\r\nline2\r\n";
        Assert.Equal(
            WorkService.DeriveContentVersionFromText(unix),
            WorkService.DeriveContentVersionFromText(windows));
    }

    [Fact]
    public void ContentVersion_DifferentContentProducesDifferentVersion()
    {
        Assert.NotEqual(
            WorkService.DeriveContentVersionFromText("content-a"),
            WorkService.DeriveContentVersionFromText("content-b"));
    }

    [Fact]
    public void ContentVersion_EmptyBytesProducesKnownSha256()
    {
        // SHA-256 of empty input is a known constant
        var version = WorkService.DeriveContentVersion(ReadOnlySpan<byte>.Empty);
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", version);
    }

    // ── Effective query hash ────────────────────────────────────────────────────

    [Fact]
    public void EffectiveQueryHash_IsDeterministicFor_SameSnapshot()
    {
        var snap = new SearchQuerySnapshot("api_key", null, "{}");
        Assert.Equal(
            WorkService.DeriveEffectiveQueryHash(snap),
            WorkService.DeriveEffectiveQueryHash(snap));
    }

    [Fact]
    public void EffectiveQueryHash_DiffersWhenNativeOverrideAdded()
    {
        var a = new SearchQuerySnapshot("api_key", null, "{}");
        var b = new SearchQuerySnapshot("api_key", "language:csharp api_key", "{}");
        Assert.NotEqual(
            WorkService.DeriveEffectiveQueryHash(a),
            WorkService.DeriveEffectiveQueryHash(b));
    }

    [Fact]
    public void EffectiveQueryHash_DiffersWhenSettingsChange()
    {
        var a = new SearchQuerySnapshot("q", null, "{}");
        var b = new SearchQuerySnapshot("q", null, "{\"maxPages\":5}");
        Assert.NotEqual(
            WorkService.DeriveEffectiveQueryHash(a),
            WorkService.DeriveEffectiveQueryHash(b));
    }

    // ── Work Item creation and persistence ─────────────────────────────────────

    [Fact]
    public async Task CreateWorkItem_PersistsStableIdentityAndQueryHash()
    {
        using var scope = fixture.Services.CreateScope();
        var (workService, dbContext) = GetServices(scope);
        var instanceId = await GetDefaultInstanceIdAsync(dbContext);

        var snapshot = new SearchQuerySnapshot("api_key pattern", null, "{}");
        var result = await workService.CreateWorkItemAsync(
            instanceId,
            SearchProviderEnum.GitHub,
            CredentialGrantScope.Admin,
            principalTelegramId: null,
            snapshot,
            adapterVersion: "github-metadata-v1");

        Assert.NotEqual(Guid.Empty, result.WorkItemStableId);
        Assert.NotEqual(Guid.Empty, result.PartitionStableId);
        Assert.Equal(64, result.EffectiveQueryHash.Length);
        Assert.True(result.IsNew);

        dbContext.ChangeTracker.Clear();
        var persisted = await dbContext.WorkItems
            .Include(w => w.Partitions)
            .SingleAsync(w => w.StableId == result.WorkItemStableId);

        Assert.False(persisted.IsTerminal);
        Assert.Single(persisted.Partitions);
        Assert.Equal("default", persisted.Partitions.First().PartitionKey);
        Assert.Equal(SearchProviderEnum.GitHub, persisted.ProviderKind);
        Assert.Equal(CredentialGrantScope.Admin, persisted.PrincipalScope);
        Assert.Equal("github-metadata-v1", persisted.AdapterVersion);
    }

    [Fact]
    public async Task CreateWorkItem_ThrowsForUnknownProviderKind()
    {
        using var scope = fixture.Services.CreateScope();
        var (workService, dbContext) = GetServices(scope);
        var instanceId = await GetDefaultInstanceIdAsync(dbContext);

        var snapshot = new SearchQuerySnapshot("q", null, "{}");
        await Assert.ThrowsAsync<ArgumentException>(() =>
            workService.CreateWorkItemAsync(
                instanceId, SearchProviderEnum.Unknown,
                CredentialGrantScope.Admin, null, snapshot, "v1"));
    }

    // ── Checkpoint writes ───────────────────────────────────────────────────────

    [Fact]
    public async Task WriteCheckpoint_PersistsContinuationAndSafeCheckpoint()
    {
        using var scope = fixture.Services.CreateScope();
        var (workService, dbContext) = GetServices(scope);
        var instanceId = await GetDefaultInstanceIdAsync(dbContext);

        var workResult = await workService.CreateWorkItemAsync(
            instanceId, SearchProviderEnum.GitHub, CredentialGrantScope.Admin, null,
            new SearchQuerySnapshot("q", null, "{}"), "github-metadata-v1");

        var checkpointResult = await workService.WriteCheckpointAsync(
            workResult.PartitionStableId,
            newContinuation: "page=3&after=cursor123",
            newAdapterVersion: "github-metadata-v1",
            lastSafeCheckpoint: "page=2&after=cursor99");

        Assert.True(checkpointResult.Succeeded);

        dbContext.ChangeTracker.Clear();
        var partition = await dbContext.WorkPartitions
            .SingleAsync(p => p.StableId == workResult.PartitionStableId);

        Assert.Equal("page=3&after=cursor123", partition.Continuation);
        Assert.Equal("page=2&after=cursor99", partition.LastSafeCheckpoint);
        Assert.Equal("github-metadata-v1", partition.ContinuationAdapterVersion);
    }

    [Fact]
    public async Task WriteCheckpoint_FailsOnTerminalPartition()
    {
        using var scope = fixture.Services.CreateScope();
        var (workService, dbContext) = GetServices(scope);
        var instanceId = await GetDefaultInstanceIdAsync(dbContext);

        var workResult = await workService.CreateWorkItemAsync(
            instanceId, SearchProviderEnum.GitHub, CredentialGrantScope.Admin, null,
            new SearchQuerySnapshot("q", null, "{}"), "github-metadata-v1");

        await workService.TerminatePartitionAsync(workResult.PartitionStableId, isComplete: true);

        var checkpointResult = await workService.WriteCheckpointAsync(
            workResult.PartitionStableId, "next-cursor", "v1", "safe-cursor");

        Assert.False(checkpointResult.Succeeded);
        Assert.Contains("terminal", checkpointResult.ConflictReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TerminatePartition_MarksWorkItemTerminalWhenAllPartitionsDone()
    {
        using var scope = fixture.Services.CreateScope();
        var (workService, dbContext) = GetServices(scope);
        var instanceId = await GetDefaultInstanceIdAsync(dbContext);

        var workResult = await workService.CreateWorkItemAsync(
            instanceId, SearchProviderEnum.GitHub, CredentialGrantScope.Admin, null,
            new SearchQuerySnapshot("q", null, "{}"), "github-metadata-v1");

        await workService.TerminatePartitionAsync(workResult.PartitionStableId, isComplete: true);

        dbContext.ChangeTracker.Clear();
        var workItem = await dbContext.WorkItems.SingleAsync(w => w.StableId == workResult.WorkItemStableId);
        Assert.True(workItem.IsTerminal);
        Assert.True(workItem.IsComplete);
    }

    // ── Result deduplication ────────────────────────────────────────────────────

    [Fact]
    public async Task PersistResults_DeduplicatesExactDuplicatesWithinBatch()
    {
        using var scope = fixture.Services.CreateScope();
        var resultService = scope.ServiceProvider.GetRequiredService<ResultPersistenceService>();

        var instanceId = Guid.NewGuid();
        var input = CreateTestInput(instanceId, "repo-1", "sha-abc", "/src/key.py");

        var batchWithDuplicate = new[] { input, input }; // exact duplicate
        var result = await resultService.PersistResultsAsync(batchWithDuplicate);

        Assert.Equal(1, result.Persisted);
        Assert.Equal(1, result.Deduplicated);
        Assert.Equal(1, result.OutboxEnqueued);
    }

    [Fact]
    public async Task PersistResults_DeduplicatesAgainstExistingDatabaseRecord()
    {
        using var scope = fixture.Services.CreateScope();
        var resultService = scope.ServiceProvider.GetRequiredService<ResultPersistenceService>();

        var instanceId = Guid.NewGuid();
        var input = CreateTestInput(instanceId, "repo-2", "sha-def", "/src/key.js");

        // First persist
        var first = await resultService.PersistResultsAsync([input]);
        Assert.Equal(1, first.Persisted);

        // Second persist of the exact same dedup key
        var second = await resultService.PersistResultsAsync([input]);
        Assert.Equal(0, second.Persisted);
        Assert.Equal(1, second.Deduplicated);
    }

    [Fact]
    public async Task PersistResults_DifferentFilePathsAreSeparateResults()
    {
        using var scope = fixture.Services.CreateScope();
        var resultService = scope.ServiceProvider.GetRequiredService<ResultPersistenceService>();

        var instanceId = Guid.NewGuid();
        var inputA = CreateTestInput(instanceId, "repo-3", "sha-ghi", "/src/a.py");
        var inputB = CreateTestInput(instanceId, "repo-3", "sha-ghi", "/src/b.py");

        var result = await resultService.PersistResultsAsync([inputA, inputB]);
        Assert.Equal(2, result.Persisted);
        Assert.Equal(0, result.Deduplicated);
        Assert.Equal(2, result.OutboxEnqueued);
    }

    [Fact]
    public async Task PersistResults_EmptyInputReturnsZeroResults()
    {
        using var scope = fixture.Services.CreateScope();
        var resultService = scope.ServiceProvider.GetRequiredService<ResultPersistenceService>();

        var result = await resultService.PersistResultsAsync([]);
        Assert.Equal(0, result.Persisted);
        Assert.Equal(0, result.Deduplicated);
        Assert.Equal(0, result.OutboxEnqueued);
    }

    [Fact]
    public async Task PersistResults_OutboxRecordsCanBeRetrievedAndMarkedProcessed()
    {
        using var scope = fixture.Services.CreateScope();
        var resultService = scope.ServiceProvider.GetRequiredService<ResultPersistenceService>();
        var dbContext = scope.ServiceProvider.GetRequiredService<DBContext>();

        var instanceId = Guid.NewGuid();
        await resultService.PersistResultsAsync([
            CreateTestInput(instanceId, "repo-4", "sha-jkl", "/src/config.env")
        ]);

        var unprocessed = await resultService.GetUnprocessedOutboxRecordsAsync(100);
        var myRecord = unprocessed.FirstOrDefault(r => !r.IsProcessed);
        Assert.NotNull(myRecord);

        await resultService.MarkOutboxProcessedAsync([myRecord.Id]);

        dbContext.ChangeTracker.Clear();
        var updated = await dbContext.ResultOutboxRecords.SingleAsync(r => r.Id == myRecord.Id);
        Assert.True(updated.IsProcessed);
        Assert.NotNull(updated.ProcessedUtc);
    }

    [Fact]
    public async Task PersistResults_ThrowsForUnknownProviderKind()
    {
        using var scope = fixture.Services.CreateScope();
        var resultService = scope.ServiceProvider.GetRequiredService<ResultPersistenceService>();

        var invalidInput = CreateTestInput(Guid.NewGuid(), "repo", "sha", "/file.py") with
        {
            ProviderKind = SearchProviderEnum.Unknown
        };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            resultService.PersistResultsAsync([invalidInput]));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static (IWorkService, DBContext) GetServices(IServiceScope scope) =>
        (scope.ServiceProvider.GetRequiredService<IWorkService>(),
         scope.ServiceProvider.GetRequiredService<DBContext>());

    private static async Task<long> GetDefaultInstanceIdAsync(DBContext dbContext)
    {
        var instance = await dbContext.SearchProviderInstances
            .Where(i => i.StableId == Data.ProviderInstanceSchema.DefaultGitHubStableId)
            .Select(i => (long?)i.Id)
            .SingleOrDefaultAsync();

        if (instance.HasValue) return instance.Value;

        // Create the default instance if missing (test isolation)
        var now = DateTime.UtcNow;
        var created = new SearchProviderInstance
        {
            StableId = Data.ProviderInstanceSchema.DefaultGitHubStableId,
            ProviderKind = SearchProviderEnum.GitHub,
            DisplayName = "GitHub (test)",
            NormalizedScheme = "https",
            NormalizedHost = "api.github.com",
            NormalizedPort = 443,
            NormalizedBasePath = "/",
            IsEnabled = true,
            AllowGlobalPublicSearch = false,
            MaxConcurrentOperations = 4,
            SettingsVersion = 1,
            SettingsJson = "{}",
            PrivateNetworkAllowlistJson = "[]",
            CreatedUtc = now,
            UpdatedUtc = now
        };
        dbContext.SearchProviderInstances.Add(created);
        await dbContext.SaveChangesAsync();
        return created.Id;
    }

    private static ProviderResultInput CreateTestInput(
        Guid instanceId,
        string repoStableId,
        string revision,
        string filePath) =>
        new(
            ProviderKind: SearchProviderEnum.GitHub,
            ProviderInstanceStableId: instanceId,
            RepositoryStableId: repoStableId,
            ImmutableRevisionOrEquivalentVersion: revision,
            NormalizedFilePath: filePath,
            RepositoryOwner: "test-org",
            RepositoryName: "test-repo",
            Snippet: "some snippet",
            ProvenanceUrl: $"https://github.com/test-org/test-repo/blob/{revision}{filePath}");
}

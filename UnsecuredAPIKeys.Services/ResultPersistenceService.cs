using Microsoft.EntityFrameworkCore;
using UnsecuredAPIKeys.Data;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Data.Models;
using UnsecuredAPIKeys.Providers._Interfaces;

namespace UnsecuredAPIKeys.Services;


/// <summary>
/// Owns transactional result persistence with per-result deduplication and outbox enqueueing.
/// Dedup identity: (ProviderInstanceStableId, RepositoryStableId,
///   ImmutableRevisionOrEquivalentVersion, NormalizedFilePath).
/// Branch names and URLs are never used as dedup keys (they are mutable).
/// Partial persistence is safe: each result is committed independently within
/// the execution strategy, and the outbox enables replay.
/// </summary>
public sealed class ResultPersistenceService(DBContext dbContext)
{
    public const string DiscoveredEventKind = "result.discovered";
    public const string DuplicateEventKind = "result.duplicate";

    /// <summary>
    /// Persists the given results. Deduplicates within the batch and against existing records.
    /// Each new result is added to the transactional outbox for downstream delivery.
    /// Returns the aggregate statistics.
    /// </summary>
    public async Task<ResultPersistenceResult> PersistResultsAsync(
        IReadOnlyList<ProviderResultInput> results,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(results);
        if (results.Count == 0)
            return new ResultPersistenceResult(0, 0, 0);

        // Deduplicate within the batch first (first-wins per dedup key)
        var batchDedup = new HashSet<(string, string, string, string)>();
        var unique = new List<ProviderResultInput>(results.Count);
        var batchDuplicateCount = 0;

        foreach (var r in results)
        {
            ValidateResultInput(r);
            var key = (
                r.ProviderInstanceStableId.ToString("D"),
                r.RepositoryStableId,
                r.ImmutableRevisionOrEquivalentVersion,
                r.NormalizedFilePath);
            if (batchDedup.Add(key))
                unique.Add(r);
            else
                batchDuplicateCount++;
        }

        var persisted = 0;
        var deduplicated = batchDuplicateCount;
        var outboxEnqueued = 0;

        var executionStrategy = dbContext.Database.CreateExecutionStrategy();

        // Persist each unique result individually so a single failure does not
        // roll back successfully committed earlier results.
        foreach (var input in unique)
        {
            await executionStrategy.ExecuteAsync(async () =>
            {
                await using var tx = await dbContext.Database.BeginTransactionAsync(cancellationToken);
                var now = DateTime.UtcNow;

                var instanceKey = input.ProviderInstanceStableId.ToString("D");
                var existing = await dbContext.ResultDeduplicationRecords
                    .Where(d =>
                        d.ProviderInstanceStableId == instanceKey &&
                        d.RepositoryStableId == input.RepositoryStableId &&
                        d.ImmutableRevisionOrEquivalentVersion == input.ImmutableRevisionOrEquivalentVersion &&
                        d.NormalizedFilePath == input.NormalizedFilePath)
                    .SingleOrDefaultAsync(cancellationToken);

                if (existing is not null)
                {
                    // Update last-seen for telemetry but do not create a new result
                    existing.LastSeenUtc = now;
                    await dbContext.SaveChangesAsync(cancellationToken);
                    await tx.CommitAsync(cancellationToken);
                    Interlocked.Increment(ref deduplicated);
                    return;
                }

                var normalizedResult = new NormalizedResult
                {
                    ProviderKind = input.ProviderKind,
                    ProviderInstanceStableId = input.ProviderInstanceStableId,
                    RepositoryStableId = input.RepositoryStableId,
                    RepositoryOwner = input.RepositoryOwner,
                    RepositoryName = input.RepositoryName,
                    ImmutableRevisionOrEquivalentVersion = input.ImmutableRevisionOrEquivalentVersion,
                    NormalizedFilePath = input.NormalizedFilePath,
                    FileName = input.FileName,
                    LineNumber = input.LineNumber,
                    Snippet = Truncate(input.Snippet, 4096),
                    ProvenanceUrl = Truncate(input.ProvenanceUrl, 2048),
                    Branch = Truncate(input.Branch, 256),
                    SearchQueryId = input.SearchQueryId,
                    WorkItemId = input.WorkItemId,
                    WorkPartitionId = input.WorkPartitionId,
                    ProvenanceJson = input.ProvenanceJson,
                    DiscoveredUtc = now
                };
                dbContext.NormalizedResults.Add(normalizedResult);
                await dbContext.SaveChangesAsync(cancellationToken);

                // Write dedup record
                dbContext.ResultDeduplicationRecords.Add(new ResultDeduplicationRecord
                {
                    ProviderInstanceStableId = instanceKey,
                    RepositoryStableId = input.RepositoryStableId,
                    ImmutableRevisionOrEquivalentVersion = input.ImmutableRevisionOrEquivalentVersion,
                    NormalizedFilePath = input.NormalizedFilePath,
                    NormalizedResultId = normalizedResult.Id,
                    FirstDiscoveredUtc = now,
                    LastSeenUtc = now
                });

                // Write outbox record
                dbContext.ResultOutboxRecords.Add(new ResultOutboxRecord
                {
                    NormalizedResultId = normalizedResult.Id,
                    EventKind = DiscoveredEventKind,
                    PayloadJson = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        normalizedResultId = normalizedResult.Id,
                        providerKind = normalizedResult.ProviderKind.ToString(),
                        repositoryStableId = normalizedResult.RepositoryStableId,
                        normalizedFilePath = normalizedResult.NormalizedFilePath,
                        discoveredUtc = normalizedResult.DiscoveredUtc
                    }),
                    CreatedUtc = now
                });

                await dbContext.SaveChangesAsync(cancellationToken);
                await tx.CommitAsync(cancellationToken);

                Interlocked.Increment(ref persisted);
                Interlocked.Increment(ref outboxEnqueued);
            });
        }

        return new ResultPersistenceResult(persisted, deduplicated, outboxEnqueued);
    }

    /// <summary>
    /// Retrieves all unprocessed outbox records up to the given batch size.
    /// Callers must mark records as processed using <see cref="MarkOutboxProcessedAsync"/>.
    /// </summary>
    public async Task<IReadOnlyList<ResultOutboxRecord>> GetUnprocessedOutboxRecordsAsync(
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        if (batchSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(batchSize), "Batch size must be positive.");

        return await dbContext.ResultOutboxRecords
            .Where(o => !o.IsProcessed)
            .OrderBy(o => o.Id)
            .Take(batchSize)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Marks the given outbox records as processed.
    /// </summary>
    public async Task MarkOutboxProcessedAsync(
        IReadOnlyList<long> outboxIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(outboxIds);
        if (outboxIds.Count == 0)
            return;

        var now = DateTime.UtcNow;
        var executionStrategy = dbContext.Database.CreateExecutionStrategy();
        await executionStrategy.ExecuteAsync(async () =>
        {
            await using var tx = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            var records = await dbContext.ResultOutboxRecords
                .Where(o => outboxIds.Contains(o.Id) && !o.IsProcessed)
                .ToListAsync(cancellationToken);

            foreach (var record in records)
            {
                record.IsProcessed = true;
                record.ProcessedUtc = now;
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
        });
    }

    private static void ValidateResultInput(ProviderResultInput r)
    {
        if (r.ProviderKind == SearchProviderEnum.Unknown)
            throw new ArgumentException("Result Provider Kind must not be Unknown.");
        if (r.ProviderInstanceStableId == Guid.Empty)
            throw new ArgumentException("Result Provider Instance Stable ID must be non-empty.");
        if (string.IsNullOrWhiteSpace(r.RepositoryStableId))
            throw new ArgumentException("Result Repository Stable ID must be non-empty.");
        if (string.IsNullOrWhiteSpace(r.ImmutableRevisionOrEquivalentVersion))
            throw new ArgumentException("Result immutable revision or content version must be non-empty.");
        if (string.IsNullOrWhiteSpace(r.NormalizedFilePath))
            throw new ArgumentException("Result normalized file path must be non-empty.");
    }

    private static string? Truncate(string? value, int maxLength) =>
        value is null || value.Length <= maxLength ? value : value[..maxLength];
}

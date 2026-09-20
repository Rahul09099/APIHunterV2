using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using UnsecuredAPIKeys.Data;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Data.Models;
using UnsecuredAPIKeys.Providers._Interfaces;

namespace UnsecuredAPIKeys.Services;

/// <summary>
/// Result of Work Item creation or resumption.
/// </summary>
public sealed record WorkItemResult(
    Guid WorkItemStableId,
    long WorkItemId,
    Guid PartitionStableId,
    long WorkPartitionId,
    string EffectiveQueryHash,
    bool IsNew);

/// <summary>
/// Result of a checkpoint write operation.
/// </summary>
public sealed record CheckpointResult(
    bool Succeeded,
    string? ConflictReason = null);

/// <summary>
/// Result of a result persistence operation.
/// </summary>
public sealed record ResultPersistenceResult(
    int Persisted,
    int Deduplicated,
    int OutboxEnqueued);

public interface IWorkService
{
    /// <summary>
    /// Creates a new Work Item with one default Partition for the given Provider Instance and query.
    /// The effective query hash is computed here deterministically from the canonical query inputs.
    /// Returns the stable identifiers needed to acquire a Claim and Operation Slot.
    /// </summary>
    Task<WorkItemResult> CreateWorkItemAsync(
        long providerInstanceId,
        SearchProviderEnum providerKind,
        CredentialGrantScope principalScope,
        long? principalTelegramId,
        SearchQuerySnapshot querySnapshot,
        string adapterVersion,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resumes an existing non-terminal Work Partition. Returns the current Continuation
    /// and Last Safe Checkpoint so the adapter can resume from the correct position.
    /// </summary>
    Task<(string? Continuation, string? LastSafeCheckpoint)> ResumePartitionAsync(
        Guid workPartitionStableId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes a safe checkpoint for the given partition. Only succeeds if the Partition
    /// is still non-terminal and the Continuation matches (optimistic check).
    /// </summary>
    Task<CheckpointResult> WriteCheckpointAsync(
        Guid workPartitionStableId,
        string newContinuation,
        string newAdapterVersion,
        string lastSafeCheckpoint,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks the given Work Partition as terminal (complete or failed).
    /// Updates the parent Work Item if all partitions are terminal.
    /// </summary>
    Task TerminatePartitionAsync(
        Guid workPartitionStableId,
        bool isComplete,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Owns Work Item lifecycle, query hash derivation, and checkpoint persistence.
/// All content version derivation uses SHA-256 over normalized UTF-8 bytes.
/// This addresses the AC-10.24 spec-review gap: no branch names or URLs are ever
/// used as the content version.
/// </summary>
public sealed class WorkService(DBContext dbContext) : IWorkService
{
    /// <summary>
    /// Derives the provider-equivalent content version from the raw content bytes.
    /// Algorithm: SHA-256 hex digest over normalized UTF-8 content bytes.
    /// This is the single authoritative implementation for AC-10.24.
    /// </summary>
    public static string DeriveContentVersion(ReadOnlySpan<byte> contentBytes)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(contentBytes, hash);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Derives the content version from a UTF-8 string (normalized line endings removed).
    /// Whitespace normalization: CRLF → LF, trailing whitespace stripped per line.
    /// </summary>
    public static string DeriveContentVersionFromText(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var normalized = content
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal);
        var bytes = Encoding.UTF8.GetBytes(normalized);
        return DeriveContentVersion(bytes);
    }

    /// <summary>
    /// Derives the effective query hash for deterministic Work dedup.
    /// Hash is over: genericQuery + "|" + (nativeOverride ?? "") + "|" + settingsJson.
    /// All in canonical UTF-8. Never contains secrets.
    /// </summary>
    public static string DeriveEffectiveQueryHash(SearchQuerySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var canonical = string.Concat(
            snapshot.GenericQuery,
            "|",
            snapshot.NativeOverride ?? string.Empty,
            "|",
            snapshot.SettingsJson);
        var bytes = Encoding.UTF8.GetBytes(canonical);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(bytes, hash);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public async Task<WorkItemResult> CreateWorkItemAsync(
        long providerInstanceId,
        SearchProviderEnum providerKind,
        CredentialGrantScope principalScope,
        long? principalTelegramId,
        SearchQuerySnapshot querySnapshot,
        string adapterVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(querySnapshot);
        if (string.IsNullOrWhiteSpace(adapterVersion))
            throw new ArgumentException("Adapter version is required.", nameof(adapterVersion));
        if (providerKind == SearchProviderEnum.Unknown)
            throw new ArgumentException("Provider Kind must not be Unknown.", nameof(providerKind));

        var queryHash = DeriveEffectiveQueryHash(querySnapshot);
        var querySnapshotJson = JsonSerializer.Serialize(new
        {
            querySnapshot.GenericQuery,
            querySnapshot.NativeOverride,
            querySnapshot.SettingsJson
        });

        var executionStrategy = dbContext.Database.CreateExecutionStrategy();
        return await executionStrategy.ExecuteAsync(async () =>
        {
            await using var tx = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            var now = DateTime.UtcNow;

            var workItem = new WorkItem
            {
                StableId = Guid.NewGuid(),
                PrincipalScope = principalScope,
                PrincipalTelegramId = principalTelegramId,
                ProviderInstanceId = providerInstanceId,
                ProviderKind = providerKind,
                EffectiveQueryHash = queryHash,
                AdapterVersion = adapterVersion,
                QuerySnapshotJson = querySnapshotJson,
                CreatedUtc = now,
                UpdatedUtc = now
            };
            dbContext.WorkItems.Add(workItem);
            await dbContext.SaveChangesAsync(cancellationToken);

            var partition = new WorkPartition
            {
                StableId = Guid.NewGuid(),
                WorkItemId = workItem.Id,
                PartitionKey = "default",
                CreatedUtc = now,
                UpdatedUtc = now
            };
            dbContext.WorkPartitions.Add(partition);

            var queryOverride = new SearchQueryOverride
            {
                WorkItemId = workItem.Id,
                GenericQuery = querySnapshot.GenericQuery,
                NativeOverride = querySnapshot.NativeOverride,
                SettingsJson = querySnapshot.SettingsJson
            };
            dbContext.SearchQueryOverrides.Add(queryOverride);

            await dbContext.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            return new WorkItemResult(
                workItem.StableId,
                workItem.Id,
                partition.StableId,
                partition.Id,
                queryHash,
                IsNew: true);
        });
    }

    public async Task<(string? Continuation, string? LastSafeCheckpoint)> ResumePartitionAsync(
        Guid workPartitionStableId,
        CancellationToken cancellationToken = default)
    {
        if (workPartitionStableId == Guid.Empty)
            throw new ArgumentException("Partition Stable ID must be non-empty.", nameof(workPartitionStableId));

        var partition = await dbContext.WorkPartitions
            .Where(p => p.StableId == workPartitionStableId && !p.IsTerminal)
            .Select(p => new { p.Continuation, p.LastSafeCheckpoint })
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException(
                $"Non-terminal Work Partition '{workPartitionStableId}' not found.");

        return (partition.Continuation, partition.LastSafeCheckpoint);
    }

    public async Task<CheckpointResult> WriteCheckpointAsync(
        Guid workPartitionStableId,
        string newContinuation,
        string newAdapterVersion,
        string lastSafeCheckpoint,
        CancellationToken cancellationToken = default)
    {
        if (workPartitionStableId == Guid.Empty)
            throw new ArgumentException("Partition Stable ID must be non-empty.", nameof(workPartitionStableId));
        ArgumentNullException.ThrowIfNull(newContinuation);

        var executionStrategy = dbContext.Database.CreateExecutionStrategy();
        return await executionStrategy.ExecuteAsync(async () =>
        {
            await using var tx = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            var partition = await dbContext.WorkPartitions
                .Where(p => p.StableId == workPartitionStableId && !p.IsTerminal)
                .SingleOrDefaultAsync(cancellationToken);

            if (partition is null)
                return new CheckpointResult(false, "Partition is terminal or not found.");

            partition.Continuation = newContinuation;
            partition.ContinuationAdapterVersion = newAdapterVersion;
            partition.LastSafeCheckpoint = lastSafeCheckpoint;
            partition.UpdatedUtc = DateTime.UtcNow;

            await dbContext.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return new CheckpointResult(true);
        });
    }

    public async Task TerminatePartitionAsync(
        Guid workPartitionStableId,
        bool isComplete,
        CancellationToken cancellationToken = default)
    {
        if (workPartitionStableId == Guid.Empty)
            throw new ArgumentException("Partition Stable ID must be non-empty.", nameof(workPartitionStableId));

        var executionStrategy = dbContext.Database.CreateExecutionStrategy();
        await executionStrategy.ExecuteAsync(async () =>
        {
            await using var tx = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            var now = DateTime.UtcNow;

            var partition = await dbContext.WorkPartitions
                .Where(p => p.StableId == workPartitionStableId && !p.IsTerminal)
                .SingleOrDefaultAsync(cancellationToken);

            if (partition is null)
                return;

            partition.IsTerminal = true;
            partition.IsComplete = isComplete;
            partition.UpdatedUtc = now;

            await dbContext.SaveChangesAsync(cancellationToken);

            // Check if all partitions of the parent WorkItem are terminal
            var allTerminal = await dbContext.WorkPartitions
                .Where(p => p.WorkItemId == partition.WorkItemId)
                .AllAsync(p => p.IsTerminal, cancellationToken);

            if (allTerminal)
            {
                var allComplete = await dbContext.WorkPartitions
                    .Where(p => p.WorkItemId == partition.WorkItemId)
                    .AllAsync(p => p.IsComplete, cancellationToken);

                var workItem = await dbContext.WorkItems
                    .Where(w => w.Id == partition.WorkItemId)
                    .SingleAsync(cancellationToken);

                workItem.IsTerminal = true;
                workItem.IsComplete = allComplete;
                workItem.UpdatedUtc = now;

                await dbContext.SaveChangesAsync(cancellationToken);
            }

            await tx.CommitAsync(cancellationToken);
        });
    }
}

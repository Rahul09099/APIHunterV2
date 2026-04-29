using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using UnsecuredAPIKeys.Data;
using UnsecuredAPIKeys.Data.Models;

namespace UnsecuredAPIKeys.Services;

/// <summary>
/// Lightweight in-memory metrics tracker.
/// Collects scraping/verification stats and periodically flushes them to ApplicationSettings.
///
/// IMPACT ON PROJECT:
/// - Zero overhead on hot paths (Interlocked increments are lock-free)
/// - Adds a single DB write every ~60 seconds (negligible)
/// - Enables the Status screen to show live performance data
/// - No external dependencies required
/// </summary>
public class MetricsService
{
    // ── Scraper metrics ──────────────────────────────────────────────────────
    private long _totalFilesScanned;
    private long _totalKeysFound;
    private long _totalDuplicatesSkipped;
    private long _totalGitHubRequests;
    private long _totalGitHubRateLimitHits;

    // ── Verifier metrics ─────────────────────────────────────────────────────
    private long _totalKeysVerified;
    private long _totalValidFound;
    private long _totalInvalidFound;
    private long _totalNetworkErrors;

    // ── Per-provider latency tracking (ms totals + counts for average) ───────
    private readonly ConcurrentDictionary<string, (long TotalMs, long Count)> _providerLatency = new();

    // ── Session start ─────────────────────────────────────────────────────────
    private readonly DateTime _sessionStart = DateTime.UtcNow;
    private DateTime _lastFlush = DateTime.UtcNow;
    private const int FlushIntervalSeconds = 60;

    // ── Singleton ─────────────────────────────────────────────────────────────
    public static readonly MetricsService Instance = new();
    private MetricsService() { }

    // ── Scraper recording ─────────────────────────────────────────────────────

    public void RecordFileScanned() => Interlocked.Increment(ref _totalFilesScanned);
    public void RecordKeyFound() => Interlocked.Increment(ref _totalKeysFound);
    public void RecordDuplicate() => Interlocked.Increment(ref _totalDuplicatesSkipped);
    public void RecordGitHubRequest() => Interlocked.Increment(ref _totalGitHubRequests);
    public void RecordGitHubRateLimit() => Interlocked.Increment(ref _totalGitHubRateLimitHits);

    // ── Verifier recording ────────────────────────────────────────────────────

    public void RecordVerified() => Interlocked.Increment(ref _totalKeysVerified);
    public void RecordValid() => Interlocked.Increment(ref _totalValidFound);
    public void RecordInvalid() => Interlocked.Increment(ref _totalInvalidFound);
    public void RecordNetworkError() => Interlocked.Increment(ref _totalNetworkErrors);

    /// <summary>
    /// Records how long a provider validation took.
    /// Call this after every ValidateKeyAsync() call.
    /// </summary>
    public void RecordProviderLatency(string providerName, long elapsedMs)
    {
        _providerLatency.AddOrUpdate(
            providerName,
            (elapsedMs, 1),
            (_, existing) => (existing.TotalMs + elapsedMs, existing.Count + 1));
    }

    // ── Snapshot ──────────────────────────────────────────────────────────────

    public MetricsSnapshot GetSnapshot()
    {
        var uptime = DateTime.UtcNow - _sessionStart;

        var latencies = _providerLatency
            .Select(kv => new ProviderLatency
            {
                ProviderName = kv.Key,
                AverageMs = kv.Value.Count > 0 ? kv.Value.TotalMs / kv.Value.Count : 0,
                TotalCalls = kv.Value.Count
            })
            .OrderByDescending(p => p.TotalCalls)
            .ToList();

        return new MetricsSnapshot
        {
            SessionUptime = uptime,
            TotalFilesScanned = Volatile.Read(ref _totalFilesScanned),
            TotalKeysFound = Volatile.Read(ref _totalKeysFound),
            TotalDuplicatesSkipped = Volatile.Read(ref _totalDuplicatesSkipped),
            TotalGitHubRequests = Volatile.Read(ref _totalGitHubRequests),
            TotalGitHubRateLimitHits = Volatile.Read(ref _totalGitHubRateLimitHits),
            TotalKeysVerified = Volatile.Read(ref _totalKeysVerified),
            TotalValidFound = Volatile.Read(ref _totalValidFound),
            TotalInvalidFound = Volatile.Read(ref _totalInvalidFound),
            TotalNetworkErrors = Volatile.Read(ref _totalNetworkErrors),
            ProviderLatencies = latencies
        };
    }

    // ── DB flush ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Flushes current metrics to ApplicationSettings if the flush interval has elapsed.
    /// Call this from the main scraper/verifier loop — it's a no-op if called too frequently.
    /// </summary>
    public async Task FlushIfDueAsync(DBContext dbContext)
    {
        if ((DateTime.UtcNow - _lastFlush).TotalSeconds < FlushIntervalSeconds)
            return;

        _lastFlush = DateTime.UtcNow;

        try
        {
            var snapshot = GetSnapshot();
            await UpsertSettingAsync(dbContext, "metrics.session_start", _sessionStart.ToString("O"));
            await UpsertSettingAsync(dbContext, "metrics.keys_found", snapshot.TotalKeysFound.ToString());
            await UpsertSettingAsync(dbContext, "metrics.keys_verified", snapshot.TotalKeysVerified.ToString());
            await UpsertSettingAsync(dbContext, "metrics.valid_found", snapshot.TotalValidFound.ToString());
            await UpsertSettingAsync(dbContext, "metrics.network_errors", snapshot.TotalNetworkErrors.ToString());
            await UpsertSettingAsync(dbContext, "metrics.github_rate_limits", snapshot.TotalGitHubRateLimitHits.ToString());
            await UpsertSettingAsync(dbContext, "metrics.last_flush", DateTime.UtcNow.ToString("O"));
            await dbContext.SaveChangesAsync();
        }
        catch
        {
            // Metrics flush is best-effort — never crash the main loop
        }
    }

    private static async Task UpsertSettingAsync(DBContext dbContext, string key, string value)
    {
        var existing = await dbContext.ApplicationSettings.FindAsync(key);
        if (existing == null)
        {
            dbContext.ApplicationSettings.Add(new ApplicationSetting
            {
                Key = key,
                Value = value,
                Description = "Auto-generated metric"
            });
        }
        else
        {
            // ApplicationSetting uses init-only properties — remove and re-add to update
            dbContext.ApplicationSettings.Remove(existing);
            dbContext.ApplicationSettings.Add(new ApplicationSetting
            {
                Key = key,
                Value = value,
                Description = "Auto-generated metric"
            });
        }
    }
}

// ── Data transfer objects ─────────────────────────────────────────────────────

public class MetricsSnapshot
{
    public TimeSpan SessionUptime { get; init; }
    public long TotalFilesScanned { get; init; }
    public long TotalKeysFound { get; init; }
    public long TotalDuplicatesSkipped { get; init; }
    public long TotalGitHubRequests { get; init; }
    public long TotalGitHubRateLimitHits { get; init; }
    public long TotalKeysVerified { get; init; }
    public long TotalValidFound { get; init; }
    public long TotalInvalidFound { get; init; }
    public long TotalNetworkErrors { get; init; }
    public List<ProviderLatency> ProviderLatencies { get; init; } = [];
}

public class ProviderLatency
{
    public string ProviderName { get; init; } = "";
    public long AverageMs { get; init; }
    public long TotalCalls { get; init; }
}

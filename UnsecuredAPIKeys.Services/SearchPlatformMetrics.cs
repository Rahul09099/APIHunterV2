using System.Collections.Concurrent;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Interfaces;

namespace UnsecuredAPIKeys.Services;

/// <summary>Instrument kinds. Duration instruments are always histograms, never gauges.</summary>
public enum SearchPlatformMetricType
{
    Counter,
    Gauge,
    Histogram
}

/// <summary>Bounded worker Claim API outcome labels.</summary>
public enum WorkerClaimApiStatus
{
    Success,
    Conflict,
    AuthError,
    TransportError
}

/// <summary>Bounded worker renewal outcome labels.</summary>
public enum WorkerRenewalStatus
{
    Success,
    Failure
}

/// <summary>Bounded cooldown reason labels.</summary>
public enum CooldownReason
{
    RateLimited,
    ForbiddenScope,
    Transient
}

/// <summary>Claim outcome labels for <c>search_credential_claim_total</c>.</summary>
public enum ClaimOutcomeLabel
{
    Success,
    Unavailable
}

/// <summary>Histogram observation with explicit bucket boundaries.</summary>
public sealed record HistogramObservation(
    long[] BucketBoundsMs,
    long[] BucketCounts,
    double SumSeconds,
    long Count);

/// <summary>Point-in-time metric sample for administration and tests.</summary>
public sealed record MetricSample(
    string Name,
    SearchPlatformMetricType Type,
    IReadOnlyDictionary<string, string> Labels,
    double Value,
    HistogramObservation? Histogram = null);

/// <summary>
/// Low-cardinality platform telemetry (Wave 15, Task 15.4). Exactly the nine required
/// instruments; labels are restricted to provider kind names, instance stable IDs,
/// outcome names, and closed reason/status vocabularies. Credential, Lease, Work,
/// Telegram, Request, and node identifiers are unrepresentable: typed recording methods
/// accept only enums, GUIDs, counts, and durations, and the string core rejects unknown
/// metric names, unknown label keys, and out-of-vocabulary values.
/// </summary>
public sealed class SearchPlatformMetrics
{
    public const string ClaimTotal = "search_credential_claim_total";
    public const string ClaimWaitSeconds = "search_credential_claim_wait_seconds";
    public const string ActiveLeases = "search_credential_active_leases";
    public const string CooldownTotal = "search_credential_cooldown_total";
    public const string OperationTotal = "search_provider_operation_total";
    public const string OperationDurationSeconds = "search_provider_operation_duration_seconds";
    public const string WorkerClaimApiTotal = "search_worker_claim_api_total";
    public const string WorkerLeaseRenewalTotal = "search_worker_lease_renewal_total";
    public const string WorkerDiscoveryTotal = "search_worker_discovery_total";

    /// <summary>Bucket boundaries (seconds) shared by both duration histograms.</summary>
    public static readonly double[] DurationBucketsSeconds =
    [
        0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30, 60
    ];

    private sealed record InstrumentDefinition(
        SearchPlatformMetricType Type,
        string[] LabelKeys);

    private static readonly IReadOnlyDictionary<string, InstrumentDefinition> Instruments =
        new Dictionary<string, InstrumentDefinition>(StringComparer.Ordinal)
        {
            [ClaimTotal] = new(SearchPlatformMetricType.Counter, ["provider", "instance", "outcome"]),
            [ClaimWaitSeconds] = new(SearchPlatformMetricType.Histogram, ["provider", "instance"]),
            [ActiveLeases] = new(SearchPlatformMetricType.Gauge, ["provider", "instance"]),
            [CooldownTotal] = new(SearchPlatformMetricType.Counter, ["provider", "instance", "reason"]),
            [OperationTotal] = new(SearchPlatformMetricType.Counter, ["provider", "instance", "outcome"]),
            [OperationDurationSeconds] = new(SearchPlatformMetricType.Histogram, ["provider", "instance"]),
            [WorkerClaimApiTotal] = new(SearchPlatformMetricType.Counter, ["status"]),
            [WorkerLeaseRenewalTotal] = new(SearchPlatformMetricType.Counter, ["status"]),
            [WorkerDiscoveryTotal] = new(SearchPlatformMetricType.Counter, ["provider", "instance"])
        };

    private static readonly HashSet<string> ProviderNames = new(
        Enum.GetNames<SearchProviderEnum>(), StringComparer.Ordinal);

    private static readonly HashSet<string> OutcomeNames = new(
        Enum.GetNames<ProviderOutcomeKind>(), StringComparer.Ordinal);

    private static readonly HashSet<string> ClaimOutcomeNames = new(
        Enum.GetNames<ClaimOutcomeLabel>(), StringComparer.Ordinal);

    private static readonly HashSet<string> CooldownReasonNames = new(
        Enum.GetNames<CooldownReason>(), StringComparer.Ordinal);

    private static readonly HashSet<string> WorkerClaimStatusNames = new(
        Enum.GetNames<WorkerClaimApiStatus>(), StringComparer.Ordinal);

    private static readonly HashSet<string> WorkerRenewalStatusNames = new(
        Enum.GetNames<WorkerRenewalStatus>(), StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, long> counters = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> gauges = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, HistogramState> histograms = new(StringComparer.Ordinal);

    private sealed class HistogramState
    {
        private readonly object gate = new();
        private readonly long[] counts = new long[DurationBucketsSeconds.Length + 1];
        private double sum;
        private long total;

        public void Observe(double seconds)
        {
            var bucket = Array.FindIndex(
                DurationBucketsSeconds, bound => seconds <= bound);
            if (bucket < 0)
            {
                bucket = counts.Length - 1;
            }

            lock (gate)
            {
                counts[bucket]++;
                sum += seconds;
                total++;
            }
        }

        public HistogramObservation Snapshot(string name)
        {
            var boundsMs = DurationBucketsSeconds.Select(bound => (long)(bound * 1000)).ToArray();
            lock (gate)
            {
                return new HistogramObservation(
                    boundsMs, (long[])counts.Clone(), sum, total);
            }
        }
    }

    // ── Typed recording API (only closed vocabularies + GUIDs) ─────────────────

    public void RecordClaim(SearchProviderEnum provider, Guid instance, ClaimOutcomeLabel outcome) =>
        AddCounter(ClaimTotal, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["provider"] = provider.ToString(),
            ["instance"] = instance.ToString("D"),
            ["outcome"] = outcome.ToString()
        }, 1);

    public void ObserveClaimWait(SearchProviderEnum provider, Guid instance, TimeSpan elapsed) =>
        ObserveHistogram(ClaimWaitSeconds, Labels(provider, instance), elapsed.TotalSeconds);

    public void LeaseAcquired(SearchProviderEnum provider, Guid instance) =>
        AddGauge(ActiveLeases, Labels(provider, instance), 1);

    public void LeaseReleased(SearchProviderEnum provider, Guid instance) =>
        AddGauge(ActiveLeases, Labels(provider, instance), -1);

    public void RecordCooldown(SearchProviderEnum provider, Guid instance, CooldownReason reason) =>
        AddCounter(CooldownTotal, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["provider"] = provider.ToString(),
            ["instance"] = instance.ToString("D"),
            ["reason"] = reason.ToString()
        }, 1);

    public void RecordOperation(SearchProviderEnum provider, Guid instance, ProviderOutcomeKind outcome) =>
        AddCounter(OperationTotal, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["provider"] = provider.ToString(),
            ["instance"] = instance.ToString("D"),
            ["outcome"] = outcome.ToString()
        }, 1);

    public void ObserveOperationDuration(SearchProviderEnum provider, Guid instance, TimeSpan elapsed) =>
        ObserveHistogram(OperationDurationSeconds, Labels(provider, instance), elapsed.TotalSeconds);

    public void RecordWorkerClaim(WorkerClaimApiStatus status) =>
        AddCounter(WorkerClaimApiTotal, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["status"] = status.ToString()
        }, 1);

    public void RecordWorkerRenewal(WorkerRenewalStatus status) =>
        AddCounter(WorkerLeaseRenewalTotal, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["status"] = status.ToString()
        }, 1);

    public void AddWorkerDiscoveries(SearchProviderEnum provider, Guid instance, long count)
    {
        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        AddCounter(WorkerDiscoveryTotal, Labels(provider, instance), count);
    }

    // ── Validated string core ──────────────────────────────────────────────────

    /// <summary>
    /// Low-level recording with strict validation. Returns false instead of recording
    /// when the metric name, label keys, or label values fall outside the closed
    /// vocabularies. Used by integrations that only have string data.
    /// </summary>
    public bool TryRecordCounter(
        string name,
        IReadOnlyDictionary<string, string> labels,
        long amount = 1)
    {
        if (!TryValidate(name, labels, SearchPlatformMetricType.Counter))
        {
            return false;
        }

        AddCounter(name, labels, amount);
        return true;
    }

    public bool TryObserveHistogram(
        string name,
        IReadOnlyDictionary<string, string> labels,
        double seconds)
    {
        if (!TryValidate(name, labels, SearchPlatformMetricType.Histogram) ||
            double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0)
        {
            return false;
        }

        ObserveHistogram(name, labels, seconds);
        return true;
    }

    private static bool TryValidate(
        string name,
        IReadOnlyDictionary<string, string> labels,
        SearchPlatformMetricType expectedType)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            !Instruments.TryGetValue(name, out var definition) ||
            definition.Type != expectedType ||
            labels.Count != definition.LabelKeys.Length)
        {
            return false;
        }

        foreach (var key in definition.LabelKeys)
        {
            if (!labels.TryGetValue(key, out var value) || !IsAllowedValue(key, value))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAllowedValue(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64)
        {
            return false;
        }

        return key switch
        {
            "provider" => ProviderNames.Contains(value),
            "instance" => Guid.TryParseExact(value, "D", out _),
            "outcome" => OutcomeNames.Contains(value) || ClaimOutcomeNames.Contains(value),
            "reason" => CooldownReasonNames.Contains(value),
            "status" => WorkerClaimStatusNames.Contains(value) || WorkerRenewalStatusNames.Contains(value),
            _ => false
        };
    }

    private static Dictionary<string, string> Labels(SearchProviderEnum provider, Guid instance) =>
        new(StringComparer.Ordinal)
        {
            ["provider"] = provider.ToString(),
            ["instance"] = instance.ToString("D")
        };

    private void AddCounter(string name, IReadOnlyDictionary<string, string> labels, long amount) =>
        counters.AddOrUpdate(SerializeKey(name, labels), amount, (_, existing) => existing + amount);

    private void AddGauge(string name, IReadOnlyDictionary<string, string> labels, long delta) =>
        gauges.AddOrUpdate(SerializeKey(name, labels), delta, (_, existing) => existing + delta);

    private void ObserveHistogram(string name, IReadOnlyDictionary<string, string> labels, double seconds) =>
        histograms.GetOrAdd(SerializeKey(name, labels), _ => new HistogramState()).Observe(seconds);

    private static string SerializeKey(string name, IReadOnlyDictionary<string, string> labels) =>
        name + "{" + string.Join(",", labels.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key}={pair.Value}")) + "}";

    // ── Introspection ──────────────────────────────────────────────────────────

    public static IReadOnlyList<string> MetricNames { get; } =
        Instruments.Keys.OrderBy(name => name, StringComparer.Ordinal).ToArray();

    public static SearchPlatformMetricType GetInstrumentType(string name) =>
        Instruments.TryGetValue(name, out var definition)
            ? definition.Type
            : throw new KeyNotFoundException($"Unknown platform metric '{name}'.");

    public static IReadOnlyList<string> GetLabelKeys(string name) =>
        Instruments.TryGetValue(name, out var definition)
            ? definition.LabelKeys
            : throw new KeyNotFoundException($"Unknown platform metric '{name}'.");

    public IReadOnlyList<MetricSample> Snapshot()
    {
        var samples = new List<MetricSample>();
        foreach (var (key, value) in counters.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            samples.Add(ParseSample(key, SearchPlatformMetricType.Counter, value, null));
        }
        foreach (var (key, value) in gauges.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            samples.Add(ParseSample(key, SearchPlatformMetricType.Gauge, value, null));
        }
        foreach (var (key, state) in histograms.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var (name, labels) = ParseKey(key);
            samples.Add(new MetricSample(
                name, SearchPlatformMetricType.Histogram, labels, state.Snapshot(name).Count,
                state.Snapshot(name)));
        }
        return samples;
    }

    private static MetricSample ParseSample(
        string key, SearchPlatformMetricType type, double value, HistogramObservation? histogram)
    {
        var (name, labels) = ParseKey(key);
        return new MetricSample(name, type, labels, value, histogram);
    }

    private static (string Name, IReadOnlyDictionary<string, string> Labels) ParseKey(string key)
    {
        var brace = key.IndexOf('{');
        var name = brace < 0 ? key : key[..brace];
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        if (brace >= 0)
        {
            var body = key[(brace + 1)..^1];
            foreach (var pair in body.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var equals = pair.IndexOf('=');
                if (equals > 0)
                {
                    labels[pair[..equals]] = pair[(equals + 1)..];
                }
            }
        }
        return (name, labels);
    }
}

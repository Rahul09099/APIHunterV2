using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using UnsecuredAPIKeys.Data;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Data.Models;
using UnsecuredAPIKeys.Providers._Interfaces;
using UnsecuredAPIKeys.Services;
using Xunit;

namespace UnsecuredAPIKeys.Tests;

/// <summary>
/// Tasks 15.3/15.4 — Platform metrics contract tests.
/// Proves all nine instruments exist with exact names and bounded label sets, both
/// duration instruments are histograms with configured buckets, the string core rejects
/// unknown metrics/keys/out-of-vocabulary values, and the typed API admits no free-form
/// identifiers (credential/Lease/Work/Telegram/Request/node IDs are unrepresentable).
/// </summary>
public sealed class SearchPlatformMetricsTests
{
    [Fact]
    public void AllNineInstruments_ExistWithExactNames()
    {
        Assert.Equal(
            new[]
            {
                "search_credential_active_leases",
                "search_credential_claim_total",
                "search_credential_claim_wait_seconds",
                "search_credential_cooldown_total",
                "search_provider_operation_duration_seconds",
                "search_provider_operation_total",
                "search_worker_claim_api_total",
                "search_worker_discovery_total",
                "search_worker_lease_renewal_total"
            },
            SearchPlatformMetrics.MetricNames);
    }

    [Fact]
    public void DurationInstruments_AreHistogramsWithConfiguredBuckets()
    {
        foreach (var name in new[]
                 {
                     SearchPlatformMetrics.ClaimWaitSeconds,
                     SearchPlatformMetrics.OperationDurationSeconds
                 })
        {
            Assert.Equal(SearchPlatformMetricType.Histogram, SearchPlatformMetrics.GetInstrumentType(name));
        }

        Assert.NotEmpty(SearchPlatformMetrics.DurationBucketsSeconds);
        Assert.True(SearchPlatformMetrics.DurationBucketsSeconds.SequenceEqual(
            SearchPlatformMetrics.DurationBucketsSeconds.OrderBy(bound => bound)));
    }

    [Fact]
    public void CounterAndGaugeInstruments_HaveExpectedTypes()
    {
        Assert.Equal(SearchPlatformMetricType.Counter, SearchPlatformMetrics.GetInstrumentType(SearchPlatformMetrics.ClaimTotal));
        Assert.Equal(SearchPlatformMetricType.Gauge, SearchPlatformMetrics.GetInstrumentType(SearchPlatformMetrics.ActiveLeases));
        Assert.Equal(SearchPlatformMetricType.Counter, SearchPlatformMetrics.GetInstrumentType(SearchPlatformMetrics.CooldownTotal));
        Assert.Equal(SearchPlatformMetricType.Counter, SearchPlatformMetrics.GetInstrumentType(SearchPlatformMetrics.OperationTotal));
        Assert.Equal(SearchPlatformMetricType.Counter, SearchPlatformMetrics.GetInstrumentType(SearchPlatformMetrics.WorkerClaimApiTotal));
        Assert.Equal(SearchPlatformMetricType.Counter, SearchPlatformMetrics.GetInstrumentType(SearchPlatformMetrics.WorkerLeaseRenewalTotal));
        Assert.Equal(SearchPlatformMetricType.Counter, SearchPlatformMetrics.GetInstrumentType(SearchPlatformMetrics.WorkerDiscoveryTotal));
    }

    [Fact]
    public void LabelKeys_AreBoundedPerInstrument()
    {
        Assert.Equal(
            ["provider", "instance", "outcome"],
            SearchPlatformMetrics.GetLabelKeys(SearchPlatformMetrics.ClaimTotal));
        Assert.Equal(
            ["provider", "instance"],
            SearchPlatformMetrics.GetLabelKeys(SearchPlatformMetrics.ClaimWaitSeconds));
        Assert.Equal(
            ["provider", "instance", "reason"],
            SearchPlatformMetrics.GetLabelKeys(SearchPlatformMetrics.CooldownTotal));
        Assert.Equal(
            ["status"],
            SearchPlatformMetrics.GetLabelKeys(SearchPlatformMetrics.WorkerClaimApiTotal));
    }

    [Fact]
    public void StringCore_RejectsUnknownMetricsKeysAndValues()
    {
        var metrics = new SearchPlatformMetrics();
        var instance = Guid.NewGuid().ToString("D");

        Assert.False(metrics.TryRecordCounter(
            "search_github_requests_total",
            new Dictionary<string, string> { ["provider"] = "GitHub" }));
        Assert.False(metrics.TryRecordCounter(
            SearchPlatformMetrics.ClaimTotal,
            new Dictionary<string, string>
            {
                ["provider"] = "GitHub",
                ["instance"] = instance
            }));
        Assert.False(metrics.TryRecordCounter(
            SearchPlatformMetrics.ClaimTotal,
            new Dictionary<string, string>
            {
                ["provider"] = "NotAProvider",
                ["instance"] = instance,
                ["outcome"] = "Success"
            }));
        Assert.False(metrics.TryRecordCounter(
            SearchPlatformMetrics.ClaimTotal,
            new Dictionary<string, string>
            {
                ["provider"] = "GitHub",
                ["instance"] = instance,
                ["outcome"] = "ghp_canary_credential_material"
            }));
        Assert.False(metrics.TryRecordCounter(
            SearchPlatformMetrics.CooldownTotal,
            new Dictionary<string, string>
            {
                ["provider"] = "GitHub",
                ["instance"] = instance,
                ["reason"] = Guid.NewGuid().ToString("D")
            }));
        Assert.False(metrics.TryObserveHistogram(
            SearchPlatformMetrics.ClaimWaitSeconds,
            new Dictionary<string, string>
            {
                ["provider"] = "GitHub",
                ["instance"] = "not-a-guid"
            },
            1.0));
        Assert.Empty(metrics.Snapshot());
    }

    [Fact]
    public void TypedApi_AdmitsNoFreeFormIdentifiers()
    {
        // Every public recording method takes only enums, GUIDs, counts, and durations.
        // Credential, Lease, Work, Telegram, Request, and node identifiers cannot be passed.
        var forbiddenFragments = new[] { "credential", "lease", "work", "telegram", "request", "node", "token", "secret" };

        foreach (var method in typeof(SearchPlatformMetrics).GetMethods(
                     BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            if (method.Name is nameof(SearchPlatformMetrics.Snapshot)
                or nameof(SearchPlatformMetrics.TryRecordCounter)
                or nameof(SearchPlatformMetrics.TryObserveHistogram))
            {
                continue;
            }

            foreach (var parameter in method.GetParameters())
            {
                var typeName = parameter.ParameterType.FullName ?? string.Empty;
                Assert.DoesNotContain("System.String", typeName, StringComparison.Ordinal);
                foreach (var fragment in forbiddenFragments)
                {
                    Assert.DoesNotContain(fragment, parameter.Name ?? string.Empty, StringComparison.OrdinalIgnoreCase);
                }
            }
        }
    }

    [Fact]
    public void TypedRecording_ProducesBoundedSnapshot()
    {
        var metrics = new SearchPlatformMetrics();
        var instance = Guid.NewGuid();

        metrics.RecordClaim(SearchProviderEnum.GitHub, instance, ClaimOutcomeLabel.Success);
        metrics.RecordClaim(SearchProviderEnum.GitHub, instance, ClaimOutcomeLabel.Unavailable);
        metrics.ObserveClaimWait(SearchProviderEnum.GitHub, instance, TimeSpan.FromMilliseconds(250));
        metrics.LeaseAcquired(SearchProviderEnum.GitHub, instance);
        metrics.LeaseAcquired(SearchProviderEnum.GitHub, instance);
        metrics.LeaseReleased(SearchProviderEnum.GitHub, instance);
        metrics.RecordCooldown(SearchProviderEnum.GitHub, instance, CooldownReason.RateLimited);
        metrics.RecordOperation(SearchProviderEnum.GitLab, instance, ProviderOutcomeKind.AuthInvalid);
        metrics.ObserveOperationDuration(SearchProviderEnum.GitLab, instance, TimeSpan.FromSeconds(2));
        metrics.RecordWorkerClaim(WorkerClaimApiStatus.Success);
        metrics.RecordWorkerRenewal(WorkerRenewalStatus.Failure);
        metrics.AddWorkerDiscoveries(SearchProviderEnum.GitHub, instance, 7);

        var snapshot = metrics.Snapshot();
        Assert.Equal(10, snapshot.Count);

        var gauge = snapshot.Single(sample =>
            sample.Name == SearchPlatformMetrics.ActiveLeases);
        Assert.Equal(SearchPlatformMetricType.Gauge, gauge.Type);
        Assert.Equal(1, gauge.Value);

        var wait = snapshot.Single(sample =>
            sample.Name == SearchPlatformMetrics.ClaimWaitSeconds);
        Assert.Equal(SearchPlatformMetricType.Histogram, wait.Type);
        Assert.NotNull(wait.Histogram);
        Assert.Equal(1, wait.Histogram.Count);
        Assert.True(wait.Histogram.SumSeconds > 0);

        var discoveries = snapshot.Single(sample =>
            sample.Name == SearchPlatformMetrics.WorkerDiscoveryTotal);
        Assert.Equal(7, discoveries.Value);

        var serialized = JsonSerializer.Serialize(snapshot);
        Assert.DoesNotContain("ghp_", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Histogram_BucketsObservationsDeterministically()
    {
        var metrics = new SearchPlatformMetrics();
        var instance = Guid.NewGuid();

        metrics.ObserveOperationDuration(SearchProviderEnum.GitHub, instance, TimeSpan.FromMilliseconds(5));
        metrics.ObserveOperationDuration(SearchProviderEnum.GitHub, instance, TimeSpan.FromMilliseconds(300));
        metrics.ObserveOperationDuration(SearchProviderEnum.GitHub, instance, TimeSpan.FromMinutes(2));

        var histogram = metrics.Snapshot()
            .Single(sample => sample.Name == SearchPlatformMetrics.OperationDurationSeconds)
            .Histogram;
        Assert.NotNull(histogram);
        Assert.Equal(3, histogram.Count);

        var bounds = SearchPlatformMetrics.DurationBucketsSeconds;
        var fastBucket = Array.FindIndex(bounds, bound => 0.005 <= bound);
        var midBucket = Array.FindIndex(bounds, bound => 0.3 <= bound);
        Assert.True(histogram.BucketCounts[fastBucket] >= 1);
        Assert.True(histogram.BucketCounts[midBucket] >= 1);
        Assert.Equal(1, histogram.BucketCounts[^1]);
    }
}

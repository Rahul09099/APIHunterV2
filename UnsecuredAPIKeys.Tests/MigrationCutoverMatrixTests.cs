using Microsoft.Extensions.Configuration;
using UnsecuredAPIKeys.Services;
using Xunit;

namespace UnsecuredAPIKeys.Tests;

/// <summary>
/// Task 16.1 — Fail-closed flag matrix against durable migration markers.
/// Combines the Task 2.4 configuration contract with pre-cutover/cutover/post-cutover
/// marker states: dependencies, pre-cutover-only local fallback, no automatic fallback
/// after failure, durable post-cutover prohibitions, and unhealthy invalid combinations.
/// </summary>
public sealed class MigrationCutoverMatrixTests
{
    private static IConfiguration ConfigWith(params (string Key, bool Value)[] flags)
    {
        var values = flags.ToDictionary(
            flag => flag.Key,
            flag => (string?)(flag.Value ? "true" : "false"),
            StringComparer.Ordinal);
        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }

    private static SearchPlatformPrerequisiteReadiness ReadyPrereqs(
        WorkerClaimsCutoverStage stage = WorkerClaimsCutoverStage.PreCutover) => new(
        ProtectedStorageReady: true,
        ProviderInstancesReady: true,
        ClaimApiReady: true,
        RenewalApiReady: true,
        CompletionApiReady: true,
        DurableMarkersReady: true,
        CutoverStage: stage);

    private static SearchPlatformFeatureFlagMatrixResult Evaluate(
        IConfiguration configuration,
        SearchPlatformPrerequisiteReadiness prerequisites) =>
        new SearchPlatformFeatureFlagMatrix(configuration).Evaluate(prerequisites);

    [Fact]
    public void PreCutover_FullStack_Ready()
    {
        var result = Evaluate(
            ConfigWith(
                (SearchPlatformFeatureFlagNames.ProtectedStorageEnabled, true),
                (SearchPlatformFeatureFlagNames.EnvironmentBootstrapEnabled, true),
                (SearchPlatformFeatureFlagNames.SchedulerEnabled, true),
                (SearchPlatformFeatureFlagNames.MasterScraperClaimsEnabled, true),
                (SearchPlatformFeatureFlagNames.WorkerClaimsEnabled, true),
                (SearchPlatformFeatureFlagNames.LegacyTokenSyncEnabled, true),
                (SearchPlatformFeatureFlagNames.AllowWorkerLocalFallback, false),
                (SearchPlatformFeatureFlagNames.InstancesEnabled, true)),
            ReadyPrereqs(WorkerClaimsCutoverStage.PreCutover));

        Assert.True(result.IsReady);
        Assert.Empty(result.Failures);
    }

    [Fact]
    public void PreCutover_LocalFallback_PermittedOnlyHere()
    {
        var result = Evaluate(
            ConfigWith(
                (SearchPlatformFeatureFlagNames.ProtectedStorageEnabled, true),
                (SearchPlatformFeatureFlagNames.SchedulerEnabled, true),
                (SearchPlatformFeatureFlagNames.InstancesEnabled, true),
                (SearchPlatformFeatureFlagNames.AllowWorkerLocalFallback, true)),
            ReadyPrereqs(WorkerClaimsCutoverStage.PreCutover));

        Assert.True(result.IsReady);
    }

    [Fact]
    public void PostCutover_LocalFallback_ProhibitedDurably()
    {
        var result = Evaluate(
            ConfigWith(
                (SearchPlatformFeatureFlagNames.ProtectedStorageEnabled, true),
                (SearchPlatformFeatureFlagNames.SchedulerEnabled, true),
                (SearchPlatformFeatureFlagNames.InstancesEnabled, true),
                (SearchPlatformFeatureFlagNames.AllowWorkerLocalFallback, true)),
            ReadyPrereqs(WorkerClaimsCutoverStage.PostCutover));

        Assert.False(result.IsReady);
        Assert.Contains(result.Failures, failure =>
            failure.Contains("fallback", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PostCutover_LegacyTokenSync_ProhibitedDurably()
    {
        var result = Evaluate(
            ConfigWith(
                (SearchPlatformFeatureFlagNames.ProtectedStorageEnabled, true),
                (SearchPlatformFeatureFlagNames.SchedulerEnabled, true),
                (SearchPlatformFeatureFlagNames.InstancesEnabled, true),
                (SearchPlatformFeatureFlagNames.LegacyTokenSyncEnabled, true)),
            ReadyPrereqs(WorkerClaimsCutoverStage.PostCutover));

        Assert.False(result.IsReady);
        Assert.Contains(result.Failures, failure =>
            failure.Contains("legacy", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PostCutover_Clean_Ready()
    {
        var result = Evaluate(
            ConfigWith(
                (SearchPlatformFeatureFlagNames.ProtectedStorageEnabled, true),
                (SearchPlatformFeatureFlagNames.SchedulerEnabled, true),
                (SearchPlatformFeatureFlagNames.MasterScraperClaimsEnabled, true),
                (SearchPlatformFeatureFlagNames.WorkerClaimsEnabled, true),
                (SearchPlatformFeatureFlagNames.LegacyTokenSyncEnabled, false),
                (SearchPlatformFeatureFlagNames.AllowWorkerLocalFallback, false),
                (SearchPlatformFeatureFlagNames.InstancesEnabled, true)),
            ReadyPrereqs(WorkerClaimsCutoverStage.PostCutover));

        Assert.True(result.IsReady);
    }

    [Fact]
    public void UnavailableStage_WithFallback_WithholdsReadiness()
    {
        var result = Evaluate(
            ConfigWith((SearchPlatformFeatureFlagNames.AllowWorkerLocalFallback, true)),
            ReadyPrereqs(WorkerClaimsCutoverStage.Unavailable));

        Assert.False(result.IsReady);
    }

    [Fact]
    public void Scheduler_RequiresProtectedStorageAndInstances()
    {
        var withoutStorage = Evaluate(
            ConfigWith(
                (SearchPlatformFeatureFlagNames.SchedulerEnabled, true),
                (SearchPlatformFeatureFlagNames.InstancesEnabled, true)),
            ReadyPrereqs() with { ProtectedStorageReady = false });
        Assert.False(withoutStorage.IsReady);

        var withoutInstances = Evaluate(
            ConfigWith(
                (SearchPlatformFeatureFlagNames.ProtectedStorageEnabled, true),
                (SearchPlatformFeatureFlagNames.SchedulerEnabled, true),
                (SearchPlatformFeatureFlagNames.InstancesEnabled, true)),
            ReadyPrereqs() with { ProviderInstancesReady = false });
        Assert.False(withoutInstances.IsReady);
    }

    [Fact]
    public void MasterClaims_RequireScheduler()
    {
        var result = Evaluate(
            ConfigWith((SearchPlatformFeatureFlagNames.MasterScraperClaimsEnabled, true)),
            ReadyPrereqs());

        Assert.False(result.IsReady);
        Assert.Contains(result.Failures, failure =>
            failure.Contains("common credential scheduler", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void WorkerClaims_RequireSchedulerAndAllThreeApis()
    {
        foreach (var missing in new[]
                 {
                     nameof(SearchPlatformPrerequisiteReadiness.ClaimApiReady),
                     nameof(SearchPlatformPrerequisiteReadiness.RenewalApiReady),
                     nameof(SearchPlatformPrerequisiteReadiness.CompletionApiReady)
                 })
        {
            var prerequisites = ReadyPrereqs() with
            {
                ClaimApiReady = missing != nameof(SearchPlatformPrerequisiteReadiness.ClaimApiReady),
                RenewalApiReady = missing != nameof(SearchPlatformPrerequisiteReadiness.RenewalApiReady),
                CompletionApiReady = missing != nameof(SearchPlatformPrerequisiteReadiness.CompletionApiReady)
            };
            var result = Evaluate(
                ConfigWith(
                    (SearchPlatformFeatureFlagNames.ProtectedStorageEnabled, true),
                    (SearchPlatformFeatureFlagNames.SchedulerEnabled, true),
                    (SearchPlatformFeatureFlagNames.WorkerClaimsEnabled, true),
                    (SearchPlatformFeatureFlagNames.InstancesEnabled, true)),
                prerequisites);

            Assert.False(result.IsReady);
        }
    }

    [Fact]
    public void InvalidFlagValue_IsUnhealthyAndDisabled()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [SearchPlatformFeatureFlagNames.ProtectedStorageEnabled] = "true",
                [SearchPlatformFeatureFlagNames.SchedulerEnabled] = "yes",
                [SearchPlatformFeatureFlagNames.InstancesEnabled] = "true"
            })
            .Build();

        var result = Evaluate(configuration, ReadyPrereqs());

        Assert.False(result.IsReady);
        Assert.Contains(result.Failures, failure =>
            failure.Contains(SearchPlatformFeatureFlagNames.SchedulerEnabled, StringComparison.Ordinal));
    }
}

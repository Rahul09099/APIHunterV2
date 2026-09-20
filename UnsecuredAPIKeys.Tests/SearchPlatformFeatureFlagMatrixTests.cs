using System.Text.Json;
using Microsoft.Extensions.Configuration;
using UnsecuredAPIKeys.Services;
using Xunit;

namespace UnsecuredAPIKeys.Tests;

public sealed class SearchPlatformFeatureFlagConfigurationTests
{
    [Fact]
    public void MissingConfiguration_DeniesAllEightFeatureFlagsByDefault()
    {
        var result = Evaluate(new Dictionary<string, string?>(), AllReady());

        Assert.True(result.IsReady);
        Assert.Equal(8, result.Flags.AsDictionary().Count);
        Assert.All(result.Flags.AsDictionary(), entry => Assert.False(entry.Value));
    }

    [Fact]
    public void ExactConfigurationKeys_BindAllEightFeatureFlags()
    {
        foreach (var key in SearchPlatformFeatureFlagNames.All)
        {
            var values = DisabledConfiguration();
            values[key] = "true";

            var result = Evaluate(values, AllReady());

            Assert.True(result.Flags.AsDictionary()[key], $"Expected '{key}' to bind as enabled.");
            Assert.All(
                result.Flags.AsDictionary().Where(entry => entry.Key != key),
                entry => Assert.False(entry.Value));
        }
    }

    [Fact]
    public void MalformedConfiguration_IsRejectedAndTheFlagRemainsDisabled()
    {
        const string canary = "not-a-secret-but-must-not-be-treated-as-true";
        var values = DisabledConfiguration();
        values[SearchPlatformFeatureFlagNames.SchedulerEnabled] = canary;

        var result = Evaluate(values, AllReady());

        Assert.False(result.IsReady);
        Assert.False(result.Flags.SchedulerEnabled);
        Assert.DoesNotContain(result.Failures, failure => failure.Contains(canary, StringComparison.Ordinal));
    }

    [Fact]
    public void AppSettings_DefinesEveryRequiredFlagAsDenyByDefault()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;

        Assert.False(root.GetProperty("SearchCredentials").GetProperty("ProtectedStorageEnabled").GetBoolean());
        Assert.False(root.GetProperty("SearchCredentials").GetProperty("EnvironmentBootstrapEnabled").GetBoolean());
        Assert.False(root.GetProperty("CredentialScheduler").GetProperty("Enabled").GetBoolean());
        Assert.False(root.GetProperty("CredentialScheduler").GetProperty("MasterScraperClaimsEnabled").GetBoolean());
        Assert.False(root.GetProperty("CredentialScheduler").GetProperty("WorkerClaimsEnabled").GetBoolean());
        Assert.False(root.GetProperty("CredentialScheduler").GetProperty("LegacyTokenSyncEnabled").GetBoolean());
        Assert.False(root.GetProperty("CredentialScheduler").GetProperty("AllowWorkerLocalFallback").GetBoolean());
        Assert.False(root.GetProperty("SearchProviders").GetProperty("InstancesEnabled").GetBoolean());
    }

    private static SearchPlatformFeatureFlagMatrixResult Evaluate(
        IReadOnlyDictionary<string, string?> values,
        SearchPlatformPrerequisiteReadiness prerequisites) =>
        new SearchPlatformFeatureFlagMatrix(
                new ConfigurationBuilder().AddInMemoryCollection(values).Build())
            .Evaluate(prerequisites);

    private static Dictionary<string, string?> DisabledConfiguration() =>
        SearchPlatformFeatureFlagNames.All.ToDictionary(key => key, _ => (string?)"false");

    private static SearchPlatformPrerequisiteReadiness AllReady() => new(
        ProtectedStorageReady: true,
        ProviderInstancesReady: true,
        ClaimApiReady: true,
        RenewalApiReady: true,
        CompletionApiReady: true,
        DurableMarkersReady: true,
        CutoverStage: WorkerClaimsCutoverStage.PreCutover);
}

public sealed class SearchPlatformFeatureFlagMatrixTests
{
    [Fact]
    public void Scheduler_RequiresProtectedStorageProviderInstancesAndDurableReadiness()
    {
        var enabled = Enabled(
            SearchPlatformFeatureFlagNames.ProtectedStorageEnabled,
            SearchPlatformFeatureFlagNames.InstancesEnabled,
            SearchPlatformFeatureFlagNames.SchedulerEnabled);

        Assert.True(Evaluate(enabled, AllReady()).IsReady);

        foreach (var missingFlag in new[]
                 {
                     SearchPlatformFeatureFlagNames.ProtectedStorageEnabled,
                     SearchPlatformFeatureFlagNames.InstancesEnabled
                 })
        {
            var missingDependency = new Dictionary<string, string?>(enabled)
            {
                [missingFlag] = "false"
            };
            Assert.False(Evaluate(missingDependency, AllReady()).IsReady);
        }

        Assert.False(Evaluate(enabled, AllReady() with { ProtectedStorageReady = false }).IsReady);
        Assert.False(Evaluate(enabled, AllReady() with { ProviderInstancesReady = false }).IsReady);
        Assert.False(Evaluate(enabled, AllReady() with { DurableMarkersReady = false }).IsReady);
    }

    [Fact]
    public void MasterClaims_RequireTheCommonScheduler()
    {
        var valid = Enabled(
            SearchPlatformFeatureFlagNames.ProtectedStorageEnabled,
            SearchPlatformFeatureFlagNames.InstancesEnabled,
            SearchPlatformFeatureFlagNames.SchedulerEnabled,
            SearchPlatformFeatureFlagNames.MasterScraperClaimsEnabled);
        var schedulerDisabled = new Dictionary<string, string?>(valid)
        {
            [SearchPlatformFeatureFlagNames.SchedulerEnabled] = "false"
        };

        Assert.True(Evaluate(valid, AllReady()).IsReady);
        Assert.False(Evaluate(schedulerDisabled, AllReady()).IsReady);
    }

    [Fact]
    public void WorkerClaims_RequireTheSchedulerAndAllThreeApis()
    {
        var valid = Enabled(
            SearchPlatformFeatureFlagNames.ProtectedStorageEnabled,
            SearchPlatformFeatureFlagNames.InstancesEnabled,
            SearchPlatformFeatureFlagNames.SchedulerEnabled,
            SearchPlatformFeatureFlagNames.WorkerClaimsEnabled);

        Assert.True(Evaluate(valid, AllReady()).IsReady);

        var schedulerDisabled = new Dictionary<string, string?>(valid)
        {
            [SearchPlatformFeatureFlagNames.SchedulerEnabled] = "false"
        };
        Assert.False(Evaluate(schedulerDisabled, AllReady()).IsReady);
        Assert.False(Evaluate(valid, AllReady() with { ClaimApiReady = false }).IsReady);
        Assert.False(Evaluate(valid, AllReady() with { RenewalApiReady = false }).IsReady);
        Assert.False(Evaluate(valid, AllReady() with { CompletionApiReady = false }).IsReady);
    }

    [Fact]
    public void LocalFallback_IsAllowedOnlyWithAnExplicitPreCutoverMarker()
    {
        var fallback = Enabled(SearchPlatformFeatureFlagNames.AllowWorkerLocalFallback);

        Assert.True(Evaluate(fallback, AllReady()).IsReady);
        Assert.False(Evaluate(
            fallback,
            AllReady() with { CutoverStage = WorkerClaimsCutoverStage.Unavailable }).IsReady);
        Assert.False(Evaluate(
            fallback,
            AllReady() with { CutoverStage = WorkerClaimsCutoverStage.PostCutover }).IsReady);
    }

    [Fact]
    public void UnavailableCutoverStage_FailsClosedForAllCompatibilityModes()
    {
        var unavailable = AllReady() with
        {
            CutoverStage = WorkerClaimsCutoverStage.Unavailable
        };

        Assert.False(Evaluate(Enabled(), unavailable).IsReady);
        Assert.False(Evaluate(
            Enabled(SearchPlatformFeatureFlagNames.LegacyTokenSyncEnabled),
            unavailable).IsReady);
        Assert.False(Evaluate(
            Enabled(SearchPlatformFeatureFlagNames.AllowWorkerLocalFallback),
            unavailable).IsReady);
    }

    [Fact]
    public void PostCutover_ForbidsLegacyCredentialSyncAndLocalFallback()
    {
        var postCutover = AllReady() with { CutoverStage = WorkerClaimsCutoverStage.PostCutover };

        Assert.False(Evaluate(
            Enabled(SearchPlatformFeatureFlagNames.LegacyTokenSyncEnabled),
            postCutover).IsReady);
        Assert.False(Evaluate(
            Enabled(SearchPlatformFeatureFlagNames.AllowWorkerLocalFallback),
            postCutover).IsReady);
        Assert.True(Evaluate(Enabled(), postCutover).IsReady);
    }

    [Fact]
    public void RuntimeFailure_DoesNotAutomaticallyEnableWorkerLocalFallback()
    {
        var result = Evaluate(
            Enabled(
                SearchPlatformFeatureFlagNames.ProtectedStorageEnabled,
                SearchPlatformFeatureFlagNames.InstancesEnabled,
                SearchPlatformFeatureFlagNames.SchedulerEnabled),
            AllReady() with
            {
                ProtectedStorageReady = false,
                ProviderInstancesReady = false,
                DurableMarkersReady = false
            });

        Assert.False(result.IsReady);
        Assert.False(result.Flags.AllowWorkerLocalFallback);
    }

    [Fact]
    public void EveryAcceptedCombination_SatisfiesTheSafetyInvariants()
    {
        // Exhaust all 2^8 flag combinations for both durable migration stages.
        // **Validates: Requirements 15.1-15.17**
        foreach (var stage in new[]
                 {
                     WorkerClaimsCutoverStage.PreCutover,
                     WorkerClaimsCutoverStage.PostCutover
                 })
        {
            for (var bits = 0; bits < 1 << SearchPlatformFeatureFlagNames.All.Count; bits++)
            {
                var values = DisabledConfiguration();
                for (var index = 0; index < SearchPlatformFeatureFlagNames.All.Count; index++)
                {
                    values[SearchPlatformFeatureFlagNames.All[index]] =
                        (bits & (1 << index)) == 0 ? "false" : "true";
                }

                var result = Evaluate(values, AllReady() with { CutoverStage = stage });
                if (!result.IsReady)
                {
                    continue;
                }

                var flags = result.Flags;
                if (flags.SchedulerEnabled)
                {
                    Assert.True(flags.ProtectedStorageEnabled);
                    Assert.True(flags.InstancesEnabled);
                }

                if (flags.MasterScraperClaimsEnabled || flags.WorkerClaimsEnabled)
                {
                    Assert.True(flags.SchedulerEnabled);
                }

                if (flags.AllowWorkerLocalFallback)
                {
                    Assert.Equal(WorkerClaimsCutoverStage.PreCutover, stage);
                }

                if (stage == WorkerClaimsCutoverStage.PostCutover)
                {
                    Assert.False(flags.AllowWorkerLocalFallback);
                    Assert.False(flags.LegacyTokenSyncEnabled);
                }
            }
        }
    }

    [Fact]
    public async Task InvalidMatrix_WithOtherwiseReadyDatabaseWithholdsSchedulingReadiness()
    {
        await using var store = await ProviderInstanceSqliteStore.CreateWithLegacyCredentialsAsync();
        await new DatabaseService(store.Context).InitializeDatabaseAsync();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(Enabled(
                SearchPlatformFeatureFlagNames.ProtectedStorageEnabled,
                SearchPlatformFeatureFlagNames.InstancesEnabled,
                SearchPlatformFeatureFlagNames.SchedulerEnabled))
            .Build();
        var runtimeReadiness = new SearchPlatformRuntimeReadinessState();
        var service = new SearchPlatformSchedulingReadinessService(
            new ProviderInstanceReadinessService(store.Context),
            store.Context,
            new SearchPlatformFeatureFlagMatrix(configuration),
            runtimeReadiness);

        var denied = await service.EvaluateAsync();

        Assert.True(denied.DatabaseReady);
        Assert.True(denied.SchemaReady);
        Assert.True(denied.ProviderInstancesReady);
        Assert.True(denied.MarkersReady);
        Assert.False(denied.ConfigurationReady);
        Assert.False(denied.IsReady);

        runtimeReadiness.Update(new SearchPlatformRuntimeReadiness(
            ProtectedStorageReady: true,
            ClaimApiReady: false,
            RenewalApiReady: false,
            CompletionApiReady: false));

        var allowed = await service.EvaluateAsync();

        Assert.True(allowed.ConfigurationReady);
        Assert.True(allowed.IsReady);
    }

    private static SearchPlatformFeatureFlagMatrixResult Evaluate(
        IReadOnlyDictionary<string, string?> values,
        SearchPlatformPrerequisiteReadiness prerequisites) =>
        new SearchPlatformFeatureFlagMatrix(
                new ConfigurationBuilder().AddInMemoryCollection(values).Build())
            .Evaluate(prerequisites);

    private static Dictionary<string, string?> Enabled(params string[] keys)
    {
        var values = DisabledConfiguration();
        foreach (var key in keys)
        {
            values[key] = "true";
        }

        return values;
    }

    private static Dictionary<string, string?> DisabledConfiguration() =>
        SearchPlatformFeatureFlagNames.All.ToDictionary(key => key, _ => (string?)"false");

    private static SearchPlatformPrerequisiteReadiness AllReady() => new(
        ProtectedStorageReady: true,
        ProviderInstancesReady: true,
        ClaimApiReady: true,
        RenewalApiReady: true,
        CompletionApiReady: true,
        DurableMarkersReady: true,
        CutoverStage: WorkerClaimsCutoverStage.PreCutover);
}

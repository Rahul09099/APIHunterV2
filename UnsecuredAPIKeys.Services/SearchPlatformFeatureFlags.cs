using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using UnsecuredAPIKeys.Data;
using UnsecuredAPIKeys.Data.Common;

namespace UnsecuredAPIKeys.Services;

/// <summary>
/// Exact configuration paths for the staged search-platform migration contract.
/// Missing values are disabled; no caller receives an implicit opt-in.
/// </summary>
public static class SearchPlatformFeatureFlagNames
{
    public const string ProtectedStorageEnabled = "SearchCredentials:ProtectedStorageEnabled";
    public const string EnvironmentBootstrapEnabled = "SearchCredentials:EnvironmentBootstrapEnabled";
    public const string SchedulerEnabled = "CredentialScheduler:Enabled";
    public const string MasterScraperClaimsEnabled = "CredentialScheduler:MasterScraperClaimsEnabled";
    public const string WorkerClaimsEnabled = "CredentialScheduler:WorkerClaimsEnabled";
    public const string LegacyTokenSyncEnabled = "CredentialScheduler:LegacyTokenSyncEnabled";
    public const string AllowWorkerLocalFallback = "CredentialScheduler:AllowWorkerLocalFallback";
    public const string InstancesEnabled = "SearchProviders:InstancesEnabled";

    public static IReadOnlyList<string> All { get; } =
    [
        ProtectedStorageEnabled,
        EnvironmentBootstrapEnabled,
        SchedulerEnabled,
        MasterScraperClaimsEnabled,
        WorkerClaimsEnabled,
        LegacyTokenSyncEnabled,
        AllowWorkerLocalFallback,
        InstancesEnabled
    ];
}

/// <summary>
/// Immutable, explicitly bound feature-flag values. This type contains no fallback
/// mutation behavior; runtime failures can only make readiness unhealthy.
/// </summary>
public sealed record SearchPlatformFeatureFlags(
    bool ProtectedStorageEnabled,
    bool EnvironmentBootstrapEnabled,
    bool SchedulerEnabled,
    bool MasterScraperClaimsEnabled,
    bool WorkerClaimsEnabled,
    bool LegacyTokenSyncEnabled,
    bool AllowWorkerLocalFallback,
    bool InstancesEnabled)
{
    public IReadOnlyDictionary<string, bool> AsDictionary() =>
        new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            [SearchPlatformFeatureFlagNames.ProtectedStorageEnabled] = ProtectedStorageEnabled,
            [SearchPlatformFeatureFlagNames.EnvironmentBootstrapEnabled] = EnvironmentBootstrapEnabled,
            [SearchPlatformFeatureFlagNames.SchedulerEnabled] = SchedulerEnabled,
            [SearchPlatformFeatureFlagNames.MasterScraperClaimsEnabled] = MasterScraperClaimsEnabled,
            [SearchPlatformFeatureFlagNames.WorkerClaimsEnabled] = WorkerClaimsEnabled,
            [SearchPlatformFeatureFlagNames.LegacyTokenSyncEnabled] = LegacyTokenSyncEnabled,
            [SearchPlatformFeatureFlagNames.AllowWorkerLocalFallback] = AllowWorkerLocalFallback,
            [SearchPlatformFeatureFlagNames.InstancesEnabled] = InstancesEnabled
        };
}

public enum WorkerClaimsCutoverStage
{
    Unavailable,
    PreCutover,
    PostCutover
}

/// <summary>
/// Current non-configuration prerequisites used by the matrix. Provider Instance and
/// marker readiness come from the database; protected storage and API readiness are
/// deny-by-default until their owning implementation explicitly reports readiness.
/// </summary>
public sealed record SearchPlatformPrerequisiteReadiness(
    bool ProtectedStorageReady,
    bool ProviderInstancesReady,
    bool ClaimApiReady,
    bool RenewalApiReady,
    bool CompletionApiReady,
    bool DurableMarkersReady,
    WorkerClaimsCutoverStage CutoverStage);

public sealed record SearchPlatformFeatureFlagMatrixResult(
    SearchPlatformFeatureFlags Flags,
    bool IsReady,
    IReadOnlyList<string> Failures);

/// <summary>
/// Loads and validates the feature-flag dependency/cutover matrix. Values are parsed
/// strictly, missing values are false, and invalid values are both disabled and unhealthy.
/// </summary>
public sealed class SearchPlatformFeatureFlagMatrix(IConfiguration configuration)
{
    public SearchPlatformFeatureFlagMatrixResult Evaluate(
        SearchPlatformPrerequisiteReadiness prerequisites)
    {
        ArgumentNullException.ThrowIfNull(prerequisites);

        var failures = new List<string>();
        var flags = new SearchPlatformFeatureFlags(
            ProtectedStorageEnabled: ReadFlag(
                SearchPlatformFeatureFlagNames.ProtectedStorageEnabled,
                failures),
            EnvironmentBootstrapEnabled: ReadFlag(
                SearchPlatformFeatureFlagNames.EnvironmentBootstrapEnabled,
                failures),
            SchedulerEnabled: ReadFlag(
                SearchPlatformFeatureFlagNames.SchedulerEnabled,
                failures),
            MasterScraperClaimsEnabled: ReadFlag(
                SearchPlatformFeatureFlagNames.MasterScraperClaimsEnabled,
                failures),
            WorkerClaimsEnabled: ReadFlag(
                SearchPlatformFeatureFlagNames.WorkerClaimsEnabled,
                failures),
            LegacyTokenSyncEnabled: ReadFlag(
                SearchPlatformFeatureFlagNames.LegacyTokenSyncEnabled,
                failures),
            AllowWorkerLocalFallback: ReadFlag(
                SearchPlatformFeatureFlagNames.AllowWorkerLocalFallback,
                failures),
            InstancesEnabled: ReadFlag(
                SearchPlatformFeatureFlagNames.InstancesEnabled,
                failures));

        if (flags.SchedulerEnabled)
        {
            Require(
                flags.ProtectedStorageEnabled,
                "The common credential scheduler requires protected storage to be enabled.",
                failures);
            Require(
                flags.InstancesEnabled,
                "The common credential scheduler requires Provider Instances to be enabled.",
                failures);
            Require(
                prerequisites.ProtectedStorageReady,
                "The common credential scheduler requires protected storage readiness.",
                failures);
            Require(
                prerequisites.ProviderInstancesReady,
                "The common credential scheduler requires Provider Instance readiness.",
                failures);
            Require(
                prerequisites.DurableMarkersReady,
                "The common credential scheduler requires durable migration-marker readiness.",
                failures);
        }

        if (flags.MasterScraperClaimsEnabled)
        {
            Require(
                flags.SchedulerEnabled,
                "Master scraper Claims require the common credential scheduler.",
                failures);
        }

        if (flags.WorkerClaimsEnabled)
        {
            Require(
                flags.SchedulerEnabled,
                "Worker Claims require the common credential scheduler.",
                failures);
            Require(
                prerequisites.ClaimApiReady,
                "Worker Claims require Claim API readiness.",
                failures);
            Require(
                prerequisites.RenewalApiReady,
                "Worker Claims require renewal API readiness.",
                failures);
            Require(
                prerequisites.CompletionApiReady,
                "Worker Claims require completion API readiness.",
                failures);
        }

        if (prerequisites.CutoverStage == WorkerClaimsCutoverStage.Unavailable)
        {
            failures.Add(
                "The durable Worker Claims cutover stage is unavailable or incompatible.");
        }

        if (flags.AllowWorkerLocalFallback &&
            prerequisites.CutoverStage != WorkerClaimsCutoverStage.PreCutover)
        {
            failures.Add(
                "Worker-local credential fallback is permitted only with an explicit pre-cutover marker.");
        }

        if (prerequisites.CutoverStage == WorkerClaimsCutoverStage.PostCutover)
        {
            Require(
                !flags.AllowWorkerLocalFallback,
                "Worker-local credential fallback is prohibited after Worker Claims cutover.",
                failures);
            Require(
                !flags.LegacyTokenSyncEnabled,
                "Credential-bearing legacy token sync is prohibited after Worker Claims cutover.",
                failures);
        }

        return new SearchPlatformFeatureFlagMatrixResult(
            Flags: flags,
            IsReady: failures.Count == 0,
            Failures: failures.ToArray());
    }

    private bool ReadFlag(string key, ICollection<string> failures)
    {
        var configuredValue = configuration[key];
        if (string.IsNullOrWhiteSpace(configuredValue))
        {
            return false;
        }

        if (bool.TryParse(configuredValue, out var enabled))
        {
            return enabled;
        }

        failures.Add($"Configuration value for '{key}' must be a Boolean value.");
        return false;
    }

    private static void Require(
        bool condition,
        string failure,
        ICollection<string> failures)
    {
        if (!condition)
        {
            failures.Add(failure);
        }
    }
}

/// <summary>
/// Readiness owned by components that do not exist in the Task 2.4 slice. The state is
/// deliberately unavailable by default; future components must opt in only after their
/// own complete readiness checks pass.
/// </summary>
public sealed record SearchPlatformRuntimeReadiness(
    bool ProtectedStorageReady,
    bool ClaimApiReady,
    bool RenewalApiReady,
    bool CompletionApiReady)
{
    public static SearchPlatformRuntimeReadiness Unavailable { get; } = new(
        ProtectedStorageReady: false,
        ClaimApiReady: false,
        RenewalApiReady: false,
        CompletionApiReady: false);
}

public sealed class SearchPlatformRuntimeReadinessState
{
    private SearchPlatformRuntimeReadiness _current = SearchPlatformRuntimeReadiness.Unavailable;

    public SearchPlatformRuntimeReadiness Current => Volatile.Read(ref _current);

    public void Update(SearchPlatformRuntimeReadiness readiness)
    {
        ArgumentNullException.ThrowIfNull(readiness);
        Volatile.Write(ref _current, readiness);
    }
}

public interface ISearchPlatformSchedulingReadinessService
{
    Task<ProviderInstanceReadinessReport> EvaluateAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Combines physical/database readiness with the feature-flag matrix before scheduling
/// readiness is published. It does not start, select, or invoke any scheduler.
/// </summary>
public sealed class SearchPlatformSchedulingReadinessService(
    ProviderInstanceReadinessService providerInstanceReadinessService,
    DBContext dbContext,
    SearchPlatformFeatureFlagMatrix featureFlagMatrix,
    SearchPlatformRuntimeReadinessState runtimeReadinessState,
    CredentialGrantReadinessService? credentialGrantReadinessService = null,
    EnvironmentBootstrapReadinessState? environmentBootstrapReadinessState = null)
    : ISearchPlatformSchedulingReadinessService
{
    public async Task<ProviderInstanceReadinessReport> EvaluateAsync(
        CancellationToken cancellationToken = default)
    {
        var platformReadiness = await providerInstanceReadinessService.EvaluateAsync(cancellationToken);
        var cutoverStage = await ReadCutoverStageAsync(platformReadiness, cancellationToken);
        var runtimeReadiness = runtimeReadinessState.Current;
        var matrix = featureFlagMatrix.Evaluate(new SearchPlatformPrerequisiteReadiness(
            ProtectedStorageReady: runtimeReadiness.ProtectedStorageReady,
            ProviderInstancesReady: platformReadiness.ProviderInstancesReady,
            ClaimApiReady: runtimeReadiness.ClaimApiReady,
            RenewalApiReady: runtimeReadiness.RenewalApiReady,
            CompletionApiReady: runtimeReadiness.CompletionApiReady,
            DurableMarkersReady: platformReadiness.MarkersReady,
            CutoverStage: cutoverStage));

        var grantReadiness = credentialGrantReadinessService is null
            ? CredentialGrantReadinessResult.Ready
            : await credentialGrantReadinessService.EvaluateAsync(cancellationToken);
        var bootstrapReadiness = environmentBootstrapReadinessState?.Current ??
                                 EnvironmentBootstrapHealth.Disabled;

        return platformReadiness with
        {
            ConfigurationReady = matrix.IsReady && grantReadiness.IsReady && bootstrapReadiness.IsReady,
            BootstrapReady = bootstrapReadiness.IsReady,
            BootstrapImportedCount = bootstrapReadiness.ImportedCount,
            BootstrapCredentialStableIds = bootstrapReadiness.CredentialStableIds,
            Failures = platformReadiness.Failures
                .Concat(matrix.Failures)
                .Concat(grantReadiness.Failures)
                .Concat(bootstrapReadiness.IsReady
                    ? []
                    : new[] { "The environment credential bootstrap generation is unavailable." })
                .Distinct(StringComparer.Ordinal)
                .ToArray()
        };
    }

    private async Task<WorkerClaimsCutoverStage> ReadCutoverStageAsync(
        ProviderInstanceReadinessReport platformReadiness,
        CancellationToken cancellationToken)
    {
        if (!platformReadiness.DatabaseReady ||
            !platformReadiness.SchemaReady ||
            !platformReadiness.MarkersReady)
        {
            return WorkerClaimsCutoverStage.Unavailable;
        }

        try
        {
            var marker = await dbContext.CutoverMarkers
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    candidate => candidate.Id == ProviderInstanceSchema.MarkerRecordId &&
                                 candidate.Name == ProviderInstanceSchema.WorkerClaimsCutoverMarkerName &&
                                 candidate.Version == ProviderInstanceSchema.CurrentVersion,
                    cancellationToken);

            if (marker is null)
            {
                return WorkerClaimsCutoverStage.Unavailable;
            }

            return marker.IsComplete
                ? WorkerClaimsCutoverStage.PostCutover
                : WorkerClaimsCutoverStage.PreCutover;
        }
        catch
        {
            return WorkerClaimsCutoverStage.Unavailable;
        }
    }
}

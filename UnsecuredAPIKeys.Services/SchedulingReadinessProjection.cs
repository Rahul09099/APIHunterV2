using System.Text.Json.Serialization;

namespace UnsecuredAPIKeys.Services;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReadinessHealthStatus
{
    Healthy,
    Unhealthy
}

/// <summary>
/// Public readiness shape for scheduling-related health checks. It is intentionally
/// constructed only from bounded status values and cannot carry diagnostic text,
/// credentials, fingerprints, or key material from the internal readiness report.
/// </summary>
public sealed record SchedulingReadinessProjection(
    ReadinessHealthStatus Status,
    ReadinessHealthStatus Database,
    ReadinessHealthStatus Schema,
    ReadinessHealthStatus ProviderInstances,
    ReadinessHealthStatus ActiveMarkers,
    ReadinessHealthStatus Bootstrap,
    int BootstrapImportedCount,
    IReadOnlyList<Guid> BootstrapStableIds,
    [property: JsonConverter(typeof(JsonStringEnumConverter))]
    DatabaseCoordinationMode CoordinationMode,
    bool DistributedCoordinationReady)
{
    public static SchedulingReadinessProjection From(ProviderInstanceReadinessReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        return new SchedulingReadinessProjection(
            Status: ToStatus(report.IsReady),
            Database: ToStatus(report.DatabaseReady),
            Schema: ToStatus(report.SchemaReady),
            ProviderInstances: ToStatus(report.ProviderInstancesReady),
            ActiveMarkers: ToStatus(report.MarkersReady),
            Bootstrap: ToStatus(report.BootstrapReady),
            BootstrapImportedCount: report.BootstrapImportedCount,
            BootstrapStableIds: report.BootstrapCredentialStableIds,
            CoordinationMode: report.CoordinationMode,
            DistributedCoordinationReady: report.DistributedCoordinationReady);
    }

    private static ReadinessHealthStatus ToStatus(bool ready) =>
        ready ? ReadinessHealthStatus.Healthy : ReadinessHealthStatus.Unhealthy;
}

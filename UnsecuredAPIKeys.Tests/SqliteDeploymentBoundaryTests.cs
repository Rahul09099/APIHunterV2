using Microsoft.Extensions.Configuration;
using UnsecuredAPIKeys.Services;
using Xunit;

namespace UnsecuredAPIKeys.Tests;

/// <summary>
/// Task 9.3 — SQLite Deployment Boundary and P12 Detection Heuristic Tests.
/// Validates AC-13.27, AC-13.28, AC-13.29, AC-13.30, AC-13.31, AC-17.12, AC-17.30, AC-17.43.
/// </summary>
public sealed class SqliteDeploymentBoundaryTests
{
    [Fact]
    public void StandaloneLocalPath_EvaluatesToSingleMasterWithoutFailure()
    {
        var (mode, failure) = ProviderInstanceReadinessService.EvaluateSqliteCoordinationMode(
            "Data Source=unsecuredapikeys.db;Cache=Shared");

        Assert.Equal(DatabaseCoordinationMode.SingleMaster, mode);
        Assert.Null(failure);
    }

    [Fact]
    public void AbsoluteLocalPath_EvaluatesToSingleMaster()
    {
        var (mode, failure) = ProviderInstanceReadinessService.EvaluateSqliteCoordinationMode(
            @"Data Source=C:\App\data\unsecuredapikeys.db");

        Assert.Equal(DatabaseCoordinationMode.SingleMaster, mode);
        Assert.Null(failure);
    }

    [Fact]
    public void ExplicitStandaloneConfig_OverridesDetection()
    {
        var (mode, failure) = ProviderInstanceReadinessService.EvaluateSqliteCoordinationMode(
            @"Data Source=\\server\share\file.db",
            configuredCoordinationMode: "Standalone");

        Assert.Equal(DatabaseCoordinationMode.SingleMaster, mode);
        Assert.Null(failure);
    }

    [Theory]
    [InlineData("NetworkShared")]
    [InlineData("Distributed")]
    [InlineData("Clustered")]
    public void ExplicitNonStandaloneConfig_WithholdsCoordination(string configuredMode)
    {
        var (mode, failure) = ProviderInstanceReadinessService.EvaluateSqliteCoordinationMode(
            "Data Source=local.db",
            configuredCoordinationMode: configuredMode);

        Assert.Equal(DatabaseCoordinationMode.Unsupported, mode);
        Assert.NotNull(failure);
        Assert.Contains(configuredMode, failure);
    }

    [Theory]
    [InlineData(@"Data Source=\\fileserver\share\db.sqlite")]
    [InlineData(@"DataSource=\\nas01\volume1\app.db")]
    [InlineData("Data Source=//networkshare/path/db.sqlite")]
    [InlineData("Data Source=file://fileserver/share/db.sqlite")]
    [InlineData(@"Filename=\\server\share\db.sqlite")]
    public void UncNetworkPaths_DetectedAndReportUnsupported(string connectionString)
    {
        var (mode, failure) = ProviderInstanceReadinessService.EvaluateSqliteCoordinationMode(connectionString);

        Assert.Equal(DatabaseCoordinationMode.Unsupported, mode);
        Assert.NotNull(failure);
        Assert.Contains("UNC", failure, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Data Source=file:data.db?mode=ro")]
    [InlineData("Data Source=db.sqlite;mode=ro")]
    [InlineData("URI=file:///path/to/db.sqlite?mode=ro")]
    public void ReadOnlyUriMode_DetectedAndReportUnsupported(string connectionString)
    {
        var (mode, failure) = ProviderInstanceReadinessService.EvaluateSqliteCoordinationMode(connectionString);

        Assert.Equal(DatabaseCoordinationMode.Unsupported, mode);
        Assert.NotNull(failure);
        Assert.Contains("mode=ro", failure, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SingleMasterReport_WithholdsDistributedCoordinationReadiness()
    {
        var report = new ProviderInstanceReadinessReport(
            DatabaseReady: true,
            SchemaReady: true,
            ProviderInstancesReady: true,
            MarkersReady: true,
            CoordinationMode: DatabaseCoordinationMode.SingleMaster,
            DistributedCoordinationReady: false,
            Failures: []);

        Assert.True(report.IsReady);
        Assert.False(report.DistributedCoordinationReady);
        Assert.Equal(DatabaseCoordinationMode.SingleMaster, report.CoordinationMode);
    }

    [Fact]
    public void UnsupportedCoordinationReport_FailsReadinessAndWithholdsDistributed()
    {
        var report = new ProviderInstanceReadinessReport(
            DatabaseReady: true,
            SchemaReady: true,
            ProviderInstancesReady: true,
            MarkersReady: true,
            CoordinationMode: DatabaseCoordinationMode.Unsupported,
            DistributedCoordinationReady: false,
            Failures: ["Network-shared SQLite is unsupported."]);

        Assert.False(report.IsReady);
        Assert.False(report.DistributedCoordinationReady);
        Assert.Equal(DatabaseCoordinationMode.Unsupported, report.CoordinationMode);
    }

    [Fact]
    public void DistributedCoordinationReport_RequiresPostgreSQL()
    {
        var report = new ProviderInstanceReadinessReport(
            DatabaseReady: true,
            SchemaReady: true,
            ProviderInstancesReady: true,
            MarkersReady: true,
            CoordinationMode: DatabaseCoordinationMode.Distributed,
            DistributedCoordinationReady: true,
            Failures: []);

        Assert.True(report.IsReady);
        Assert.True(report.DistributedCoordinationReady);
        Assert.Equal(DatabaseCoordinationMode.Distributed, report.CoordinationMode);
    }

    [Fact]
    public void SchedulingReadinessProjection_FaithfullyProjectsCoordinationBoundary()
    {
        var report = new ProviderInstanceReadinessReport(
            DatabaseReady: true,
            SchemaReady: true,
            ProviderInstancesReady: true,
            MarkersReady: true,
            CoordinationMode: DatabaseCoordinationMode.SingleMaster,
            DistributedCoordinationReady: false,
            Failures: []);

        var projection = SchedulingReadinessProjection.From(report);

        Assert.Equal(ReadinessHealthStatus.Healthy, projection.Status);
        Assert.Equal(DatabaseCoordinationMode.SingleMaster, projection.CoordinationMode);
        Assert.False(projection.DistributedCoordinationReady);
    }
}

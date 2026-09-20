using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using UnsecuredAPIKeys.Data;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Services;
using UnsecuredAPIKeys.Tests.Infrastructure;
using Xunit;

namespace UnsecuredAPIKeys.Tests;

public sealed class ReadinessProjectionUnitTests
{
    [Fact]
    public void From_MapsEveryReadinessComponentWithoutDiagnosticOrSecretMaterial()
    {
        const string credentialCanary = "credential-canary-ghp-task-2-3";
        const string keyCanary = "protection-key-canary-task-2-3";
        var report = new ProviderInstanceReadinessReport(
            DatabaseReady: true,
            SchemaReady: false,
            ProviderInstancesReady: true,
            MarkersReady: false,
            CoordinationMode: DatabaseCoordinationMode.SingleMaster,
            DistributedCoordinationReady: false,
            Failures:
            [
                $"Diagnostic containing {credentialCanary}",
                $"Diagnostic containing {keyCanary}"
            ]);

        var projection = SchedulingReadinessProjection.From(report);

        Assert.Equal(ReadinessHealthStatus.Unhealthy, projection.Status);
        Assert.Equal(ReadinessHealthStatus.Healthy, projection.Database);
        Assert.Equal(ReadinessHealthStatus.Unhealthy, projection.Schema);
        Assert.Equal(ReadinessHealthStatus.Healthy, projection.ProviderInstances);
        Assert.Equal(ReadinessHealthStatus.Unhealthy, projection.ActiveMarkers);
        Assert.Equal(DatabaseCoordinationMode.SingleMaster, projection.CoordinationMode);
        Assert.False(projection.DistributedCoordinationReady);

        var json = JsonSerializer.Serialize(projection);
        Assert.DoesNotContain(credentialCanary, json, StringComparison.Ordinal);
        Assert.DoesNotContain(keyCanary, json, StringComparison.Ordinal);
        Assert.DoesNotContain("Failures", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Credential", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("KeyMaterial", json, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class ReadinessProjectionHttpTests(SharedMemorySqliteWebApiFixture fixture)
    : IClassFixture<SharedMemorySqliteWebApiFixture>
{
    [Fact]
    public async Task ReadinessEndpoint_ReturnsTypedHealthyProjection()
    {
        var report = new ProviderInstanceReadinessReport(
            DatabaseReady: true,
            SchemaReady: true,
            ProviderInstancesReady: true,
            MarkersReady: true,
            CoordinationMode: DatabaseCoordinationMode.SingleMaster,
            DistributedCoordinationReady: false,
            Failures: []);

        await WithReadinessAsync(report, async () =>
        {
            using var response = await fixture.Client.GetAsync("/api/health/readiness");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            await AssertProjectionAsync(
                response,
                overall: "Healthy",
                database: "Healthy",
                schema: "Healthy",
                providerInstances: "Healthy",
                activeMarkers: "Healthy");
        });
    }

    [Fact]
    public async Task IncompleteSchema_Returns503ForReadinessAndSchedulingWhileLivenessStaysHealthy()
    {
        const string secretCanary = "credential-canary-must-not-cross-readiness-boundary";
        var report = new ProviderInstanceReadinessReport(
            DatabaseReady: true,
            SchemaReady: false,
            ProviderInstancesReady: false,
            MarkersReady: false,
            CoordinationMode: DatabaseCoordinationMode.SingleMaster,
            DistributedCoordinationReady: false,
            Failures: [$"Missing schema near {secretCanary}"]);

        await WithReadinessAsync(report, async () =>
        {
            using var readiness = await fixture.Client.GetAsync("/api/health/readiness");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, readiness.StatusCode);
            var readinessBody = await readiness.Content.ReadAsStringAsync();
            Assert.DoesNotContain(secretCanary, readinessBody, StringComparison.Ordinal);
            await AssertProjectionAsync(
                readiness,
                overall: "Unhealthy",
                database: "Healthy",
                schema: "Unhealthy",
                providerInstances: "Unhealthy",
                activeMarkers: "Unhealthy");

            using var scheduling = await fixture.Client.PostAsync("/api/scraper/start", content: null);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, scheduling.StatusCode);
            var schedulingBody = await scheduling.Content.ReadAsStringAsync();
            Assert.DoesNotContain(secretCanary, schedulingBody, StringComparison.Ordinal);
            Assert.DoesNotContain("credential", schedulingBody, StringComparison.OrdinalIgnoreCase);

            using var liveness = await fixture.Client.GetAsync("/health");
            Assert.Equal(HttpStatusCode.OK, liveness.StatusCode);
            using var livenessJson = JsonDocument.Parse(await liveness.Content.ReadAsStringAsync());
            Assert.Equal("Healthy", livenessJson.RootElement.GetProperty("status").GetString());
        });
    }

    [Fact]
    public async Task UnavailableDatabase_Returns503ForReadinessAndScheduling()
    {
        const string keyCanary = "fingerprint-key-canary-must-not-be-returned";
        var report = new ProviderInstanceReadinessReport(
            DatabaseReady: false,
            SchemaReady: false,
            ProviderInstancesReady: false,
            MarkersReady: false,
            CoordinationMode: DatabaseCoordinationMode.SingleMaster,
            DistributedCoordinationReady: false,
            Failures: [$"Database failed while loading {keyCanary}"]);

        await WithReadinessAsync(report, async () =>
        {
            using var readiness = await fixture.Client.GetAsync("/api/health/readiness");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, readiness.StatusCode);
            var readinessBody = await readiness.Content.ReadAsStringAsync();
            Assert.DoesNotContain(keyCanary, readinessBody, StringComparison.Ordinal);
            await AssertProjectionAsync(
                readiness,
                overall: "Unhealthy",
                database: "Unhealthy",
                schema: "Unhealthy",
                providerInstances: "Unhealthy",
                activeMarkers: "Unhealthy");

            using var scheduling = await fixture.Client.PostAsync("/api/scraper/start", content: null);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, scheduling.StatusCode);
            var schedulingBody = await scheduling.Content.ReadAsStringAsync();
            Assert.DoesNotContain(keyCanary, schedulingBody, StringComparison.Ordinal);
            Assert.DoesNotContain("key", schedulingBody, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task InvalidFeatureFlagMatrix_Returns503BeforeSchedulingWhileLivenessStaysHealthy()
    {
        var report = new ProviderInstanceReadinessReport(
            DatabaseReady: true,
            SchemaReady: true,
            ProviderInstancesReady: true,
            MarkersReady: true,
            CoordinationMode: DatabaseCoordinationMode.SingleMaster,
            DistributedCoordinationReady: false,
            Failures: ["The scheduling feature-flag matrix is invalid."])
        {
            ConfigurationReady = false
        };

        await WithReadinessAsync(report, async () =>
        {
            using var readiness = await fixture.Client.GetAsync("/api/health/readiness");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, readiness.StatusCode);
            await AssertProjectionAsync(
                readiness,
                overall: "Unhealthy",
                database: "Healthy",
                schema: "Healthy",
                providerInstances: "Healthy",
                activeMarkers: "Healthy");

            using var scheduling = await fixture.Client.PostAsync("/api/scraper/start", content: null);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, scheduling.StatusCode);

            using var liveness = await fixture.Client.GetAsync("/health");
            Assert.Equal(HttpStatusCode.OK, liveness.StatusCode);
        });
    }

    [Fact]
    public async Task ReadinessGate_ReevaluatesTheDurableCutoverBeforeEverySchedulingDecision()
    {
        var configuration = fixture.Services.GetRequiredService<IConfiguration>();
        var key = SearchPlatformFeatureFlagNames.LegacyTokenSyncEnabled;
        var previousValue = configuration[key];

        try
        {
            configuration[key] = "true";
            await SetWorkerClaimsCutoverAsync(isComplete: false);

            using var preCutover = await fixture.Client.GetAsync("/api/health/readiness");
            Assert.Equal(HttpStatusCode.OK, preCutover.StatusCode);

            await SetWorkerClaimsCutoverAsync(isComplete: true);

            using var postCutover = await fixture.Client.GetAsync("/api/health/readiness");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, postCutover.StatusCode);

            using var scheduling = await fixture.Client.PostAsync("/api/scraper/start", content: null);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, scheduling.StatusCode);
        }
        finally
        {
            configuration[key] = previousValue;
            await SetWorkerClaimsCutoverAsync(isComplete: false);
        }
    }

    private async Task SetWorkerClaimsCutoverAsync(bool isComplete)
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DBContext>();
        var marker = await dbContext.CutoverMarkers.SingleAsync(candidate =>
            candidate.Id == ProviderInstanceSchema.MarkerRecordId &&
            candidate.Name == ProviderInstanceSchema.WorkerClaimsCutoverMarkerName);
        marker.IsComplete = isComplete;
        marker.UpdatedUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync();
    }

    private async Task WithReadinessAsync(
        ProviderInstanceReadinessReport report,
        Func<Task> assertion)
    {
        fixture.SetReadinessOverride(report);
        try
        {
            await assertion();
        }
        finally
        {
            fixture.ClearReadinessOverride();
        }
    }

    private static async Task AssertProjectionAsync(
        HttpResponseMessage response,
        string overall,
        string database,
        string schema,
        string providerInstances,
        string activeMarkers)
    {
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = payload.RootElement;
        Assert.Equal(overall, root.GetProperty("status").GetString());
        Assert.Equal(database, root.GetProperty("database").GetString());
        Assert.Equal(schema, root.GetProperty("schema").GetString());
        Assert.Equal(providerInstances, root.GetProperty("providerInstances").GetString());
        Assert.Equal(activeMarkers, root.GetProperty("activeMarkers").GetString());
        Assert.Equal("SingleMaster", root.GetProperty("coordinationMode").GetString());
        Assert.False(root.GetProperty("distributedCoordinationReady").GetBoolean());
        Assert.False(root.TryGetProperty("failures", out _));
        Assert.False(root.TryGetProperty("credentials", out _));
        Assert.False(root.TryGetProperty("keys", out _));
    }
}

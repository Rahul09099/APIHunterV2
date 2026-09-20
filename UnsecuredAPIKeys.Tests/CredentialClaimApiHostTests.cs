using System.Net;
using System.Text;
using UnsecuredAPIKeys.Services;
using UnsecuredAPIKeys.Tests.Infrastructure;
using Xunit;

namespace UnsecuredAPIKeys.Tests;

/// <summary>
/// Tasks 12.1/12.3 — Claim API route deployment and readiness independence at the host level.
/// Proves the Claim routes are deployed behind scheduling readiness (503 while unhealthy)
/// while configuration sync stays on its own credential-free path without a readiness
/// gate, so the three Claim APIs can deploy before Worker execution begins.
/// </summary>
public sealed class CredentialClaimApiHostTests(SharedMemorySqliteWebApiFixture fixture)
    : IClassFixture<SharedMemorySqliteWebApiFixture>
{
    private static ProviderInstanceReadinessReport ReadyReport() => new(
        DatabaseReady: true,
        SchemaReady: true,
        ProviderInstancesReady: true,
        MarkersReady: true,
        CoordinationMode: DatabaseCoordinationMode.SingleMaster,
        DistributedCoordinationReady: false,
        Failures: []);

    private static ProviderInstanceReadinessReport UnreadyReport() => new(
        DatabaseReady: false,
        SchemaReady: false,
        ProviderInstancesReady: false,
        MarkersReady: false,
        CoordinationMode: DatabaseCoordinationMode.Unsupported,
        DistributedCoordinationReady: false,
        Failures: ["Claim API readiness is withheld for this test."]);

    [Fact]
    public async Task ClaimRoute_WithoutToken_Returns401WhenReady()
    {
        fixture.SetReadinessOverride(ReadyReport());
        try
        {
            using var content = new StringContent("{}", Encoding.UTF8, "application/json");
            using var response = await fixture.Client.PostAsync(
                "/api/v1/nodes/credential-claims", content);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        finally
        {
            fixture.ClearReadinessOverride();
        }
    }

    [Fact]
    public async Task ClaimRoute_WhenNotReady_Returns503WhileSyncHasNoReadinessGate()
    {
        fixture.SetReadinessOverride(UnreadyReport());
        try
        {
            using var content = new StringContent("{}", Encoding.UTF8, "application/json");
            using var claimResponse = await fixture.Client.PostAsync(
                "/api/v1/nodes/credential-claims", content);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, claimResponse.StatusCode);

            // Configuration sync is credential-free and independent of Claim readiness:
            // it answers from its own auth path (401 for a missing token, never 503).
            using var syncResponse = await fixture.Client.GetAsync("/api/v1/nodes/sync");
            Assert.Equal(HttpStatusCode.Unauthorized, syncResponse.StatusCode);
        }
        finally
        {
            fixture.ClearReadinessOverride();
        }
    }
}

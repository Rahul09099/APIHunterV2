using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using UnsecuredAPIKeys.Data;
using UnsecuredAPIKeys.Data.Models;
using UnsecuredAPIKeys.Services;
using UnsecuredAPIKeys.Tests.Infrastructure;
using Xunit;

namespace UnsecuredAPIKeys.Tests;

/// <summary>
/// Tasks 15.1/15.2 — Administrator surface deployment tests at the host level.
/// Proves the credential-health and public-search-consent routes are deployed behind
/// administrator authentication without credential exposure.
/// </summary>
public sealed class CredentialHealthApiHostTests(SharedMemorySqliteWebApiFixture fixture)
    : IClassFixture<SharedMemorySqliteWebApiFixture>
{
    [Fact]
    public async Task HealthCredentials_WithoutAdmin_ReturnsUnauthorized()
    {
        using var response = await fixture.Client.GetAsync("/api/credential-health/credentials");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task HealthMetrics_WithoutAdmin_ReturnsUnauthorized()
    {
        using var response = await fixture.Client.GetAsync("/api/credential-health/metrics");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ConsentGrant_WithoutToken_ReturnsUnauthorized()
    {
        using var content = new StringContent(
            """{"reason":"host deployment canary"}""", Encoding.UTF8, "application/json");
        using var response = await fixture.Client.PostAsync(
            $"/api/provider-instances/{Guid.NewGuid():D}/public-search-consent", content);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ConsentGrant_NonAdminNode_ReturnsForbidden()
    {
        using var scope = fixture.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DBContext>();
        dbContext.TelegramSubscribers.Add(new TelegramSubscriber
        {
            TelegramId = 78111,
            NodeToken = "node-token-health-host-canary",
            IsAdmin = false
        });
        await dbContext.SaveChangesAsync();

        using var content = new StringContent(
            """{"reason":"non-admin consent attempt"}""", Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"/api/provider-instances/{Guid.NewGuid():D}/public-search-consent")
        {
            Content = content
        };
        request.Headers.Add("X-Node-Token", "node-token-health-host-canary");
        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}

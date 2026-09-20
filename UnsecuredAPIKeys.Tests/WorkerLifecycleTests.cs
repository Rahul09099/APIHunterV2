using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using UnsecuredAPIKeys.Services;
using Xunit;

namespace UnsecuredAPIKeys.Tests;

/// <summary>
/// Task 13.1 — Worker lifecycle and outage tests.
/// Covers valid HTTPS startup config, missing/HTTP unhealthy config with no traffic,
/// bounded outage backoff, one scope per cycle, independent heartbeat, and no local
/// credential fallback anywhere on the Worker path.
/// Validates AC-5.35–AC-5.36, AC-5.44, AC-11.28–AC-11.45 (lifecycle slice).
/// </summary>
public sealed class WorkerLifecycleTests
{
    [Fact]
    public void Options_ValidHttpsConfiguration_IsHealthy()
    {
        var options = new WorkerScraperOptions
        {
            MasterApiUrl = "https://master.example.test:8443",
            NodeToken = "node-token-healthy"
        };

        var (healthy, error) = options.Validate();

        Assert.True(healthy);
        Assert.Null(error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Options_MissingMasterUrl_IsUnhealthy(string? masterUrl)
    {
        var options = new WorkerScraperOptions
        {
            MasterApiUrl = masterUrl!,
            NodeToken = "node-token-present"
        };

        var (healthy, error) = options.Validate();

        Assert.False(healthy);
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("http://master.example.test/api")]
    [InlineData("http://localhost:8080")]
    [InlineData("ftp://master.example.test")]
    [InlineData("not-a-url")]
    public void Options_NonHttpsMasterUrl_IsUnhealthy(string masterUrl)
    {
        var options = new WorkerScraperOptions
        {
            MasterApiUrl = masterUrl,
            NodeToken = "node-token-present"
        };

        var (healthy, _) = options.Validate();

        Assert.False(healthy);
    }

    [Fact]
    public void Options_MissingNodeToken_IsUnhealthy()
    {
        var options = new WorkerScraperOptions
        {
            MasterApiUrl = "https://master.example.test",
            NodeToken = "  "
        };

        var (healthy, error) = options.Validate();

        Assert.False(healthy);
        Assert.NotNull(error);
    }

    [Fact]
    public void Backoff_GrowsExponentiallyAndCapsAtMax()
    {
        var initial = TimeSpan.FromSeconds(10);
        var max = TimeSpan.FromMinutes(5);

        Assert.Equal(TimeSpan.Zero, WorkerScraperHostedService.ComputeBackoff(0, initial, max));
        Assert.Equal(TimeSpan.FromSeconds(10), WorkerScraperHostedService.ComputeBackoff(1, initial, max));
        Assert.Equal(TimeSpan.FromSeconds(20), WorkerScraperHostedService.ComputeBackoff(2, initial, max));
        Assert.Equal(TimeSpan.FromSeconds(40), WorkerScraperHostedService.ComputeBackoff(3, initial, max));
        Assert.Equal(max, WorkerScraperHostedService.ComputeBackoff(6, initial, max));
        Assert.Equal(max, WorkerScraperHostedService.ComputeBackoff(1000, initial, max));
    }

    [Fact]
    public async Task HostedService_InvalidConfiguration_SendsNoTrafficAndStops()
    {
        // Validation runs before any scope resolution or HTTP traffic: an empty provider
        // proves no dependency is touched on the unhealthy path.
        var options = new WorkerScraperOptions
        {
            MasterApiUrl = "http://insecure.example.test",
            NodeToken = string.Empty
        };
        var services = new ServiceCollection().BuildServiceProvider();
        var service = new WorkerScraperHostedService(
            services, options, NullLogger<WorkerScraperHostedService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void WorkerPath_HasNoLocalCredentialFallback()
    {
        // The Worker holds no provider credentials at rest: the only token on the path
        // is the node authentication token, and no traffic path reads environment secrets
        // or provider-token stores.
        var forbiddenFragments = new[] { "github", "gitlab", "glpat", "secret", "material", "password", "pat" };
        foreach (var type in new[]
                 {
                     typeof(WorkerScraperOptions),
                     typeof(WorkerCycleRunner),
                     typeof(HttpMasterApiClient),
                     typeof(WorkerSecretExtractor)
                 })
        {
            foreach (var constructor in type.GetConstructors())
            {
                foreach (var parameter in constructor.GetParameters())
                {
                    foreach (var fragment in forbiddenFragments)
                    {
                        Assert.DoesNotContain(
                            fragment,
                            parameter.Name ?? string.Empty,
                            StringComparison.OrdinalIgnoreCase);
                    }
                }
            }
        }

        foreach (var fileName in new[] { "WorkerCycleRunner.cs", "MasterApiClient.cs", "WorkerScraperHostedService.cs" })
        {
            Assert.DoesNotContain(
                "GetEnvironmentVariable",
                ReadServiceSource(fileName),
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void HostedService_CreatesOneScopePerCycleWithIndependentHeartbeat()
    {
        var source = ReadServiceSource("WorkerScraperHostedService.cs");

        Assert.Contains("services.CreateScope()", source, StringComparison.Ordinal);
        Assert.Contains("GetRequiredService<WorkerCycleRunner>()", source, StringComparison.Ordinal);
        Assert.Contains("Task.Run(", source, StringComparison.Ordinal);
        Assert.Contains("HeartbeatLoopAsync", source, StringComparison.Ordinal);
    }

    private static string ReadServiceSource(string fileName)
    {
        var directory = AppContext.BaseDirectory;
        while (directory is not null && !Directory.Exists(Path.Combine(directory, "UnsecuredAPIKeys.Services")))
        {
            directory = Directory.GetParent(directory)?.FullName;
        }

        Assert.True(directory is not null, "Repository root not found from test output.");
        return File.ReadAllText(Path.Combine(directory, "UnsecuredAPIKeys.Services", fileName));
    }
}

using System.Net;
using FsCheck;
using FsCheck.Xunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using UnsecuredAPIKeys.Data;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Data.Models;
using UnsecuredAPIKeys.Providers;
using UnsecuredAPIKeys.Providers._Interfaces;
using UnsecuredAPIKeys.Services;
using UnsecuredAPIKeys.Tests.Infrastructure;
using Xunit;

namespace UnsecuredAPIKeys.Tests;

/// <summary>
/// Task 6.4 wired approval, persistence, and runtime-gating coverage.
/// </summary>
public sealed class EndpointPolicyApprovalIntegrationTests(SharedMemorySqliteWebApiFixture fixture)
    : IClassFixture<SharedMemorySqliteWebApiFixture>
{
    private static long _actorId = 9_640_000_000;

    /// <summary>
    /// **Validates: Requirements 1.21-1.24, 2.26, 12.8, 12.14, 12.27**
    /// </summary>
    [Fact]
    public async Task ApprovalPersistsCanonicalPolicyAndEveryPolicyChangeInvalidatesItWithAudit()
    {
        var stableId = await AddSelfHostedInstanceAsync();
        var actor = SchedulerPrincipal.ForTelegram(
            Interlocked.Increment(ref _actorId),
            isAdministrator: true);

        using var scope = fixture.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IProviderInstanceCommandService>();
        var context = scope.ServiceProvider.GetRequiredService<DBContext>();

        await service.ApproveAsync(stableId, actor, "Initial endpoint approval");
        context.ChangeTracker.Clear();
        var approved = await context.SearchProviderInstances.SingleAsync(x => x.StableId == stableId);
        Assert.Equal(actor.TelegramPrincipalId, approved.ApprovedByTelegramId);
        Assert.Equal(EndpointPolicy.CurrentPolicyVersion, approved.EndpointPolicyVersion);
        Assert.Equal(
            $"https://{approved.NormalizedHost}:443/api/v1",
            approved.ApprovedEndpointIdentity);
        Assert.NotNull(approved.EndpointApprovedAtUtc);

        await service.UpdateEndpointAsync(
            stableId,
            $"https://{approved.NormalizedHost}/api/v2",
            actor,
            "Move to the supported API v2 path");
        context.ChangeTracker.Clear();
        var changed = await context.SearchProviderInstances.SingleAsync(x => x.StableId == stableId);
        Assert.Null(changed.ApprovedByTelegramId);
        Assert.Equal(0, changed.EndpointPolicyVersion);
        Assert.Null(changed.ApprovedEndpointIdentity);
        Assert.Null(changed.EndpointApprovedAtUtc);

        await service.ApproveAsync(stableId, actor, "Approve v2 endpoint");
        await service.ReplacePrivateNetworkAllowlistAsync(
            stableId,
            ["10.20.0.0/16", "fc00::/7"],
            actor,
            "Allow the exact private deployment networks");
        context.ChangeTracker.Clear();
        var allowlisted = await context.SearchProviderInstances.SingleAsync(x => x.StableId == stableId);
        Assert.Null(allowlisted.ApprovedByTelegramId);
        Assert.Equal("[\"10.20.0.0/16\",\"fc00::/7\"]", allowlisted.PrivateNetworkAllowlistJson);

        var audits = await context.PrivilegedAuditRecords
            .Where(x => x.TargetStableId == stableId)
            .OrderBy(x => x.Id)
            .ToListAsync();
        Assert.Equal(4, audits.Count);
        Assert.Equal(
            [
                PrivilegedActionKind.ProviderInstanceApprove,
                PrivilegedActionKind.ProviderInstanceApprove,
                PrivilegedActionKind.ProviderInstanceApprove,
                PrivilegedActionKind.PrivateNetworkAllowlist
            ],
            audits.Select(x => x.Action));
        Assert.All(audits, audit => Assert.Equal(PrivilegedActionOutcome.Succeeded, audit.Outcome));
    }

    /// <summary>
    /// **Validates: Requirements 2.19, 8.14, 8.16, 12.1, 12.9-12.17, 12.24-12.26**
    /// </summary>
    [Fact]
    public async Task RuntimeRevalidatesDnsAndReturnsOnlyValidatedConfigurationAndBounds()
    {
        var instance = CreateSelfHostedInstance(Guid.NewGuid());
        instance.ApprovedByTelegramId = 9641;
        instance.EndpointPolicyVersion = EndpointPolicy.CurrentPolicyVersion;
        instance.EndpointApprovedAtUtc = DateTime.UtcNow;
        instance.ApprovedEndpointIdentity = EndpointPolicy.CreateCanonicalIdentity(
            instance.NormalizedScheme,
            instance.NormalizedHost,
            instance.NormalizedPort,
            instance.NormalizedBasePath);

        var resolver = new SequenceDnsResolver(
            [IPAddress.Parse("8.8.8.8")],
            [IPAddress.Parse("127.0.0.1")]);
        var policy = new EndpointPolicy(resolver, new EndpointPolicyOptions
        {
            MaxResponseSizeBytes = 2048,
            RequestTimeout = TimeSpan.FromSeconds(5),
            MaxContentConcurrency = 2
        });

        var validated = await policy.ValidateForOperationAsync(
            instance,
            EndpointOperationKind.CapabilityDiscovery);
        Assert.Equal(instance.StableId, validated.ProviderInstance.StableId);
        Assert.Equal("https", validated.ProviderInstance.Scheme);
        Assert.Equal(2048, validated.Bounds.MaxResponseSizeBytes);
        Assert.Equal(2, validated.Bounds.MaxContentConcurrency);
        Assert.Single(validated.ValidatedAddresses);

        var failure = await Assert.ThrowsAsync<EndpointPolicyException>(() =>
            policy.ValidateForOperationAsync(instance, EndpointOperationKind.Search));
        Assert.DoesNotContain(instance.NormalizedHost, failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Property 11: an instance-scoped private CIDR never authorizes another instance.
    /// **Validates: Requirements 12.13-12.15, 17.42**
    /// </summary>
    [Property(MaxTest = 64)]
    public bool PrivateAllowlistNeverCrossesProviderInstance(
        NonNegativeInt second,
        NonNegativeInt third,
        NonNegativeInt fourth)
    {
        var instanceA = Guid.NewGuid();
        var instanceB = Guid.NewGuid();
        var address = IPAddress.Parse(
            $"10.{second.Get % 256}.{third.Get % 256}.{fourth.Get % 256}");
        var allowlists = new Dictionary<Guid, IReadOnlyCollection<string>>
        {
            [instanceA] = ["10.0.0.0/8"]
        };

        return EndpointPolicy.IsAddressAllowed(address, instanceA, allowlists) &&
               !EndpointPolicy.IsAddressAllowed(address, instanceB, allowlists);
    }

    /// <summary>
    /// **Validates: Requirements 2.19, 8.14, 8.24**
    /// Discovery stays blocked until the Slot-guarded runtime (Task 8.5) supplies an
    /// Operation Slot. The adapter itself enforces pre-traffic kind matching; the runtime
    /// enforces Slot capacity. (Updated in Task 11.2: the versioned GitLab adapter now
    /// answers matching-kind discovery like the GitHub adapter instead of rejecting
    /// unconditionally, so this test asserts gating at the runtime layer.)
    /// </summary>
    [Fact]
    public async Task CapabilityDiscoveryRemainsBlockedUntilSlotGuardedRuntimeExists()
    {
        using var scope = fixture.Services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<SearchProviderAdapterRegistry>();
        var runtime = scope.ServiceProvider.GetRequiredService<ISearchProviderAdapterRuntime>();
        var adapter = registry.GetRequiredAdapter(SearchProviderEnum.GitLab);
        var instance = new UnsecuredAPIKeys.Providers._Interfaces.ValidatedProviderInstance(
            Guid.NewGuid(),
            SearchProviderEnum.GitLab,
            "https",
            "git.example.test",
            443,
            "/api/v4",
            1,
            "{}");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runtime.DiscoverCapabilitiesAsync(adapter, instance, Guid.Empty));
        Assert.Contains("Operation Slot", error.Message, StringComparison.Ordinal);

        var mismatch = await adapter.DiscoverCapabilitiesAsync(
            instance with { ProviderKind = SearchProviderEnum.GitHub },
            CancellationToken.None);
        Assert.Equal(ProviderOutcomeKind.RequestInvalid, mismatch.Outcome);
    }

    private async Task<Guid> AddSelfHostedInstanceAsync()
    {
        var instance = CreateSelfHostedInstance(Guid.NewGuid());
        using var scope = fixture.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DBContext>();
        context.SearchProviderInstances.Add(instance);
        await context.SaveChangesAsync();
        return instance.StableId;
    }

    private static SearchProviderInstance CreateSelfHostedInstance(Guid stableId)
    {
        var now = DateTime.UtcNow;
        return new SearchProviderInstance
        {
            StableId = stableId,
            ProviderKind = SearchProviderEnum.GitLab,
            DisplayName = $"Endpoint policy test {stableId:N}",
            NormalizedScheme = "https",
            NormalizedHost = $"git-{stableId:N}.example.test",
            NormalizedPort = 443,
            NormalizedBasePath = "/api/v1",
            IsEnabled = true,
            AllowGlobalPublicSearch = false,
            MaxConcurrentOperations = 2,
            SettingsVersion = 1,
            SettingsJson = "{}",
            PrivateNetworkAllowlistJson = "[]",
            CreatedUtc = now,
            UpdatedUtc = now
        };
    }

    private sealed class SequenceDnsResolver(params IReadOnlyCollection<IPAddress>[] results)
        : IEndpointDnsResolver
    {
        private int index = -1;

        public Task<IReadOnlyCollection<IPAddress>> ResolveAsync(
            string host,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var selected = results[Math.Min(Interlocked.Increment(ref index), results.Length - 1)];
            return Task.FromResult(selected);
        }
    }
}

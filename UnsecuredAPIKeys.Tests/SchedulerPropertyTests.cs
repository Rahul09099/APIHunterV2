using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using UnsecuredAPIKeys.Data;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Data.Models;
using UnsecuredAPIKeys.Providers._Interfaces;
using UnsecuredAPIKeys.Services;
using UnsecuredAPIKeys.Tests.Infrastructure;
using Xunit;

namespace UnsecuredAPIKeys.Tests;

/// <summary>
/// Task 8.1/8.2 — Credential Scheduler property and unit tests.
/// Task 8.3 — Operation Slot capacity and terminal state tests.
/// Task 8.4 — Health transition tests (AC-5.63, AC-6.16, AC-6.19).
/// </summary>
public sealed class SchedulerPropertyTests(SharedMemorySqliteWebApiFixture fixture)
    : IClassFixture<SharedMemorySqliteWebApiFixture>
{
    // ── Deterministic jitter source for tests ──────────────────────────────────

    private sealed class ZeroJitterSource : ISchedulerJitterSource
    {
        public TimeSpan Next(TimeSpan maxJitter) => TimeSpan.Zero;
    }

    private sealed class FixedJitterSource(TimeSpan value) : ISchedulerJitterSource
    {
        public TimeSpan Next(TimeSpan maxJitter) => value > maxJitter ? maxJitter : value;
    }

    // ── LeasePolicyOptions validation ──────────────────────────────────────────

    [Fact]
    public void LeasePolicyOptions_DefaultsAreConsistent()
    {
        var policy = new LeasePolicyOptions();
        Assert.True(policy.DefaultLeaseDuration > TimeSpan.Zero);
        Assert.True(policy.MaxSingleLeaseDuration >= policy.DefaultLeaseDuration);
        Assert.True(policy.PerRenewalCap >= policy.MaxSingleLeaseDuration);
        Assert.InRange(policy.RenewalThresholdFraction, 0.0, 1.0);
        Assert.True(policy.ForbiddenScopeCooldown > TimeSpan.Zero);
        Assert.True(policy.TransientBackoffBase > TimeSpan.Zero);
        Assert.True(policy.MaxTransientBackoff >= policy.TransientBackoffBase);
        Assert.True(policy.MaxConsecutiveTransientFailures > 0);
    }

    [Fact]
    public void CryptographicJitterSource_ProducesValueWithinRange()
    {
        var source = new CryptographicSchedulerJitterSource();
        var max = TimeSpan.FromSeconds(30);
        for (var i = 0; i < 100; i++)
        {
            var jitter = source.Next(max);
            Assert.InRange(jitter.Ticks, 0, max.Ticks);
        }
    }

    [Fact]
    public void CryptographicJitterSource_ReturnsZeroForZeroMaxJitter()
    {
        var source = new CryptographicSchedulerJitterSource();
        Assert.Equal(TimeSpan.Zero, source.Next(TimeSpan.Zero));
    }

    // ── Scheduler: Claim acquires exclusive Lease ──────────────────────────────

    [Fact]
    public async Task Scheduler_ClaimAcquiresExclusiveLease_WhenCredentialIsEligible()
    {
        using var scope = fixture.Services.CreateScope();
        var (scheduler, dbContext, workItem) = await SetupTestWorkItemAsync(scope);

        var outcome = await scheduler.TryClaimAsync(
            workItem.ProviderInstanceStableId,
            workItem.WorkItemStableId,
            workItem.PartitionKey,
            nodeId: "test-node-1",
            requestId: Guid.NewGuid(),
            CredentialGrantScope.Admin,
            principalTelegramId: null);

        Assert.True(outcome.IsSuccess, $"Expected success but got: {outcome.Unavailable?.Reason}");
        Assert.NotEqual(Guid.Empty, outcome.Success!.LeaseId);
        Assert.Equal(workItem.ProviderInstanceStableId, outcome.Success.ProviderInstanceStableId);
        Assert.True(outcome.Success.CredentialRevision > 0);

        // Verify the credential now holds the lease
        dbContext.ChangeTracker.Clear();
        var credential = await dbContext.SearchProviderTokens
            .Where(t => t.StableId == outcome.Success.CredentialStableId)
            .SingleAsync();
        Assert.Equal(outcome.Success.LeaseId, credential.LeaseId);
        Assert.Equal("test-node-1", credential.LeaseOwnerNodeId);
    }

    [Fact]
    public async Task Scheduler_ClaimFails_WhenNoCooldownFreeCredentialExists()
    {
        using var scope = fixture.Services.CreateScope();
        var (scheduler, dbContext, workItem) = await SetupTestWorkItemAsync(scope);

        // Cool down the credential
        var credential = await GetTestCredentialAsync(dbContext, workItem.ProviderInstanceStableId);
        credential.CooldownUntilUtc = DateTime.UtcNow.AddHours(1);
        await dbContext.SaveChangesAsync();

        var outcome = await scheduler.TryClaimAsync(
            workItem.ProviderInstanceStableId,
            workItem.WorkItemStableId,
            workItem.PartitionKey,
            nodeId: "test-node-1",
            requestId: Guid.NewGuid(),
            CredentialGrantScope.Admin,
            principalTelegramId: null);

        Assert.False(outcome.IsSuccess);
        Assert.NotNull(outcome.Unavailable);
    }

    // ── AC-5.63: Per-renewal cap ───────────────────────────────────────────────

    [Fact]
    public async Task Scheduler_RenewalRejectsWhenPerRenewalCapExceeded_AC563()
    {
        using var scope = fixture.Services.CreateScope();
        var (scheduler, dbContext, workItem) = await SetupTestWorkItemAsync(scope, new LeasePolicyOptions
        {
            DefaultLeaseDuration = TimeSpan.FromMinutes(5),
            MaxSingleLeaseDuration = TimeSpan.FromMinutes(15),
            PerRenewalCap = TimeSpan.FromMinutes(10), // short cap for test
            ContinuousOperationCap = TimeSpan.FromHours(8),
            RenewalThresholdFraction = 0.7,
            ForbiddenScopeCooldown = TimeSpan.FromHours(1),
            TransientBackoffBase = TimeSpan.FromSeconds(30),
            MaxTransientBackoff = TimeSpan.FromMinutes(30),
            MaxConsecutiveTransientFailures = 5,
            RateLimitCooldown = TimeSpan.FromMinutes(15)
        });

        var claimOutcome = await scheduler.TryClaimAsync(
            workItem.ProviderInstanceStableId,
            workItem.WorkItemStableId,
            workItem.PartitionKey,
            nodeId: "test-node-1",
            requestId: Guid.NewGuid(),
            CredentialGrantScope.Admin,
            principalTelegramId: null);

        Assert.True(claimOutcome.IsSuccess);

        // Simulate the Lease having been held for longer than PerRenewalCap by back-dating LeaseAcquiredUtc
        var credential = await dbContext.SearchProviderTokens
            .Where(t => t.StableId == claimOutcome.Success!.CredentialStableId)
            .SingleAsync();
        credential.LeaseAcquiredUtc = DateTime.UtcNow.AddMinutes(-20); // 20 minutes ago > 10 min cap
        await dbContext.SaveChangesAsync();

        var renewalResult = await scheduler.TryRenewAsync(
            claimOutcome.Success!.LeaseId,
            claimOutcome.Success.CredentialStableId,
            claimOutcome.Success.CredentialRevision,
            requestedExtension: TimeSpan.FromMinutes(5));

        Assert.False(renewalResult.Succeeded);
        Assert.Contains("cap", renewalResult.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    // ── AC-6.16: ForbiddenScope cooldown is policy-configured, not a flag ─────

    [Fact]
    public async Task Scheduler_ForbiddenScopeAppliesPolicyCooldown_NotFeatureFlag_AC616()
    {
        using var scope = fixture.Services.CreateScope();
        var cooldown = TimeSpan.FromMinutes(42);
        var (scheduler, dbContext, workItem) = await SetupTestWorkItemAsync(scope, new LeasePolicyOptions
        {
            ForbiddenScopeCooldown = cooldown
        });

        var claimOutcome = await scheduler.TryClaimAsync(
            workItem.ProviderInstanceStableId,
            workItem.WorkItemStableId,
            workItem.PartitionKey,
            nodeId: "test-node-1",
            requestId: Guid.NewGuid(),
            CredentialGrantScope.Admin,
            principalTelegramId: null);

        Assert.True(claimOutcome.IsSuccess);

        await scheduler.CompleteAsync(
            claimOutcome.Success!.LeaseId,
            claimOutcome.Success.CredentialStableId,
            claimOutcome.Success.CredentialRevision,
            ProviderOutcomeKind.ForbiddenScope);

        dbContext.ChangeTracker.Clear();
        var credential = await dbContext.SearchProviderTokens
            .Where(t => t.StableId == claimOutcome.Success.CredentialStableId)
            .SingleAsync();

        Assert.NotNull(credential.CooldownUntilUtc);
        var actualCooldown = credential.CooldownUntilUtc.Value - DateTime.UtcNow;
        Assert.True(actualCooldown > TimeSpan.FromMinutes(40),
            $"Expected ~42min cooldown but got {actualCooldown.TotalMinutes:F1}min");

        // Verify no Lease remains
        Assert.Null(credential.LeaseId);
    }

    // ── AC-6.19: Injectable jitter for transient backoff ──────────────────────

    [Fact]
    public async Task Scheduler_TransientFailure_UsesInjectableJitter_AC619()
    {
        using var scope = fixture.Services.CreateScope();
        var fixedJitter = TimeSpan.FromSeconds(17);
        var (scheduler, dbContext, workItem) = await SetupTestWorkItemAsync(
            scope,
            new LeasePolicyOptions { TransientBackoffBase = TimeSpan.FromSeconds(30) },
            new FixedJitterSource(fixedJitter));

        var claimOutcome = await scheduler.TryClaimAsync(
            workItem.ProviderInstanceStableId,
            workItem.WorkItemStableId,
            workItem.PartitionKey,
            nodeId: "test-node-1",
            requestId: Guid.NewGuid(),
            CredentialGrantScope.Admin,
            principalTelegramId: null);

        Assert.True(claimOutcome.IsSuccess);

        await scheduler.CompleteAsync(
            claimOutcome.Success!.LeaseId,
            claimOutcome.Success.CredentialStableId,
            claimOutcome.Success.CredentialRevision,
            ProviderOutcomeKind.Transient);

        dbContext.ChangeTracker.Clear();
        var credential = await dbContext.SearchProviderTokens
            .Where(t => t.StableId == claimOutcome.Success.CredentialStableId)
            .SingleAsync();

        Assert.NotNull(credential.CooldownUntilUtc);
        // Expected: 30s base + 17s jitter = 47s total; allow ±5s for test timing
        var actualCooldown = credential.CooldownUntilUtc.Value - DateTime.UtcNow;
        Assert.InRange(actualCooldown.TotalSeconds, 40, 55);
    }

    // ── Health transitions ─────────────────────────────────────────────────────

    [Fact]
    public async Task Scheduler_AuthInvalid_DisablesCredential()
    {
        using var scope = fixture.Services.CreateScope();
        var (scheduler, dbContext, workItem) = await SetupTestWorkItemAsync(scope);

        var claimOutcome = await scheduler.TryClaimAsync(
            workItem.ProviderInstanceStableId,
            workItem.WorkItemStableId,
            workItem.PartitionKey,
            nodeId: "test-node-1",
            requestId: Guid.NewGuid(),
            CredentialGrantScope.Admin,
            principalTelegramId: null);

        Assert.True(claimOutcome.IsSuccess);

        await scheduler.CompleteAsync(
            claimOutcome.Success!.LeaseId,
            claimOutcome.Success.CredentialStableId,
            claimOutcome.Success.CredentialRevision,
            ProviderOutcomeKind.AuthInvalid);

        dbContext.ChangeTracker.Clear();
        var credential = await dbContext.SearchProviderTokens
            .Where(t => t.StableId == claimOutcome.Success.CredentialStableId)
            .SingleAsync();

        Assert.False(credential.IsEnabled);
        Assert.Equal("AuthInvalid", credential.DisabledReason);
        Assert.NotNull(credential.DisabledAtUtc);
        Assert.Null(credential.LeaseId);
    }

    [Fact]
    public async Task Scheduler_Success_ClearsTransientFailuresAndLease()
    {
        using var scope = fixture.Services.CreateScope();
        var (scheduler, dbContext, workItem) = await SetupTestWorkItemAsync(scope);

        var claimOutcome = await scheduler.TryClaimAsync(
            workItem.ProviderInstanceStableId,
            workItem.WorkItemStableId,
            workItem.PartitionKey,
            nodeId: "test-node-1",
            requestId: Guid.NewGuid(),
            CredentialGrantScope.Admin,
            principalTelegramId: null);

        Assert.True(claimOutcome.IsSuccess);

        // Pre-set some transient failures to verify they get cleared
        var credential = await dbContext.SearchProviderTokens
            .Where(t => t.StableId == claimOutcome.Success!.CredentialStableId)
            .SingleAsync();
        credential.ConsecutiveTransientFailures = 3;
        credential.CooldownUntilUtc = DateTime.UtcNow.AddMinutes(1);
        await dbContext.SaveChangesAsync();

        await scheduler.CompleteAsync(
            claimOutcome.Success!.LeaseId,
            claimOutcome.Success.CredentialStableId,
            claimOutcome.Success.CredentialRevision,
            ProviderOutcomeKind.Success);

        dbContext.ChangeTracker.Clear();
        credential = await dbContext.SearchProviderTokens
            .Where(t => t.StableId == claimOutcome.Success.CredentialStableId)
            .SingleAsync();

        Assert.Equal(0, credential.ConsecutiveTransientFailures);
        Assert.Null(credential.CooldownUntilUtc);
        Assert.Null(credential.LeaseId);
        Assert.True(credential.IsEnabled);
    }

    // ── Operation Slot capacity ────────────────────────────────────────────────

    [Fact]
    public async Task Scheduler_RejectsClaimWhenConcurrencyLimitReached()
    {
        using var scope = fixture.Services.CreateScope();

        // Create an instance with MaxConcurrentOperations = 1 and two credentials
        var dbContext = scope.ServiceProvider.GetRequiredService<DBContext>();
        var (instanceId, instanceStableId) = await CreateTestInstanceAsync(dbContext, maxConcurrent: 1);
        var cred1StableId = await CreateTestCredentialAsync(dbContext, instanceId);
        var cred2StableId = await CreateTestCredentialAsync(dbContext, instanceId);
        var workItem1 = await CreateTestWorkItemAsync(dbContext, instanceId, instanceStableId);
        var workItem2 = await CreateTestWorkItemAsync(dbContext, instanceId, instanceStableId);

        var policy = new LeasePolicyOptions();
        var scheduler = CreateScheduler(scope, policy);

        // First claim should succeed
        var claim1 = await scheduler.TryClaimAsync(
            instanceStableId, workItem1.WorkItemStableId, workItem1.PartitionKey,
            "node-1", Guid.NewGuid(), CredentialGrantScope.Admin, null);
        Assert.True(claim1.IsSuccess);

        // Second claim on same instance should fail (capacity exhausted)
        var claim2 = await scheduler.TryClaimAsync(
            instanceStableId, workItem2.WorkItemStableId, workItem2.PartitionKey,
            "node-1", Guid.NewGuid(), CredentialGrantScope.Admin, null);
        Assert.False(claim2.IsSuccess);
        Assert.Contains("concurrent", claim2.Unavailable!.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private record SetupContext(
        Guid ProviderInstanceStableId,
        Guid WorkItemStableId,
        string PartitionKey);

    private async Task<(ICredentialScheduler, DBContext, SetupContext)> SetupTestWorkItemAsync(
        IServiceScope scope,
        LeasePolicyOptions? policyOverride = null,
        ISchedulerJitterSource? jitterOverride = null)
    {
        var dbContext = scope.ServiceProvider.GetRequiredService<DBContext>();
        var (instanceId, instanceStableId) = await CreateTestInstanceAsync(dbContext, maxConcurrent: 4);
        await CreateTestCredentialAsync(dbContext, instanceId);

        var workItemResult = await scope.ServiceProvider
            .GetRequiredService<IWorkService>()
            .CreateWorkItemAsync(
                instanceId,
                SearchProviderEnum.GitHub,
                CredentialGrantScope.Admin,
                null,
                new Providers._Interfaces.SearchQuerySnapshot("q", null, "{}"),
                "github-metadata-v1");

        var policy = policyOverride ?? new LeasePolicyOptions();
        var scheduler = CreateScheduler(scope, policy, jitterOverride);

        return (scheduler, dbContext, new SetupContext(
            instanceStableId,
            workItemResult.WorkItemStableId,
            "default"));
    }

    private static ICredentialScheduler CreateScheduler(
        IServiceScope scope,
        LeasePolicyOptions policy,
        ISchedulerJitterSource? jitter = null)
    {
        var dbContext = scope.ServiceProvider.GetRequiredService<DBContext>();
        var clock = scope.ServiceProvider.GetRequiredService<IDatabaseUtcClock>();
        return new SqliteCredentialScheduler(
            dbContext, policy, jitter ?? new ZeroJitterSource(), clock);
    }

    private static async Task<(long Id, Guid StableId)> CreateTestInstanceAsync(
        DBContext dbContext,
        int maxConcurrent = 4)
    {
        var stableId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var instance = new SearchProviderInstance
        {
            StableId = stableId,
            ProviderKind = SearchProviderEnum.GitHub,
            DisplayName = $"Scheduler test {stableId:N}",
            NormalizedScheme = "https",
            NormalizedHost = $"scheduler-test-{stableId:N}.example.test",
            NormalizedPort = 443,
            NormalizedBasePath = "/",
            IsEnabled = true,
            AllowGlobalPublicSearch = false,
            MaxConcurrentOperations = maxConcurrent,
            SettingsVersion = 1,
            SettingsJson = "{}",
            PrivateNetworkAllowlistJson = "[]",
            CreatedUtc = now,
            UpdatedUtc = now
        };
        dbContext.SearchProviderInstances.Add(instance);
        await dbContext.SaveChangesAsync();
        return (instance.Id, instance.StableId);
    }

    private static async Task<Guid> CreateTestCredentialAsync(DBContext dbContext, long instanceId)
    {
        var stableId = Guid.NewGuid();
        // Use a minimal but valid protected credential (envelope version 0 = no envelope)
        var token = new SearchProviderToken
        {
            StableId = stableId,
            SearchProvider = SearchProviderEnum.GitHub,
            ProviderInstanceId = instanceId,
            IsEnabled = true,
            Source = Data.Common.CredentialSource.Environment,
            CreatedUtc = DateTime.UtcNow.AddSeconds(-2),
            UpdatedUtc = DateTime.UtcNow.AddSeconds(-2)
        };
        dbContext.SearchProviderTokens.Add(token);
        await dbContext.SaveChangesAsync();
        return stableId;
    }

    private static async Task<SetupContext> CreateTestWorkItemAsync(
        DBContext dbContext,
        long instanceId,
        Guid instanceStableId)
    {
        var baseTime = DateTime.UtcNow.AddSeconds(-2);
        var workItem = new WorkItem
        {
            StableId = Guid.NewGuid(),
            PrincipalScope = CredentialGrantScope.Admin,
            ProviderInstanceId = instanceId,
            ProviderKind = SearchProviderEnum.GitHub,
            EffectiveQueryHash = new string('a', 64),
            AdapterVersion = "github-metadata-v1",
            QuerySnapshotJson = "{}",
            CreatedUtc = baseTime,
            UpdatedUtc = baseTime
        };
        dbContext.WorkItems.Add(workItem);
        await dbContext.SaveChangesAsync();

        var partition = new WorkPartition
        {
            StableId = Guid.NewGuid(),
            WorkItemId = workItem.Id,
            PartitionKey = "default",
            CreatedUtc = baseTime,
            UpdatedUtc = baseTime
        };
        dbContext.WorkPartitions.Add(partition);
        await dbContext.SaveChangesAsync();

        return new SetupContext(instanceStableId, workItem.StableId, "default");
    }

    private static async Task<SearchProviderToken> GetTestCredentialAsync(
        DBContext dbContext,
        Guid instanceStableId)
    {
        return await dbContext.SearchProviderTokens
            .Where(t =>
                t.ProviderInstance != null &&
                t.ProviderInstance.StableId == instanceStableId &&
                t.IsEnabled &&
                t.LeaseId == null)
            .FirstAsync();
    }
}

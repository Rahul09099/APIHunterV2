using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using UnsecuredAPIKeys.Data;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Data.DTOs;
using UnsecuredAPIKeys.Data.Models;
using UnsecuredAPIKeys.Providers._Interfaces;
using UnsecuredAPIKeys.Services;
using Xunit;

namespace UnsecuredAPIKeys.Tests;

/// <summary>
/// Task 13.3 — Worker discovery provenance validation tests.
/// Seeds durable instance/work/claim/slot identity directly and proves the validator
/// accepts fully-attributed discoveries while rejecting omitted, unknown, conflicting,
/// or mismatched identity per item.
/// </summary>
public sealed class WorkerDiscoveryProvenanceTests
{
    private const long TelegramId = 77001;

    private sealed class Store : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        public Store(SqliteConnection connection, DBContext context)
        {
            this.connection = connection;
            Context = context;
        }

        public DBContext Context { get; }
        public SearchProviderInstance Instance { get; set; } = null!;
        public Guid WorkStableId { get; set; }
        public long WorkId { get; set; }
        public Guid PartitionStableId { get; set; }
        public Guid RequestId { get; set; }
        public Guid LeaseId { get; set; }
        public Guid SlotId { get; set; }

        public static async Task<Store> CreateAsync()
        {
            var sqlConnection = new SqliteConnection("Data Source=:memory:");
            await sqlConnection.OpenAsync();
            var context = new DBContext(
                new DbContextOptionsBuilder<DBContext>()
                    .UseSqlite(sqlConnection)
                    .Options);
            await context.Database.EnsureCreatedAsync();

            var now = DateTime.UtcNow;
            var instance = new SearchProviderInstance
            {
                StableId = ProviderInstanceSchema.DefaultGitHubStableId,
                ProviderKind = SearchProviderEnum.GitHub,
                DisplayName = "GitHub SaaS",
                NormalizedScheme = "https",
                NormalizedHost = "api.github.com",
                NormalizedPort = 443,
                NormalizedBasePath = "/",
                IsEnabled = true,
                MaxConcurrentOperations = 4,
                SettingsVersion = 1,
                SettingsJson = "{}",
                PrivateNetworkAllowlistJson = "[]",
                CreatedUtc = now,
                UpdatedUtc = now
            };
            context.SearchProviderInstances.Add(instance);
            await context.SaveChangesAsync();

            var work = await new WorkService(context).CreateWorkItemAsync(
                instance.Id,
                SearchProviderEnum.GitHub,
                CredentialGrantScope.User,
                TelegramId,
                new SearchQuerySnapshot("SECRET", null, "{}"),
                "test-v1");

            var requestId = Guid.NewGuid();
            var leaseId = Guid.NewGuid();
            var slotId = Guid.NewGuid();
            context.CredentialClaimRecords.Add(new CredentialClaimRecord
            {
                PrincipalScope = CredentialGrantScope.User,
                PrincipalTelegramId = TelegramId,
                RequestId = requestId,
                CredentialStableId = Guid.NewGuid(),
                ProviderInstanceStableId = instance.StableId,
                WorkItemId = work.WorkItemId,
                LeaseId = leaseId,
                LeaseOwnerNodeId = $"node:{TelegramId}",
                LeaseAcquiredUtc = now,
                LeaseExpiresUtc = now.AddMinutes(5),
                CredentialRevision = 3,
                CreatedUtc = now,
                UpdatedUtc = now
            });
            context.OperationSlots.Add(new OperationSlot
            {
                SlotId = slotId,
                ProviderInstanceId = instance.Id,
                RequestId = requestId,
                PrincipalScope = CredentialGrantScope.User,
                PrincipalTelegramId = TelegramId,
                WorkItemId = work.WorkItemId,
                PartitionKey = "default",
                AcquiredUtc = now,
                ExpiresUtc = now.AddMinutes(5),
                Revision = 0,
                CreatedUtc = now,
                UpdatedUtc = now
            });
            await context.SaveChangesAsync();

            return new Store(sqlConnection, context)
            {
                Instance = instance,
                WorkStableId = work.WorkItemStableId,
                WorkId = work.WorkItemId,
                PartitionStableId = work.PartitionStableId,
                RequestId = requestId,
                LeaseId = leaseId,
                SlotId = slotId
            };
        }

        public NodeReportDto ValidDiscovery() => new()
        {
            ApiKey = "SECRET-KEY-1",
            ApiType = ApiTypeEnum.AWSIAM,
            RepoName = "demo",
            RepoOwner = "octo",
            FilePath = "app/.env",
            FileUrl = "https://github.com/octo/demo/blob/abc/app/.env",
            ProviderKind = SearchProviderEnum.GitHub,
            ProviderInstanceStableId = Instance.StableId,
            WorkItemStableId = WorkStableId,
            WorkPartitionStableId = PartitionStableId,
            PartitionKey = "default",
            LeaseId = LeaseId,
            OperationSlotId = SlotId
        };

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private static SchedulerPrincipal Principal() =>
        SchedulerPrincipal.ForTelegram(TelegramId, isAdministrator: false);

    [Fact]
    public async Task FullyAttributedDiscovery_IsAccepted()
    {
        await using var store = await Store.CreateAsync();
        var validator = new WorkerDiscoveryReportValidator(store.Context);

        var result = await validator.ValidateAsync(Principal(), [store.ValidDiscovery()]);

        Assert.Equal(1, result.AcceptedCount);
        Assert.Equal(0, result.RejectedCount);
        Assert.True(result.Items[0].Accepted);
    }

    [Fact]
    public async Task MinimalDiscovery_WithoutOptionalIdentity_IsAccepted()
    {
        await using var store = await Store.CreateAsync();
        var validator = new WorkerDiscoveryReportValidator(store.Context);
        var discovery = store.ValidDiscovery();
        discovery.WorkItemStableId = null;
        discovery.WorkPartitionStableId = null;
        discovery.PartitionKey = null;
        discovery.OperationSlotId = null;

        var result = await validator.ValidateAsync(Principal(), [discovery]);

        Assert.Equal(1, result.AcceptedCount);
    }

    [Fact]
    public async Task OmittedProviderKind_IsRejected()
    {
        await using var store = await Store.CreateAsync();
        var validator = new WorkerDiscoveryReportValidator(store.Context);
        var discovery = store.ValidDiscovery();
        discovery.ProviderKind = SearchProviderEnum.Unknown;

        var result = await validator.ValidateAsync(Principal(), [discovery]);

        Assert.Equal(0, result.AcceptedCount);
        Assert.Equal(1, result.RejectedCount);
        Assert.Contains("provider kind", result.Items[0].RejectionReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnknownInstance_IsRejected()
    {
        await using var store = await Store.CreateAsync();
        var validator = new WorkerDiscoveryReportValidator(store.Context);
        var discovery = store.ValidDiscovery();
        discovery.ProviderInstanceStableId = Guid.NewGuid();

        var result = await validator.ValidateAsync(Principal(), [discovery]);

        Assert.Equal(0, result.AcceptedCount);
        Assert.Contains("unknown", result.Items[0].RejectionReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConflictingKindAndInstance_IsRejected()
    {
        await using var store = await Store.CreateAsync();
        var validator = new WorkerDiscoveryReportValidator(store.Context);
        var discovery = store.ValidDiscovery();
        discovery.ProviderKind = SearchProviderEnum.GitLab;

        var result = await validator.ValidateAsync(Principal(), [discovery]);

        Assert.Equal(0, result.AcceptedCount);
        Assert.Contains("conflict", result.Items[0].RejectionReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OmittedLease_IsRejected()
    {
        await using var store = await Store.CreateAsync();
        var validator = new WorkerDiscoveryReportValidator(store.Context);
        var discovery = store.ValidDiscovery();
        discovery.LeaseId = null;

        var result = await validator.ValidateAsync(Principal(), [discovery]);

        Assert.Equal(0, result.AcceptedCount);
    }

    [Fact]
    public async Task UnknownLease_IsRejected()
    {
        await using var store = await Store.CreateAsync();
        var validator = new WorkerDiscoveryReportValidator(store.Context);
        var discovery = store.ValidDiscovery();
        discovery.LeaseId = Guid.NewGuid();

        var result = await validator.ValidateAsync(Principal(), [discovery]);

        Assert.Equal(0, result.AcceptedCount);
    }

    [Fact]
    public async Task ForeignPrincipalLease_IsRejected()
    {
        await using var store = await Store.CreateAsync();
        var validator = new WorkerDiscoveryReportValidator(store.Context);

        var result = await validator.ValidateAsync(
            SchedulerPrincipal.ForTelegram(77002, isAdministrator: false),
            [store.ValidDiscovery()]);

        Assert.Equal(0, result.AcceptedCount);
    }

    [Fact]
    public async Task ConflictingWorkIdentity_IsRejected()
    {
        await using var store = await Store.CreateAsync();
        var validator = new WorkerDiscoveryReportValidator(store.Context);
        var discovery = store.ValidDiscovery();
        discovery.WorkItemStableId = Guid.NewGuid();

        var result = await validator.ValidateAsync(Principal(), [discovery]);

        Assert.Equal(0, result.AcceptedCount);
    }

    [Fact]
    public async Task MismatchedPartitionKey_IsRejected()
    {
        await using var store = await Store.CreateAsync();
        var validator = new WorkerDiscoveryReportValidator(store.Context);
        var discovery = store.ValidDiscovery();
        discovery.WorkPartitionStableId = null;
        discovery.PartitionKey = "other-partition";

        var result = await validator.ValidateAsync(Principal(), [discovery]);

        Assert.Equal(0, result.AcceptedCount);
    }

    [Fact]
    public async Task MismatchedSlotIdentity_IsRejected()
    {
        await using var store = await Store.CreateAsync();
        var validator = new WorkerDiscoveryReportValidator(store.Context);
        var discovery = store.ValidDiscovery();
        discovery.OperationSlotId = Guid.NewGuid();

        var result = await validator.ValidateAsync(Principal(), [discovery]);

        Assert.Equal(0, result.AcceptedCount);
    }

    [Fact]
    public async Task BatchFailure_DoesNotRejectValidSibling()
    {
        await using var store = await Store.CreateAsync();
        var validator = new WorkerDiscoveryReportValidator(store.Context);
        var bad = store.ValidDiscovery();
        bad.LeaseId = Guid.NewGuid();

        var result = await validator.ValidateAsync(Principal(), [bad, store.ValidDiscovery()]);

        Assert.Equal(1, result.AcceptedCount);
        Assert.Equal(1, result.RejectedCount);
    }
}

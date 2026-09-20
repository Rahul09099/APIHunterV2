using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using UnsecuredAPIKeys.Data;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Data.DTOs;
using UnsecuredAPIKeys.Data.Models;
using UnsecuredAPIKeys.Providers;
using UnsecuredAPIKeys.Providers._Interfaces;
using UnsecuredAPIKeys.Providers.Search_Providers;
using UnsecuredAPIKeys.Services;
using UnsecuredAPIKeys.Tests.Infrastructure;
using UnsecuredAPIKeys.WebAPI.Controllers;
using Xunit;

namespace UnsecuredAPIKeys.Tests;

/// <summary>
/// Task 12.1 — Claim/Renew/Complete WebAPI contract tests.
/// Asserts exact request/response fields, expected Revision, one-secret/no-store Claim,
/// non-secret renew/complete, active and terminal replay, node ownership, and
/// 200/400/401/403/409 semantics (503 is covered at the host level). Canary
/// credential material and node tokens are injected through log capture and proven
/// absent from every emitted diagnostic.
/// Validates AC-1.6–AC-1.9, AC-5.20–AC-5.24, AC-5.37–AC-5.45, AC-5.50, AC-11.1–AC-11.25.
/// Task 12.3 — sync/claim DTO surfaces carry no control-plane secrets.
/// </summary>
public sealed class CredentialClaimApiTests
{
    private const long TelegramId = 77001;
    private const string NodeToken = "node-token-claim-contract-canary";
    private const string ForeignToken = "node-token-claim-contract-foreign";
    private const string CredentialCanary = "ghp_claim_api_canary_material_001";

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public readonly List<string> Messages = [];

        IDisposable ILogger.BeginScope<TState>(TState state) => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    private sealed class FixedClock(DateTime utcNow) : IDatabaseUtcClock
    {
        public Task<DateTime> GetUtcNowAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(utcNow);
    }

    private sealed class ZeroJitterSource : ISchedulerJitterSource
    {
        public TimeSpan Next(TimeSpan maxJitter) => TimeSpan.Zero;
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        public Harness(
            SqliteConnection connection,
            DBContext context,
            CredentialClaimsController controller,
            CapturingLogger<CredentialClaimsController> logger,
            Guid workStableId)
        {
            this.connection = connection;
            Context = context;
            Controller = controller;
            Logger = logger;
            WorkStableId = workStableId;
        }

        public DBContext Context { get; }
        public CredentialClaimsController Controller { get; }
        public CapturingLogger<CredentialClaimsController> Logger { get; }
        public Guid WorkStableId { get; }

        public static async Task<Harness> CreateAsync(bool seedForeignNode = false)
        {
            var sqlConnection = new SqliteConnection("Data Source=:memory:");
            await sqlConnection.OpenAsync();
            var context = new DBContext(
                new DbContextOptionsBuilder<DBContext>()
                    .UseSqlite(sqlConnection)
                    .Options);
            await context.Database.EnsureCreatedAsync();

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["SearchCredentials:Protection:ActiveKeyVersion"] = "7",
                    ["SearchCredentials:Protection:Keys:7"] = Convert.ToBase64String(
                        Enumerable.Repeat((byte)0x37, 32).ToArray()),
                    ["SearchCredentials:Fingerprint:ActiveKeyVersion"] = "11",
                    ["SearchCredentials:Fingerprint:Keys:11"] = Convert.ToBase64String(
                        Enumerable.Repeat((byte)0x5B, 32).ToArray())
                })
                .Build();

            var protection = new CredentialProtectionService(configuration);
            var fingerprint = new CredentialFingerprintService(configuration);
            var guard = new CredentialStorageMigrationGuard(configuration);
            var storage = new CredentialStorageService(protection, fingerprint, guard);
            var materialAccess = new CredentialMaterialAccessService(context, protection, guard);
            var databaseService = new DatabaseService(context, storage);

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
            context.TelegramSubscribers.Add(new TelegramSubscriber
            {
                TelegramId = TelegramId,
                NodeToken = NodeToken,
                IsAdmin = false
            });
            if (seedForeignNode)
            {
                context.TelegramSubscribers.Add(new TelegramSubscriber
                {
                    TelegramId = 77002,
                    NodeToken = ForeignToken,
                    IsAdmin = false
                });
            }
            await context.SaveChangesAsync();

            await databaseService.AddGitHubTokenAsync(context, CredentialCanary, addedBy: TelegramId);

            var work = await new WorkService(context).CreateWorkItemAsync(
                instance.Id,
                SearchProviderEnum.GitHub,
                CredentialGrantScope.User,
                TelegramId,
                new SearchQuerySnapshot("SECRET", null, "{}"),
                "test-v1");

            var clock = new FixedClock(now);
            var logger = new CapturingLogger<CredentialClaimsController>();
            var controller = new CredentialClaimsController(
                context,
                new SqliteCredentialScheduler(
                    context,
                    new LeasePolicyOptions(),
                    new ZeroJitterSource(),
                    clock,
                    NullLogger<SqliteCredentialScheduler>.Instance),
                materialAccess,
                new CredentialGrantEvaluator(context),
                new NodePrincipalResolver(context),
                new LeasePolicyOptions(),
                clock,
                new WorkService(context),
                new SearchProviderAdapterRegistry(
                [
                    new GitHubSearchProviderAdapter(),
                    new GitLabSearchProviderAdapter()
                ]),
                logger)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext()
                }
            };

            return new Harness(sqlConnection, context, controller, logger, work.WorkItemStableId);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private static (int Status, JsonElement Body) ReadBody(IActionResult result)
    {
        var completed = Assert.IsType<ObjectResult>(result);
        var status = completed.StatusCode ?? 200;
        return (status, JsonSerializer.SerializeToElement(completed.Value));
    }

    private static CredentialClaimRequest ClaimRequest(Guid workStableId, Guid? requestId = null) => new(
        ProviderInstanceSchema.DefaultGitHubStableId,
        workStableId,
        "default",
        requestId ?? Guid.NewGuid());

    private static void AssertNoCanaries(
        CapturingLogger<CredentialClaimsController> logger,
        string context)
    {
        foreach (var message in logger.Messages)
        {
            Assert.DoesNotContain(CredentialCanary, message, StringComparison.Ordinal);
            Assert.DoesNotContain(NodeToken, message, StringComparison.Ordinal);
            Assert.DoesNotContain(ForeignToken, message, StringComparison.Ordinal);
        }
        Assert.NotEmpty(logger.Messages);
    }

    // ── Authentication: 401 / 403 ──────────────────────────────────────────────

    [Fact]
    public async Task Claim_WithoutNodeToken_Returns401()
    {
        await using var harness = await Harness.CreateAsync();

        var (status, _) = ReadBody(await harness.Controller.Claim(
            null, ClaimRequest(harness.WorkStableId)));

        Assert.Equal(401, status);
    }

    [Fact]
    public async Task Claim_UnknownToken_Returns401()
    {
        await using var harness = await Harness.CreateAsync();

        var (status, _) = ReadBody(await harness.Controller.Claim(
            "no-such-node-token", ClaimRequest(harness.WorkStableId)));

        Assert.Equal(401, status);
    }

    [Fact]
    public async Task Claim_UnmappedNode_Returns403()
    {
        await using var harness = await Harness.CreateAsync();
        harness.Context.TelegramSubscribers.Add(new TelegramSubscriber
        {
            TelegramId = 0,
            NodeToken = "unmapped-node-token"
        });
        await harness.Context.SaveChangesAsync();

        var (status, _) = ReadBody(await harness.Controller.Claim(
            "unmapped-node-token", ClaimRequest(harness.WorkStableId)));

        Assert.Equal(403, status);
    }

    [Fact]
    public async Task Claim_WithoutGrant_Returns403()
    {
        await using var harness = await Harness.CreateAsync(seedForeignNode: true);

        // The foreign node holds no User/Global grant for the instance credential.
        var (status, _) = ReadBody(await harness.Controller.Claim(
            ForeignToken, ClaimRequest(harness.WorkStableId)));

        Assert.Equal(403, status);
        Assert.Empty(harness.Context.CredentialClaimRecords);
    }

    // ── Claim: one-secret issuance + replay ────────────────────────────────────

    [Fact]
    public async Task Claim_Success_ReturnsOneSecretLeaseAndRedactsLogs()
    {
        await using var harness = await Harness.CreateAsync();
        var requestId = Guid.NewGuid();

        var (status, body) = ReadBody(await harness.Controller.Claim(
            NodeToken, ClaimRequest(harness.WorkStableId, requestId)));

        Assert.Equal(200, status);
        Assert.Equal(CredentialCanary, body.GetProperty("CredentialMaterial").GetString());
        Assert.False(body.GetProperty("Replayed").GetBoolean());
        Assert.NotEqual(Guid.Empty, Guid.Parse(body.GetProperty("LeaseId").GetString()!));
        Assert.Equal(210, body.GetProperty("RenewalThresholdSeconds").GetDouble());

        AssertNoCanaries(harness.Logger, "claim");
        Assert.Contains(
            harness.Logger.Messages,
            message => message.Contains(body.GetProperty("LeaseId").GetString()!, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Claim_ActiveReplay_ReturnsSameLeaseWithoutMaterial()
    {
        await using var harness = await Harness.CreateAsync();
        var requestId = Guid.NewGuid();

        var (_, first) = ReadBody(await harness.Controller.Claim(
            NodeToken, ClaimRequest(harness.WorkStableId, requestId)));
        var firstLease = first.GetProperty("LeaseId").GetString();

        var (status, second) = ReadBody(await harness.Controller.Claim(
            NodeToken, ClaimRequest(harness.WorkStableId, requestId)));

        Assert.Equal(200, status);
        Assert.Equal(firstLease, second.GetProperty("LeaseId").GetString());
        Assert.True(second.GetProperty("Replayed").GetBoolean());
        Assert.True(second.GetProperty("CredentialMaterial").ValueKind == JsonValueKind.Null);
        Assert.Single(harness.Context.CredentialClaimRecords);

        AssertNoCanaries(harness.Logger, "active replay");
    }

    [Fact]
    public async Task Claim_TerminalReplay_ReturnsMetadataWithoutMaterial()
    {
        await using var harness = await Harness.CreateAsync();
        var requestId = Guid.NewGuid();

        var (_, first) = ReadBody(await harness.Controller.Claim(
            NodeToken, ClaimRequest(harness.WorkStableId, requestId)));
        var leaseId = Guid.Parse(first.GetProperty("LeaseId").GetString()!);
        var revision = first.GetProperty("CredentialRevision").GetInt64();
        var credentialStableId = Guid.Parse(first.GetProperty("CredentialStableId").GetString()!);

        var (_, completed) = ReadBody(await harness.Controller.Complete(
            NodeToken,
            leaseId,
            new CredentialLeaseCompleteRequest(credentialStableId, revision, "Success")));
        Assert.True(completed.GetProperty("Succeeded").GetBoolean());

        var (status, replay) = ReadBody(await harness.Controller.Claim(
            NodeToken, ClaimRequest(harness.WorkStableId, requestId)));

        Assert.Equal(200, status);
        Assert.True(replay.GetProperty("Replayed").GetBoolean());
        Assert.True(replay.GetProperty("CredentialMaterial").ValueKind == JsonValueKind.Null);
        Assert.Equal(leaseId, Guid.Parse(replay.GetProperty("LeaseId").GetString()!));
        Assert.Equal(1, await harness.Context.CredentialClaimRecords.CountAsync());

        AssertNoCanaries(harness.Logger, "terminal replay");
    }

    [Fact]
    public async Task Claim_InstanceSaturated_Returns409WithRetryAfter()
    {
        await using var harness = await Harness.CreateAsync();

        var instance = await harness.Context.SearchProviderInstances.SingleAsync();
        instance.MaxConcurrentOperations = 1;
        await harness.Context.SaveChangesAsync();

        var (firstStatus, _) = ReadBody(await harness.Controller.Claim(
            NodeToken, ClaimRequest(harness.WorkStableId)));
        Assert.Equal(200, firstStatus);

        var (status, body) = ReadBody(await harness.Controller.Claim(
            NodeToken, ClaimRequest(harness.WorkStableId)));

        Assert.Equal(409, status);
        Assert.Equal(300, body.GetProperty("RetryAfterSeconds").GetDouble());
        Assert.True(harness.Controller.HttpContext.Response.Headers.ContainsKey("Retry-After"));
    }

    // ── Renew ──────────────────────────────────────────────────────────────────

    private static async Task<(Guid LeaseId, long Revision, Guid CredentialStableId)> ClaimLeaseAsync(Harness harness)
    {
        var (_, body) = ReadBody(await harness.Controller.Claim(
            NodeToken, ClaimRequest(harness.WorkStableId)));
        return (
            Guid.Parse(body.GetProperty("LeaseId").GetString()!),
            body.GetProperty("CredentialRevision").GetInt64(),
            Guid.Parse(body.GetProperty("CredentialStableId").GetString()!));
    }

    [Fact]
    public async Task Renew_Success_ExtendsLeaseWithoutSecret()
    {
        await using var harness = await Harness.CreateAsync();
        var (leaseId, revision, credentialStableId) = await ClaimLeaseAsync(harness);

        var (status, body) = ReadBody(await harness.Controller.Renew(
            NodeToken,
            leaseId,
            new CredentialLeaseRenewRequest(
                credentialStableId,
                revision,
                harness.WorkStableId,
                ProviderInstanceSchema.DefaultGitHubStableId,
                "default",
                60)));

        Assert.Equal(200, status);
        Assert.True(body.GetProperty("Succeeded").GetBoolean());
        Assert.NotNull(body.GetProperty("NewLeaseExpiresUtc").GetString());
        Assert.True(body.GetProperty("FailureReason").ValueKind == JsonValueKind.Null);

        AssertNoCanaries(harness.Logger, "renew");
    }

    [Fact]
    public async Task Renew_WrongRevision_Returns409()
    {
        await using var harness = await Harness.CreateAsync();
        var (leaseId, revision, credentialStableId) = await ClaimLeaseAsync(harness);

        var (status, body) = ReadBody(await harness.Controller.Renew(
            NodeToken,
            leaseId,
            new CredentialLeaseRenewRequest(
                credentialStableId,
                revision + 999,
                harness.WorkStableId,
                ProviderInstanceSchema.DefaultGitHubStableId,
                "default",
                60)));

        Assert.Equal(409, status);
        Assert.False(body.GetProperty("Succeeded").GetBoolean());
    }

    [Fact]
    public async Task Renew_ForeignNode_Returns403()
    {
        await using var harness = await Harness.CreateAsync(seedForeignNode: true);
        var (leaseId, revision, credentialStableId) = await ClaimLeaseAsync(harness);

        var (status, _) = ReadBody(await harness.Controller.Renew(
            ForeignToken,
            leaseId,
            new CredentialLeaseRenewRequest(
                credentialStableId,
                revision,
                harness.WorkStableId,
                ProviderInstanceSchema.DefaultGitHubStableId,
                "default",
                60)));

        Assert.Equal(403, status);
    }

    [Fact]
    public async Task Renew_PartitionMismatch_Returns409()
    {
        await using var harness = await Harness.CreateAsync();
        var (leaseId, revision, credentialStableId) = await ClaimLeaseAsync(harness);

        var (status, _) = ReadBody(await harness.Controller.Renew(
            NodeToken,
            leaseId,
            new CredentialLeaseRenewRequest(
                credentialStableId,
                revision,
                harness.WorkStableId,
                ProviderInstanceSchema.DefaultGitHubStableId,
                "other-partition",
                60)));

        Assert.Equal(409, status);
    }

    // ── Complete ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Complete_Success_ReleasesLeaseWithoutSecret()
    {
        await using var harness = await Harness.CreateAsync();
        var (leaseId, revision, credentialStableId) = await ClaimLeaseAsync(harness);

        var (status, body) = ReadBody(await harness.Controller.Complete(
            NodeToken,
            leaseId,
            new CredentialLeaseCompleteRequest(credentialStableId, revision, "Success")));

        Assert.Equal(200, status);
        Assert.True(body.GetProperty("Succeeded").GetBoolean());

        harness.Context.ChangeTracker.Clear();
        var credential = await harness.Context.SearchProviderTokens.SingleAsync();
        Assert.Null(credential.LeaseId);
        Assert.True(credential.IsEnabled);

        AssertNoCanaries(harness.Logger, "complete");
    }

    [Fact]
    public async Task Complete_UnknownOutcome_Returns400()
    {
        await using var harness = await Harness.CreateAsync();
        var (leaseId, revision, credentialStableId) = await ClaimLeaseAsync(harness);

        var (status, _) = ReadBody(await harness.Controller.Complete(
            NodeToken,
            leaseId,
            new CredentialLeaseCompleteRequest(credentialStableId, revision, "Explode")));

        Assert.Equal(400, status);
    }

    [Fact]
    public async Task Complete_UnknownLease_Returns409()
    {
        await using var harness = await Harness.CreateAsync();

        var (status, body) = ReadBody(await harness.Controller.Complete(
            NodeToken,
            Guid.NewGuid(),
            new CredentialLeaseCompleteRequest(Guid.NewGuid(), 0, "Success")));

        Assert.Equal(409, status);
        Assert.False(body.GetProperty("Succeeded").GetBoolean());
    }

    [Fact]
    public async Task Complete_TerminalReplay_Returns200()
    {
        await using var harness = await Harness.CreateAsync();
        var (leaseId, revision, credentialStableId) = await ClaimLeaseAsync(harness);
        var request = new CredentialLeaseCompleteRequest(credentialStableId, revision, "Success");

        var (firstStatus, _) = ReadBody(await harness.Controller.Complete(NodeToken, leaseId, request));
        var (secondStatus, second) = ReadBody(await harness.Controller.Complete(NodeToken, leaseId, request with
        {
            ExpectedRevision = revision + 1
        }));

        Assert.Equal(200, firstStatus);
        Assert.Equal(200, secondStatus);
        Assert.True(second.GetProperty("Succeeded").GetBoolean());
    }

    // ── Task 12.3: sync/claim surfaces carry no control-plane secrets ──────────

    [Fact]
    public void ClaimAndSyncDtos_ExposeNoSecretMembersExceptOneSecretClaimResponse()
    {
        var forbiddenFragments = new[] { "token", "secret", "password", "fingerprint" };

        foreach (var type in new[]
                 {
                     typeof(NodeSyncDTO),
                     typeof(SearchQueryDTO),
                     typeof(CredentialClaimRequest),
                     typeof(CredentialLeaseRenewRequest),
                     typeof(CredentialLeaseCompleteRequest),
                     typeof(CredentialLeaseRenewResponse),
                     typeof(CredentialLeaseCompleteResponse)
                 })
        {
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                foreach (var fragment in forbiddenFragments)
                {
                    Assert.DoesNotContain(
                        fragment,
                        property.Name,
                        StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        var materialProperty = Assert.Single(
            typeof(CredentialClaimResponse).GetProperties(),
            property => forbiddenFragments.Any(
                fragment => property.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase)));
        Assert.Equal("CredentialMaterial", materialProperty.Name);
    }

    [Fact]
    public void ControlPlaneRedactor_RemovesNodeTokenAndCredentialFromDiagnostics()
    {
        var redactor = new ControlPlaneSecretRedactor();
        var rendered = $"X-Node-Token: {NodeToken}; credential={CredentialCanary}; leaseId={Guid.NewGuid()}";

        var redacted = redactor.Redact(rendered);

        Assert.DoesNotContain(NodeToken, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(CredentialCanary, redacted, StringComparison.Ordinal);
    }

    // ── Task 13.2/13.3: Worker claim identity ───────────────────────────────────

    [Fact]
    public async Task Claim_WithoutWorkItem_CreatesWorkAndCarriesSlotIdentity()
    {
        await using var harness = await Harness.CreateAsync();
        var workCountBefore = await harness.Context.WorkItems.CountAsync();

        var (status, body) = ReadBody(await harness.Controller.Claim(
            NodeToken,
            new CredentialClaimRequest(
                ProviderInstanceSchema.DefaultGitHubStableId,
                Guid.Empty,
                "default",
                Guid.NewGuid(),
                GenericQuery: "SECRET")));

        Assert.Equal(200, status);
        Assert.Equal(workCountBefore + 1, await harness.Context.WorkItems.CountAsync());
        Assert.NotEqual(Guid.Empty, Guid.Parse(body.GetProperty("WorkItemStableId").GetString()!));
        Assert.NotEqual(Guid.Empty, Guid.Parse(body.GetProperty("OperationSlotId").GetString()!));
        Assert.Equal("default", body.GetProperty("PartitionKey").GetString());
        Assert.Equal(CredentialCanary, body.GetProperty("CredentialMaterial").GetString());

        AssertNoCanaries(harness.Logger, "worker claim");
    }

    [Fact]
    public async Task Claim_TerminalReplay_CarriesSlotIdentityWithoutMaterial()
    {
        await using var harness = await Harness.CreateAsync();
        var requestId = Guid.NewGuid();

        var (_, first) = ReadBody(await harness.Controller.Claim(
            NodeToken, ClaimRequest(harness.WorkStableId, requestId)));
        var slotId = first.GetProperty("OperationSlotId").GetString();
        Assert.False(string.IsNullOrEmpty(slotId));
        Assert.NotEqual(Guid.Empty.ToString(), slotId);

        var leaseId = Guid.Parse(first.GetProperty("LeaseId").GetString()!);
        var revision = first.GetProperty("CredentialRevision").GetInt64();
        var credentialStableId = Guid.Parse(first.GetProperty("CredentialStableId").GetString()!);
        await harness.Controller.Complete(
            NodeToken,
            leaseId,
            new CredentialLeaseCompleteRequest(credentialStableId, revision, "Success"));

        var (status, replay) = ReadBody(await harness.Controller.Claim(
            NodeToken, ClaimRequest(harness.WorkStableId, requestId)));

        Assert.Equal(200, status);
        Assert.True(replay.GetProperty("Replayed").GetBoolean());
        Assert.Equal(slotId, replay.GetProperty("OperationSlotId").GetString());
    }
}

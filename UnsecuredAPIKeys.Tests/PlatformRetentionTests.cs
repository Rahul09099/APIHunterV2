using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using UnsecuredAPIKeys.Data;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Data.Models;
using UnsecuredAPIKeys.Services;
using Xunit;

namespace UnsecuredAPIKeys.Tests;

/// <summary>
/// Tasks 15.3/15.4 — Bounded audit/Claim retention tests.
/// Proves the purge preserves active Claims, terminal Claims inside the retry window
/// (and terminal Claims without terminal evidence, fail-safe), and in-window audit
/// records, while remaining bounded under release load.
/// Validates AC-13.45, AC-14.39, AC-17.49.
/// </summary>
public sealed class PlatformRetentionTests
{
    private sealed class FixedClock(DateTime utcNow) : IDatabaseUtcClock
    {
        public Task<DateTime> GetUtcNowAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(utcNow);
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        public Harness(SqliteConnection connection, DBContext context, DateTime now)
        {
            this.connection = connection;
            Context = context;
            Service = new PlatformRetentionService(
                context,
                new PlatformRetentionOptions(),
                new FixedClock(now));
        }

        public DBContext Context { get; }
        public PlatformRetentionService Service { get; }

        public static async Task<Harness> CreateAsync(DateTime now)
        {
            var sqlConnection = new SqliteConnection("Data Source=:memory:");
            await sqlConnection.OpenAsync();
            var context = new DBContext(
                new DbContextOptionsBuilder<DBContext>()
                    .UseSqlite(sqlConnection)
                    .Options);
            await context.Database.EnsureCreatedAsync();
            return new Harness(sqlConnection, context, now);
        }

        public void AddClaim(bool terminal, DateTime? terminalizedUtc, DateTime acquiredUtc)
        {
            Context.CredentialClaimRecords.Add(new CredentialClaimRecord
            {
                PrincipalScope = CredentialGrantScope.Admin,
                PrincipalTelegramId = null,
                RequestId = Guid.NewGuid(),
                CredentialStableId = Guid.NewGuid(),
                ProviderInstanceStableId = Guid.NewGuid(),
                WorkItemId = 1,
                LeaseId = Guid.NewGuid(),
                LeaseOwnerNodeId = "node:999",
                LeaseAcquiredUtc = acquiredUtc,
                LeaseExpiresUtc = acquiredUtc.AddMinutes(5),
                CredentialRevision = 1,
                IsTerminal = terminal,
                TerminalOutcome = terminal ? "Success" : null,
                TerminalizedUtc = terminalizedUtc,
                CreatedUtc = acquiredUtc,
                UpdatedUtc = acquiredUtc
            });
        }

        public void AddAudit(DateTime occurredUtc, long actor = 999)
        {
            Context.PrivilegedAuditRecords.Add(new PrivilegedAuditRecord
            {
                ActorTelegramId = actor,
                Action = PrivilegedActionKind.PublicSearchConsent,
                TargetStableId = Guid.NewGuid(),
                OccurredUtc = occurredUtc,
                Outcome = PrivilegedActionOutcome.Succeeded,
                SanitizedReason = "Retention load canary."
            });
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    [Fact]
    public async Task Purge_PreservesActiveAndWindowedRecordsWhileBoundingVolume()
    {
        var now = new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);
        await using var harness = await Harness.CreateAsync(now);

        for (var i = 0; i < 5; i++)
        {
            harness.AddClaim(terminal: false, terminalizedUtc: null, acquiredUtc: now.AddMinutes(-1));
        }
        for (var i = 0; i < 10; i++)
        {
            harness.AddClaim(terminal: true, terminalizedUtc: now.AddDays(-30), acquiredUtc: now.AddDays(-31));
        }
        for (var i = 0; i < 10; i++)
        {
            harness.AddClaim(terminal: true, terminalizedUtc: now.AddDays(-1), acquiredUtc: now.AddDays(-2));
        }
        for (var i = 0; i < 3; i++)
        {
            harness.AddClaim(terminal: true, terminalizedUtc: null, acquiredUtc: now.AddDays(-60));
        }
        for (var i = 0; i < 1200; i++)
        {
            harness.AddAudit(now.AddDays(-100));
        }
        for (var i = 0; i < 50; i++)
        {
            harness.AddAudit(now.AddDays(-1));
        }
        await harness.Context.SaveChangesAsync();

        var started = DateTime.UtcNow;
        var result = await harness.Service.PurgeAsync();
        var elapsed = DateTime.UtcNow - started;

        Assert.Equal(10, result.ExpiredTerminalClaimsRemoved);
        Assert.Equal(1200, result.ExpiredAuditRecordsRemoved);
        Assert.Equal(5 + 10 + 3, await harness.Context.CredentialClaimRecords.CountAsync());
        Assert.Equal(50, await harness.Context.PrivilegedAuditRecords.CountAsync());

        // Release-load purge completes promptly with exactly the expected survivors.
        Assert.True(elapsed < TimeSpan.FromSeconds(30));
        Assert.Equal(18, await harness.Context.CredentialClaimRecords.CountAsync());
    }
}

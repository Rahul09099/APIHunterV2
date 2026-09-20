using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using UnsecuredAPIKeys.Data;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Data.Models;
using UnsecuredAPIKeys.Services;
using Xunit;

namespace UnsecuredAPIKeys.Tests;

/// <summary>
/// Retry audit: Scheduler Claim returns earliest-cooldown/lease RetryAfter
/// when no credential is eligible (AC-5.x earliest-retry). Covers all providers
/// via the shared scheduler path (no provider branch).
/// </summary>
public sealed class SchedulerEarliestRetryTests
{
    private sealed class SeededDb : IAsyncDisposable
    {
        public DBContext Db { get; }
        public SearchProviderInstance Instance { get; }
        private readonly SqliteConnection connection;

        private SeededDb(DBContext db, SearchProviderInstance instance, SqliteConnection connection)
        {
            Db = db;
            Instance = instance;
            this.connection = connection;
        }

        public static async Task<SeededDb> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<DBContext>().UseSqlite(connection).Options;
            var db = new DBContext(options);
            await db.Database.EnsureCreatedAsync();

            var now = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Utc);
            var instance = new SearchProviderInstance
            {
                StableId = Guid.NewGuid(),
                ProviderKind = SearchProviderEnum.Sourcegraph,
                DisplayName = "RetryAudit",
                NormalizedScheme = "https",
                NormalizedHost = "sourcegraph.com",
                NormalizedPort = 443,
                NormalizedBasePath = "/.api",
                IsEnabled = true,
                MaxConcurrentOperations = 4,
                SettingsVersion = 1,
                SettingsJson = "{}",
                PrivateNetworkAllowlistJson = "[]",
                CreatedUtc = now,
                UpdatedUtc = now
            };
            db.SearchProviderInstances.Add(instance);
            await db.SaveChangesAsync();
            return new SeededDb(db, instance, connection);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private static SearchProviderToken CreateToken(SearchProviderInstance instance, DateTime now, DateTime? cooldown, DateTime? leaseExpiry)
    {
#pragma warning disable CS0618 // Token is migration-only; tests seed rows directly.
        return new SearchProviderToken
        {
            StableId = Guid.NewGuid(),
            Token = string.Empty,
            SearchProvider = instance.ProviderKind,
            ProviderInstanceId = instance.Id,
            Source = CredentialSource.Manual,
            IsEnabled = true,
            CreatedUtc = now,
            UpdatedUtc = now,
            CooldownUntilUtc = cooldown,
            LeaseId = leaseExpiry.HasValue ? Guid.NewGuid() : null,
            LeaseOwnerNodeId = leaseExpiry.HasValue ? "node-1" : null,
            LeaseRequestId = leaseExpiry.HasValue ? Guid.NewGuid() : null,
            LeaseAcquiredUtc = leaseExpiry.HasValue ? now : null,
            LeaseExpiresUtc = leaseExpiry,
            LastClaimedUtc = now
        };
#pragma warning restore CS0618
    }

    [Fact]
    public async Task NoEligible_ReturnsEarliestCooldownAsRetryAfter()
    {
        await using var seeded = await SeededDb.CreateAsync();
        var db = seeded.Db;
        var instance = seeded.Instance;
        var now = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Utc);
        db.SearchProviderTokens.Add(CreateToken(instance, now, now.AddMinutes(10), null));
        db.SearchProviderTokens.Add(CreateToken(instance, now, now.AddMinutes(3), null));
        await db.SaveChangesAsync();

        var retry = await SqliteCredentialScheduler.ResolveEarliestCredentialRetryAsync(
            db, instance.Id, now, CancellationToken.None);

        Assert.NotNull(retry);
        Assert.InRange(retry.Value.TotalMinutes, 2.5, 3.5);
    }

    [Fact]
    public async Task NoEligible_ReturnsEarliestLeaseExpiryWhenSoonerThanCooldown()
    {
        await using var seeded = await SeededDb.CreateAsync();
        var db = seeded.Db;
        var instance = seeded.Instance;
        var now = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Utc);
        db.SearchProviderTokens.Add(CreateToken(instance, now, now.AddMinutes(10), null));
        db.SearchProviderTokens.Add(CreateToken(instance, now, null, now.AddMinutes(2)));
        await db.SaveChangesAsync();

        var retry = await SqliteCredentialScheduler.ResolveEarliestCredentialRetryAsync(
            db, instance.Id, now, CancellationToken.None);

        Assert.NotNull(retry);
        Assert.InRange(retry.Value.TotalMinutes, 1.5, 2.5);
    }

    [Fact]
    public async Task NoCredentials_ReturnsNullRetryAfter()
    {
        await using var seeded = await SeededDb.CreateAsync();
        var db = seeded.Db;
        var instance = seeded.Instance;
        var now = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Utc);

        var retry = await SqliteCredentialScheduler.ResolveEarliestCredentialRetryAsync(
            db, instance.Id, now, CancellationToken.None);

        Assert.Null(retry);
    }
}

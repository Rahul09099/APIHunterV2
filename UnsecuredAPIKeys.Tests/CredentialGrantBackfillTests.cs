using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using UnsecuredAPIKeys.Data;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Data.Models;
using UnsecuredAPIKeys.Services;
using Xunit;

namespace UnsecuredAPIKeys.Tests;

/// <summary>
/// Concrete persistence tests for Task 3.2 legacy grant backfill and uniqueness.
/// **Validates: Requirements 4.43-4.45, 13.8**
/// </summary>
public sealed class CredentialGrantBackfillTests
{
    [Theory]
    [InlineData("Global", CredentialGrantScope.Global)]
    [InlineData("Admin", CredentialGrantScope.Admin)]
    public async Task Backfill_CreatesOwnedAndExplicitUnownedGrantsIdempotently(
        string configuredScope,
        CredentialGrantScope expectedScope)
    {
        await using var store = await GrantTestStore.CreateAsync();
        store.Context.SearchProviderTokens.AddRange(
            NewCredential(42),
            NewCredential(addedByTelegramId: null));
        await store.Context.SaveChangesAsync();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [CredentialGrantBackfillConfiguration.UnownedCredentialScope] = configuredScope
            })
            .Build();
        var state = new CredentialGrantBackfillState();
        var service = new CredentialGrantBackfillService(store.Context, configuration, state);

        var first = await service.BackfillAsync();
        var second = await service.BackfillAsync();

        Assert.Equal(1, first.UserGrantsAdded);
        Assert.Equal(1, first.CompatibilityGrantsAdded);
        Assert.Equal(new CredentialGrantBackfillResult(0, 0), second);
        Assert.Equal(CredentialGrantBackfillStatus.Ready, state.Status);

        var grants = await store.Context.CredentialGrants
            .AsNoTracking()
            .OrderBy(grant => grant.CredentialId)
            .ToListAsync();
        Assert.Collection(
            grants,
            grant =>
            {
                Assert.Equal(CredentialGrantScope.User, grant.Scope);
                Assert.Equal(42, grant.TelegramPrincipalId);
            },
            grant =>
            {
                Assert.Equal(expectedScope, grant.Scope);
                Assert.Null(grant.TelegramPrincipalId);
            });
    }

    [Fact]
    public async Task Backfill_MissingUnownedPolicyFailsBeforeCreatingAnyGrant()
    {
        await using var store = await GrantTestStore.CreateAsync();
        store.Context.SearchProviderTokens.AddRange(
            NewCredential(42),
            NewCredential(addedByTelegramId: null));
        await store.Context.SaveChangesAsync();

        var state = new CredentialGrantBackfillState();
        var service = new CredentialGrantBackfillService(
            store.Context,
            new ConfigurationBuilder().Build(),
            state);

        var error = await Record.ExceptionAsync(() => service.BackfillAsync());

        Assert.IsType<InvalidOperationException>(error);
        Assert.Equal(CredentialGrantBackfillStatus.Failed, state.Status);
        Assert.Empty(await store.Context.CredentialGrants.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Store_RejectsDuplicateCredentialScopePrincipalIdentity()
    {
        await using var store = await GrantTestStore.CreateAsync();
        var credential = NewCredential(42);
        credential.CredentialGrants.Add(NewUserGrant(42));
        credential.CredentialGrants.Add(NewUserGrant(42));
        store.Context.SearchProviderTokens.Add(credential);

        var error = await Record.ExceptionAsync(() => store.Context.SaveChangesAsync());

        Assert.IsType<DbUpdateException>(error);
    }

    private static SearchProviderToken NewCredential(long? addedByTelegramId) => new()
    {
        Token = $"task-3-2-{Guid.NewGuid():N}",
        SearchProvider = SearchProviderEnum.GitHub,
        IsEnabled = true,
        AddedByTelegramId = addedByTelegramId
    };

    private static CredentialGrant NewUserGrant(long telegramPrincipalId) => new()
    {
        Scope = CredentialGrantScope.User,
        TelegramPrincipalId = telegramPrincipalId,
        CreatedUtc = DateTime.UtcNow
    };

    private sealed class GrantTestStore(
        SqliteConnection connection,
        DBContext context) : IAsyncDisposable
    {
        public DBContext Context { get; } = context;

        public static async Task<GrantTestStore> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<DBContext>()
                .UseSqlite(connection)
                .Options;
            var context = new DBContext(options);
            await context.Database.EnsureCreatedAsync();
            return new GrantTestStore(connection, context);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}

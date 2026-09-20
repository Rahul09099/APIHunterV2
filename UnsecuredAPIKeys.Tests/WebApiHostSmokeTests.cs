using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using UnsecuredAPIKeys.Data;
using UnsecuredAPIKeys.Tests.Infrastructure;
using Xunit;

namespace UnsecuredAPIKeys.Tests;

public sealed class WebApiHostSmokeTests(SharedMemorySqliteWebApiFixture fixture)
    : IClassFixture<SharedMemorySqliteWebApiFixture>
{
    [Fact]
    public async Task AccessController_IsReachableThroughWebApiHost()
    {
        using var response = await fixture.Client.GetAsync("/api/access/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Session expired or invalid", payload.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Startup_CreatesModelSchemaInUniqueSharedMemorySqliteDatabase()
    {
        var connectionSettings = new SqliteConnectionStringBuilder(fixture.ConnectionString);
        Assert.Equal(fixture.DatabaseName, connectionSettings.DataSource);
        Assert.Equal(SqliteOpenMode.Memory, connectionSettings.Mode);
        Assert.Equal(SqliteCacheMode.Shared, connectionSettings.Cache);

        using var scope = fixture.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DBContext>();

        Assert.True(dbContext.Database.IsSqlite());
        Assert.True(await dbContext.Database.CanConnectAsync());

        var expectedTables = dbContext.Model.GetEntityTypes()
            .Select(entityType => entityType.GetTableName())
            .Where(tableName => tableName is not null)
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(expectedTables);

        // A second connection proves that the fixture uses shared-cache memory rather than
        // relying on one connection-local database. Startup, not this test, creates the schema.
        await using var independentConnection = new SqliteConnection(fixture.ConnectionString);
        await independentConnection.OpenAsync();
        await using var command = independentConnection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table';";

        var actualTables = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            actualTables.Add(reader.GetString(0));
        }

        Assert.All(expectedTables, tableName => Assert.Contains(tableName, actualTables));
    }
}

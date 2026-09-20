using Microsoft.EntityFrameworkCore;
using Npgsql;
using UnsecuredAPIKeys.Data;
using UnsecuredAPIKeys.Services;
using Xunit;

namespace UnsecuredAPIKeys.Tests.Infrastructure;

/// <summary>
/// Disposable PostgreSQL fixture for concurrency and transaction integration tests (Task 9.1).
/// Creates an isolated temporary database for the test run and tears it down on disposal.
/// Gracefully marks IsAvailable = false if PostgreSQL is unreachable in the current environment.
/// </summary>
public sealed class DisposablePostgresFixture : IAsyncLifetime
{
    private string? _masterConnectionString;
    private string? _testDatabaseName;

    public bool IsAvailable { get; private set; }
    public string? UnavailableReason { get; private set; }
    public string? ConnectionString { get; private set; }
    public DbContextOptions<DBContext>? DbOptions { get; private set; }

    public async Task InitializeAsync()
    {
        var candidates = GetMasterConnectionCandidates();

        foreach (var candidate in candidates)
        {
            try
            {
                var normalized = DBContext.ConvertPostgresUrl(candidate);
                await using var conn = new NpgsqlConnection(normalized);
                await conn.OpenAsync();

                _masterConnectionString = normalized;
                break;
            }
            catch (Exception ex)
            {
                UnavailableReason = $"Connection attempt failed ({ex.GetType().Name}): {ex.Message}";
            }
        }

        if (string.IsNullOrEmpty(_masterConnectionString))
        {
            IsAvailable = false;
            UnavailableReason ??= "No reachable PostgreSQL master connection was found.";
            return;
        }

        try
        {
            _testDatabaseName = $"apihunter_test_{Guid.NewGuid():N}";

            // Create temporary test database
            await using (var masterConn = new NpgsqlConnection(_masterConnectionString))
            {
                await masterConn.OpenAsync();
                await using var cmd = masterConn.CreateCommand();
                cmd.CommandText = $"CREATE DATABASE \"{_testDatabaseName}\";";
                await cmd.ExecuteNonQueryAsync();
            }

            // Build test connection string
            var builder = new NpgsqlConnectionStringBuilder(_masterConnectionString)
            {
                Database = _testDatabaseName,
                Pooling = true,
                MaxPoolSize = 50,
                MinPoolSize = 0,
                ConnectionIdleLifetime = 10,
                Timeout = 10,
                CommandTimeout = 30
            };
            ConnectionString = builder.ConnectionString;

            var optionsBuilder = new DbContextOptionsBuilder<DBContext>();
            optionsBuilder.UseNpgsql(ConnectionString, npgsqlOptions =>
            {
                npgsqlOptions.EnableRetryOnFailure(
                    maxRetryCount: 3,
                    maxRetryDelay: TimeSpan.FromSeconds(2),
                    errorCodesToAdd: null);
            });
            DbOptions = optionsBuilder.Options;

            // Initialize schema and parity tables
            await using var initContext = CreateContext();
            var dbService = new DatabaseService(initContext);
            await dbService.InitializeDatabaseAsync();

            IsAvailable = true;
            UnavailableReason = null;
        }
        catch (Exception ex)
        {
            IsAvailable = false;
            UnavailableReason = $"Failed to provision test database: {ex.Message}";
        }
    }

    public DBContext CreateContext()
    {
        if (!IsAvailable || DbOptions is null)
        {
            throw new InvalidOperationException($"PostgreSQL fixture is unavailable: {UnavailableReason}");
        }

        return new DBContext(DbOptions);
    }

    public async Task DisposeAsync()
    {
        if (!IsAvailable || string.IsNullOrEmpty(_masterConnectionString) || string.IsNullOrEmpty(_testDatabaseName))
        {
            return;
        }

        try
        {
            // Clear all pooled connections before dropping
            NpgsqlConnection.ClearAllPools();

            await using var masterConn = new NpgsqlConnection(_masterConnectionString);
            await masterConn.OpenAsync();
            await using var cmd = masterConn.CreateCommand();
            cmd.CommandText = $"DROP DATABASE IF EXISTS \"{_testDatabaseName}\" WITH (FORCE);";
            await cmd.ExecuteNonQueryAsync();
        }
        catch
        {
            // Best-effort cleanup
        }
    }

    private static IEnumerable<string> GetMasterConnectionCandidates()
    {
        var testEnv = Environment.GetEnvironmentVariable("POSTGRES_TEST_CONNECTION_STRING");
        if (!string.IsNullOrWhiteSpace(testEnv))
            yield return testEnv;

        var standardEnv = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING");
        if (!string.IsNullOrWhiteSpace(standardEnv))
            yield return standardEnv;

        var databaseUrl = Environment.GetEnvironmentVariable("DATABASE_URL");
        if (!string.IsNullOrWhiteSpace(databaseUrl))
            yield return databaseUrl;

        // Local development defaults
        yield return "Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=postgres;Timeout=2;Command Timeout=5";
        yield return "Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=root;Timeout=2;Command Timeout=5";
        yield return "Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=admin;Timeout=2;Command Timeout=5";
        yield return "Host=localhost;Port=5432;Database=postgres;Username=postgres;Timeout=2;Command Timeout=5";
    }
}

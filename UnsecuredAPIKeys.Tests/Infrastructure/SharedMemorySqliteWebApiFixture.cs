using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using UnsecuredAPIKeys.Data;
using UnsecuredAPIKeys.Services;
using Xunit;

namespace UnsecuredAPIKeys.Tests.Infrastructure;

/// <summary>
/// Starts the real WebAPI pipeline against an isolated, GUID-named SQLite database.
/// A keep-alive connection preserves the in-memory database while application scopes
/// open and close their own connections through the shared cache.
/// </summary>
public sealed class SharedMemorySqliteWebApiFixture : IAsyncLifetime
{
    private readonly SqliteConnection _keepAliveConnection;
    private TestWebApplicationFactory? _factory;

    public SharedMemorySqliteWebApiFixture()
    {
        DatabaseName = $"ApiHunterV2Tests_{Guid.NewGuid():N}";
        ConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabaseName,
            Mode = SqliteOpenMode.Memory,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString();

        _keepAliveConnection = new SqliteConnection(ConnectionString);
    }

    public string DatabaseName { get; }

    public string ConnectionString { get; }

    public HttpClient Client { get; private set; } = null!;

    public IServiceProvider Services =>
        _factory?.Services ?? throw new InvalidOperationException("The WebAPI fixture has not been initialized.");

    public async Task InitializeAsync()
    {
        await _keepAliveConnection.OpenAsync();

        _factory = new TestWebApplicationFactory(ConnectionString);
        Client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
    }

    public void SetReadinessOverride(ProviderInstanceReadinessReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        Services.GetRequiredService<TestReadinessOverrideState>().Update(report);
    }

    public void ClearReadinessOverride() =>
        Services.GetRequiredService<TestReadinessOverrideState>().Update(null);

    public async Task DisposeAsync()
    {
        Client?.Dispose();

        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        await _keepAliveConnection.DisposeAsync();
    }

    private sealed class TestWebApplicationFactory(string connectionString)
        : WebApplicationFactory<global::Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["SearchCredentials:Protection:ActiveKeyVersion"] = "7",
                    ["SearchCredentials:Protection:Keys:7"] = Convert.ToBase64String(
                        Enumerable.Repeat((byte)0x37, 32).ToArray()),
                    ["SearchCredentials:Fingerprint:ActiveKeyVersion"] = "11",
                    ["SearchCredentials:Fingerprint:Keys:11"] = Convert.ToBase64String(
                        Enumerable.Repeat((byte)0x5B, 32).ToArray())
                });
            });
            builder.ConfigureServices(services =>
            {
                // Hosted production loops are unrelated to request/schema smoke tests and
                // can perform external work. The TestServer request pipeline remains active.
                services.RemoveAll<IHostedService>();

                services.RemoveAll<DBContext>();
                services.RemoveAll<DbContextOptions<DBContext>>();
                services.RemoveAll<IDbContextFactory<DBContext>>();

                services.AddDbContext<DBContext>(options =>
                    options.UseSqlite(connectionString));
                services.AddDbContextFactory<DBContext>(options =>
                    options.UseSqlite(connectionString), ServiceLifetime.Scoped);

                // Production refreshes readiness at every HTTP scheduling boundary. Tests
                // may explicitly override the evaluator while preserving real evaluation by default.
                services.AddSingleton<TestReadinessOverrideState>();
                services.RemoveAll<ISearchPlatformSchedulingReadinessService>();
                services.AddScoped<ISearchPlatformSchedulingReadinessService>(serviceProvider =>
                    new TestSchedulingReadinessEvaluator(
                        serviceProvider.GetRequiredService<SearchPlatformSchedulingReadinessService>(),
                        serviceProvider.GetRequiredService<TestReadinessOverrideState>()));
            });
        }
    }

    private sealed class TestReadinessOverrideState
    {
        private ProviderInstanceReadinessReport? _current;

        public ProviderInstanceReadinessReport? Current => Volatile.Read(ref _current);

        public void Update(ProviderInstanceReadinessReport? report) =>
            Volatile.Write(ref _current, report);
    }

    private sealed class TestSchedulingReadinessEvaluator(
        SearchPlatformSchedulingReadinessService inner,
        TestReadinessOverrideState overrideState)
        : ISearchPlatformSchedulingReadinessService
    {
        public Task<ProviderInstanceReadinessReport> EvaluateAsync(
            CancellationToken cancellationToken = default)
        {
            var report = overrideState.Current;
            return report is null
                ? inner.EvaluateAsync(cancellationToken)
                : Task.FromResult(report);
        }
    }
}

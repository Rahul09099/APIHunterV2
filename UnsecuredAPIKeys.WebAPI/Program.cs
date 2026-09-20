using Microsoft.EntityFrameworkCore;
using UnsecuredAPIKeys.Data;
using UnsecuredAPIKeys.Providers;
using UnsecuredAPIKeys.Providers._Interfaces;
using UnsecuredAPIKeys.Services;
using UnsecuredAPIKeys.Services.Telegram;
using UnsecuredAPIKeys.WebAPI.Services;

// Set ASPNETCORE_URLS to 0.0.0.0:{PORT} BEFORE WebApplication.CreateBuilder runs
var renderPort = Environment.GetEnvironmentVariable("PORT") ?? "8080";
Environment.SetEnvironmentVariable("ASPNETCORE_URLS", $"http://0.0.0.0:{renderPort}");

// Disable file system watchers BEFORE WebApplication.CreateBuilder runs to prevent inotify limit (128) container crash on Render/Docker
Environment.SetEnvironmentVariable("DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE", "false");
Environment.SetEnvironmentVariable("DOTNET_USE_POLLING_FILE_WATCHER", "true");

// Configure .NET GC for memory conservation on Render Free Tier (512MB RAM)
Environment.SetEnvironmentVariable("DOTNET_gcServer", "0");
Environment.SetEnvironmentVariable("DOTNET_GCConserveMemory", "5");
Environment.SetEnvironmentVariable("DOTNET_GCHeapHardLimitPercent", "75");

var builder = WebApplication.CreateBuilder(args);

// Explicitly configure Kestrel to listen on 0.0.0.0 for Render's port detection scanner
if (int.TryParse(renderPort, out int portNumber))
{
    builder.WebHost.ConfigureKestrel(options =>
    {
        options.ListenAnyIP(portNumber);
    });
}

// Disable reloadOnChange FileSystemWatcher to prevent inotify limit (128) container crash (Status 139) on Render/Docker
builder.Host.ConfigureAppConfiguration((hostingContext, config) =>
{
    config.Sources.Clear();
    config.AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
          .AddJsonFile($"appsettings.{hostingContext.HostingEnvironment.EnvironmentName}.json", optional: true, reloadOnChange: false)
          .AddEnvironmentVariables();
});

// Add services to the container
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Configure Database
var connectionString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING");
var dbPath = Environment.GetEnvironmentVariable("DATABASE_PATH")
    ?? Path.Combine(AppContext.BaseDirectory, "unsecuredapikeys.db");

if (!string.IsNullOrEmpty(connectionString))
{
    var parsedConnectionString = DBContext.ConvertPostgresUrl(connectionString);
    var maskedConnectionString = parsedConnectionString.Contains("Password=")
        ? System.Text.RegularExpressions.Regex.Replace(parsedConnectionString, "Password=[^;]+", "Password=********")
        : parsedConnectionString;

    Console.WriteLine("🗄️ Database: Using PostgreSQL");
    Console.WriteLine($"🗄️ Connection: {maskedConnectionString}");

    const int maxRetryCount = 5;
    var maxRetryDelay = TimeSpan.FromSeconds(10);

    builder.Services.AddDbContext<DBContext>(options =>
        options.UseNpgsql(parsedConnectionString, npgsqlOptions =>
            npgsqlOptions.EnableRetryOnFailure(
                maxRetryCount: maxRetryCount,
                maxRetryDelay: maxRetryDelay,
                errorCodesToAdd: null)));
    builder.Services.AddDbContextFactory<DBContext>(options =>
        options.UseNpgsql(parsedConnectionString, npgsqlOptions =>
            npgsqlOptions.EnableRetryOnFailure(
                maxRetryCount: maxRetryCount,
                maxRetryDelay: maxRetryDelay,
                errorCodesToAdd: null)), ServiceLifetime.Scoped);
}
else
{
    Console.WriteLine($"🗄️ Database: Using SQLite ({dbPath})");
    builder.Services.AddDbContext<DBContext>(options =>
        options.UseSqlite($"Data Source={dbPath}"));
    builder.Services.AddDbContextFactory<DBContext>(options =>
        options.UseSqlite($"Data Source={dbPath}"), ServiceLifetime.Scoped);
}

// Detecting Mode
var isWorkerMode = string.Equals(Environment.GetEnvironmentVariable("IS_WORKER_MODE"), "true", StringComparison.OrdinalIgnoreCase);

// Workers use the master endpoint supplied by deployment configuration.
var masterApiUrl = Environment.GetEnvironmentVariable("MASTER_API_URL");

var nodeToken = Environment.GetEnvironmentVariable("NODE_TOKEN");

// Register services
builder.Services.AddHttpClient();
builder.Services.AddSearchProviderAdapters();
builder.Services.AddSingleton<CredentialProtectionService>();
builder.Services.AddSingleton<CredentialFingerprintService>();
builder.Services.AddSingleton<CredentialStorageMigrationGuard>();
builder.Services.AddSingleton<CredentialStorageService>();
builder.Services.AddScoped<CredentialMaterialAccessService>();
builder.Services.AddScoped<CredentialPlaintextMigrationService>();
builder.Services.AddScoped<CredentialProtectionReadinessService>();
builder.Services.AddScoped<DatabaseService>();
builder.Services.AddScoped<INodePrincipalResolver, NodePrincipalResolver>();
builder.Services.AddScoped<ICredentialGrantEvaluator, CredentialGrantEvaluator>();
builder.Services.AddSingleton<CredentialGrantBackfillState>();
builder.Services.AddScoped<CredentialGrantBackfillService>();
builder.Services.AddScoped<CredentialGrantReadinessService>();
builder.Services.AddSingleton<IPrivilegePolicy, PrivilegePolicy>();
builder.Services.AddSingleton<IAuditReasonSanitizer, AuditReasonSanitizer>();
builder.Services.AddSingleton<CredentialStateService>();
builder.Services.AddSingleton<CredentialMutationGate>();
builder.Services.AddScoped<CredentialDuplicateReconciliationService>();
builder.Services.AddScoped<WorkerDiscoveryReportValidator>();
builder.Services.AddScoped<IDatabaseUtcClock, DatabaseUtcClock>();
builder.Services.AddSingleton(new EndpointPolicyOptions());
builder.Services.AddSingleton<IEndpointDnsResolver, EndpointDnsResolver>();
builder.Services.AddScoped<EndpointPolicy>();
if (!isWorkerMode)
{
    builder.Services.AddSingleton<EnvironmentBootstrapReadinessState>();
    builder.Services.AddScoped<EnvironmentBootstrapService>();
}
builder.Services.AddScoped<IProviderInstanceCommandService, ProviderInstanceCommandService>();
builder.Services.AddScoped<ProviderInstanceCatalogService>();
builder.Services.AddScoped<ICredentialManagementService, CredentialManagementService>();
builder.Services.AddScoped<ProviderInstanceReadinessService>();
builder.Services.AddScoped<SearchPlatformSchedulingReadinessService>();
builder.Services.AddScoped<ISearchPlatformSchedulingReadinessService>(serviceProvider =>
    serviceProvider.GetRequiredService<SearchPlatformSchedulingReadinessService>());
builder.Services.AddSingleton<SearchPlatformFeatureFlagMatrix>();
builder.Services.AddSingleton<SearchPlatformRuntimeReadinessState>();
builder.Services.AddSingleton<ProviderInstanceReadinessState>();
builder.Services.AddScoped<SchedulingReadinessFilter>();
builder.Services.AddScoped<VerifierService>();
builder.Services.AddSingleton<BackgroundJobManager>();
builder.Services.AddSingleton<DashboardAccessService>();

// Wave 7: Work pipeline and result persistence
builder.Services.AddScoped<IWorkService, WorkService>();
builder.Services.AddScoped<ResultPersistenceService>();

// Wave 14: Public-search consent and credential-free public operations
builder.Services.AddScoped<IPublicSearchConsentService, PublicSearchConsentService>();
builder.Services.AddScoped<PublicOperationSlotService>();
builder.Services.AddScoped<PublicSearchOperationService>();

// Wave 8 & 9: Credential Scheduler with injectable jitter (AC-6.19), policy options, and Postgres/SQLite strategies
builder.Services.AddSingleton<LeasePolicyOptions>();
builder.Services.AddSingleton<ISchedulerJitterSource, CryptographicSchedulerJitterSource>();
builder.Services.AddScoped<SqliteCredentialScheduler>();
builder.Services.AddScoped<PostgresCredentialScheduler>();
builder.Services.AddScoped<ICredentialScheduler>(serviceProvider =>
{
    var context = serviceProvider.GetRequiredService<DBContext>();
    return context.Database.IsNpgsql()
        ? serviceProvider.GetRequiredService<PostgresCredentialScheduler>()
        : serviceProvider.GetRequiredService<SqliteCredentialScheduler>();
});

// Wave 8.5: Safe Provider Adapter Runtime and Outcome Classifier
builder.Services.AddSingleton<ISearchProviderOutcomeClassifier, SearchProviderOutcomeClassifier>();
builder.Services.AddScoped<ISearchProviderAdapterRuntime, SearchProviderAdapterRuntime>();

// Wave 14: Public-search consent and credential-free public operations
builder.Services.AddScoped<IPublicSearchConsentService, PublicSearchConsentService>();
builder.Services.AddScoped<PublicOperationSlotService>();
builder.Services.AddScoped<PublicSearchOperationService>();

// Wave 15: Platform telemetry, role-filtered health, and bounded retention
builder.Services.AddSingleton(new SearchPlatformMetrics());
builder.Services.AddSingleton(new PlatformRetentionOptions());
builder.Services.AddScoped<CredentialHealthProjectionService>();
builder.Services.AddScoped<PlatformRetentionService>();

// Wave 16: Durable Worker-Claims cutover and rollback drain
builder.Services.AddScoped<WorkerClaimsCutoverService>();

if (isWorkerMode)
{
    Console.WriteLine("👻 Mode: GHOST WORKER NODE");
    Console.WriteLine($"📡 Master API Target: {masterApiUrl}");
    Console.WriteLine($"🔑 Node Token Configured: {(!string.IsNullOrEmpty(nodeToken) ? "Yes" : "No")}");
    builder.Services.AddHostedService<NodeKeepAliveService>();

    // Wave 13: claim-enabled Worker orchestrator. Master mode never registers these;
    // Master scraping routes through the common Scheduler instead (Task 10.4).
    builder.Services.AddSingleton(new WorkerScraperOptions
    {
        MasterApiUrl = masterApiUrl ?? string.Empty,
        NodeToken = nodeToken ?? string.Empty
    });
    builder.Services.AddHttpClient("WorkerMaster");
    builder.Services.AddScoped<IMasterApiClient>(serviceProvider =>
        new HttpMasterApiClient(
            serviceProvider.GetRequiredService<IHttpClientFactory>().CreateClient("WorkerMaster"),
            serviceProvider.GetRequiredService<WorkerScraperOptions>().MasterApiUrl,
            serviceProvider.GetRequiredService<WorkerScraperOptions>().NodeToken));
    builder.Services.AddScoped<WorkerSecretExtractor>();
    builder.Services.AddScoped<WorkerCycleRunner>();
    builder.Services.AddHostedService<WorkerScraperHostedService>();
}
else
{
    Console.WriteLine("👑 Mode: MASTER CENTRAL NODE");
    builder.Services.AddHostedService<TelegramBotService>();
}

var app = builder.Build();

// Health check endpoints remain process-liveness probes. Scheduling readiness is
// evaluated independently and includes the fail-closed feature-flag matrix.
app.MapGet("/health", () => Results.Ok(new { status = "Healthy", timestamp = DateTime.UtcNow }));
app.MapGet("/api/health", () => Results.Ok(new { status = "Healthy", timestamp = DateTime.UtcNow }));

// Initialize and validate every supported database provider. PostgreSQL may no longer
// assume an externally supplied schema is current; SQLite remains a one-Master mode.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<DBContext>();
    var readinessState = scope.ServiceProvider.GetRequiredService<ProviderInstanceReadinessState>();
    try
    {
        if (db.Database.IsSqlite())
        {
            var dbDir = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(dbDir) && !Directory.Exists(dbDir))
            {
                Directory.CreateDirectory(dbDir);
                Console.WriteLine($"📁 Created database directory: {dbDir}");
            }
        }

        var databaseService = scope.ServiceProvider.GetRequiredService<DatabaseService>();
        await databaseService.InitializeDatabaseAsync();

        if (!isWorkerMode)
        {
            await scope.ServiceProvider
                .GetRequiredService<EnvironmentBootstrapService>()
                .RunAsync();
        }

        var grantBackfill = scope.ServiceProvider.GetRequiredService<CredentialGrantBackfillService>();
        await grantBackfill.BackfillAsync();

        // Default-instance linking is completed by database initialization. Environment
        // imports are protected writes, and explicit grants are backfilled before duplicate
        // identities are reconciled under the common mutation gate.
        await scope.ServiceProvider
            .GetRequiredService<CredentialDuplicateReconciliationService>()
            .ReconcileAsync();

        var migrationGuard = scope.ServiceProvider.GetRequiredService<CredentialStorageMigrationGuard>();
        if (migrationGuard.CanVerifyProtectedReadsOnStartup)
        {
            var migrationService = scope.ServiceProvider.GetRequiredService<CredentialPlaintextMigrationService>();
            var verification = await migrationService.VerifyEnabledProtectedReadsAsync();
            Console.WriteLine(
                $"[READY] Protected credential verification: " +
                $"{(verification.AllEnabledCredentialsUseProtectedReads ? "verified" : "failed")}; " +
                $"failures={verification.FailedCredentialStableIds.Count}.");
        }

        var protectionReadiness = await scope.ServiceProvider
            .GetRequiredService<CredentialProtectionReadinessService>()
            .EvaluateAsync();
        var runtimeReadinessState = scope.ServiceProvider
            .GetRequiredService<SearchPlatformRuntimeReadinessState>();
        var priorRuntimeReadiness = runtimeReadinessState.Current;
        runtimeReadinessState.Update(priorRuntimeReadiness with
        {
            ProtectedStorageReady = protectionReadiness.IsReady
        });

        var readinessService = scope.ServiceProvider.GetRequiredService<ISearchPlatformSchedulingReadinessService>();
        var readiness = await readinessService.EvaluateAsync();
        readinessState.Update(readiness);

        Console.WriteLine(
            $"[READY] Search platform: {(readiness.IsReady ? "ready" : "not ready")}; " +
            $"coordination={readiness.CoordinationMode}; " +
            $"distributed={readiness.DistributedCoordinationReady}.");
        if (!readiness.IsReady)
        {
            Console.WriteLine($"[READY] Validation withheld readiness ({readiness.Failures.Count} issue(s)).");
        }
    }
    catch (Exception ex)
    {
        var coordinationMode = db.Database.IsSqlite()
            ? DatabaseCoordinationMode.SingleMaster
            : db.Database.IsNpgsql()
                ? DatabaseCoordinationMode.Distributed
                : DatabaseCoordinationMode.Unsupported;
        readinessState.Update(new ProviderInstanceReadinessReport(
            DatabaseReady: false,
            SchemaReady: false,
            ProviderInstancesReady: false,
            MarkersReady: false,
            CoordinationMode: coordinationMode,
            DistributedCoordinationReady: false,
            Failures: ["Database startup initialization failed."])
        {
            ConfigurationReady = false
        });
        Console.WriteLine($"⚠️ Database initialization failed closed ({ex.GetType().Name}).");
    }
}

// Configure HTTP request pipeline
app.UseSwagger();
app.UseSwaggerUI();

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseAuthorization();
app.MapControllers();

app.Run();

// Expose the minimal-host entry point to WebApplicationFactory without changing runtime behavior.
public partial class Program
{
}

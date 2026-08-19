using Microsoft.EntityFrameworkCore;
using UnsecuredAPIKeys.Data;
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

    builder.Services.AddDbContext<DBContext>(options =>
        options.UseNpgsql(parsedConnectionString));
    builder.Services.AddDbContextFactory<DBContext>(options =>
        options.UseNpgsql(parsedConnectionString), ServiceLifetime.Scoped);
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
builder.Services.AddScoped<DatabaseService>();
builder.Services.AddScoped<VerifierService>();
builder.Services.AddSingleton<BackgroundJobManager>();
builder.Services.AddSingleton<DashboardAccessService>();

if (isWorkerMode)
{
    Console.WriteLine("👻 Mode: GHOST WORKER NODE");
    Console.WriteLine($"📡 Master API Target: {masterApiUrl}");
    Console.WriteLine($"🔑 Node Token Configured: {(!string.IsNullOrEmpty(nodeToken) ? "Yes" : "No")}");
    builder.Services.AddHostedService<NodeKeepAliveService>();
}
else
{
    Console.WriteLine("👑 Mode: MASTER CENTRAL NODE");
    builder.Services.AddHostedService<TelegramBotService>();
}

var app = builder.Build();

// Health check endpoints placed at top of pipeline for immediate <1ms responses to Render health probes
app.MapGet("/health", () => Results.Ok(new { status = "Healthy", timestamp = DateTime.UtcNow }));
app.MapGet("/api/health", () => Results.Ok(new { status = "Healthy", timestamp = DateTime.UtcNow }));

// Auto-create SQLite database directory and schema if Npgsql connection string is not present
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<DBContext>();
    try
    {
        if (string.IsNullOrEmpty(connectionString))
        {
            var dbDir = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(dbDir) && !Directory.Exists(dbDir))
            {
                Directory.CreateDirectory(dbDir);
                Console.WriteLine($"📁 Created database directory: {dbDir}");
            }
            db.Database.EnsureCreated();
            Console.WriteLine("✅ SQLite Database initialized successfully.");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"⚠️ Database initialization warning: {ex.Message}");
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

using Microsoft.EntityFrameworkCore;
using UnsecuredAPIKeys.Data;
using UnsecuredAPIKeys.Services;
using UnsecuredAPIKeys.Services.Telegram;
using UnsecuredAPIKeys.WebAPI.Services;

var builder = WebApplication.CreateBuilder(args);

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
}
else
{
    Console.WriteLine($"🗄️ Database: Using SQLite ({dbPath})");
    builder.Services.AddDbContext<DBContext>(options =>
        options.UseSqlite($"Data Source={dbPath}"));
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

// Configure ScraperService with Worker Mode if applicable
builder.Services.AddScoped<ScraperService>(sp => {
    var db = sp.GetRequiredService<DBContext>();
    var http = sp.GetRequiredService<IHttpClientFactory>();
    var logger = sp.GetService<ILogger<ScraperService>>();
    var scraper = new ScraperService(db, http, logger)
    {
        IsWorkerMode = isWorkerMode,
        MasterApiUrl = masterApiUrl,
        NodeToken = nodeToken
    };
    return scraper;
});

// Only start the Telegram Bot if NOT in worker mode
if (!isWorkerMode)



{
    Console.WriteLine("🤖 Mode: Master (Telegram Bot Enabled)");
    builder.Services.AddHostedService<TelegramBotService>();
    builder.Services.AddHostedService<NodeKeepAliveService>();
}
else
{
    Console.WriteLine("👻 Mode: Ghost Worker (Telegram Bot Disabled)");
    Console.WriteLine($"🛰️ Reporting to: {masterApiUrl}");
}

// Configure CORS (allow all for now, tighten in production)
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

var app = builder.Build();

// Initialize database (Only for Master Node) - Run in background to prevent Health Check Timeouts
if (!isWorkerMode)
{
    _ = Task.Run(async () =>
    {
        try
        {
            using (var scope = app.Services.CreateScope())
            {
                var dbService = scope.ServiceProvider.GetRequiredService<DatabaseService>();
                await dbService.InitializeDatabaseAsync();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CRITICAL] Background DB Initialization Failed: {ex.Message}");
        }
    });
}

// Configure the HTTP request pipeline
// Only enable Swagger if explicitly requested or in Dev (Recommended for security)
if (app.Environment.IsDevelopment() || Environment.GetEnvironmentVariable("SWAGGER_ENABLED") == "true")
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "UnsecuredAPIKeys API V1");
        c.RoutePrefix = string.Empty; // Swagger UI at root
    });
}
else
{
    // Simple landing page for public URL
    app.MapGet("/", () => "📡 APIHunterV2 Master Node is Online.");
}

app.UseCors("AllowAll");
app.UseStaticFiles();
app.UseAuthorization();
app.MapControllers();

// Health check endpoint
app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }));

// 💎 Visual Network Dashboard
app.MapGet("/dashboard", (IWebHostEnvironment env) => {
    var path = Path.Combine(env.WebRootPath ?? Path.Combine(AppContext.BaseDirectory, "wwwroot"), "index.html");
    if (!File.Exists(path))
    {
        return Results.Text("<h1>Visual Dashboard: Under Construction</h1><p>Static files are currently being written. Please refresh in a moment.</p>", "text/html");
    }
    return Results.File(path, "text/html");
});

// 🛰️ AUTO-START WORKER (If in Worker Mode)
if (isWorkerMode)
{
    _ = Task.Run(async () =>
    {
        using var scope = app.Services.CreateScope();
        var scraper = scope.ServiceProvider.GetRequiredService<ScraperService>();
        await scraper.RunAsync(CancellationToken.None);
    });
}

// Ensure the app listens on the port provided by Render (Fixes Port Scan Timeout)
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
Console.WriteLine($"🚀 Server: Starting on port {port}");
app.Run($"http://0.0.0.0:{port}");

using Microsoft.EntityFrameworkCore;
using UnsecuredAPIKeys.Data;
using UnsecuredAPIKeys.Services;
using UnsecuredAPIKeys.Services.Telegram;

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
var masterApiUrl = Environment.GetEnvironmentVariable("MASTER_API_URL");
var nodeToken = Environment.GetEnvironmentVariable("NODE_TOKEN");

// Register services
builder.Services.AddHttpClient();
builder.Services.AddScoped<DatabaseService>();
builder.Services.AddScoped<VerifierService>();
builder.Services.AddSingleton<BackgroundJobManager>();

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

// Initialize database
using (var scope = app.Services.CreateScope())
{
    var dbService = scope.ServiceProvider.GetRequiredService<DatabaseService>();
    await dbService.InitializeDatabaseAsync();
}

// Configure the HTTP request pipeline
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "UnsecuredAPIKeys API V1");
    c.RoutePrefix = string.Empty; // Swagger UI at root
});

app.UseCors("AllowAll");
app.UseAuthorization();
app.MapControllers();

// Health check endpoint
app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }));

app.Run();

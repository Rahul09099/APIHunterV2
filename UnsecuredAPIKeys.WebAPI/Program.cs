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
    var parsedConnectionString = ConvertPostgresUrl(connectionString);
    Console.WriteLine("🗄️ Database: Using PostgreSQL");
    builder.Services.AddDbContext<DBContext>(options =>
        options.UseNpgsql(parsedConnectionString));
}
else
{
    Console.WriteLine($"🗄️ Database: Using SQLite ({dbPath})");
    builder.Services.AddDbContext<DBContext>(options =>
        options.UseSqlite($"Data Source={dbPath}"));
}

// Register services
builder.Services.AddHttpClient();
builder.Services.AddScoped<DatabaseService>();
builder.Services.AddScoped<ScraperService>();
builder.Services.AddScoped<VerifierService>();
builder.Services.AddSingleton<BackgroundJobManager>();
builder.Services.AddHostedService<TelegramBotService>();

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
    var db = scope.ServiceProvider.GetRequiredService<DBContext>();
    await db.Database.EnsureCreatedAsync();
    
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

static string ConvertPostgresUrl(string url)
{
    if (string.IsNullOrEmpty(url) || !url.StartsWith("postgres://")) return url;
    try
    {
        var uri = new Uri(url);
        var userInfo = uri.UserInfo.Split(':');
        return $"Host={uri.Host};Port={uri.Port};Database={uri.AbsolutePath.TrimStart('/')};Username={userInfo[0]};Password={(userInfo.Length > 1 ? userInfo[1] : "")};SSL Mode=Require;Trust Server Certificate=true";
    }
    catch { return url; }
}

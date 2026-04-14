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

// Initialize database (Only for Master Node)
if (!isWorkerMode)
{
    using (var scope = app.Services.CreateScope())
    {
        var dbService = scope.ServiceProvider.GetRequiredService<DatabaseService>();
        await dbService.InitializeDatabaseAsync();
    }
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
app.UseAuthorization();
app.MapControllers();

// Health check endpoint
app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }));

// 💎 Visual Network Dashboard
app.MapGet("/dashboard", () => {
    var dashboardHtml = @"<!DOCTYPE html>
<html lang=""en"">
<head>
    <meta charset=""UTF-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
    <title>APIHunter | Global Dashboard</title>
    <link href=""https://fonts.googleapis.com/css2?family=Outfit:wght@300;400;600;700&display=swap"" rel=""stylesheet"">
    <style>
        :root {
            --bg: #050505;
            --accent: #00f2ff;
            --glass: rgba(255, 255, 255, 0.03);
            --border: rgba(255, 255, 255, 0.1);
        }

        * { margin:0; padding:0; box-sizing:border-box; font-family: 'Outfit', sans-serif; }

        body {
            background-color: var(--bg);
            color: white;
            min-height: 100vh;
            display: flex;
            flex-direction: column;
            align-items: center;
            overflow-x: hidden;
        }

        /* Abstract Background Decor */
        .orb {
            position: absolute;
            width: 400px;
            height: 400px;
            background: radial-gradient(circle, rgba(0, 242, 255, 0.08) 0%, transparent 70%);
            border-radius: 50%;
            z-index: -1;
            filter: blur(50px);
        }
        .orb-1 { top: -100px; left: -100px; }
        .orb-2 { bottom: -100px; right: -100px; }

        header {
            width: 100%;
            padding: 40px 20px;
            max-width: 1200px;
            display: flex;
            justify-content: space-between;
            align-items: center;
        }

        .logo {
            font-size: 24px;
            font-weight: 700;
            letter-spacing: -0.5px;
            display: flex;
            align-items: center;
            gap: 10px;
        }
        .logo span { color: var(--accent); }

        .status-badge {
            padding: 8px 16px;
            background: var(--glass);
            border: 1px solid var(--border);
            border-radius: 100px;
            font-size: 13px;
            font-weight: 500;
            display: flex;
            align-items: center;
            gap: 8px;
        }
        .status-dot {
            width: 8px;
            height: 8px;
            background: #22c55e;
            border-radius: 50%;
            box-shadow: 0 0 10px #22c55e;
            animation: pulse 2s infinite;
        }

        @keyframes pulse {
            0% { transform: scale(1); opacity: 1; }
            50% { transform: scale(1.5); opacity: 0.5; }
            100% { transform: scale(1); opacity: 1; }
        }

        .dashboard-container {
            width: 100%;
            max-width: 1200px;
            padding: 20px;
            display: grid;
            grid-template-columns: repeat(auto-fit, minmax(280px, 1fr));
            gap: 20px;
            margin-top: 20px;
        }

        .card {
            background: var(--glass);
            backdrop-filter: blur(20px);
            -webkit-backdrop-filter: blur(20px);
            border: 1px solid var(--border);
            border-radius: 24px;
            padding: 40px;
            transition: transform 0.3s ease, border-color 0.3s ease;
        }
        .card:hover {
            transform: translateY(-5px);
            border-color: rgba(0, 242, 255, 0.4);
        }

        .card-label {
            font-size: 14px;
            color: rgba(255, 255, 255, 0.5);
            font-weight: 500;
            margin-bottom: 10px;
            text-transform: uppercase;
            letter-spacing: 1px;
        }

        .card-value {
            font-size: 48px;
            font-weight: 700;
            margin-bottom: 5px;
        }

        .card-footer {
            font-size: 13px;
            color: rgba(255, 255, 255, 0.4);
        }

        .history-section {
            width: 100%;
            max-width: 1200px;
            padding: 40px 20px;
        }

        .history-header {
            font-size: 18px;
            font-weight: 600;
            margin-bottom: 24px;
            display: flex;
            align-items: center;
            gap: 12px;
        }

        .footer {
            margin-top: auto;
            padding: 60px 20px;
            color: rgba(255, 255, 255, 0.2);
            font-size: 13px;
        }

        /* Responsive Fixes */
        @media (max-width: 768px) {
            .card-value { font-size: 36px; }
            header { padding: 30px 20px; }
        }
    </style>
</head>
<body>
    <div class=""orb orb-1""></div>
    <div class=""orb orb-2""></div>

    <header>
        <div class=""logo"">API<span>HUNTER</span></div>
        <div class=""status-badge"">
            <div class=""status-dot""></div>
            MASTER NODE LIVE
        </div>
    </header>

    <div class=""dashboard-container"">
        <div class=""card"">
            <div class=""card-label"">Active Ghost Nodes</div>
            <div class=""card-value"" id=""stat-nodes"">0</div>
            <div class=""card-footer"">📡 Online Workers</div>
        </div>
        <div class=""card"">
            <div class=""card-label"">Keys Discovered</div>
            <div class=""card-value"" id=""stat-keys"">0</div>
            <div class=""card-footer"">🔑 Valid & Leak Verified</div>
        </div>
        <div class=""card"">
            <div class=""card-label"">Search Spectrum</div>
            <div class=""card-value"" id=""stat-queries"">0</div>
            <div class=""card-footer"">🔍 Active Hunter Queries</div>
        </div>
    </div>

    <div class=""history-section"">
        <div class=""history-header"">
            🛰️ LAST SIGNAL DETECTED: <span id=""stat-last-seen"" style=""color: var(--accent);"">WAITING...</span>
        </div>
    </div>

    <div class=""footer"">
        &copy; 2026 APIHunter Decentalized Network. All rights secured.
    </div>

    <script>
        async function fetchStats() {
            try {
                const response = await fetch('/api/v1/nodes/stats');
                const data = await response.json();
                
                document.getElementById('stat-nodes').innerText = data.activeNodes;
                document.getElementById('stat-keys').innerText = data.totalKeys;
                document.getElementById('stat-queries').innerText = data.activeQueries;
                
                if (data.lastDiscoveryAt) {
                    const date = new Date(data.lastDiscoveryAt);
                    document.getElementById('stat-last-seen').innerText = date.toLocaleString();
                } else {
                    document.getElementById('stat-last-seen').innerText = 'NO DATA YET';
                }
            } catch (err) {
                console.error('Failed to fetch stats:', err);
            }
        }

        // Fresh data every 30 seconds
        fetchStats();
        setInterval(fetchStats, 30000);
    </script>
</body>
</html>";
    return Results.Content(dashboardHtml, "text/html");
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

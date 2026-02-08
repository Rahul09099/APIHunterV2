using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using UnsecuredAPIKeys.Data;
using UnsecuredAPIKeys.Data.Models;
using UnsecuredAPIKeys.Data.Common;
using System.Text;
using System.Net.Http;
using Microsoft.EntityFrameworkCore;
using Telegram.Bot.Types.ReplyMarkups;

namespace UnsecuredAPIKeys.Services.Telegram;

public class TelegramBotService : BackgroundService
{
    private readonly ILogger<TelegramBotService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly ITelegramBotClient _botClient;
    private readonly long _adminChatId;
    private readonly BackgroundJobManager _jobManager;

    public TelegramBotService(
        ILogger<TelegramBotService> logger,
        IServiceProvider serviceProvider,
        BackgroundJobManager jobManager)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _jobManager = jobManager;

        var token = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN");
        var chatIdStr = Environment.GetEnvironmentVariable("TELEGRAM_ADMIN_CHAT_ID");

        if (string.IsNullOrEmpty(token))
        {
            _logger.LogWarning("TELEGRAM_BOT_TOKEN is not set. Telegram Bot will not start.");
            _botClient = null!;
            return;
        }

        _botClient = new TelegramBotClient(token);
        
        if (long.TryParse(chatIdStr, out long chatId))
        {
            _adminChatId = chatId;
        }
        else
        {
            _logger.LogWarning("TELEGRAM_ADMIN_CHAT_ID is not set or invalid.");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_botClient == null) return;

        _logger.LogInformation("Telegram Bot Service is starting...");

        var receiverOptions = new ReceiverOptions
        {
            AllowedUpdates = Array.Empty<UpdateType>() // Receive all update types
        };

        _botClient.StartReceiving(
            updateHandler: HandleUpdateAsync,
            errorHandler: HandlePollingErrorAsync,
            receiverOptions: receiverOptions,
            cancellationToken: stoppingToken
        );

        // Notify admin that bot is online
        if (_adminChatId != 0)
        {
            await _botClient.SendMessage(
                chatId: _adminChatId,
                text: "🚀 UnsecuredAPIKeys Bot is online!",
                cancellationToken: stoppingToken);
        }

        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
    {
        long chatId = 0;
        string? messageText = null;
        string? callbackData = null;

        if (update.Message is { Text: { } text })
        {
            chatId = update.Message.Chat.Id;
            messageText = text;
        }
        else if (update.CallbackQuery is { Data: { } data, Message: { } msg })
        {
            chatId = msg.Chat.Id;
            callbackData = data;
        }

        if (chatId == 0) return;

        // Simple authorization
        if (_adminChatId != 0 && chatId != _adminChatId)
        {
            await botClient.SendMessage(chatId, "⛔ Unauthorized access.", cancellationToken: cancellationToken);
            return;
        }

        if (messageText != null)
        {
            _logger.LogInformation("Received Telegram message: '{Text}' from '{ChatId}'", messageText, chatId);
            await HandleCommand(chatId, messageText, cancellationToken);
        }
        else if (callbackData != null)
        {
            _logger.LogInformation("Received Telegram callback: '{Data}' from '{ChatId}'", callbackData, chatId);
            await HandleCallback(chatId, callbackData, update.CallbackQuery!.Id, cancellationToken);
        }
    }

    private async Task HandleCommand(long chatId, string messageText, CancellationToken cancellationToken)
    {
        try
        {
            var command = messageText.Split(' ')[0].ToLower();
            var args = messageText.Contains(' ') ? messageText.Substring(messageText.IndexOf(' ') + 1) : "";

            switch (command)
            {
                case "/start":
                case "/help":
                    await HandleHelpCommand(chatId, cancellationToken);
                    break;
                case "/status":
                    await HandleStatusCommand(chatId, cancellationToken);
                    break;
                case "/stats":
                    await HandleStatsCommand(chatId, cancellationToken);
                    break;
                case "/start_scraper":
                    await HandleStartScraperCommand(chatId, cancellationToken);
                    break;
                case "/stop_scraper":
                    await HandleStopJobCommand(chatId, args, "Scraper", cancellationToken);
                    break;
                case "/scraper_jobs":
                    await HandleListJobsCommand(chatId, "Scraper", cancellationToken);
                    break;
                case "/start_verifier":
                    await HandleStartVerifierCommand(chatId, args, cancellationToken);
                    break;
                case "/stop_verifier":
                    await HandleStopJobCommand(chatId, args, "Verifier", cancellationToken);
                    break;
                case "/verifier_jobs":
                    await HandleListJobsCommand(chatId, "Verifier", cancellationToken);
                    break;
                case "/api_types":
                    await HandleListApiTypesCommand(chatId, cancellationToken);
                    break;
                case "/tokens":
                    await HandleListTokensCommand(chatId, cancellationToken);
                    break;
                case "/add_token":
                    await HandleAddTokenCommand(chatId, args, cancellationToken);
                    break;
                case "/delete_token":
                    await HandleDeleteTokenCommand(chatId, args, cancellationToken);
                    break;
                case "/queries":
                    await HandleListQueriesCommand(chatId, cancellationToken);
                    break;
                case "/add_query":
                    await HandleAddQueryCommand(chatId, args, cancellationToken);
                    break;
                case "/delete_query":
                    await HandleDeleteQueryCommand(chatId, args, cancellationToken);
                    break;
                case "/toggle_query":
                    await HandleToggleQueryCommand(chatId, args, cancellationToken);
                    break;
                case "/valid_keys":
                    await HandleValidKeysCommand(chatId, cancellationToken);
                    break;
                case "/export":
                    await HandleExportCommand(chatId, args, cancellationToken);
                    break;
                case "/reset_database":
                    await HandleResetDatabaseCommand(chatId, args, cancellationToken);
                    break;
                default:
                    if (messageText.StartsWith("/"))
                        await _botClient.SendMessage(chatId, "❓ Unknown command. Use /help to see available commands.", cancellationToken: cancellationToken);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling Telegram command");
            await _botClient.SendMessage(chatId, $"❌ Error: {ex.Message}", cancellationToken: cancellationToken);
        }
    }

    private async Task HandleCallback(long chatId, string callbackData, string queryId, CancellationToken cancellationToken)
    {
        try
        {
            await _botClient.AnswerCallbackQuery(queryId, cancellationToken: cancellationToken);

            if (callbackData.StartsWith("scrape_group:"))
            {
                var parts = callbackData.Split(':');
                if (parts.Length == 3)
                {
                    var groupName = parts[1];
                    var mode = parts[2];
                    var isDeep = mode == "deep";

                    var jobId = _jobManager.StartJob($"Scraper-{groupName}", async (ct) =>
                    {
                        using var scope = _serviceProvider.CreateScope();
                        var dbContext = scope.ServiceProvider.GetRequiredService<DBContext>();
                        var httpClientFactory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();
                        var scraper = new ScraperService(dbContext, httpClientFactory);
                        await scraper.RunScrapeByGroupAsync(groupName, isDeep, ct);
                    });

                    await _botClient.SendMessage(chatId, $"🚀 Scraper started for *{groupName}* ({mode} mode)!\nJob ID: `{jobId}`", parseMode: ParseMode.Markdown, cancellationToken: cancellationToken);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling Telegram callback");
            await _botClient.SendMessage(chatId, $"❌ Error: {ex.Message}", cancellationToken: cancellationToken);
        }
    }

    private Task HandlePollingErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
    {
        _logger.LogError(exception, "Telegram Bot API Polling Error");
        return Task.CompletedTask;
    }

    #region Command Handlers

    private async Task HandleHelpCommand(long chatId, CancellationToken ct)
    {
        var help = new StringBuilder();
        help.AppendLine("🤖 <b>UnsecuredAPIKeys Bot Commands</b>");
        help.AppendLine();
        help.AppendLine("📊 <b>General</b>");
        help.AppendLine("/status - Overall status & active jobs");
        help.AppendLine("/stats - Detailed statistics");
        help.AppendLine("/help - Show this message");
        help.AppendLine();
        help.AppendLine("🔍 <b>Scraper</b>");
        help.AppendLine("/start_scraper - Start interactive scraper");
        help.AppendLine("/stop_scraper &lt;id&gt; - Stop a job");
        help.AppendLine("/scraper_jobs - List scraper jobs");
        help.AppendLine();
        help.AppendLine("✅ <b>Verifier</b>");
        help.AppendLine("/start_verifier [types] - Start verifier");
        help.AppendLine("/stop_verifier &lt;id&gt; - Stop a job");
        help.AppendLine("/verifier_jobs - List verifier jobs");
        help.AppendLine("/api_types - List supported API types");
        help.AppendLine();
        help.AppendLine("⚙️ <b>Config</b>");
        help.AppendLine("/tokens - List GitHub tokens");
        help.AppendLine("/add_token &lt;token&gt; - Add GitHub token");
        help.AppendLine("/delete_token &lt;id&gt; - Delete GitHub token");
        help.AppendLine("/queries - List search queries");
        help.AppendLine("/add_query &lt;query&gt; - Add search query");
        help.AppendLine("/delete_query &lt;id&gt; - Delete search query");
        help.AppendLine("/toggle_query &lt;id&gt; - Toggle a query");
        help.AppendLine();
        help.AppendLine("💾 <b>Data</b>");
        help.AppendLine("/valid_keys - Count of valid keys");
        help.AppendLine("/export [csv|json] - Get keys file");
        help.AppendLine("/reset_database CONFIRM_RESET - Wipe DB");

        await _botClient.SendMessage(chatId, help.ToString(), parseMode: ParseMode.Html, cancellationToken: ct);
    }

    private async Task HandleStatusCommand(long chatId, CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DBContext>();
        var dbService = scope.ServiceProvider.GetRequiredService<DatabaseService>();
        
        var stats = await dbService.GetCategorizedStatisticsAsync(dbContext);
        var activeJobs = _jobManager.GetAllJobs().Where(j => j.Status == "Running").ToList();

        var sb = new StringBuilder();
        sb.AppendLine("<b>📊 Current Status</b>");
        sb.AppendLine($"Total Keys: {stats.TotalKeys}");
        sb.AppendLine($"Valid: {stats.ValidKeys} ✅");
        sb.AppendLine($"Invalid: {stats.InvalidKeys} ❌");
        sb.AppendLine($"Unverified: {stats.UnverifiedKeys} ⏳");
        sb.AppendLine($"GitHub Tokens: {stats.GitHubTokensCount}");
        sb.AppendLine();
        sb.AppendLine($"<b>🏃 Active Jobs:</b> {activeJobs.Count}");
        foreach (var job in activeJobs)
        {
            var jobId = System.Net.WebUtility.HtmlEncode(job.JobId);
            sb.AppendLine($"- {job.JobType}: <code>{jobId}</code>");
        }

        await _botClient.SendMessage(chatId, sb.ToString(), parseMode: ParseMode.Html, cancellationToken: ct);
    }

    private async Task HandleStatsCommand(long chatId, CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DBContext>();
        var dbService = scope.ServiceProvider.GetRequiredService<DatabaseService>();
        
        var stats = await dbService.GetCategorizedStatisticsAsync(dbContext);

        var sb = new StringBuilder();
        sb.AppendLine("<b>📈 Detailed Statistics</b>");
        
        foreach (var category in stats.Categories)
        {
            sb.AppendLine();
            sb.AppendLine($"<b>【 {System.Net.WebUtility.HtmlEncode(category.Value.CategoryName)} 】</b>");
            foreach (var type in category.Value.ApiTypes)
            {
                sb.AppendLine($"{System.Net.WebUtility.HtmlEncode(type.ApiTypeName)}: {type.KeyCount}");
            }
        }

        await _botClient.SendMessage(chatId, sb.ToString(), parseMode: ParseMode.Html, cancellationToken: ct);
    }

    private async Task HandleStartScraperCommand(long chatId, CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DBContext>();
        var httpClientFactory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();
        var scraper = new ScraperService(dbContext, httpClientFactory);

        var groups = await scraper.GetAvailableGroupsAsync(ct);

    if (groups.Count == 0)
    {
        await _botClient.SendMessage(chatId, "⚠️ No targets to scrape. Please ensure you have:\n1. Enabled <b>Search Queries</b> (/queries)\n2. Enabled <b>GitHub Tokens</b> (/tokens)", parseMode: ParseMode.Html, cancellationToken: ct);
        return;
    }

        var buttons = groups.Select(g => new[] { InlineKeyboardButton.WithCallbackData(g, $"scrape_group:{g}:lite"), InlineKeyboardButton.WithCallbackData($"{g} (Deep)", $"scrape_group:{g}:deep") }).ToArray();
        
        var inlineKeyboard = new InlineKeyboardMarkup(buttons);

        await _botClient.SendMessage(
            chatId: chatId,
            text: "🔍 Select a provider group and mode to scrape:",
            replyMarkup: inlineKeyboard,
            cancellationToken: ct);
    }

    // Callback query handler (part of the main handler in a real scenario, but simplified here)
    // For this demonstration, I'll implement a standalone method that could be called if the library supported it easily.
    // In a real implementation, I'd need to handle UpdateType.CallbackQuery in HandleUpdateAsync.

    private async Task HandleStartVerifierCommand(long chatId, string args, CancellationToken ct)
    {
        HashSet<ApiTypeEnum>? selectedTypes = null;
        if (!string.IsNullOrWhiteSpace(args))
        {
            selectedTypes = new HashSet<ApiTypeEnum>();
            foreach (var typeName in args.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (Enum.TryParse<ApiTypeEnum>(typeName.Trim(), true, out var apiType))
                    selectedTypes.Add(apiType);
            }
        }

        var jobId = _jobManager.StartJob("Verifier", async (cancellationToken) =>
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<DBContext>();
            var httpClientFactory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();
            var verifier = new VerifierService(dbContext, httpClientFactory, selectedTypes);
            await verifier.RunAsync(cancellationToken);
        });

        await _botClient.SendMessage(chatId, $"✅ Verifier started! Job ID: <code>{System.Net.WebUtility.HtmlEncode(jobId)}</code>", parseMode: ParseMode.Html, cancellationToken: ct);
    }

    private async Task HandleStopJobCommand(long chatId, string jobId, string type, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            var jobs = _jobManager.GetAllJobs().Where(j => j.JobType == type && j.Status == "Running").ToList();
            if (jobs.Count == 1)
            {
                jobId = jobs[0].JobId;
            }
            else
            {
                await _botClient.SendMessage(chatId, $"❌ Please specify a Job ID. Use /{type.ToLower()}_jobs to see active IDs.", cancellationToken: ct);
                return;
            }
        }

        var success = _jobManager.StopJob(jobId);
        if (success)
            await _botClient.SendMessage(chatId, $"⏹️ Stop requested for {type} job <code>{System.Net.WebUtility.HtmlEncode(jobId)}</code>.", parseMode: ParseMode.Html, cancellationToken: ct);
        else
            await _botClient.SendMessage(chatId, "⚠️ Job not found or already stopped.", cancellationToken: ct);
    }

    private async Task HandleListJobsCommand(long chatId, string type, CancellationToken ct)
    {
        var jobs = _jobManager.GetAllJobs().Where(j => j.JobType == type).ToList();
        if (jobs.Count == 0)
        {
            await _botClient.SendMessage(chatId, $"No {type} jobs found.", cancellationToken: ct);
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"<b>📋 Recent {type} Jobs:</b>");
        foreach (var job in jobs.TakeLast(5))
        {
            var jobId = System.Net.WebUtility.HtmlEncode(job.JobId);
            sb.AppendLine($"- <code>{jobId}</code>: {job.Status} (Started: {job.StartedAt})");
        }

        await _botClient.SendMessage(chatId, sb.ToString(), parseMode: ParseMode.Html, cancellationToken: ct);
    }

    private async Task HandleListApiTypesCommand(long chatId, CancellationToken ct)
    {
        var types = Enum.GetValues<ApiTypeEnum>().Where(t => t != ApiTypeEnum.Unknown).ToList();
        var sb = new StringBuilder();
        sb.AppendLine("<b>📋 Supported API Types:</b>");
        foreach (var type in types.OrderBy(t => t.ToString()))
        {
            sb.AppendLine($"- {type}");
        }
        await _botClient.SendMessage(chatId, sb.ToString(), parseMode: ParseMode.Html, cancellationToken: ct);
    }

    private async Task HandleListTokensCommand(long chatId, CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DBContext>();
        var tokens = await dbContext.SearchProviderTokens.Where(t => t.SearchProvider == SearchProviderEnum.GitHub).ToListAsync(ct);

        if (tokens.Count == 0)
        {
            await _botClient.SendMessage(chatId, "No GitHub tokens found.", cancellationToken: ct);
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("<b>🔑 GitHub Tokens:</b>");
        foreach (var t in tokens)
        {
            var preview = t.Token.Length > 10 ? System.Net.WebUtility.HtmlEncode(t.Token.Substring(0, 10)) + "..." : "***";
            sb.AppendLine($"- ID: <code>{t.Id}</code> | {preview} | Enabled: {t.IsEnabled}");
        }

        await _botClient.SendMessage(chatId, sb.ToString(), parseMode: ParseMode.Html, cancellationToken: ct);
    }

    private async Task HandleAddTokenCommand(long chatId, string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            await _botClient.SendMessage(chatId, "❌ Please provide the token: /add_token <token>", cancellationToken: ct);
            return;
        }

        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DBContext>();
        var dbService = scope.ServiceProvider.GetRequiredService<DatabaseService>();
        await dbService.AddGitHubTokenAsync(dbContext, token);

        await _botClient.SendMessage(chatId, "✅ GitHub token added successfully!", cancellationToken: ct);
    }

    private async Task HandleDeleteTokenCommand(long chatId, string arg, CancellationToken ct)
    {
        if (!int.TryParse(arg, out int id))
        {
            await _botClient.SendMessage(chatId, "❌ Please provide the token ID: /delete_token <id>", cancellationToken: ct);
            return;
        }

        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DBContext>();
        var dbService = scope.ServiceProvider.GetRequiredService<DatabaseService>();
        await dbService.DeleteGitHubTokenAsync(dbContext, id);

        await _botClient.SendMessage(chatId, $"✅ Token {id} deleted.", cancellationToken: ct);
    }

    private async Task HandleListQueriesCommand(long chatId, CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DBContext>();
        var queries = await dbContext.SearchQueries.ToListAsync(ct);

        var sb = new StringBuilder();
        sb.AppendLine("<b>🔍 Search Queries:</b>");
        foreach (var q in queries.Take(20))
        {
            var queryText = System.Net.WebUtility.HtmlEncode(q.Query);
            sb.AppendLine($"- ID: <code>{q.Id}</code> | {queryText} | Enabled: {q.IsEnabled}");
        }
        if (queries.Count > 20) sb.AppendLine("...(more queries in DB)");

        await _botClient.SendMessage(chatId, sb.ToString(), parseMode: ParseMode.Html, cancellationToken: ct);
    }

    private async Task HandleAddQueryCommand(long chatId, string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            await _botClient.SendMessage(chatId, "❌ Please provide the query: /add_query <query>", cancellationToken: ct);
            return;
        }

        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DBContext>();
        dbContext.SearchQueries.Add(new SearchQuery { Query = query, IsEnabled = true });
        await dbContext.SaveChangesAsync(ct);

        await _botClient.SendMessage(chatId, "✅ Search query added!", cancellationToken: ct);
    }

    private async Task HandleDeleteQueryCommand(long chatId, string arg, CancellationToken ct)
    {
        if (!int.TryParse(arg, out int id))
        {
            await _botClient.SendMessage(chatId, "❌ Please provide the query ID: /delete_query <id>", cancellationToken: ct);
            return;
        }

        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DBContext>();
        var query = await dbContext.SearchQueries.FindAsync(id);
        if (query != null)
        {
            dbContext.SearchQueries.Remove(query);
            await dbContext.SaveChangesAsync(ct);
            await _botClient.SendMessage(chatId, $"✅ Query {id} deleted.", cancellationToken: ct);
        }
        else await _botClient.SendMessage(chatId, "❌ Query not found.", cancellationToken: ct);
    }

    private async Task HandleToggleQueryCommand(long chatId, string arg, CancellationToken ct)
    {
        if (!int.TryParse(arg, out int id))
        {
            await _botClient.SendMessage(chatId, "❌ Please provide the query ID: /toggle_query <id>", cancellationToken: ct);
            return;
        }

        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DBContext>();
        var query = await dbContext.SearchQueries.FindAsync(id);
        if (query != null)
        {
            query.IsEnabled = !query.IsEnabled;
            await dbContext.SaveChangesAsync(ct);
            await _botClient.SendMessage(chatId, $"✅ Query {id} is now {(query.IsEnabled ? "Enabled" : "Disabled")}.", cancellationToken: ct);
        }
        else await _botClient.SendMessage(chatId, "❌ Query not found.", cancellationToken: ct);
    }

    private async Task HandleValidKeysCommand(long chatId, CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DBContext>();
        var validKeys = await dbContext.APIKeys
            .Where(k => k.Status == ApiStatusEnum.Valid)
            .GroupBy(k => k.ApiType)
            .Select(g => new { apiType = g.Key.ToString(), count = g.Count() })
            .ToListAsync(ct);

        var sb = new StringBuilder();
        sb.AppendLine("<b>✅ Valid Keys Count:</b>");
        foreach (var v in validKeys.OrderByDescending(x => x.count))
        {
            sb.AppendLine($"- {System.Net.WebUtility.HtmlEncode(v.apiType)}: {v.count}");
        }
        sb.AppendLine();
        sb.AppendLine($"Total Valid: {validKeys.Sum(x => x.count)}");

        await _botClient.SendMessage(chatId, sb.ToString(), parseMode: ParseMode.Html, cancellationToken: ct);
    }

    private async Task HandleExportCommand(long chatId, string format, CancellationToken ct)
    {
        string fmt = (format?.ToLower() == "json") ? "json" : "csv";
        string fileName = $"valid_keys_{DateTime.Now:yyyyMMdd_HHmmss}.{fmt}";
        string filePath = Path.Combine(Path.GetTempPath(), fileName);

        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DBContext>();
        var dbService = scope.ServiceProvider.GetRequiredService<DatabaseService>();
        
        await dbService.ExportKeysAsync(dbContext, filePath, true, fmt);

        using var stream = System.IO.File.OpenRead(filePath);
        await _botClient.SendDocument(
            chatId: chatId,
            document: InputFile.FromStream(stream, fileName),
            caption: $"📂 Exported valid keys in {fmt.ToUpper()} format.",
            cancellationToken: ct);

        try { System.IO.File.Delete(filePath); } catch { }
    }

    private async Task HandleResetDatabaseCommand(long chatId, string arg, CancellationToken ct)
    {
        if (arg != "CONFIRM_RESET")
        {
            await _botClient.SendMessage(chatId, "⚠️ To reset the database, use: /reset_database CONFIRM_RESET", cancellationToken: ct);
            return;
        }

        using var scope = _serviceProvider.CreateScope();
        var dbService = scope.ServiceProvider.GetRequiredService<DatabaseService>();
        await dbService.ResetDatabaseAsync();

        await _botClient.SendMessage(chatId, "💥 Database has been reset and re-initialized.", cancellationToken: ct);
    }

    #endregion
}

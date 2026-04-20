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

        // Set bot commands menu
        await _botClient.SetMyCommands(new[]
        {
            new BotCommand { Command = "status", Description = "Mission Control" },
            new BotCommand { Command = "stats", Description = "Statistics" },
            new BotCommand { Command = "start_scraper", Description = "Start Scraper" },
            new BotCommand { Command = "start_verifier", Description = "Start Verifier" },
            new BotCommand { Command = "valid_keys", Description = "Valid Keys" },
            new BotCommand { Command = "export", Description = "Export Data" },
            new BotCommand { Command = "help", Description = "Show Commands" }
        }, cancellationToken: stoppingToken);

        // Notify admin that bot is online
        if (_adminChatId != 0)
        {
            await _botClient.SendMessage(
                chatId: _adminChatId,
                text: "<b>💎 APIHunterV2 Dashboard Online</b>\n⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯\n<i>Satellite connection established.</i>",
                parseMode: ParseMode.Html,
                cancellationToken: stoppingToken);
        }

        // Live Notification Loop
        var lastCheck = DateTime.UtcNow;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

                if (_adminChatId == 0) continue;

                using var scope = _serviceProvider.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<DBContext>();

                var newValidKeys = await dbContext.APIKeys
                    .Where(k => (k.Status == ApiStatusEnum.Valid || k.Status == ApiStatusEnum.ValidNoCredits) && k.LastCheckedUTC > lastCheck)
                    .OrderBy(k => k.LastCheckedUTC)
                    .ToListAsync(stoppingToken);

                if (newValidKeys.Any())
                {
                    foreach (var key in newValidKeys)
                    {
                        var statusStr = key.Status == ApiStatusEnum.Valid ? "✅ VALID" : "⚠️ QUOTA EXCEEDED";
                        var colorIcon = key.Status == ApiStatusEnum.Valid ? "🟢" : "🟡";
                        
                        var sb = new StringBuilder();
                        sb.AppendLine($"{colorIcon} <b>NEW {key.ApiType.ToString().ToUpper()} KEY FOUND</b>");
                        sb.AppendLine("⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯");
                        sb.AppendLine($"<b>Status:</b> <code>{statusStr}</code>");
                        
                        if (!string.IsNullOrEmpty(key.AccountTier))
                            sb.AppendLine($"<b>Tier:</b>   <code>{key.AccountTier}</code>");
                            
                        if (!string.IsNullOrEmpty(key.Balance))
                            sb.AppendLine($"<b>Value:</b>  <code>{key.Balance}</code>");

                        sb.AppendLine($"<b>Key:</b>    <code>{key.ApiKey.Substring(0, Math.Min(12, key.ApiKey.Length))}...</code>");
                        sb.AppendLine("⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯");
                        
                        var keyboard = new InlineKeyboardMarkup(new[]
                        {
                            new [] { InlineKeyboardButton.WithCallbackData("📂 Export Intelligence", $"export_key:{key.Id}") }
                        });

                        // Notify owner or super admin
                        var targetChatId = key.DiscoveredByTelegramId ?? _adminChatId;
                        if (targetChatId != 0)
                        {
                            await _botClient.SendMessage(
                                chatId: targetChatId,
                                text: sb.ToString(),
                                parseMode: ParseMode.Html,
                                replyMarkup: keyboard,
                                cancellationToken: stoppingToken);
                        }
                        
                        // If key is not owned by super admin, also notify super admin (optional, for monitoring)
                        if (targetChatId != _adminChatId && _adminChatId != 0)
                        {
                             await _botClient.SendMessage(
                                chatId: _adminChatId,
                                text: $"<i>[Monitor] New key found by user {targetChatId}:</i>\n{sb}",
                                parseMode: ParseMode.Html,
                                cancellationToken: stoppingToken);
                        }
                    }
                    lastCheck = newValidKeys.Max(k => k.LastCheckedUTC) ?? lastCheck;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in Telegram notification loop");
            }
        }
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
 
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DBContext>();
        
        // Authorization Logics
        var user = await dbContext.TelegramSubscribers.FindAsync(new object[] { chatId }, cancellationToken);
        var isSuperAdmin = _adminChatId != 0 && chatId == _adminChatId;
        var hasActiveSub = user != null && user.SubscriptionExpiryUtc > DateTime.UtcNow;
        var isAdmin = isSuperAdmin || (user != null && user.IsAdmin);

        if (!isSuperAdmin && !hasActiveSub)
        {
            // Allow /id and /help for everyone
            if (messageText == "/id")
            {
                await botClient.SendMessage(chatId, $"Your Telegram ID: <code>{chatId}</code>", parseMode: ParseMode.Html, cancellationToken: cancellationToken);
                return;
            }
            
            await botClient.SendMessage(chatId, "⛔ <b>Access Denied</b>\n⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯\nYour account does not have an active subscription. Please contact the administrator.\n\nYour ID: <code>" + chatId + "</code>", parseMode: ParseMode.Html, cancellationToken: cancellationToken);
            return;
        }

        if (messageText != null)
        {
            _logger.LogInformation("Received Telegram message: '{Text}' from '{ChatId}'", messageText, chatId);
            await HandleCommand(chatId, messageText, user, isAdmin, isSuperAdmin, cancellationToken);
        }
        else if (callbackData != null)
        {
            _logger.LogInformation("Received Telegram callback: '{Data}' from '{ChatId}'", callbackData, chatId);
            await HandleCallback(chatId, callbackData, update.CallbackQuery!.Id, isAdmin, cancellationToken);
        }
    }

    private async Task HandleCommand(long chatId, string messageText, TelegramSubscriber? user, bool isAdmin, bool isSuperAdmin, CancellationToken cancellationToken)
    {
        try
        {
            var command = messageText.Split(' ')[0].ToLower();
            var args = messageText.Contains(' ') ? messageText.Substring(messageText.IndexOf(' ') + 1) : "";

            switch (command)
            {
                case "/start":
                case "/help":
                    await HandleHelpCommand(chatId, isAdmin, cancellationToken);
                    break;
                case "/status":
                    await HandleStatusCommand(chatId, isAdmin, cancellationToken);
                    break;
                case "/stats":
                    await HandleStatsCommand(chatId, isAdmin, cancellationToken);
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
                    await HandleListTokensCommand(chatId, isAdmin, cancellationToken);
                    break;
                case "/add_token":
                    await HandleAddTokenCommand(chatId, args, cancellationToken);
                    break;
                case "/delete_token":
                    await HandleDeleteTokenCommand(chatId, args, isAdmin, cancellationToken);
                    break;
                case "/queries":
                    if (isAdmin) await HandleListQueriesCommand(chatId, cancellationToken);
                    break;
                case "/add_query":
                    if (isAdmin) await HandleAddQueryCommand(chatId, args, cancellationToken);
                    break;
                case "/delete_query":
                    if (isAdmin) await HandleDeleteQueryCommand(chatId, args, cancellationToken);
                    break;
                case "/toggle_query":
                    if (isAdmin) await HandleToggleQueryCommand(chatId, args, cancellationToken);
                    break;
                case "/valid_keys":
                    await HandleValidKeysCommand(chatId, isAdmin, cancellationToken);
                    break;
                case "/export":
                    await HandleExportCommand(chatId, args, isAdmin, cancellationToken);
                    break;
                case "/my_sub":
                    await HandleMySubCommand(chatId, user, cancellationToken);
                    break;
                case "/id":
                    await _botClient.SendMessage(chatId, $"Your Telegram ID: <code>{chatId}</code>", parseMode: ParseMode.Html, cancellationToken: cancellationToken);
                    break;
                case "/add_sub":
                    if (isAdmin) await HandleAddSubCommand(chatId, args, cancellationToken);
                    break;
                case "/remove_sub":
                    if (isAdmin) await HandleRemoveSubCommand(chatId, args, cancellationToken);
                    break;
                case "/list_subs":
                    if (isAdmin) await HandleListSubsCommand(chatId, cancellationToken);
                    break;
                case "/admins":
                    if (isAdmin) await HandleListAdminsCommand(chatId, cancellationToken);
                    break;
                case "/set_admin":
                    if (isSuperAdmin) await HandleSetAdminCommand(chatId, args, cancellationToken);
                    break;
                case "/reset_database":
                    if (isSuperAdmin) await HandleResetDatabaseCommand(chatId, args, cancellationToken);
                    break;
                case "/node_token":
                    await HandleNodeTokenCommand(chatId, cancellationToken);
                    break;
                case "/master_url":
                    await HandleMasterUrlCommand(chatId, cancellationToken);
                    break;
                case "/node_status":
                    await HandleNodeStatusCommand(chatId, isAdmin, cancellationToken);
                    break;
                case "/purge_junk":
                    if (isAdmin) await HandlePurgeJunkCommand(chatId, cancellationToken);
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

    private async Task HandleCallback(long chatId, string callbackData, string queryId, bool isAdmin, CancellationToken cancellationToken)
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
                        await scraper.RunScrapeByGroupAsync(groupName, isDeep, chatId, ct);
                    });
 
                    await _botClient.SendMessage(chatId, $"🚀 Scraper started for *{groupName}* ({mode} mode)!\nJob ID: `{jobId}`", parseMode: ParseMode.Markdown, cancellationToken: cancellationToken);
                }
            }
            else if (callbackData == "status_refresh")
            {
                await HandleStatusCommand(chatId, isAdmin, cancellationToken);
            }
            else if (callbackData == "jobs_list")
            {
                var jobs = _jobManager.GetAllJobs().Where(j => j.Status == "Running").ToList();
                if (!jobs.Any())
                {
                    await _botClient.SendMessage(chatId, "No active jobs running.", cancellationToken: cancellationToken);
                }
                else
                {
                    var sb = new StringBuilder("<b>🏃 ACTIVE DEPLOYMENTS</b>\n⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯\n");
                    foreach (var job in jobs)
                    {
                        sb.AppendLine($"▸ {job.JobType}: <code>{job.JobId.Substring(0, 8)}</code>");
                    }
                    await _botClient.SendMessage(chatId, sb.ToString(), parseMode: ParseMode.Html, cancellationToken: cancellationToken);
                }
            }
            else if (callbackData == "purge_junk")
            {
                if (isAdmin)
                {
                    using var scope = _serviceProvider.CreateScope();
                    var dbContext = scope.ServiceProvider.GetRequiredService<DBContext>();
                    var dbService = scope.ServiceProvider.GetRequiredService<DatabaseService>();
                    var count = await dbService.PurgeJunkSourcesAsync(dbContext);
                    await _botClient.SendMessage(chatId, $"🧹 <b>Database Optimization Complete</b>\n⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯\nPurged <code>{count}</code> junk repository references from invalid keys.", parseMode: ParseMode.Html, cancellationToken: cancellationToken);
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

    private async Task HandleHelpCommand(long chatId, bool isAdmin, CancellationToken ct)
    {
        var help = new StringBuilder();
        help.AppendLine("<b>🤖 UnsecuredAPIKeys Bot Commands</b>");
        help.AppendLine("⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯");
        help.AppendLine("📊 <b>General</b>");
        help.AppendLine("├ /status - Overall status &amp; active jobs");
        help.AppendLine("├ /stats - Detailed statistics");
        help.AppendLine("└ /help - Show this message");
        help.AppendLine();
        help.AppendLine("🔍 <b>Scraper</b>");
        help.AppendLine("├ /start_scraper - Start interactive scraper");
        help.AppendLine("├ /stop_scraper &lt;id&gt; - Stop a job");
        help.AppendLine("└ /scraper_jobs - List scraper jobs");
        help.AppendLine();
        help.AppendLine("✅ <b>Verifier</b>");
        help.AppendLine("├ /start_verifier [types] - Start verifier");
        help.AppendLine("├ /stop_verifier &lt;id&gt; - Stop a job");
        help.AppendLine("├ /verifier_jobs - List verifier jobs");
        help.AppendLine("└ /api_types - List supported API types");
        help.AppendLine();
        help.AppendLine("👻 <b>Ghost Node (Worker)</b>");
        help.AppendLine("├ /master_url - Master connection URL");
        help.AppendLine("├ /node_token - Your personal access token");
        help.AppendLine("├ /tokens - List your GitHub tokens");
        help.AppendLine("├ /add_token &lt;token&gt; - Add GitHub token");
        help.AppendLine("├ /node_status - View your node status");
        help.AppendLine("└ /delete_token &lt;id&gt; - Delete your token");

        if (isAdmin)
        {
            help.AppendLine();
            help.AppendLine("👤 <b>Admin Management</b>");
            help.AppendLine("├ /add_sub &lt;id&gt; &lt;days&gt; - Add subscriber");
            help.AppendLine("├ /remove_sub &lt;id&gt; - Remove access");
            help.AppendLine("├ /list_subs - List all subscribers");
            help.AppendLine("├ /admins - List all admins");
            help.AppendLine("└ /set_admin &lt;id&gt; &lt;true/false&gt; - Toggle admin");
        }

        if (isAdmin)
        {
            help.AppendLine();
            help.AppendLine("⚙️ <b>Global Config</b>");
            help.AppendLine("├ /queries - List search queries");
            help.AppendLine("├ /add_query &lt;query&gt; - Add search query");
            help.AppendLine("├ /delete_query &lt;id&gt; - Delete search query");
            help.AppendLine("└ /toggle_query &lt;id&gt; - Toggle a query");
        }
 
        help.AppendLine();
        help.AppendLine("💾 <b>Data</b>");
        help.AppendLine("├ /valid_keys - Count of valid keys");
        help.AppendLine("├ /export [csv|json] - Get keys file");
        if (isAdmin) help.AppendLine("├ /reset_database CONFIRM_RESET - Wipe DB");
        if (isAdmin) help.AppendLine("└ /purge_junk - Purge references for invalid keys");
 
        help.AppendLine();
        help.AppendLine("📡 <b>Ghost Node</b>");
        help.AppendLine("├ /node_token - Your worker key");
        help.AppendLine("├ /master_url - Connection address");
        help.AppendLine("└ /node_status - Network health");
 
        help.AppendLine();
        help.AppendLine("👤 <b>Account</b>");
        help.AppendLine("├ /my_sub - Subscription status");
        help.AppendLine("└ /id - Your Telegram ID");

        help.AppendLine("⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯");
        help.AppendLine("<i>Use the menu button for quick access.</i>");

        await _botClient.SendMessage(chatId, help.ToString(), parseMode: ParseMode.Html, cancellationToken: ct);
    }

    private async Task HandleStatusCommand(long chatId, bool isAdmin, CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DBContext>();
        var dbService = scope.ServiceProvider.GetRequiredService<DatabaseService>();
        
        // Filter by chatId if not admin
        long? filterBy = isAdmin ? null : chatId;
        var stats = await dbService.GetCategorizedStatisticsAsync(dbContext, filterBy);
        var activeJobs = _jobManager.GetAllJobs().Where(j => j.Status == "Running").ToList();

        var sb = new StringBuilder();
        sb.AppendLine("<b>📡 SATELLITE STATUS</b>");
        sb.AppendLine("⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯");
        
        // 1. Database Health Section (Top)
        sb.AppendLine();
        sb.AppendLine("<b>💾 DATABASE HEALTH (Supabase)</b>");
        double dbSizeMb = stats.DatabaseSizeBytes / (1024.0 * 1024.0);
        const double dbLimitMb = 500.0; // Supabase Free Tier Limit
        double dbUsagePercent = Math.Min(dbSizeMb / dbLimitMb, 1.0);
        
        string dbSizeStr = dbSizeMb > 1024 ? $"{(dbSizeMb / 1024.0):F2} GB" : $"{dbSizeMb:F2} MB";
        sb.AppendLine($"<b>Storage:</b> {GetProgressBar(dbUsagePercent)} {dbUsagePercent:P1}");
        sb.AppendLine($"<b>Used:</b> <code>{dbSizeStr} / {dbLimitMb} MB</code>");
        sb.AppendLine();

        // 2. Key Statistics (Bottom)
        double validPercent = stats.TotalKeys > 0 ? (double)stats.ValidKeys / stats.TotalKeys : 0;
        sb.AppendLine($"<b>Health Index:</b> {GetProgressBar(validPercent)} {validPercent:P0}");
        sb.AppendLine($"<b>Total Keys:</b> <code>{stats.TotalKeys}</code>");
        sb.AppendLine($"<b>🟢 Valid:</b> <code>{stats.ValidKeys}</code>");
        sb.AppendLine($"<b>🔴 Invalid:</b> <code>{stats.InvalidKeys}</code>");
        sb.AppendLine($"<b>⏳ Hidden:</b> <code>{stats.UnverifiedKeys}</code>");
        sb.AppendLine();

        sb.AppendLine($"<b>🔑 Tokens:</b> <code>{stats.GitHubTokensCount} active</code>");
        sb.AppendLine($"<b>🏃 Jobs:</b> <code>{activeJobs.Count} running</code>");

        if (activeJobs.Any())
        {
            foreach (var job in activeJobs)
            {
                sb.AppendLine($"- {job.JobType}: <code>{job.JobId}</code>");
            }
        }
        else
        {
            sb.AppendLine("<i>No active deployments running</i>");
        }
        
        sb.AppendLine("⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯");
        sb.AppendLine($"<i>System time: {DateTime.UtcNow:HH:mm} UTC</i>");

        var keyboardButtons = new List<InlineKeyboardButton[]>
        {
            new [] { 
                InlineKeyboardButton.WithCallbackData("🔄 Refresh", "status_refresh"), 
                InlineKeyboardButton.WithCallbackData("📋 Active Jobs", "jobs_list") 
            }
        };

        var keyboard = new InlineKeyboardMarkup(keyboardButtons);

        await _botClient.SendMessage(chatId, sb.ToString(), parseMode: ParseMode.Html, replyMarkup: keyboard, cancellationToken: ct);
    }
    
    private string GetProgressBar(double percent)
    {
        const int totalBlocks = 10;
        int activeBlocks = (int)(percent * totalBlocks);
        return new string('█', activeBlocks) + new string('░', totalBlocks - activeBlocks);
    }

    private async Task HandleStatsCommand(long chatId, bool isAdmin, CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DBContext>();
        var dbService = scope.ServiceProvider.GetRequiredService<DatabaseService>();
        
        // Filter by chatId if not admin
        long? filterBy = isAdmin ? null : chatId;
        var stats = await dbService.GetCategorizedStatisticsAsync(dbContext, filterBy);

        var sb = new StringBuilder();
        sb.AppendLine("<b>🏆 PROVIDER SCOREBOARD</b>");
        sb.AppendLine("⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯");
        
        foreach (var category in stats.Categories)
        {
            sb.AppendLine();
            sb.AppendLine($"<b>【 {System.Net.WebUtility.HtmlEncode(category.Value.CategoryName.ToUpper())} 】</b>");
            foreach (var type in category.Value.ApiTypes.OrderByDescending(t => t.KeyCount))
            {
                if (type.KeyCount == 0) continue;
                
                string icon = type.ApiTypeName.Contains("OpenAI") ? "🤖" : 
                             type.ApiTypeName.Contains("Google") ? "☁️" :
                             type.ApiTypeName.Contains("Anthropic") ? "🧠" : "✨";
                             
                sb.AppendLine($"{icon} {System.Net.WebUtility.HtmlEncode(type.ApiTypeName)}: <code>{type.KeyCount}</code>");
            }
        }
        
        sb.AppendLine();
        sb.AppendLine("⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯");

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

    private async Task HandleListTokensCommand(long chatId, bool isAdmin, CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DBContext>();
        var dbService = scope.ServiceProvider.GetRequiredService<DatabaseService>();
        
        // Admins see all tokens, subscribers see their own
        long? filterBy = isAdmin ? null : chatId;
        var tokens = await dbService.GetGitHubTokensAsync(dbContext, filterBy);

        if (tokens.Count == 0)
        {
            await _botClient.SendMessage(chatId, "No GitHub tokens found for your account.", cancellationToken: ct);
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("<b>🔑 GitHub Tokens:</b>");
        foreach (var t in tokens)
        {
            var preview = t.Token.Length > 10 ? System.Net.WebUtility.HtmlEncode(t.Token.Substring(0, 10)) + "..." : "***";
            var owner = t.AddedByTelegramId.HasValue ? $"[Owner: {t.AddedByTelegramId}]" : "[System]";
            sb.AppendLine($"- ID: <code>{t.Id}</code> | {preview} | Enabled: {t.IsEnabled} {(isAdmin ? owner : "")}");
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
        
        // Pass chatId as owner
        await dbService.AddGitHubTokenAsync(dbContext, token, chatId);

        await _botClient.SendMessage(chatId, "✅ GitHub token added successfully!", cancellationToken: ct);
    }

    private async Task HandleDeleteTokenCommand(long chatId, string arg, bool isAdmin, CancellationToken ct)
    {
        if (!int.TryParse(arg, out int id))
        {
            await _botClient.SendMessage(chatId, "❌ Please provide the token ID: /delete_token <id>", cancellationToken: ct);
            return;
        }

        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DBContext>();
        var dbService = scope.ServiceProvider.GetRequiredService<DatabaseService>();
        
        // Security check: Only owner or admin can delete
        var token = await dbContext.SearchProviderTokens.FindAsync(id);
        if (token == null)
        {
            await _botClient.SendMessage(chatId, $"❌ Token ID {id} not found.", cancellationToken: ct);
            return;
        }

        if (!isAdmin && token.AddedByTelegramId != chatId)
        {
            await _botClient.SendMessage(chatId, "⛔ You can only delete tokens that you added yourself.", cancellationToken: ct);
            return;
        }

        await dbService.DeleteGitHubTokenAsync(dbContext, id);
        await _botClient.SendMessage(chatId, $"✅ Token ID {id} deleted successfully.", cancellationToken: ct);
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

    private async Task HandleValidKeysCommand(long chatId, bool isAdmin, CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DBContext>();
        
        var query = dbContext.APIKeys.Where(k => k.Status == ApiStatusEnum.Valid);
        if (!isAdmin)
        {
            query = query.Where(k => k.DiscoveredByTelegramId == chatId);
        }

        var validKeys = await query
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

    private async Task HandleExportCommand(long chatId, string format, bool isAdmin, CancellationToken ct)
    {
        string fmt = (format?.ToLower() == "json") ? "json" : "csv";
        string fileName = $"valid_keys_{DateTime.Now:yyyyMMdd_HHmmss}.{fmt}";
        string filePath = Path.Combine(Path.GetTempPath(), fileName);

        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DBContext>();
        var dbService = scope.ServiceProvider.GetRequiredService<DatabaseService>();
        
        // Filter by chatId if not admin
        long? filterBy = isAdmin ? null : chatId;
        await dbService.ExportKeysAsync(dbContext, filePath, true, fmt, filterBy);

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

    private async Task HandleMySubCommand(long chatId, TelegramSubscriber? user, CancellationToken ct)
    {
        if (user == null && chatId == _adminChatId)
        {
            await _botClient.SendMessage(chatId, "<b>💎 Premium Status:</b> <code>Super Admin (Lifetime)</code>", parseMode: ParseMode.Html, cancellationToken: ct);
            return;
        }

        if (user == null)
        {
            await _botClient.SendMessage(chatId, "❌ You do not have an active subscription.", cancellationToken: ct);
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("<b>💎 YOUR SUBSCRIPTION</b>");
        sb.AppendLine("⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯");
        sb.AppendLine($"<b>ID:</b> <code>{chatId}</code>");
        sb.AppendLine($"<b>Status:</b> {(user.SubscriptionExpiryUtc > DateTime.UtcNow ? "🟢 Active" : "🔴 Expired")}");
        sb.AppendLine($"<b>Expiry:</b> <code>{user.SubscriptionExpiryUtc:yyyy-MM-dd HH:mm} UTC</code>");
        sb.AppendLine($"<b>Role:</b> {(user.IsAdmin ? "Admin" : "Subscriber")}");
        sb.AppendLine("⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯");
        
        await _botClient.SendMessage(chatId, sb.ToString(), parseMode: ParseMode.Html, cancellationToken: ct);
    }

    private async Task HandleAddSubCommand(long chatId, string args, CancellationToken ct)
    {
        var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !long.TryParse(parts[0], out long targetId) || !int.TryParse(parts[1], out int days))
        {
            await _botClient.SendMessage(chatId, "❌ Usage: <code>/add_sub &lt;userId&gt; &lt;days&gt;</code>", parseMode: ParseMode.Html, cancellationToken: ct);
            return;
        }

        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DBContext>();
        
        var user = await dbContext.TelegramSubscribers.FindAsync(new object[] { targetId }, ct);
        if (user == null)
        {
            user = new TelegramSubscriber { TelegramId = targetId, SubscriptionExpiryUtc = DateTime.UtcNow.AddDays(days) };
            dbContext.TelegramSubscribers.Add(user);
        }
        else
        {
            user.SubscriptionExpiryUtc = (user.SubscriptionExpiryUtc > DateTime.UtcNow ? user.SubscriptionExpiryUtc : DateTime.UtcNow).AddDays(days);
        }

        await dbContext.SaveChangesAsync(ct);
        await _botClient.SendMessage(chatId, $"✅ <b>Subscription Updated</b>\nUser: <code>{targetId}</code>\nNew Expiry: <code>{user.SubscriptionExpiryUtc:yyyy-MM-dd}</code>", parseMode: ParseMode.Html, cancellationToken: ct);
        
        // Notify the user with full Ghost Node instructions
        try 
        { 
            var image = "rahul09099/apihunter-worker:latest";
            var masterUrl = Environment.GetEnvironmentVariable("RENDER_EXTERNAL_URL") 
                           ?? Environment.GetEnvironmentVariable("MASTER_API_URL")
                           ?? "https://your-bot.onrender.com";

            var msg = new StringBuilder();
            msg.AppendLine("🎊 <b>WELCOME TO THE NETWORK!</b>");
            msg.AppendLine($"Your subscription is active until: <code>{user.SubscriptionExpiryUtc:yyyy-MM-dd}</code>");
            msg.AppendLine("⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯");
            msg.AppendLine("🚀 <b>PROPER RENDER SETUP GUIDE:</b>");
            msg.AppendLine();
            msg.AppendLine("1️⃣ <b>Create a Web Service</b> on Render.");
            msg.AppendLine($"2️⃣ <b>Image URL:</b> <code>{image}</code>");
            msg.AppendLine("3️⃣ <b>Runtime:</b> Docker");
            msg.AppendLine();
            msg.AppendLine("⚙️ <b>REQUIRED ENVIRONMENT VARIABLES:</b>");
            msg.AppendLine($"• <code>IS_WORKER_MODE</code> = <code>true</code>");
            msg.AppendLine($"• <code>MASTER_API_URL</code> = <code>{masterUrl}</code>");
            msg.AppendLine($"• <code>NODE_TOKEN</code> = <code>{user.NodeToken ?? "[Click /node_token to generate]"}</code>");
            msg.AppendLine($"• <code>PORT</code> = <code>10000</code>");
            msg.AppendLine();
            msg.AppendLine("🔑 <b>Access Key:</b> Click /node_token to view or generate your secure key.");
            if (string.IsNullOrEmpty(user.NodeToken))
            {
                msg.AppendLine("<i>(New users: you must run the command above first to create your key!)</i>");
            }
            msg.AppendLine();
            msg.AppendLine("🏁 <b>Finish:</b> Deploy and check /node_status.");
            msg.AppendLine();
            msg.AppendLine("<i>Need help? Check the detailed Subscriber Guide in the repository!</i>");

            await _botClient.SendMessage(targetId, msg.ToString(), parseMode: ParseMode.Html, cancellationToken: ct); 
        } 
        catch { }
    }

    private async Task HandleRemoveSubCommand(long chatId, string arg, CancellationToken ct)
    {
        if (!long.TryParse(arg, out long targetId))
        {
            await _botClient.SendMessage(chatId, "❌ Usage: <code>/remove_sub &lt;userId&gt;</code>", parseMode: ParseMode.Html, cancellationToken: ct);
            return;
        }

        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DBContext>();
        var user = await dbContext.TelegramSubscribers.FindAsync(new object[] { targetId }, ct);
        
        if (user != null)
        {
            user.SubscriptionExpiryUtc = DateTime.UtcNow.AddMinutes(-1);
            await dbContext.SaveChangesAsync(ct);
            await _botClient.SendMessage(chatId, $"✅ Access revoked for <code>{targetId}</code>.", parseMode: ParseMode.Html, cancellationToken: ct);
        }
        else await _botClient.SendMessage(chatId, "❌ User not found.", cancellationToken: ct);
    }

    private async Task HandleListSubsCommand(long chatId, CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DBContext>();
        var subs = await dbContext.TelegramSubscribers.ToListAsync(ct);

        if (!subs.Any())
        {
            await _botClient.SendMessage(chatId, "No subscribers found.", cancellationToken: ct);
            return;
        }

        var sb = new StringBuilder("<b>📋 REGISTERED USERS</b>\n⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯\n");
        foreach (var s in subs)
        {
            var status = s.SubscriptionExpiryUtc > DateTime.UtcNow ? "🟢" : "🔴";
            var nameStr = !string.IsNullOrEmpty(s.Username) ? $" (@{s.Username})" : "";
            sb.AppendLine($"{status} <code>{s.TelegramId}</code>{nameStr} - {(s.IsAdmin ? "Admin" : "Sub")} (Ends: {s.SubscriptionExpiryUtc:MM/dd})");
        }
        await _botClient.SendMessage(chatId, sb.ToString(), parseMode: ParseMode.Html, cancellationToken: ct);
    }

    private async Task HandleSetAdminCommand(long chatId, string args, CancellationToken ct)
    {
        var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            await _botClient.SendMessage(chatId, "❌ Usage: <code>/set_admin &lt;id1,id2...&gt; &lt;true|false&gt;</code>", parseMode: ParseMode.Html, cancellationToken: ct);
            return;
        }

        var idsStr = parts[0].Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (!bool.TryParse(parts[1], out bool isAdmin))
        {
            await _botClient.SendMessage(chatId, "❌ Invalid boolean value. Use <code>true</code> or <code>false</code>.", parseMode: ParseMode.Html, cancellationToken: ct);
            return;
        }

        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DBContext>();
        
        var results = new StringBuilder($"<b>Admin Update Results:</b>\n");
        foreach (var idStr in idsStr)
        {
            if (long.TryParse(idStr.Trim(), out long targetId))
            {
                var user = await dbContext.TelegramSubscribers.FindAsync(new object[] { targetId }, ct);
                if (user != null)
                {
                    user.IsAdmin = isAdmin;
                    results.AppendLine($"- <code>{targetId}</code>: {(isAdmin ? "✅ Promoted" : "❌ Demoted")}");
                }
                else results.AppendLine($"- <code>{targetId}</code>: ⚠️ Not found");
            }
            else results.AppendLine($"- <code>{idStr}</code>: ❌ Invalid ID");
        }

        await dbContext.SaveChangesAsync(ct);
        await _botClient.SendMessage(chatId, results.ToString(), parseMode: ParseMode.Html, cancellationToken: ct);
    }

    private async Task HandleListAdminsCommand(long chatId, CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DBContext>();
        var admins = await dbContext.TelegramSubscribers.Where(s => s.IsAdmin).ToListAsync(ct);

        var sb = new StringBuilder("<b>🛡️ COMMAND STAFF (ADMINS)</b>\n⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯\n");
        sb.AppendLine($"👑 Super Admin: <code>{_adminChatId}</code>");
        
        foreach (var admin in admins)
        {
            if (admin.TelegramId == _adminChatId) continue;
            sb.AppendLine($"👤 Admin: <code>{admin.TelegramId}</code>");
        }
        
        await _botClient.SendMessage(chatId, sb.ToString(), parseMode: ParseMode.Html, cancellationToken: ct);
    }

    private async Task HandleNodeTokenCommand(long chatId, CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DBContext>();
        
        var user = await dbContext.TelegramSubscribers.FindAsync(new object[] { chatId }, ct);
        
        // If user is missing but is the super admin, auto-register them
        if (user == null)
        {
            if (chatId == _adminChatId)
            {
                user = new TelegramSubscriber 
                { 
                    TelegramId = chatId, 
                    IsAdmin = true, 
                    CreatedAtUtc = DateTime.UtcNow, 
                    SubscriptionExpiryUtc = DateTime.UtcNow.AddYears(99) 
                };
                dbContext.TelegramSubscribers.Add(user);
                await dbContext.SaveChangesAsync(ct);
            }
            else
            {
                await _botClient.SendMessage(chatId, "❌ User record not found. Please contact an administrator.", cancellationToken: ct);
                return;
            }
        }

        if (string.IsNullOrEmpty(user.NodeToken))
        {
            user.NodeToken = Guid.NewGuid().ToString("N");
            await dbContext.SaveChangesAsync(ct);
        }

        var sb = new StringBuilder();
        sb.AppendLine("<b>👻 GHOST NODE SECURITY</b>");
        sb.AppendLine("⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯");
        sb.AppendLine("Your private access token for distributed worker nodes:");
        sb.AppendLine();
        sb.AppendLine($"<code>{user.NodeToken}</code>");
        sb.AppendLine();
        sb.AppendLine("⚠️ <b>Keep this secret!</b> Use this token when deploying your personal instance on Render.");

        await _botClient.SendMessage(chatId, sb.ToString(), parseMode: ParseMode.Html, cancellationToken: ct);
    }

    private async Task HandleMasterUrlCommand(long chatId, CancellationToken ct)
    {
        var masterUrl = Environment.GetEnvironmentVariable("RENDER_EXTERNAL_URL") 
                      ?? Environment.GetEnvironmentVariable("MASTER_API_URL")
                      ?? "https://your-app.onrender.com";

        var sb = new StringBuilder();
        sb.AppendLine("<b>📡 MASTER API ENDPOINT</b>");
        sb.AppendLine("⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯");
        sb.AppendLine("Connect your worker node to this address:");
        sb.AppendLine();
        sb.AppendLine($"<code>{masterUrl}</code>");

        await _botClient.SendMessage(chatId, sb.ToString(), parseMode: ParseMode.Html, cancellationToken: ct);
    }

    private async Task HandleNodeStatusCommand(long chatId, bool isAdmin, CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DBContext>();
        
        var query = dbContext.TelegramSubscribers.Where(s => s.NodeToken != null);

        // Filter by user if not admin
        if (!isAdmin)
        {
            query = query.Where(s => s.TelegramId == chatId);
        }

        var nodes = await query
            .OrderByDescending(s => s.LastNodeHeartbeatUtc)
            .ToListAsync(ct);

        if (!nodes.Any())
        {
            await _botClient.SendMessage(chatId, isAdmin ? "No active worker nodes found in the system." : "You don't have an active worker node yet.", cancellationToken: ct);
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine(isAdmin ? "<b>🛰️ NETWORK TOPOLOGY</b>" : "<b>🛰️ YOUR NODE STATUS</b>");
        sb.AppendLine("⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯");

        foreach (var node in nodes)
        {
            string status;
            string lastSeen;

            if (node.TelegramId == _adminChatId)
            {
                status = "🟢 Online (Master)";
                lastSeen = "N/A (Local)";
            }
            else
            {
                status = node.LastNodeHeartbeatUtc.HasValue && (DateTime.UtcNow - node.LastNodeHeartbeatUtc.Value).TotalMinutes < 10
                    ? "🟢 Online"
                    : "🔴 Offline";
                    
                lastSeen = node.LastNodeHeartbeatUtc.HasValue 
                    ? node.LastNodeHeartbeatUtc.Value.ToString("g") 
                    : "Never";
            }

            var displayName = !string.IsNullOrEmpty(node.Username) ? $"@{node.Username} (<code>{node.TelegramId}</code>)" : $"<code>{node.TelegramId}</code>";
            sb.AppendLine($"<b>Node:</b> {displayName}");
            sb.AppendLine($"<b>Status:</b> {status}");
            sb.AppendLine($"<b>Last Heartbeat:</b> {lastSeen}");
            sb.AppendLine("⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯");
        }

        await _botClient.SendMessage(chatId, sb.ToString(), parseMode: ParseMode.Html, cancellationToken: ct);
    }

    private async Task HandlePurgeJunkCommand(long chatId, CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DBContext>();
        var dbService = scope.ServiceProvider.GetRequiredService<DatabaseService>();
        
        await _botClient.SendMessage(chatId, "⏳ Purging junk repository references for invalid keys...", cancellationToken: ct);
        var count = await dbService.PurgeJunkSourcesAsync(dbContext);
        await _botClient.SendMessage(chatId, $"✅ Purged {count} junk references. Database space reclaimed.", cancellationToken: ct);
    }

    #endregion
}

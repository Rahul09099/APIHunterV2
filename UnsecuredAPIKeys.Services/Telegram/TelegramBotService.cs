using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
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

        var isWorkerMode = string.Equals(Environment.GetEnvironmentVariable("IS_WORKER_MODE"), "true", StringComparison.OrdinalIgnoreCase);
        if (isWorkerMode)
        {
            _logger.LogInformation("IS_WORKER_MODE is true. Telegram Bot long-polling disabled on worker node.");
            return;
        }

        _logger.LogInformation("Telegram Bot Service is starting on Master node...");

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
            new BotCommand { Command = "dashboard", Description = "Open Visual Dashboard" },
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

        // Anti-Sleep Loop (for Render Free Tier)
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var myUrl = Environment.GetEnvironmentVariable("RENDER_EXTERNAL_URL");
                using var scope = _serviceProvider.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<DBContext>();
                var httpClientFactory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();
                using var client = httpClientFactory.CreateClient();

                // 1. Ping Master Node
                if (!string.IsNullOrEmpty(myUrl))
                {
                    try
                    {
                        await client.GetAsync(myUrl, stoppingToken);
                        _logger.LogInformation("Self-ping sent to Master: {Url}", myUrl);
                    }
                    catch (Exception ex) { _logger.LogWarning("Master self-ping failed: {Msg}", ex.Message); }
                }

                // 2. Ping Worker Nodes (Ghost Nodes)
                var workerUrls = await dbContext.TelegramSubscribers
                    .Where(s => s.NodeUrl != null && s.LastNodeHeartbeatUtc > DateTime.UtcNow.AddHours(-24))
                    .Select(s => s.NodeUrl)
                    .Distinct()
                    .ToListAsync(stoppingToken);

                foreach (var url in workerUrls)
                {
                    if (string.IsNullOrEmpty(url)) continue;
                    try
                    {
                        await client.GetAsync(url, stoppingToken);
                        _logger.LogInformation("Anti-sleep ping sent to Worker: {Url}", url);
                    }
                    catch (Exception ex) { _logger.LogWarning("Worker ping failed for {Url}: {Msg}", url, ex.Message); }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Global anti-sleep loop error: {Msg}", ex.Message);
            }

            // Wait 14 minutes before next cycle
            await Task.Delay(TimeSpan.FromMinutes(14), stoppingToken);
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
        var user = await dbContext.TelegramSubscribers.FindAsync(chatId, cancellationToken);
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
            await botClient.SendChatAction(chatId, ChatAction.Typing, cancellationToken: cancellationToken);
            await HandleCommand(chatId, messageText, user, isAdmin, isSuperAdmin, cancellationToken);
        }
        else if (callbackData != null)
        {
            _logger.LogInformation("Received Telegram callback: '{Data}' from '{ChatId}'", callbackData, chatId);
            await botClient.SendChatAction(chatId, ChatAction.Typing, cancellationToken: cancellationToken);
            await HandleCallback(chatId, callbackData, update.CallbackQuery!.Id, isAdmin, update.CallbackQuery.Message?.MessageId, cancellationToken);
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
                case "/dashboard":
                    await HandleDashboardCommand(chatId, cancellationToken);
                    break;
                case "/status":
                    await HandleStatusCommand(chatId, isAdmin, cancellationToken);
                    break;
                case "/start_scraper":
                    await HandleStartScraperCommand(chatId, cancellationToken);
                    break;
                case "/stats":
                    await HandleStatsCommand(chatId, isAdmin, cancellationToken);
                    break;
                case "/stop_scraper":
                    await HandleStopJobCommand(chatId, args, "Scraper", isAdmin, cancellationToken);
                    break;
                case "/scraper_jobs":
                    await HandleListJobsCommand(chatId, "Scraper", isAdmin, cancellationToken);
                    break;
                case "/start_verifier":
                    await HandleStartVerifierCommand(chatId, args, cancellationToken);
                    break;
                case "/stop_verifier":
                    await HandleStopJobCommand(chatId, args, "Verifier", isAdmin, cancellationToken);
                    break;
                case "/verifier_jobs":
                    await HandleListJobsCommand(chatId, "Verifier", isAdmin, cancellationToken);
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
                case "/export_creds":
                    await HandleExportCredsCommand(chatId, args, isAdmin, cancellationToken);
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
                case "/set_deploy_hook":
                    await HandleSetDeployHookCommand(chatId, args, cancellationToken);
                    break;
                case "/remove_deploy_hook":
                    await HandleRemoveDeployHookCommand(chatId, cancellationToken);
                    break;
                case "/redeploy_node":
                    await HandleRedeployNodeCommand(chatId, cancellationToken);
                    break;
                case "/redeploy_all":
                case "/deploy_workers":
                    if (isAdmin) await HandleRedeployAllCommand(chatId, cancellationToken);
                    break;
                case "/purge":
                    await HandlePurgeCommand(chatId, isAdmin, cancellationToken);
                    break;
                case "/user_dash":
                    if (isAdmin)
                    {
                        if (long.TryParse(args, out long targetId))
                            await HandleUserDashCommand(chatId, targetId, cancellationToken);
                        else
                            await _botClient.SendMessage(chatId, "❌ Usage: <code>/user_dash &lt;telegram_id&gt;</code>", parseMode: ParseMode.Html, cancellationToken: cancellationToken);
                    }
                    break;
                case "/manage_tokens":
                    if (isAdmin)
                    {
                        if (long.TryParse(args, out long tokenTargetId))
                            await HandleAdminManageTokensCommand(chatId, tokenTargetId, cancellationToken);
                        else
                            await _botClient.SendMessage(chatId, "❌ Usage: <code>/manage_tokens &lt;telegram_id&gt;</code>", parseMode: ParseMode.Html, cancellationToken: cancellationToken);
                    }
                    break;
                case "/add_token_for":
                    if (isAdmin)
                    {
                        // /add_token_for <userId> <token>
                        var atfParts = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                        if (atfParts.Length == 2 && long.TryParse(atfParts[0], out long atfUserId))
                            await HandleAdminAddTokenForUserCommand(chatId, atfUserId, atfParts[1], cancellationToken);
                        else
                            await _botClient.SendMessage(chatId, "❌ Usage: <code>/add_token_for &lt;userId&gt; &lt;token&gt;</code>", parseMode: ParseMode.Html, cancellationToken: cancellationToken);
                    }
                    break;
                case "/broadcast":
                    if (isAdmin && !string.IsNullOrWhiteSpace(args))
                        await HandleBroadcastCommand(chatId, args, cancellationToken);
                    else if (isAdmin)
                        await _botClient.SendMessage(chatId, "❌ Usage: <code>/broadcast &lt;message&gt;</code>", parseMode: ParseMode.Html, cancellationToken: cancellationToken);
                    break;
                case "/stop_all":
                    if (isAdmin) await HandleStopAllJobsCommand(chatId, cancellationToken);
                    break;
                case "/partition_status":
                    if (isAdmin) await HandlePartitionStatusCommand(chatId, cancellationToken);
                    break;
                case "/vacuum":
                    if (isAdmin) await HandleVacuumCommand(chatId, cancellationToken);
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

    private async Task HandleCallback(long chatId, string callbackData, string queryId, bool isAdmin, int? messageId, CancellationToken cancellationToken)
    {
        try
        {
            // Immediate feedback for the user
            await _botClient.AnswerCallbackQuery(queryId, "Processing...", cancellationToken: cancellationToken);
 
            if (callbackData.StartsWith("scrape_group:"))
            {
                var parts = callbackData.Split(':');
                if (parts.Length == 3)
                {
                    var groupName = parts[1];
                    var mode = parts[2];
                    var isDeep = mode == "deep";
 
                    var jobName = $"Scraper-{groupName}";
                    var isAlreadyRunning = _jobManager.GetAllJobs().Any(j => j.JobType == jobName && j.Status == "Running");
                    if (isAlreadyRunning)
                    {
                        await _botClient.AnswerCallbackQuery(queryId, $"⚠️ Scraper for {groupName} is already running!", cancellationToken: cancellationToken);
                        return;
                    }
 
                    var jobId = _jobManager.StartJob(jobName, async (ct) =>
                    {
                        using var scope = _serviceProvider.CreateScope();
                        var dbContext = scope.ServiceProvider.GetRequiredService<DBContext>();
                        var dbContextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<DBContext>>();
                        var httpClientFactory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();
                        var scraper = new ScraperService(dbContext, dbContextFactory, httpClientFactory);
                        await scraper.RunScrapeByGroupAsync(groupName, isDeep, chatId, ct);
                    }, chatId);
 
                    await _botClient.SendMessage(chatId, $"🚀 Scraper started for *{groupName}* ({mode} mode)!\nJob ID: `{jobId}`", parseMode: ParseMode.Markdown, cancellationToken: cancellationToken);
                }
            }
            else if (callbackData == "status_refresh")
            {
                await HandleStatusCommand(chatId, isAdmin, cancellationToken);
            }
            else if (callbackData == "jobs_list")
            {
                var jobs = _jobManager.GetAllJobs().Where(j => j.Status == "Running" && (isAdmin || j.OwnerTelegramId == chatId)).ToList();
                if (!jobs.Any())
                {
                    await _botClient.SendMessage(chatId, "No active jobs running.", cancellationToken: cancellationToken);
                }
                else
                {
                    // Pre-fetch owner info if admin
                    var ownerMap = new Dictionary<long, string?>();
                    if (isAdmin)
                    {
                        var ownerIds = jobs.Select(j => j.OwnerTelegramId).Where(id => id.HasValue).Cast<long>().Distinct().ToList();
                        if (ownerIds.Any())
                        {
                            using var scope = _serviceProvider.CreateScope();
                            var dbContext = scope.ServiceProvider.GetRequiredService<DBContext>();
                            ownerMap = await dbContext.TelegramSubscribers
                                .Where(s => ownerIds.Contains(s.TelegramId))
                                .ToDictionaryAsync(s => s.TelegramId, s => s.Username, cancellationToken);
                        }
                    }

                    var sb = new StringBuilder("<b>🏃 ACTIVE DEPLOYMENTS</b>\n⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯\n");
                    foreach (var job in jobs)
                    {
                        string ownerStr = "";
                        if (isAdmin && job.OwnerTelegramId.HasValue)
                        {
                            var identity = ownerMap.TryGetValue(job.OwnerTelegramId.Value, out var uname) && !string.IsNullOrEmpty(uname) 
                                ? $"(@{uname})" 
                                : $"(ID: {job.OwnerTelegramId})";
                            ownerStr = $" | {identity}";
                        }
                        sb.AppendLine($"▸ {job.JobType}: <code>{job.JobId}</code>{ownerStr}");
                    }
                    await _botClient.SendMessage(chatId, sb.ToString(), parseMode: ParseMode.Html, cancellationToken: cancellationToken);
                }
            }
            else if (callbackData == "purge_junk" || callbackData == "purge")
            {
                if (isAdmin)
                {
                    await HandlePurgeCommand(chatId, true, cancellationToken);
                }
            }
            else if (callbackData == "admin_list_subs")
            {
                if (isAdmin) await HandleListSubsCommand(chatId, cancellationToken);
            }
            else if (callbackData.StartsWith("user_dash:"))
            {
                if (isAdmin)
                {
                    var userId = long.Parse(callbackData.Split(':')[1]);
                    await HandleUserDashCommand(chatId, userId, cancellationToken);
                }
            }
            else if (callbackData.StartsWith("admin_scrape:"))
            {
                if (isAdmin)
                {
                    var userId = long.Parse(callbackData.Split(':')[1]);
                    await HandleAdminStartScraperCommand(chatId, userId, cancellationToken);
                }
            }
            else if (callbackData.StartsWith("admin_run_scrape:"))
            {
                if (isAdmin)
                {
                    var parts = callbackData.Split(':');
                    var userId = long.Parse(parts[1]);
                    var group = parts[2];
                    var mode = parts[3];
                    var isDeep = mode == "deep";

                    _jobManager.StartJob($"Scraper-{group} (@{userId})", async (ct) =>
                    {
                        using var scope = _serviceProvider.CreateScope();
                        var dbContext = scope.ServiceProvider.GetRequiredService<DBContext>();
                        var dbContextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<DBContext>>();
                        var httpClientFactory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();
                        var scraper = new ScraperService(dbContext, dbContextFactory, httpClientFactory);
                        await scraper.RunScrapeByGroupAsync(group, isDeep, chatId, ct);
                    }, userId);

                    await _botClient.SendMessage(chatId, $"🚀 [Admin] Scraper started for user <code>{userId}</code> on group <b>{group}</b>.", parseMode: ParseMode.Html, cancellationToken: cancellationToken);
                }
            }
            else if (callbackData.StartsWith("admin_sub_manage:"))
            {
                if (isAdmin)
                {
                    var userId = long.Parse(callbackData.Split(':')[1]);
                    await HandleAdminSubManageCommand(chatId, userId, cancellationToken);
                }
            }
            else if (callbackData.StartsWith("admin_manage_tokens:"))
            {
                if (isAdmin)
                {
                    var userId = long.Parse(callbackData.Split(':')[1]);
                    await HandleAdminManageTokensCommand(chatId, userId, cancellationToken);
                }
            }            else if (callbackData.StartsWith("admin_sub_ext:"))
            {
                if (isAdmin)
                {
                    var parts = callbackData.Split(':');
                    var userId = long.Parse(parts[1]);
                    var days = int.Parse(parts[2]);

                    using var scope = _serviceProvider.CreateScope();
                    var dbContext = scope.ServiceProvider.GetRequiredService<DBContext>();
                    var user = await dbContext.TelegramSubscribers.FindAsync(userId, cancellationToken);
                    if (user != null)
                    {
                        if (user.SubscriptionExpiryUtc < DateTime.UtcNow)
                            user.SubscriptionExpiryUtc = DateTime.UtcNow.AddDays(days);
                        else
                            user.SubscriptionExpiryUtc = user.SubscriptionExpiryUtc.AddDays(days);

                        await dbContext.SaveChangesAsync(cancellationToken);
                        await _botClient.AnswerCallbackQuery(queryId, "✅ Subscription extended!", cancellationToken: cancellationToken);
                    }
                }
            }
            else if (callbackData.StartsWith("admin_stats:"))
            {
                if (isAdmin)
                {
                    var userId = long.Parse(callbackData.Split(':')[1]);
                    await HandleStatsCommand(chatId, true, cancellationToken, userId); 
                }
            }
            else if (callbackData.StartsWith("admin_tokens:"))
            {
                if (isAdmin)
                {
                    var userId = long.Parse(callbackData.Split(':')[1]);
                    await HandleListTokensCommand(chatId, true, cancellationToken, userId);
                }
            }
            else if (callbackData.StartsWith("admin_export:"))
            {
                if (isAdmin)
                {
                    var userId = long.Parse(callbackData.Split(':')[1]);
                    await HandleExportCommand(chatId, "csv", true, cancellationToken, userId); 
                }
            }
            else if (callbackData.StartsWith("scrape_page:"))
            {
                var page = int.Parse(callbackData.Split(':')[1]);
                await HandleStartScraperCommand(chatId, cancellationToken, page, messageId);
            }
            else if (callbackData == "scrape_all")
            {
                var isAlreadyRunning = _jobManager.GetAllJobs().Any(j => j.JobType == "AutoScraper-All" && j.Status == "Running");
                if (isAlreadyRunning)
                {
                    await _botClient.AnswerCallbackQuery(queryId, "⚠️ Comprehensive scan is already running!", cancellationToken: cancellationToken);
                    return;
                }
 
                var jobId = _jobManager.StartJob("AutoScraper-All", async (ct) =>
                {
                    using var scope = _serviceProvider.CreateScope();
                    var dbContext = scope.ServiceProvider.GetRequiredService<DBContext>();
                    var dbContextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<DBContext>>();
                    var httpClientFactory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();
                    var scraper = new ScraperService(dbContext, dbContextFactory, httpClientFactory);
                    await scraper.RunScrapeAllGroupsAsync(chatId, ct);
                }, chatId);

                await _botClient.SendMessage(chatId, $"🚀 <b>Comprehensive Scan Started!</b>\n⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯\nAll groups will be searched sequentially in Lite mode.\nJob ID: <code>{jobId.Substring(0, 8)}</code>", parseMode: ParseMode.Html, cancellationToken: cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling Telegram callback");
            await _botClient.SendMessage(chatId, $"❌ Error: {ex.Message}", cancellationToken: cancellationToken);
        }
    }

    private async Task HandlePollingErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is global::Telegram.Bot.Exceptions.ApiRequestException apiEx && apiEx.ErrorCode == 409)
        {
            _logger.LogWarning("Telegram Bot 409 Conflict: Another instance is polling getUpdates with the same bot token. Retrying in 10 seconds...");
            await Task.Delay(10000, cancellationToken);
            return;
        }

        _logger.LogError(exception, "Telegram Bot API Polling Error");
    }

    #region Command Handlers

    private async Task HandleHelpCommand(long chatId, bool isAdmin, CancellationToken ct)
    {
        var help = new StringBuilder();
        help.AppendLine("<b>🤖 UnsecuredAPIKeys Bot Commands</b>");
        help.AppendLine("⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯");
        help.AppendLine("📊 <b>General</b>");
        help.AppendLine("├ /status - Overall status &amp; active jobs");
        help.AppendLine("├ /dashboard - Launch visual control center");
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
        help.AppendLine("├ /delete_token &lt;id&gt; - Delete your token");
        help.AppendLine("├ /node_status - View your node status");
        help.AppendLine("├ /set_deploy_hook &lt;url&gt; - Save Render Deploy Hook");
        help.AppendLine("├ /remove_deploy_hook - Clear Deploy Hook");
        help.AppendLine("└ /redeploy_node - Deploy your worker from Telegram");

        if (isAdmin)
        {
            help.AppendLine();
            help.AppendLine("👤 <b>Admin Management</b>");
            help.AppendLine("├ /add_sub &lt;id&gt; &lt;days&gt; - Add subscriber");
            help.AppendLine("├ /remove_sub &lt;id&gt; - Remove access");
            help.AppendLine("├ /list_subs - List all subscribers");
            help.AppendLine("├ /admins - List all admins");
            help.AppendLine("├ /set_admin &lt;id&gt; &lt;true/false&gt; - Toggle admin");
            help.AppendLine("├ /manage_tokens &lt;id&gt; - Manage user's GitHub tokens");
            help.AppendLine("├ /add_token_for &lt;id&gt; &lt;token&gt; - Add token for user");
            help.AppendLine("├ /broadcast &lt;msg&gt; - Message all subscribers");
            help.AppendLine("├ /stop_all - Kill all running jobs");
            help.AppendLine("├ /redeploy_all - Redeploy all subscriber nodes");
            help.AppendLine("└ /partition_status - Node query distribution");
        }

        if (isAdmin)
        {
            help.AppendLine();
            help.AppendLine("⚙️ <b>Global Config</b>");
            help.AppendLine("├ /queries - List search queries");
            help.AppendLine("├ /add_query &lt;query&gt; - Add search query");
            help.AppendLine("├ /delete_query &lt;id&gt; - Delete search query");
            help.AppendLine("└ /toggle_query &lt;id&gt; - Toggle a query");

            help.AppendLine();
            help.AppendLine("🧹 <b>Database Optimization</b>");
            help.AppendLine("├ /purge - Clean junk source records");
            help.AppendLine("└ /vacuum - Full DB optimization (Safe mode)");
        }
 
        if (isAdmin)
        {
            help.AppendLine();
            help.AppendLine("💾 <b>Data</b>");
            help.AppendLine("├ /valid_keys - Count of valid keys");
            help.AppendLine("├ /export [csv|json] [--validNoCredit] - Get keys file");
            help.AppendLine("├ /purge - Clean junk records (Master)");
            help.AppendLine("├ /purge_junk - Purge references for invalid keys (Optimization)");
            help.AppendLine("└ /reset_database CONFIRM_RESET - Wipe DB");
        }
 
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

    private async Task HandleDashboardCommand(long chatId, CancellationToken ct)
    {
        var masterUrl = Environment.GetEnvironmentVariable("RENDER_EXTERNAL_URL") 
                      ?? Environment.GetEnvironmentVariable("MASTER_API_URL")
                      ?? "https://unsecuredapikeys-api-aezg.onrender.com";
        masterUrl = masterUrl.TrimEnd('/');
        var dashboardUrl = $"{masterUrl}/dashboard";
        
        var inlineKeyboard = new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithWebApp("📊 Open Dashboard", new WebAppInfo { Url = dashboardUrl })
            }
        });

        await _botClient.SendMessage(
            chatId,
            "📊 <b>APIHunter Control Center</b>\nClick the button below to open the visual dashboard directly in Telegram.",
            parseMode: ParseMode.Html,
            replyMarkup: inlineKeyboard,
            cancellationToken: ct
        );
    }

    private async Task HandleStatusCommand(long chatId, bool isAdmin, CancellationToken ct)
    {
        var processingMsg = await _botClient.SendMessage(chatId, "⏳ <b>Accessing satellite link...</b>", parseMode: ParseMode.Html, cancellationToken: ct);

        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DBContext>();
        var dbService = scope.ServiceProvider.GetRequiredService<DatabaseService>();
        
        // Filter by chatId if not admin
        long? filterBy = isAdmin ? null : chatId;
        var stats = await dbService.GetCategorizedStatisticsAsync(dbContext, filterBy);

        // Filter active jobs: Admins see all, Users see only their own
        var activeJobs = _jobManager.GetAllJobs()
            .Where(j => j.Status == "Running" && (isAdmin || j.OwnerTelegramId == chatId))
            .ToList();

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
        sb.AppendLine($"<b>Total Keys:</b> <code>{stats.TotalKeys}</code>");
        sb.AppendLine($"<b>🟢 Valid:</b> <code>{stats.ValidKeys}</code>");
        sb.AppendLine($"<b>🔴 Invalid:</b> <code>{stats.InvalidKeys}</code>");
        sb.AppendLine($"<b>⏳ Hidden:</b> <code>{stats.UnverifiedKeys}</code>");
        sb.AppendLine();

        sb.AppendLine($"<b>🔑 Tokens:</b> <code>{stats.GitHubTokensCount} active</code>");
        sb.AppendLine($"<b>🏃 Jobs:</b> <code>{activeJobs.Count} running</code>");

        if (activeJobs.Any())
        {
            // Fetch owners for active jobs if admin
            var ownerMap = new Dictionary<long, string?>();
            if (isAdmin)
            {
                var ownerIds = activeJobs.Select(j => j.OwnerTelegramId).Where(id => id.HasValue).Cast<long>().Distinct().ToList();
                if (ownerIds.Any())
                {
                    ownerMap = await dbContext.TelegramSubscribers
                        .Where(s => ownerIds.Contains(s.TelegramId))
                        .ToDictionaryAsync(s => s.TelegramId, s => s.Username, ct);
                }
            }

            foreach (var job in activeJobs)
            {
                string ownerStr = "";
                if (isAdmin && job.OwnerTelegramId.HasValue)
                {
                    var identity = ownerMap.TryGetValue(job.OwnerTelegramId.Value, out var uname) && !string.IsNullOrEmpty(uname) 
                        ? $"(@{uname})" 
                        : $"(ID: {job.OwnerTelegramId})";
                    ownerStr = $" | {identity}";
                }
                sb.AppendLine($"- {job.JobType}: <code>{job.JobId}</code>{ownerStr}");
            }
        }
        else
        {
            sb.AppendLine("<i>No active deployments running</i>");
        }
        
        sb.AppendLine("⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯");
        sb.AppendLine($"<i>System time: {DateTime.UtcNow.ToIst():HH:mm} IST</i>");

        var keyboardButtons = new List<InlineKeyboardButton[]>
        {
            new [] { 
                InlineKeyboardButton.WithCallbackData("🔄 Refresh", "status_refresh"), 
                InlineKeyboardButton.WithCallbackData("📋 Active Jobs", "jobs_list") 
            }
        };


        var keyboard = new InlineKeyboardMarkup(keyboardButtons);
        await _botClient.EditMessageText(chatId, processingMsg.MessageId, sb.ToString(), parseMode: ParseMode.Html, replyMarkup: keyboard, cancellationToken: ct);
    }
    
    private string GetProgressBar(double percent)
    {
        const int totalBlocks = 10;
        int activeBlocks = (int)(percent * totalBlocks);
        return new string('█', activeBlocks) + new string('░', totalBlocks - activeBlocks);
    }

    private async Task HandleStatsCommand(long chatId, bool isAdmin, CancellationToken ct, long? targetUserId = null)
    {
        var processingMsg = await _botClient.SendMessage(chatId, "⏳ <b>Compiling provider metrics...</b>", parseMode: ParseMode.Html, cancellationToken: ct);

        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DBContext>();
        var dbService = scope.ServiceProvider.GetRequiredService<DatabaseService>();
        
        // Filter: Use targetUserId if provided (Admin mode), otherwise filter by chatId if not admin
        long? filterBy = targetUserId ?? (isAdmin ? null : chatId);
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

        await _botClient.EditMessageText(chatId, processingMsg.MessageId, sb.ToString(), parseMode: ParseMode.Html, cancellationToken: ct);
    }

    private async Task HandleStartScraperCommand(long chatId, CancellationToken ct, int page = 0, int? messageId = null)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DBContext>();
        var dbService = scope.ServiceProvider.GetRequiredService<DatabaseService>();
        var dbContextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<DBContext>>();
        var httpClientFactory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();
        var scraper = new ScraperService(dbContext, dbContextFactory, httpClientFactory);

        var allGroups = await scraper.GetAvailableGroupsAsync(ct);
        var stats = await dbService.GetCategorizedStatisticsAsync(dbContext);

        if (allGroups.Count == 0)
        {
            await _botClient.SendMessage(chatId, "⚠️ <b>System Offline</b>\n⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯\nNo targets to scrape. Please ensure you have:\n1. Enabled <b>Search Queries</b> (/queries)\n2. Enabled <b>GitHub Tokens</b> (/tokens)", parseMode: ParseMode.Html, cancellationToken: ct);
            return;
        }

        // Logic-based categorization — update this when adding new providers
        var reasoning = new[] { "OpenAI", "Anthropic", "Google", "DeepSeek", "xAI", "Cohere", "Groq", "Mistral AI", "Perplexity", "Cerebras" };
        var generation = new[] { "Runway", "Kling AI", "Pollo AI", "A2E AI", "Stability AI", "ElevenLabs" };
        var infra = new[] { "Together AI", "Fireworks AI", "Replicate", "Hugging Face", "PiAPI", "OpenRouter", "Voyage AI", "AWS Bedrock", "Azure OpenAI" };

        var categoryPages = new List<(string Name, string Icon, string[] Targets)>();
        categoryPages.Add(("Reasoning", "🟢", reasoning));
        categoryPages.Add(("Media Generation", "🟣", generation));
        categoryPages.Add(("Infrastructure", "🔵", infra));

        // Detect if there are "other" groups not in the lists
        var otherGroups = allGroups.Where(g => !reasoning.Contains(g) && !generation.Contains(g) && !infra.Contains(g)).ToArray();
        if (otherGroups.Any())
        {
            categoryPages.Add(("Others", "⚪", otherGroups));
        }

        var totalPages = categoryPages.Count;
        if (page < 0) page = 0;
        if (page >= totalPages) page = totalPages - 1;

        var currentPage = categoryPages[page];
        var pageGroups = allGroups.Where(g => currentPage.Targets.Contains(g)).OrderBy(g => Array.IndexOf(currentPage.Targets, g)).ToList();

        var hasTokens = await dbContext.SearchProviderTokens
            .AnyAsync(t => t.IsEnabled && t.SearchProvider == SearchProviderEnum.GitHub, ct);

        var sb = new StringBuilder();
        sb.AppendLine($"<b>📡 MISSION CONTROL: SCRAPER</b>");
        sb.AppendLine("⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯");

        if (!hasTokens)
        {
            sb.AppendLine("⚠️ <b>WARNING: No GitHub Tokens Configured!</b>");
            sb.AppendLine("<i>The scraper requires at least one active GitHub token to run.</i>");
            sb.AppendLine("👉 Add a token using <code>/add_token &lt;token&gt;</code>");
            sb.AppendLine("⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯");
        }

        sb.AppendLine("Select a target provider below to begin key discovery.");
        sb.AppendLine();

        var keyboardButtons = new List<InlineKeyboardButton[]>();

        // 1. Primary Action: Scrape All (Keep on every page as requested)
        if (page == 0)
        {
            keyboardButtons.Add(new[] { InlineKeyboardButton.WithCallbackData("🚀 RUN COMPREHENSIVE SCAN (ALL)", "scrape_all") });
            keyboardButtons.Add(new[] { InlineKeyboardButton.WithCallbackData("⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯", "noop") });
        }
        
        // Category Header with Stats
        string categoryTotal = "0";
        if (stats.Categories.TryGetValue(ApiCategoryEnum.AIAndLLM, out var aiStats))
        {
            // Map ApiTypeName to the short names used in our groupings
            var currentCategoryCounts = aiStats.ApiTypes
                .Where(t => {
                    var name = t.ApiTypeName;
                    var shortName = name.Replace("Claude", "").Replace("AI", "").Replace("ML", "").Trim();
                    // Handle special cases
                    if (name == "GoogleAI") shortName = "Google";
                    if (name == "XAI") shortName = "xAI";
                    
                    return currentPage.Targets.Any(target => target.Contains(shortName, StringComparison.OrdinalIgnoreCase));
                })
                .Sum(t => t.KeyCount);
            categoryTotal = currentCategoryCounts.ToString();
        }
        
        keyboardButtons.Add(new[] { InlineKeyboardButton.WithCallbackData($"⎯⎯⎯⎯ {currentPage.Icon} {currentPage.Name.ToUpper()} ({categoryTotal}) ⎯⎯⎯⎯", "noop") });

        foreach (var g in pageGroups)
        {
            keyboardButtons.Add(new[] { 
                InlineKeyboardButton.WithCallbackData($"⚡ {g}", $"scrape_group:{g}:lite"), 
                InlineKeyboardButton.WithCallbackData($"🔍 {g}", $"scrape_group:{g}:deep") 
            });
        }

        if (!pageGroups.Any())
        {
            keyboardButtons.Add(new[] { InlineKeyboardButton.WithCallbackData("<i>No active queries for this category</i>", "noop") });
        }

        // Navigation Row
        var navRow = new List<InlineKeyboardButton>();
        if (page > 0)
        {
            navRow.Add(InlineKeyboardButton.WithCallbackData("⬅️ Back", $"scrape_page:{page - 1}"));
        }
        
        navRow.Add(InlineKeyboardButton.WithCallbackData($"{page + 1} / {totalPages}", "noop"));

        if (page < totalPages - 1)
        {
            navRow.Add(InlineKeyboardButton.WithCallbackData("Next ➡️", $"scrape_page:{page + 1}"));
        }

        keyboardButtons.Add(navRow.ToArray());

        var keyboard = new InlineKeyboardMarkup(keyboardButtons);

        if (messageId.HasValue)
        {
            try {
                await _botClient.EditMessageText(chatId, messageId.Value, sb.ToString(), parseMode: ParseMode.Html, replyMarkup: keyboard, cancellationToken: ct);
            } catch { /* Ignore potential "message is not modified" errors */ }
        }
        else
        {
            await _botClient.SendMessage(chatId, sb.ToString(), parseMode: ParseMode.Html, replyMarkup: keyboard, cancellationToken: ct);
        }
    }

    // Callback query handler (part of the main handler in a real scenario, but simplified here)
    // For this demonstration, I'll implement a standalone method that could be called if the library supported it easily.
    // In a real implementation, I'd need to handle UpdateType.CallbackQuery in HandleUpdateAsync.

    private async Task HandleStartVerifierCommand(long chatId, string args, CancellationToken ct)
    {
        bool reVerifyOnly = false;
        HashSet<ApiTypeEnum>? selectedTypes = null;
        
        if (!string.IsNullOrWhiteSpace(args))
        {
            reVerifyOnly = args.Contains("--reverify", StringComparison.OrdinalIgnoreCase);
            
            var cleanedArgs = args.Replace("--reverify", "", StringComparison.OrdinalIgnoreCase);
            var parts = cleanedArgs.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0)
            {
                selectedTypes = new HashSet<ApiTypeEnum>();
                foreach (var typeName in parts)
                {
                    if (Enum.TryParse<ApiTypeEnum>(typeName.Trim(), true, out var apiType))
                        selectedTypes.Add(apiType);
                }
                if (selectedTypes.Count == 0) selectedTypes = null;
            }
        }

        var isAlreadyRunning = _jobManager.GetAllJobs().Any(j => j.JobType == "Verifier" && j.Status == "Running");
        if (isAlreadyRunning)
        {
            await _botClient.SendMessage(chatId, "⚠️ <b>Verifier is already running!</b>\nPlease wait for the current run to complete.", parseMode: ParseMode.Html, cancellationToken: ct);
            return;
        }

        var jobId = _jobManager.StartJob("Verifier", async (cancellationToken) =>
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<DBContext>();
            var dbContextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<DBContext>>();
            var httpClientFactory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();
            var verifier = new VerifierService(dbContext, dbContextFactory, httpClientFactory, selectedTypes, reVerifyOnly);
            await verifier.RunAsync(cancellationToken);
        }, chatId);

        var modeMsg = reVerifyOnly ? "Re-verification" : "Standard verification";
        await _botClient.SendMessage(chatId, $"✅ Verifier ({modeMsg}) started! Job ID: <code>{System.Net.WebUtility.HtmlEncode(jobId)}</code>", parseMode: ParseMode.Html, cancellationToken: ct);
    }

    private async Task HandleStopJobCommand(long chatId, string jobId, string type, bool isAdmin, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            var jobs = _jobManager.GetAllJobs().Where(j => j.JobType == type && j.Status == "Running" && (isAdmin || j.OwnerTelegramId == chatId)).ToList();
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

        var job = _jobManager.GetJobInfo(jobId);
        if (job == null || (!isAdmin && job.OwnerTelegramId != chatId))
        {
             await _botClient.SendMessage(chatId, "⚠️ Job not found or you don't have permission to stop it.", cancellationToken: ct);
             return;
        }

        var success = _jobManager.StopJob(jobId);
        if (success)
            await _botClient.SendMessage(chatId, $"⏹️ Stop requested for {type} job <code>{System.Net.WebUtility.HtmlEncode(jobId)}</code>.", parseMode: ParseMode.Html, cancellationToken: ct);
        else
            await _botClient.SendMessage(chatId, "⚠️ Job not found or already stopped.", cancellationToken: ct);
    }

    private async Task HandleListJobsCommand(long chatId, string type, bool isAdmin, CancellationToken ct)
    {
        var jobs = _jobManager.GetAllJobs().Where(j => j.JobType == type && (isAdmin || j.OwnerTelegramId == chatId)).ToList();
        if (jobs.Count == 0)
        {
            await _botClient.SendMessage(chatId, $"No {type} jobs found.", cancellationToken: ct);
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"<b>📋 Recent {type} Jobs:</b>");

        // Fetch owners if admin
        var ownerMap = new Dictionary<long, string?>();
        if (isAdmin)
        {
            var ownerIds = jobs.Select(j => j.OwnerTelegramId).Where(id => id.HasValue).Cast<long>().Distinct().ToList();
            if (ownerIds.Any())
            {
                using var scope = _serviceProvider.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<DBContext>();
                ownerMap = await dbContext.TelegramSubscribers
                    .Where(s => ownerIds.Contains(s.TelegramId))
                    .ToDictionaryAsync(s => s.TelegramId, s => s.Username, ct);
            }
        }

        foreach (var job in jobs.TakeLast(5))
        {
            var jobId = System.Net.WebUtility.HtmlEncode(job.JobId);
            string ownerStr = "";
            if (isAdmin && job.OwnerTelegramId.HasValue)
            {
                var identity = ownerMap.TryGetValue(job.OwnerTelegramId.Value, out var uname) && !string.IsNullOrEmpty(uname) 
                    ? $"(@{uname})" 
                    : $"(ID: {job.OwnerTelegramId})";
                ownerStr = $" | {identity}";
            }
            sb.AppendLine($"- <code>{jobId}</code>: {job.Status} (Started: {job.StartedAt:HH:mm}){ownerStr}");
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

    private async Task HandleListTokensCommand(long chatId, bool isAdmin, CancellationToken ct, long? targetUserId = null)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DBContext>();
        var dbService = scope.ServiceProvider.GetRequiredService<DatabaseService>();
        
        // Filter: Use targetUserId if provided (Admin mode), otherwise filter by chatId if not admin
        long? filterBy = targetUserId ?? (isAdmin ? null : chatId);
        var tokens = await dbService.GetGitHubTokensAsync(dbContext, filterBy);

        if (tokens.Count == 0)
        {
            await _botClient.SendMessage(chatId, "No GitHub tokens found for your account.", cancellationToken: ct);
            return;
        }

        var httpClientFactory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();
        var statusTasks = tokens.Select(async t =>
        {
            var status = await CheckTokenStatusAsync(t.Token, httpClientFactory);
            return (t.Id, Status: status);
        });
        var statuses = (await Task.WhenAll(statusTasks)).ToDictionary(x => x.Id, x => x.Status);

        var sb = new StringBuilder();
        sb.AppendLine("<b>🔑 GitHub Tokens:</b>");
        foreach (var t in tokens)
        {
            var preview = System.Net.WebUtility.HtmlEncode(t.Token);
            var owner = t.AddedByTelegramId.HasValue ? $" [Owner: {t.AddedByTelegramId}]" : " [System]";
            var status = statuses[t.Id];
            sb.AppendLine($"- ID: <code>{t.Id}</code> | {preview} | Enabled: {t.IsEnabled}{(isAdmin ? owner : "")}   <b>{status}</b>");
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

    private async Task HandleExportCommand(long chatId, string args, bool isAdmin, CancellationToken ct, long? targetUserId = null)
    {
        string fmt = (args != null && args.Contains("json", StringComparison.OrdinalIgnoreCase)) ? "json" : "csv";
        
        ApiStatusEnum? status = ApiStatusEnum.Valid;
        string statusLabel = "valid";
        
        if (args != null && args.Contains("--validNoCredit", StringComparison.OrdinalIgnoreCase))
        {
            status = ApiStatusEnum.ValidNoCredits;
            statusLabel = "Valid No Credits";
        }

        string fileName = $"export_{statusLabel.Replace(" ", "_").ToLower()}_{DateTime.Now:yyyyMMdd_HHmmss}.{fmt}";
        string filePath = Path.Combine(Path.GetTempPath(), fileName);

        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DBContext>();
        var dbService = scope.ServiceProvider.GetRequiredService<DatabaseService>();
        
        // Filter by chatId if not admin
        long? filterBy = targetUserId ?? (isAdmin ? null : chatId);
        await dbService.ExportKeysAsync(dbContext, filePath, fmt, status, filterBy);

        using var stream = System.IO.File.OpenRead(filePath);
        await _botClient.SendDocument(
            chatId: chatId,
            document: InputFile.FromStream(stream, fileName),
            caption: $"📂 Exported {statusLabel} keys in {fmt.ToUpper()} format.",
            cancellationToken: ct);

        try { System.IO.File.Delete(filePath); } catch { }
    }

    private async Task HandleExportCredsCommand(long chatId, string args, bool isAdmin, CancellationToken ct)
    {
        if (!isAdmin)
        {
            await _botClient.SendMessage(chatId, "❌ You must be an administrator to export server credentials.", cancellationToken: ct);
            return;
        }

        string fmt = (args != null && args.Contains("json", StringComparison.OrdinalIgnoreCase)) ? "json" : "csv";
        string fileName = $"server_credentials_{DateTime.Now:yyyyMMdd_HHmmss}.{fmt}";
        string filePath = Path.Combine(Path.GetTempPath(), fileName);

        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DBContext>();
        var dbService = scope.ServiceProvider.GetRequiredService<DatabaseService>();

        await dbService.ExportServerCredentialsAsync(dbContext, filePath, fmt);

        using var stream = System.IO.File.OpenRead(filePath);
        await _botClient.SendDocument(
            chatId: chatId,
            document: InputFile.FromStream(stream, fileName),
            caption: $"📂 Exported server credentials in {fmt.ToUpper()} format.",
            cancellationToken: ct);

        try { System.IO.File.Delete(filePath); } catch { }
    }

    private async Task HandleResetDatabaseCommand(long chatId, string arg, CancellationToken ct)
    {
        if (arg != "CONFIRM_RESET")
        {
            await _botClient.SendMessage(chatId, "⚠️ <b>CRITICAL ACTION</b>\n⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯\nThis will wipe all keys, queries, and subscribers. This cannot be undone.\n\nTo proceed, use: <code>/reset_database CONFIRM_RESET</code>", parseMode: ParseMode.Html, cancellationToken: ct);
            return;
        }

        var statusMsg = await _botClient.SendMessage(chatId, "💣 <b>Initiating thermal reset...</b>", parseMode: ParseMode.Html, cancellationToken: ct);

        using var scope = _serviceProvider.CreateScope();
        var dbService = scope.ServiceProvider.GetRequiredService<DatabaseService>();
        await dbService.ResetDatabaseAsync();

        await _botClient.EditMessageText(chatId, statusMsg.MessageId, "✅ <b>System Reset Complete.</b>\nDatabase has been wiped and re-initialized.", parseMode: ParseMode.Html, cancellationToken: ct);
    }

    private async Task HandleMySubCommand(long chatId, TelegramSubscriber? user, CancellationToken ct)
    {
        if (user == null && chatId == _adminChatId)
        {
            await _botClient.SendMessage(chatId, "<b>💎 Premium Status:</b> <code>Super Admin (Lifetime)</code>\n\n<b>📡 Ghost Node Controls:</b>\n• <code>/node_status</code> - Check live worker health\n• <code>/master_url</code> - Master connection address\n• <code>/node_token</code> - Your private access token", parseMode: ParseMode.Html, cancellationToken: ct);
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
        sb.AppendLine("<b>📡 Ghost Node Controls:</b>");
        sb.AppendLine("• <code>/node_status</code> - Check live worker health");
        sb.AppendLine("• <code>/master_url</code> - Master connection address");
        sb.AppendLine("• <code>/node_token</code> - Your private access token");
        sb.AppendLine("• <code>/set_deploy_hook</code> - Register one-click updates");
        sb.AppendLine("• <code>/redeploy_node</code> - Redeploy your worker");
        
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
        
        var user = await dbContext.TelegramSubscribers.FindAsync(targetId, ct);
        if (user == null)
        {
            user = new TelegramSubscriber 
            { 
                TelegramId = targetId, 
                SubscriptionExpiryUtc = DateTime.UtcNow.AddDays(days),
                NodeToken = Guid.NewGuid().ToString("N"),
                CreatedAtUtc = DateTime.UtcNow
            };
            dbContext.TelegramSubscribers.Add(user);
        }
        else
        {
            if (string.IsNullOrEmpty(user.NodeToken))
            {
                user.NodeToken = Guid.NewGuid().ToString("N");
            }
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
            msg.AppendLine("🚀 <b>QUICK SETUP GUIDE (RENDER / CLOUD):</b>");
            msg.AppendLine();
            msg.AppendLine("1️⃣ <b>Create Web Service</b> on Render (or Railway / Koyeb / VPS).");
            msg.AppendLine($"2️⃣ <b>Docker Image:</b> <code>{image}</code>");
            msg.AppendLine();
            msg.AppendLine("⚙️ <b>REQUIRED ENVIRONMENT VARIABLES:</b>");
            msg.AppendLine($"• <code>IS_WORKER_MODE</code> = <code>true</code>");
            msg.AppendLine($"• <code>MASTER_API_URL</code> = <code>{masterUrl}</code>");
            msg.AppendLine($"• <code>NODE_TOKEN</code> = <code>{user.NodeToken}</code>");
            msg.AppendLine($"• <code>PORT</code> = <code>10000</code>");
            msg.AppendLine();
            msg.AppendLine("🔄 <b>ONE-CLICK AUTO-UPDATES:</b>");
            msg.AppendLine("Copy your Render Deploy Hook from Settings and run:");
            msg.AppendLine("👉 <code>/set_deploy_hook &lt;your_url&gt;</code>");
            msg.AppendLine();
            msg.AppendLine("🏁 <b>Finish:</b> Deploy your service and check <code>/node_status</code> in the bot.");
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
        var user = await dbContext.TelegramSubscribers.FindAsync(targetId, ct);
        
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
        var processingMsg = await _botClient.SendMessage(chatId, "⏳ <b>Retrieving intelligence dossiers...</b>", parseMode: ParseMode.Html, cancellationToken: ct);

        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DBContext>();
        var subs = await dbContext.TelegramSubscribers.OrderByDescending(s => s.CreatedAtUtc).ToListAsync(ct);

        if (!subs.Any())
        {
            await _botClient.EditMessageText(chatId, processingMsg.MessageId, "No subscribers found.", cancellationToken: ct);
            return;
        }

        var sb = new StringBuilder("<b>📋 REGISTERED USERS MANAGEMENT</b>\n");
        sb.AppendLine("⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯");
        sb.AppendLine("Select a user to view full profile and remote actions:");
        
        var buttons = new List<InlineKeyboardButton[]>();
        foreach (var s in subs)
        {
            var status = s.SubscriptionExpiryUtc > DateTime.UtcNow ? "🟢" : "🔴";
            var nameStr = !string.IsNullOrEmpty(s.Username) ? $"@{s.Username}" : s.TelegramId.ToString();
            var label = $"{status} {nameStr} ({(s.IsAdmin ? "Admin" : "Sub")})";
            
            buttons.Add(new[] { InlineKeyboardButton.WithCallbackData(label, $"user_dash:{s.TelegramId}") });
        }

        var keyboard = new InlineKeyboardMarkup(buttons);
        await _botClient.EditMessageText(chatId, processingMsg.MessageId, sb.ToString(), parseMode: ParseMode.Html, replyMarkup: keyboard, cancellationToken: ct);
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
                var user = await dbContext.TelegramSubscribers.FindAsync(targetId, ct);
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
        
        var user = await dbContext.TelegramSubscribers.FindAsync(chatId, ct);
        
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
            var hookStatus = !string.IsNullOrEmpty(node.DeployHook) ? "✅ Configured" : "❌ Not Set";
            sb.AppendLine($"<b>Node:</b> {displayName}");
            sb.AppendLine($"<b>Status:</b> {status}");
            sb.AppendLine($"<b>Deploy Hook:</b> {hookStatus}");
            sb.AppendLine($"<b>Last Heartbeat:</b> {lastSeen}");
            sb.AppendLine("⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯");
        }

        if (isAdmin)
        {
            sb.AppendLine();
            sb.AppendLine("<i>Use /partition_status to see query distribution across nodes.</i>");
        }

        await _botClient.SendMessage(chatId, sb.ToString(), parseMode: ParseMode.Html, cancellationToken: ct);
    }

    private async Task HandleSetDeployHookCommand(long chatId, string args, CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DBContext>();

        var user = await dbContext.TelegramSubscribers.FindAsync(new object[] { chatId }, ct);
        if (user == null)
        {
            await _botClient.SendMessage(chatId, "❌ You must be registered as a subscriber/admin to use this command.", parseMode: ParseMode.Html, cancellationToken: ct);
            return;
        }

        if (string.IsNullOrWhiteSpace(args))
        {
            await _botClient.SendMessage(chatId, "❌ Usage: <code>/set_deploy_hook &lt;render_deploy_hook_url&gt;</code>\n\nExample:\n<code>/set_deploy_hook https://api.render.com/deploy/srv-xxxxxxxxxxxx?key=yyyyyyyyyyyy</code>", parseMode: ParseMode.Html, cancellationToken: ct);
            return;
        }

        var hookUrl = args.Trim();
        if (!Uri.TryCreate(hookUrl, UriKind.Absolute, out var uriResult) ||
            !(uriResult.Scheme == Uri.UriSchemeHttp || uriResult.Scheme == Uri.UriSchemeHttps))
        {
            await _botClient.SendMessage(chatId, "❌ <b>Invalid URL format!</b>\n\nYour Deploy Hook must be a valid HTTP or HTTPS URL.\n\n<b>Example (Render):</b>\n<code>https://api.render.com/deploy/srv-xxxxxxxxxxxx?key=yyyyyyyyyyyy</code>\n\n<b>Example (Railway / Koyeb / Custom):</b>\n<code>https://your-platform-webhook-url</code>", parseMode: ParseMode.Html, cancellationToken: ct);
            return;
        }

        user.DeployHook = hookUrl;
        await dbContext.SaveChangesAsync(ct);

        var sb = new StringBuilder();
        sb.AppendLine("✅ <b>Deploy Hook Saved Successfully!</b>");
        sb.AppendLine("⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯");
        sb.AppendLine("Your private node is now integrated with the bot's automated deployment engine.");
        sb.AppendLine();
        sb.AppendLine("💡 You can now trigger a direct deployment of your node from Telegram anytime using:");
        sb.AppendLine("👉 <code>/redeploy_node</code>");

        await _botClient.SendMessage(chatId, sb.ToString(), parseMode: ParseMode.Html, cancellationToken: ct);
    }

    private async Task HandleRemoveDeployHookCommand(long chatId, CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DBContext>();

        var user = await dbContext.TelegramSubscribers.FindAsync(new object[] { chatId }, ct);
        if (user == null)
        {
            await _botClient.SendMessage(chatId, "❌ You must be registered as a subscriber/admin to use this command.", parseMode: ParseMode.Html, cancellationToken: ct);
            return;
        }

        user.DeployHook = null;
        await dbContext.SaveChangesAsync(ct);

        await _botClient.SendMessage(chatId, "🗑️ <b>Render Deploy Hook has been removed successfully.</b>", parseMode: ParseMode.Html, cancellationToken: ct);
    }

    private async Task HandleRedeployNodeCommand(long chatId, CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DBContext>();

        var user = await dbContext.TelegramSubscribers.FindAsync(new object[] { chatId }, ct);
        if (user == null || string.IsNullOrEmpty(user.DeployHook))
        {
            var sb = new StringBuilder();
            sb.AppendLine("❌ <b>No Deploy Hook Registered!</b>");
            sb.AppendLine("⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯");
            sb.AppendLine("To trigger deployments from Telegram, you must first register your Render Deploy Hook:");
            sb.AppendLine();
            sb.AppendLine("<b>How to get your Deploy Hook URL:</b>");
            sb.AppendLine("1. Log in to your <b>Render Dashboard</b>.");
            sb.AppendLine("2. Select your Ghost Node <b>Web Service</b>.");
            sb.AppendLine("3. Click on the <b>Settings</b> tab in the sidebar.");
            sb.AppendLine("4. Scroll down to the <b>Deploy Hook</b> section.");
            sb.AppendLine("5. Copy the unique URL.");
            sb.AppendLine("6. Register it here using:");
            sb.AppendLine("<code>/set_deploy_hook &lt;your_copied_url&gt;</code>");

            await _botClient.SendMessage(chatId, sb.ToString(), parseMode: ParseMode.Html, cancellationToken: ct);
            return;
        }

        await _botClient.SendMessage(chatId, "📡 Sending trigger to Render...", parseMode: ParseMode.Html, cancellationToken: ct);

        try
        {
            var httpClientFactory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();
            using var httpClient = httpClientFactory.CreateClient();
            httpClient.Timeout = TimeSpan.FromSeconds(15);

            var response = await httpClient.GetAsync(user.DeployHook, ct);

            if (response.IsSuccessStatusCode)
            {
                var sb = new StringBuilder();
                sb.AppendLine("🚀 <b>Redeployment Triggered Successfully!</b>");
                sb.AppendLine("⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯");
                sb.AppendLine("Render has accepted the build request. It will now pull the latest Docker image (<code>rahul09099/apihunter-worker:latest</code>) and spin up your node.");
                sb.AppendLine();
                sb.AppendLine("📊 Check <code>/node_status</code> in about 1-2 minutes to verify your node is 🟢 <b>Online</b>.");

                await _botClient.SendMessage(chatId, sb.ToString(), parseMode: ParseMode.Html, cancellationToken: ct);
            }
            else
            {
                await _botClient.SendMessage(chatId, $"❌ <b>Redeployment trigger failed!</b>\n\nRender returned HTTP Status: <code>{(int)response.StatusCode} {response.ReasonPhrase}</code>.\n\nPlease verify your Deploy Hook URL is correct.", parseMode: ParseMode.Html, cancellationToken: ct);
            }
        }
        catch (Exception ex)
        {
            await _botClient.SendMessage(chatId, $"❌ <b>Redeployment trigger encountered an error!</b>\n\nError: <code>{ex.Message}</code>", parseMode: ParseMode.Html, cancellationToken: ct);
        }
    }

    private async Task HandleRedeployAllCommand(long chatId, CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DBContext>();

        // Find all active subscribers who have a registered deploy hook (excluding local Master)
        var activeNodesWithHooks = await dbContext.TelegramSubscribers
            .Where(s => s.NodeToken != null && s.DeployHook != null && s.TelegramId != _adminChatId)
            .ToListAsync(ct);

        if (!activeNodesWithHooks.Any())
        {
            await _botClient.SendMessage(chatId, "❌ <b>No worker nodes have registered Render Deploy Hooks in the database.</b>", parseMode: ParseMode.Html, cancellationToken: ct);
            return;
        }

        await _botClient.SendMessage(chatId, $"⚡ <b>Initiating mass redeployment of {activeNodesWithHooks.Count} worker nodes...</b>", parseMode: ParseMode.Html, cancellationToken: ct);

        var httpClientFactory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();
        var tasks = activeNodesWithHooks.Select(async node =>
        {
            try
            {
                using var httpClient = httpClientFactory.CreateClient();
                httpClient.Timeout = TimeSpan.FromSeconds(15);
                var response = await httpClient.GetAsync(node.DeployHook!, ct);
                return (node, Success: response.IsSuccessStatusCode, Detail: response.IsSuccessStatusCode ? "Triggered" : $"Failed (HTTP {(int)response.StatusCode})");
            }
            catch (Exception ex)
            {
                return (node, Success: false, Detail: $"Failed ({ex.Message})");
            }
        });

        var results = await Task.WhenAll(tasks);

        var sb = new StringBuilder();
        sb.AppendLine("🏁 <b>MASS DEPLOYMENT COMPLETE REPORT</b>");
        sb.AppendLine("⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯");

        int successCount = 0;
        int failCount = 0;

        foreach (var (node, success, detail) in results)
        {
            var displayName = !string.IsNullOrEmpty(node.Username) ? $"@{node.Username}" : $"User {node.TelegramId}";
            var statusIcon = success ? "🟢" : "🔴";
            
            if (success) successCount++;
            else failCount++;

            sb.AppendLine($"{statusIcon} <b>{displayName}</b>: <code>{detail}</code>");
        }

        sb.AppendLine("⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯");
        sb.AppendLine($"📊 <b>Total Nodes Triggered:</b> {successCount} succeeded, {failCount} failed.");

        await _botClient.SendMessage(chatId, sb.ToString(), parseMode: ParseMode.Html, cancellationToken: ct);
    }

    private async Task HandleUserDashCommand(long chatId, long targetUserId, CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DBContext>();
        var dbService = scope.ServiceProvider.GetRequiredService<DatabaseService>();
        
        var targetUser = await dbContext.TelegramSubscribers.FindAsync(targetUserId, ct);
        if (targetUser == null)
        {
            await _botClient.SendMessage(chatId, "❌ User not found.", cancellationToken: ct);
            return;
        }

        var stats = await dbService.GetCategorizedStatisticsAsync(dbContext, targetUserId);
        
        var nameStr = !string.IsNullOrEmpty(targetUser.Username) ? $"@{targetUser.Username}" : $"{targetUserId}";
        var sb = new StringBuilder();
        sb.AppendLine($"<b>👤 USER INSIGHT: {nameStr}</b>");
        sb.AppendLine("⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯");
        sb.AppendLine($"<b>ID:</b> <code>{targetUserId}</code>");
        sb.AppendLine($"<b>Status:</b> {(targetUser.SubscriptionExpiryUtc > DateTime.UtcNow ? "🟢 Active" : "🔴 Expired")}");
        sb.AppendLine($"<b>Expiry:</b> {targetUser.SubscriptionExpiryUtc.ToIst():yyyy-MM-dd HH:mm} IST");
        sb.AppendLine();
        sb.AppendLine($"<b>Total Found:</b> <code>{stats.TotalKeys}</code>");
        sb.AppendLine($"<b>Valid Keys:</b> <code>{stats.ValidKeys}</code>");
        sb.AppendLine($"<b>Active Tokens:</b> <code>{stats.GitHubTokensCount}</code>");
        sb.AppendLine($"<b>Deploy Hook:</b> {(!string.IsNullOrEmpty(targetUser.DeployHook) ? "✅ Configured" : "❌ Not Set")}");
        sb.AppendLine("⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯");
        sb.AppendLine("<i>[Admin Mode] Remote actions operate silently.</i>");

        var keyboard = new InlineKeyboardMarkup(new[]
        {
            new [] { InlineKeyboardButton.WithCallbackData("🔍 Start Scraper", $"admin_scrape:{targetUserId}"), InlineKeyboardButton.WithCallbackData("📊 View Stats", $"admin_stats:{targetUserId}") },
            new [] { InlineKeyboardButton.WithCallbackData("🔑 View Tokens", $"admin_tokens:{targetUserId}"), InlineKeyboardButton.WithCallbackData("💾 Export Data", $"admin_export:{targetUserId}") },
            new [] { InlineKeyboardButton.WithCallbackData("⚙️ Manage Tokens", $"admin_manage_tokens:{targetUserId}"), InlineKeyboardButton.WithCallbackData("👤 Manage Sub", $"admin_sub_manage:{targetUserId}") },
            new [] { InlineKeyboardButton.WithCallbackData("🔙 Back to List", "admin_list_subs") }
        });

        await _botClient.SendMessage(chatId, sb.ToString(), parseMode: ParseMode.Html, replyMarkup: keyboard, cancellationToken: ct);
    }

    private async Task HandleAdminStartScraperCommand(long chatId, long targetUserId, CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DBContext>();
        var dbContextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<DBContext>>();
        var httpClientFactory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();
        var scraper = new ScraperService(dbContext, dbContextFactory, httpClientFactory);

        var groups = await scraper.GetAvailableGroupsAsync(ct);

        if (groups.Count == 0)
        {
            await _botClient.SendMessage(chatId, "⚠️ No targets available for this user's config.", cancellationToken: ct);
            return;
        }

        var rows = new List<InlineKeyboardButton[]>();
        
        foreach (var g in groups)
        {
             rows.Add(new[] { 
                InlineKeyboardButton.WithCallbackData($"⚡ {g}", $"admin_run_scrape:{targetUserId}:{g}:lite"), 
                InlineKeyboardButton.WithCallbackData($"🔍 {g}", $"admin_run_scrape:{targetUserId}:{g}:deep") 
            });
        }
        
        rows.Add(new[] { InlineKeyboardButton.WithCallbackData("🔙 Back to User Dash", $"user_dash:{targetUserId}") });

        var inlineKeyboard = new InlineKeyboardMarkup(rows);

        await _botClient.SendMessage(
            chatId: chatId,
            text: $"🔍 <b>ADMIN: Remote Scraper Control</b>\n⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯\nSelect mode for @{targetUserId}:",
            parseMode: ParseMode.Html,
            replyMarkup: inlineKeyboard,
            cancellationToken: ct);
    }

    private async Task HandleAdminSubManageCommand(long chatId, long targetUserId, CancellationToken ct)
    {
        var keyboard = new InlineKeyboardMarkup(new[]
        {
            new [] { InlineKeyboardButton.WithCallbackData("+7 Days", $"admin_sub_ext:{targetUserId}:7"), InlineKeyboardButton.WithCallbackData("+30 Days", $"admin_sub_ext:{targetUserId}:30") },
            new [] { InlineKeyboardButton.WithCallbackData("+90 Days", $"admin_sub_ext:{targetUserId}:90"), InlineKeyboardButton.WithCallbackData("+365 Days", $"admin_sub_ext:{targetUserId}:365") },
            new [] { InlineKeyboardButton.WithCallbackData("🔙 Back to User Dash", $"user_dash:{targetUserId}") }
        });

        await _botClient.SendMessage(chatId, $"👤 <b>Edit Subscription for:</b> <code>{targetUserId}</code>\nChoose duration to add:", parseMode: ParseMode.Html, replyMarkup: keyboard, cancellationToken: ct);
    }

    private async Task HandlePurgeCommand(long chatId, bool isAdmin, CancellationToken ct)
    {
        if (!isAdmin)
        {
            await _botClient.SendMessage(chatId, "❌ Restricted to Admins.", cancellationToken: ct);
            return;
        }

        var statusMsg = await _botClient.SendMessage(chatId, "🧹 <b>Scanning for junk data...</b>", parseMode: ParseMode.Html, cancellationToken: ct);

        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DBContext>();
        var dbService = scope.ServiceProvider.GetRequiredService<DatabaseService>();

        int purgedCount = await dbService.PurgeInvalidReferencesAsync(dbContext);
        
        // Estimate space saved: average record ~2KB
        double savedMb = (purgedCount * 2.0) / 1024.0; 

        var sb = new StringBuilder();
        sb.AppendLine("<b>✅ DATABASE OPTIMIZATION COMPLETE</b>");
        sb.AppendLine("⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯");
        sb.AppendLine($"🧹 <b>Purged:</b> <code>{purgedCount}</code> source records");
        sb.AppendLine($"💾 <b>Estimated Saved:</b> <code>~{savedMb:F2} MB</code>");
        sb.AppendLine();
        sb.AppendLine("<i>All Invalid keys were kept for duplicate detection, only their source code context was removed.</i>");

        await _botClient.EditMessageText(chatId, statusMsg.MessageId, sb.ToString(), parseMode: ParseMode.Html, cancellationToken: ct);
    }

    private async Task HandleVacuumCommand(long chatId, CancellationToken ct)
    {
        var runningJobs = _jobManager.GetAllJobs().Where(j => j.Status == "Running").ToList();
        
        if (runningJobs.Any())
        {
            await _botClient.SendMessage(chatId, 
                $"⚠️ <b>Cannot run vacuum!</b>\n⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯\nThere are <code>{runningJobs.Count}</code> jobs currently running. Please wait for them to finish or stop them with /stop_all before running vacuum to avoid database locks.", 
                parseMode: ParseMode.Html, cancellationToken: ct);
            return;
        }

        var statusMsg = await _botClient.SendMessage(chatId, "🧹 <b>Reclaiming database storage space...</b>\n<i>This may take a moment.</i>", parseMode: ParseMode.Html, cancellationToken: ct);

        try 
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<DBContext>();
            var dbService = scope.ServiceProvider.GetRequiredService<DatabaseService>();

            var beforeSize = await dbService.GetDatabaseSizeInBytesAsync();
            await dbService.VacuumDatabaseAsync(dbContext);
            var afterSize = await dbService.GetDatabaseSizeInBytesAsync();

            double savedMb = (beforeSize - afterSize) / (1024.0 * 1024.0);

            var sb = new StringBuilder();
            sb.AppendLine("<b>✅ DATABASE VACUUM COMPLETE</b>");
            sb.AppendLine("⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯");
            sb.AppendLine($"💾 <b>Space Reclaimed:</b> <code>~{Math.Max(0, savedMb):F2} MB</code>");
            sb.AppendLine($"📊 <b>Current Size:</b> <code>{(afterSize / (1024.0 * 1024.0)):F2} MB</code>");
            sb.AppendLine();
            sb.AppendLine("<i>Database file has been optimized and unused space returned to the system.</i>");

            await _botClient.EditMessageText(chatId, statusMsg.MessageId, sb.ToString(), parseMode: ParseMode.Html, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            await _botClient.EditMessageText(chatId, statusMsg.MessageId, $"❌ <b>Vacuum failed:</b> {ex.Message}", parseMode: ParseMode.Html, cancellationToken: ct);
        }
    }

    // ── New Admin Commands ────────────────────────────────────────────────────

    /// <summary>
    /// Admin: view and manage GitHub tokens for a specific subscriber.
    /// </summary>
    private async Task HandleAdminManageTokensCommand(long chatId, long targetUserId, CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DBContext>();

        var targetUser = await dbContext.TelegramSubscribers.FindAsync(targetUserId, ct);
        if (targetUser == null)
        {
            await _botClient.SendMessage(chatId, $"❌ User <code>{targetUserId}</code> not found.", parseMode: ParseMode.Html, cancellationToken: ct);
            return;
        }

        var tokens = await dbContext.SearchProviderTokens
            .Where(t => t.AddedByTelegramId == targetUserId && t.SearchProvider == SearchProviderEnum.GitHub)
            .OrderBy(t => t.Id)
            .ToListAsync(ct);

        var nameStr = !string.IsNullOrEmpty(targetUser.Username) ? $"@{targetUser.Username}" : $"{targetUserId}";
        var sb = new StringBuilder();
        sb.AppendLine($"<b>🔑 TOKENS FOR {System.Net.WebUtility.HtmlEncode(nameStr)}</b>");
        sb.AppendLine("⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯");

        if (!tokens.Any())
        {
            sb.AppendLine("<i>No GitHub tokens found for this user.</i>");
        }
        else
        {
            var httpClientFactory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();
            var statusTasks = tokens.Select(async t =>
            {
                var status = await CheckTokenStatusAsync(t.Token, httpClientFactory);
                return (t.Id, Status: status);
            });
            var statuses = (await Task.WhenAll(statusTasks)).ToDictionary(x => x.Id, x => x.Status);

            foreach (var t in tokens)
            {
                var status = t.IsEnabled ? "🟢" : "🔴";
                var statusText = statuses[t.Id];
                var fullToken = System.Net.WebUtility.HtmlEncode(t.Token);
                sb.AppendLine($"{status} ID: <code>{t.Id}</code> | <code>{fullToken}</code> | <b>{statusText}</b>");
            }
        }

        sb.AppendLine();
        sb.AppendLine($"<i>To add a token: /add_token_for {targetUserId} &lt;token&gt;</i>");
        sb.AppendLine($"<i>To delete a token: /delete_token &lt;id&gt;</i>");

        await _botClient.SendMessage(chatId, sb.ToString(), parseMode: ParseMode.Html, cancellationToken: ct);
    }

    /// <summary>
    /// Admin: add a GitHub token on behalf of a subscriber.
    /// </summary>
    private async Task HandleAdminAddTokenForUserCommand(long chatId, long targetUserId, string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            await _botClient.SendMessage(chatId, "❌ Token cannot be empty.", cancellationToken: ct);
            return;
        }

        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DBContext>();
        var dbService = scope.ServiceProvider.GetRequiredService<DatabaseService>();

        var targetUser = await dbContext.TelegramSubscribers.FindAsync(targetUserId, ct);
        if (targetUser == null)
        {
            await _botClient.SendMessage(chatId, $"❌ User <code>{targetUserId}</code> not found.", parseMode: ParseMode.Html, cancellationToken: ct);
            return;
        }

        // Check for duplicate
        var exists = await dbContext.SearchProviderTokens
            .AnyAsync(t => t.Token == token && t.SearchProvider == SearchProviderEnum.GitHub, ct);

        if (exists)
        {
            await _botClient.SendMessage(chatId, "⚠️ This token already exists in the system.", cancellationToken: ct);
            return;
        }

        await dbService.AddGitHubTokenAsync(dbContext, token, targetUserId);

        var nameStr = !string.IsNullOrEmpty(targetUser.Username) ? $"@{targetUser.Username}" : $"{targetUserId}";
        await _botClient.SendMessage(chatId,
            $"✅ GitHub token added for <b>{System.Net.WebUtility.HtmlEncode(nameStr)}</b>.",
            parseMode: ParseMode.Html, cancellationToken: ct);
    }

    /// <summary>
    /// Admin: broadcast a message to all active subscribers.
    /// </summary>
    private async Task HandleBroadcastCommand(long chatId, string message, CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DBContext>();

        var activeSubs = await dbContext.TelegramSubscribers
            .Where(s => s.SubscriptionExpiryUtc > DateTime.UtcNow)
            .Select(s => s.TelegramId)
            .ToListAsync(ct);

        var statusMsg = await _botClient.SendMessage(chatId,
            $"📢 Broadcasting to <b>{activeSubs.Count}</b> subscribers...",
            parseMode: ParseMode.Html, cancellationToken: ct);

        int sent = 0, failed = 0;
        var broadcastText = $"📢 <b>Admin Broadcast</b>\n⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯\n{System.Net.WebUtility.HtmlEncode(message)}";

        foreach (var subId in activeSubs)
        {
            if (subId == chatId) continue; // Don't send to self
            try
            {
                await _botClient.SendMessage(subId, broadcastText, parseMode: ParseMode.Html, cancellationToken: ct);
                sent++;
                await Task.Delay(50, ct); // Avoid Telegram flood limits
            }
            catch { failed++; }
        }

        await _botClient.EditMessageText(chatId, statusMsg.MessageId,
            $"✅ <b>Broadcast Complete</b>\n⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯\n📤 Sent: <code>{sent}</code>\n❌ Failed: <code>{failed}</code>",
            parseMode: ParseMode.Html, cancellationToken: ct);
    }

    /// <summary>
    /// Admin: stop all running jobs across all users.
    /// </summary>
    private async Task HandleStopAllJobsCommand(long chatId, CancellationToken ct)
    {
        var runningJobs = _jobManager.GetAllJobs().Where(j => j.Status == "Running").ToList();

        if (!runningJobs.Any())
        {
            await _botClient.SendMessage(chatId, "ℹ️ No running jobs to stop.", cancellationToken: ct);
            return;
        }

        int stopped = 0;
        foreach (var job in runningJobs)
        {
            if (_jobManager.StopJob(job.JobId)) stopped++;
        }

        await _botClient.SendMessage(chatId,
            $"⏹️ <b>All Jobs Stopped</b>\n⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯\nStopped <code>{stopped}</code> of <code>{runningJobs.Count}</code> running jobs.",
            parseMode: ParseMode.Html, cancellationToken: ct);
    }

    /// <summary>
    /// Admin: show how queries are currently partitioned across active nodes.
    /// </summary>
    private async Task HandlePartitionStatusCommand(long chatId, CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DBContext>();

        var activeThreshold = DateTime.UtcNow.AddMinutes(-10);
        var activeNodes = await dbContext.TelegramSubscribers
            .Where(s => s.NodeToken != null && s.LastNodeHeartbeatUtc > activeThreshold)
            .OrderBy(s => s.TelegramId)
            .ToListAsync(ct);

        var allQueries = await dbContext.SearchQueries
            .Where(q => q.IsEnabled)
            .OrderBy(q => q.Id)
            .ToListAsync(ct);

        var sb = new StringBuilder();
        sb.AppendLine("<b>🗂️ QUERY PARTITION STATUS</b>");
        sb.AppendLine("⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯");
        sb.AppendLine($"<b>Active Nodes:</b> <code>{activeNodes.Count}</code>");
        sb.AppendLine($"<b>Total Queries:</b> <code>{allQueries.Count}</code>");
        sb.AppendLine();

        if (!activeNodes.Any())
        {
            sb.AppendLine("<i>No active nodes. All queries will be assigned to the first node that syncs.</i>");
        }
        else
        {
            for (int i = 0; i < activeNodes.Count; i++)
            {
                var node = activeNodes[i];
                var nodeQueries = allQueries
                    .Select((q, idx) => (q, idx))
                    .Where(x => x.idx % activeNodes.Count == i)
                    .Select(x => x.q)
                    .ToList();

                var nameStr = !string.IsNullOrEmpty(node.Username) ? $"@{node.Username}" : $"{node.TelegramId}";
                var lastSeen = node.LastNodeHeartbeatUtc.HasValue
                    ? $"{(DateTime.UtcNow - node.LastNodeHeartbeatUtc.Value).TotalMinutes:F0}m ago"
                    : "never";

                sb.AppendLine($"<b>Node {i + 1}:</b> {System.Net.WebUtility.HtmlEncode(nameStr)} (seen {lastSeen})");
                sb.AppendLine($"  Queries: <code>{nodeQueries.Count}</code> — {System.Net.WebUtility.HtmlEncode(string.Join(", ", nodeQueries.Take(5).Select(q => q.Query)) + (nodeQueries.Count > 5 ? "..." : ""))}");
                sb.AppendLine();
            }
        }

        sb.AppendLine("⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯");
        sb.AppendLine("<i>Nodes are considered active if heartbeat &lt; 10 min ago.</i>");

        await _botClient.SendMessage(chatId, sb.ToString(), parseMode: ParseMode.Html, cancellationToken: ct);
    }

    private async Task<string> CheckTokenStatusAsync(string token, IHttpClientFactory httpClientFactory)
    {
        try
        {
            using var client = httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("UnsecuredAPIKeys-Bot/1.1");
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            
            var response = await client.GetAsync("https://api.github.com/rate_limit");
            
            if (response.IsSuccessStatusCode)
            {
                if (response.Headers.Contains("X-RateLimit-Remaining"))
                {
                    var remainingStr = response.Headers.GetValues("X-RateLimit-Remaining").FirstOrDefault();
                    if (int.TryParse(remainingStr, out int remaining) && remaining == 0)
                    {
                        return "Rate Limited";
                    }
                }
                return "Valid";
            }
            else if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                return "Invalid";
            }
            else
            {
                return $"Error ({response.StatusCode})";
            }
        }
        catch
        {
            return "Connection Error";
        }
    }

    #endregion
}

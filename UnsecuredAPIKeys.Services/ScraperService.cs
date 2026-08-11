using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using System.Linq;
using System.Net.Http.Json;
using UnsecuredAPIKeys.Data;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Data.DTOs;
using UnsecuredAPIKeys.Data.Models;
using UnsecuredAPIKeys.Providers;
using UnsecuredAPIKeys.Providers._Interfaces;
using UnsecuredAPIKeys.Providers.Search_Providers;

namespace UnsecuredAPIKeys.Services;

/// <summary>
/// Scraper service for finding API keys on GitHub.
/// </summary>
public class ScraperService
{
    private readonly DBContext _dbContext;
    private readonly IDbContextFactory<DBContext> _dbContextFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ScraperService>? _logger;
    private readonly IReadOnlyList<IApiKeyProvider> _providers;
    private CancellationTokenSource? _cancellationTokenSource;
    private readonly SemaphoreSlim _parallelSemaphore = new(8); // Limit concurrency for Render Free Tier (512MB RAM)

    // Pre-compiled regex patterns for performance (compiled once at startup, not per-file)
    private readonly IReadOnlyList<(IApiKeyProvider Provider, System.Text.RegularExpressions.Regex Regex)> _compiledPatterns;

    private int _newKeysFound;
    private int _duplicateKeysFound;
    private readonly ISearchProvider _searchProvider;

    // Worker Mode Properties
    public bool IsWorkerMode { get; set; } = false;
    public string? MasterApiUrl { get; set; }
    public string? NodeToken { get; set; }

    // Helper class to manage token index across async calls
    private class TokenCursor
    {
        public int Index { get; set; }
    }

    // Helper class to track deep search statistics
    private class DeepSearchStats
    {
        public int TotalRangesSearched { get; set; }
        public int TotalResultsFound { get; set; }
        public int NewKeysFound { get; set; }
        public int DuplicateKeysFound { get; set; }
        public DateTime SearchStartDate { get; set; }
        public DateTime SearchEndDate { get; set; }
    }

    // ── Distributed Scrape Lock ───────────────────────────────────────────────
    // Uses ApplicationSettings table as a lightweight mutex.
    // Key format:  scrape_lock:<queryId>
    // Value format: <nodeIdentifier>|<ISO8601 timestamp>
    // A lock is considered stale after 15 minutes (covers the longest scrape cycle).

    private const string LockPrefix = "scrape_lock:";
    private static readonly TimeSpan LockTtl = TimeSpan.FromMinutes(15);

    private string NodeIdentifier =>
        NodeToken ?? Environment.GetEnvironmentVariable("RENDER_EXTERNAL_URL") ?? "master";

    /// <summary>
    /// Try to acquire a scrape lock for a query.
    /// Returns true if the lock was acquired (this node should proceed).
    /// Returns false if another node already holds a fresh lock (skip this query).
    /// </summary>
    private async Task<bool> TryAcquireScrapeQueryLockAsync(long queryId, CancellationToken ct)
    {
        var key = $"{LockPrefix}{queryId}";
        var now = DateTime.UtcNow;

        try
        {
            var existing = await _dbContext.ApplicationSettings
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Key == key, ct);

            if (existing != null)
            {
                // Parse existing lock: "nodeId|timestamp"
                var parts = existing.Value.Split('|');
                if (parts.Length == 2 && DateTime.TryParse(parts[1], out var lockedAt))
                {
                    var age = now - lockedAt;
                    if (age < LockTtl && parts[0] != NodeIdentifier)
                    {
                        // Another node holds a fresh lock — skip
                        _logger?.LogDebug("Query {Id} locked by {Node}, skipping", queryId, parts[0]);
                        return false;
                    }
                }
                // Lock is stale or belongs to us — overwrite it
                await _dbContext.ApplicationSettings
                    .Where(s => s.Key == key)
                    .ExecuteDeleteAsync(ct);
            }

            // Insert our lock
            _dbContext.ApplicationSettings.Add(new ApplicationSetting
            {
                Key = key,
                Value = $"{NodeIdentifier}|{now:O}",
                Description = "Scrape lock — auto-expires after 15 min"
            });
            await _dbContext.SaveChangesAsync(ct);
            return true;
        }
        catch (Exception ex)
        {
            // If lock acquisition fails (e.g. race condition unique constraint), skip the query
            _logger?.LogDebug("Lock acquisition failed for query {Id}: {Msg}", queryId, ex.Message);
            return false;
        }
    }

    /// <summary>Release the scrape lock for a query when done.</summary>
    private async Task ReleaseScrapeQueryLockAsync(long queryId)
    {
        var key = $"{LockPrefix}{queryId}";
        try
        {
            await _dbContext.ApplicationSettings
                .Where(s => s.Key == key && s.Value.StartsWith(NodeIdentifier + "|"))
                .ExecuteDeleteAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogDebug("Lock release failed for query {Id}: {Msg}", queryId, ex.Message);
        }
    }

    public ScraperService(DBContext dbContext, IDbContextFactory<DBContext> dbContextFactory, IHttpClientFactory httpClientFactory, ILogger<ScraperService>? logger = null)
    {
        _dbContext = dbContext;
        _dbContextFactory = dbContextFactory;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _providers = ApiProviderRegistry.ScraperProviders;
        _searchProvider = new GitHubSearchProvider();

        // Pre-compile all regex patterns once at startup for performance
        var compiled = new List<(IApiKeyProvider, System.Text.RegularExpressions.Regex)>();
        foreach (var provider in _providers)
        {
            foreach (var pattern in provider.RegexPatterns)
            {
                try
                {
                    compiled.Add((provider, new System.Text.RegularExpressions.Regex(
                        pattern,
                        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase,
                        TimeSpan.FromSeconds(2))));
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning("Invalid regex pattern '{Pattern}' for provider {Provider}: {Error}", pattern, provider.ProviderName, ex.Message);
                }
            }
        }
        _compiledPatterns = compiled;
    }

    public async Task<List<string>> GetAvailableGroupsAsync(CancellationToken cancellationToken = default)
    {
        var groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Get groups from enabled search queries
        var allQueries = await _dbContext.SearchQueries
            .Where(q => q.IsEnabled)
            .ToListAsync(cancellationToken);

        foreach (var q in allQueries)
        {
            groups.Add(InferProviderFromQuery(q.Query));
        }

        return groups.OrderBy(g => g).ToList();
    }

    public async Task RunScrapeByGroupAsync(string selectedGroupName, bool isDeepSearch, long? discoveredBy, CancellationToken cancellationToken)
    {
        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        
        // Fetch tokens belonging to this user or ALL tokens if admin
        var tokenQuery = _dbContext.SearchProviderTokens
            .Where(t => t.IsEnabled && t.SearchProvider == SearchProviderEnum.GitHub);

        // If not admin, restrict to tokens added by this user
        // We'll assume if discoveredBy has a value, we should check their role.
        // Better: Fetch the subscriber record to check IsAdmin.
        var user = await _dbContext.TelegramSubscribers.FindAsync(discoveredBy);
        if (user != null && !user.IsAdmin && discoveredBy.HasValue)
        {
            tokenQuery = tokenQuery.Where(t => t.AddedByTelegramId == discoveredBy.Value);
        }

        var tokens = await tokenQuery.ToListAsync(cancellationToken);

        if (tokens.Count == 0)
        {
            _logger?.LogWarning("No enabled GitHub tokens found for user {UserId}", discoveredBy);
            if (discoveredBy.HasValue && discoveredBy.Value != 0)
            {
                await SendTelegramNotificationAsync(discoveredBy.Value, 
                    "⚠️ <b>Scraper Startup Failed</b>\n⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯\nNo enabled GitHub search tokens were found for your account.\n\n👉 Please configure at least one token using:\n<code>/add_token &lt;your_github_token&gt;</code>");
            }
            return;
        }

        var tokenCursor = new TokenCursor { Index = 0 };
        
        var allQueries = await _dbContext.SearchQueries
            .Where(q => q.IsEnabled)
            .ToListAsync(cancellationToken);

        var queriesToRun = allQueries
            .Where(q => InferProviderFromQuery(q.Query) == selectedGroupName)
            .ToList();

        if (queriesToRun.Count == 0)
        {
            _logger?.LogWarning("No queries found for group: {GroupName}", selectedGroupName);
            if (discoveredBy.HasValue && discoveredBy.Value != 0)
            {
                await SendTelegramNotificationAsync(discoveredBy.Value, 
                    $"⚠️ <b>Scraper Startup Warning</b>\n⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯\nNo enabled search queries were found for target group <b>{System.Net.WebUtility.HtmlEncode(selectedGroupName)}</b>.\n\n👉 Please enable or add queries using <code>/queries</code> or <code>/add_query</code>.");
            }
            return;
        }

        _logger?.LogInformation("Starting {Mode} scrape for {Count} queries in group {Group}...", 
            isDeepSearch ? "DEEP" : "LITE", queriesToRun.Count, selectedGroupName);

        try
        {
            foreach (var query in queriesToRun)
            {
                if (_cancellationTokenSource.Token.IsCancellationRequested) break;

                // Acquire distributed lock — skip if another node is already scraping this query
                if (!await TryAcquireScrapeQueryLockAsync(query.Id, _cancellationTokenSource.Token))
                {
                    _logger?.LogInformation("Skipping query '{Query}' — already being scraped by another node", query.Query);
                    continue;
                }

                try
                {
                    if (isDeepSearch)
                    {
                        await RunDeepSearchAsync(tokens, query, tokenCursor, discoveredBy);
                    }
                    else
                    {
                        await RunScrapingCycleUtilsAsync(tokens, query, tokenCursor, null, discoveredBy);
                    }
                }
                finally
                {
                    await ReleaseScrapeQueryLockAsync(query.Id);
                }

                if (query != queriesToRun.Last())
                {
                    await Task.Delay(LiteLimits.SEARCH_DELAY_MS, _cancellationTokenSource.Token);
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error executing scraper for group {GroupName}", selectedGroupName);
            if (discoveredBy.HasValue && discoveredBy.Value != 0)
            {
                await SendTelegramNotificationAsync(discoveredBy.Value, 
                    $"🚨 <b>Scraper Execution Error</b>\n⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯\nAn error occurred while running scraper for <b>{System.Net.WebUtility.HtmlEncode(selectedGroupName)}</b>:\n<code>{System.Net.WebUtility.HtmlEncode(ex.Message)}</code>");
            }
            throw;
        }
    }

    private async Task SendTelegramNotificationAsync(long chatId, string message)
    {
        try
        {
            var token = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN");
            if (string.IsNullOrEmpty(token) || chatId == 0) return;

            using var client = _httpClientFactory.CreateClient();
            var url = $"https://api.telegram.org/bot{token}/sendMessage";
            var payload = new
            {
                chat_id = chatId,
                text = message,
                parse_mode = "HTML"
            };

            await client.PostAsJsonAsync(url, payload);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to send Telegram notification to chat {ChatId}", chatId);
        }
    }

    public async Task RunScrapeAllGroupsAsync(long? discoveredBy, CancellationToken cancellationToken)
    {
        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        
        var tokenQuery = _dbContext.SearchProviderTokens
            .Where(t => t.IsEnabled && t.SearchProvider == SearchProviderEnum.GitHub);

        var user = await _dbContext.TelegramSubscribers.FindAsync(discoveredBy);
        if (user != null && !user.IsAdmin && discoveredBy.HasValue)
        {
            tokenQuery = tokenQuery.Where(t => t.AddedByTelegramId == discoveredBy.Value);
        }

        var tokens = await tokenQuery.ToListAsync(cancellationToken);
        if (tokens.Count == 0)
        {
            _logger?.LogWarning("No enabled GitHub tokens found for comprehensive scan for user {UserId}", discoveredBy);
            if (discoveredBy.HasValue && discoveredBy.Value != 0)
            {
                await SendTelegramNotificationAsync(discoveredBy.Value, 
                    "⚠️ <b>Comprehensive Scan Failed to Start</b>\n⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯\nNo enabled GitHub search tokens found for your account.\n\n👉 Please configure a token using <code>/add_token &lt;your_github_token&gt;</code>.");
            }
            return;
        }

        var groups = await GetAvailableGroupsAsync(cancellationToken);
        
        _logger?.LogInformation("Starting automated scrape for {Count} groups...", groups.Count);
        
        foreach (var group in groups)
        {
            if (_cancellationTokenSource.Token.IsCancellationRequested) break;
            
            // For automated mode, we use Lite search by default to avoid excessive partitioning
            await RunScrapeByGroupAsync(group, false, discoveredBy, _cancellationTokenSource.Token);
            
            if (group != groups.Last())
            {
                await Task.Delay(5000, _cancellationTokenSource.Token);
            }
        }
        
        _logger?.LogInformation("Automated scrape completed.");
    }

    // Default RunAsync for CLI (no discovery tagging)
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (IsWorkerMode)
        {
            // Worker mode startup
            _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            
            Console.WriteLine("[cyan]🚀 Starting APIHunterV2 in GHOST WORKER Mode...[/]");
            Console.WriteLine($"[dim]Master Service: {MasterApiUrl}[/]");
            
            // Disable database tracking for workers to ensure they stay stateless
            _dbContext.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

            // Start heartbeat in background
            _ = Task.Run(() => StartHeartbeatLoop(_cancellationTokenSource.Token));

            // Main worker loop
            while (!_cancellationTokenSource.Token.IsCancellationRequested)
            {
                try 
                {
                    await RunWorkerCycleAsync(_cancellationTokenSource.Token);
                    
                    // Wait before next cycle
                    Console.WriteLine("[dim]Waiting 2 minutes before next worker sync...[/]");
                    await Task.Delay(TimeSpan.FromMinutes(2), _cancellationTokenSource.Token);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Worker cycle error");
                    await Task.Delay(10000, _cancellationTokenSource.Token);
                }
            }
        }
        else
        {
            await RunAsync(null, cancellationToken);
        }
    }

    private async Task RunWorkerCycleAsync(CancellationToken ct)
    {
        // 1. Sync tokens and queries from Master
        var syncData = await SyncWithMasterAsync(ct);
        
        // 2. Identify tokens to use
        var tokensToUse = new List<SearchProviderToken>();
        
        // Add Local Tokens (from Env Var)
        var localTokensRaw = Environment.GetEnvironmentVariable("WORKER_GITHUB_TOKENS");
        if (!string.IsNullOrEmpty(localTokensRaw))
        {
            var localTokens = localTokensRaw.Split(',', StringSplitOptions.RemoveEmptyEntries);
            foreach (var t in localTokens)
            {
                tokensToUse.Add(new SearchProviderToken { Token = t.Trim(), SearchProvider = SearchProviderEnum.GitHub, IsEnabled = true });
            }
            Console.WriteLine($"[green]Loaded {localTokens.Length} LOCAL tokens from environment.[/]");
        }

        // Add Master Tokens (assigned by admin)
        if (syncData?.Tokens != null)
        {
            foreach (var t in syncData.Tokens)
            {
                if (!tokensToUse.Any(existing => existing.Token == t.Token))
                {
                    tokensToUse.Add(new SearchProviderToken { Token = t.Token, SearchProvider = t.SearchProvider, IsEnabled = true });
                }
            }
            Console.WriteLine($"[green]Loaded {syncData.Tokens.Count} tokens from MASTER API.[/]");
        }

        if (tokensToUse.Count == 0)
        {
            _logger?.LogWarning("No GitHub tokens available. Worker is idle.");
            return;
        }

        // 3. Identify queries to use
        var queriesToRun = syncData?.Queries?.Where(q => q.IsEnabled).ToList() ?? new List<SearchQueryDTO>();
        if (queriesToRun.Count == 0)
        {
            _logger?.LogWarning("No enabled search queries found on master. Worker is idling.");
            return;
        }

        Console.WriteLine($"[yellow]Starting scrape of {queriesToRun.Count} queries (partition {syncData?.NodeIndex + 1}/{syncData?.TotalNodes})...[/]");
        var tokenCursor = new TokenCursor { Index = 0 };

        foreach (var qDto in queriesToRun)
        {
            if (ct.IsCancellationRequested) break;

            var queryModel = new SearchQuery { Id = qDto.Id, Query = qDto.Query, IsEnabled = qDto.IsEnabled };

            // Acquire distributed lock — skip if Master or another worker is already on this query
            if (!await TryAcquireScrapeQueryLockAsync(queryModel.Id, ct))
            {
                Console.WriteLine($"[dim]Skipping '{queryModel.Query}' — locked by another node[/]");
                continue;
            }

            try
            {
                await RunScrapingCycleUtilsAsync(tokensToUse, queryModel, tokenCursor, null, null);
            }
            finally
            {
                await ReleaseScrapeQueryLockAsync(queryModel.Id);
            }

            // Short delay between queries
            await Task.Delay(LiteLimits.SEARCH_DELAY_MS, ct);
        }
    }

    private async Task<NodeSyncDTO?> SyncWithMasterAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(MasterApiUrl) || string.IsNullOrEmpty(NodeToken)) return null;

        try
        {
            using var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Add("X-Node-Token", NodeToken);
            var response = await client.GetAsync($"{MasterApiUrl.TrimEnd('/')}/api/v1/nodes/sync", ct);
            
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<NodeSyncDTO>(cancellationToken: ct);
            }
            else
            {
                _logger?.LogWarning("Failed to sync with master: {Status}", response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error syncing with master");
        }
        return null;
    }

    private async Task StartHeartbeatLoop(CancellationToken ct)
    {
        Console.WriteLine("[📡] Heartbeat loop started.");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!string.IsNullOrEmpty(MasterApiUrl) && !string.IsNullOrEmpty(NodeToken))
                {
                    using var client = _httpClientFactory.CreateClient();
                    client.DefaultRequestHeaders.Add("X-Node-Token", NodeToken);
                    
                    // Attach NodeUrl to keep the worker alive on Render
                    var myUrl = Environment.GetEnvironmentVariable("RENDER_EXTERNAL_URL");
                    var heartbeatUrl = $"{MasterApiUrl.TrimEnd('/')}/api/v1/nodes/heartbeat";
                    if (!string.IsNullOrEmpty(myUrl))
                    {
                        heartbeatUrl += $"?nodeUrl={System.Net.WebUtility.UrlEncode(myUrl)}";
                    }

                    var response = await client.PostAsync(heartbeatUrl, null, ct);
                    
                    if (response.IsSuccessStatusCode)
                    {
                        _logger?.LogDebug("Heartbeat sent successfully");
                        Console.WriteLine($"[📡] {DateTime.UtcNow.ToIst():HH:mm:ss} IST Heartbeat reported to Master: [green]Success[/]");
                    }
                    else
                    {
                        Console.WriteLine($"[📡] {DateTime.UtcNow.ToIst():HH:mm:ss} IST Heartbeat reported to Master: [red]Failed ({response.StatusCode})[/]");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning("Heartbeat failed: {Msg}", ex.Message);
                Console.WriteLine($"[📡] {DateTime.UtcNow.ToIst():HH:mm:ss} IST Heartbeat error: [red]{ex.Message}[/]");
            }

            // Wait 5 minutes before next heartbeat
            await Task.Delay(TimeSpan.FromMinutes(5), ct);
        }
    }

    public async Task RunAsync(long? discoveredBy, CancellationToken cancellationToken)
    {
        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        Console.WriteLine("[cyan]Starting GitHub scraper...[/]");

        // Get GitHub tokens
        var tokens = await _dbContext.SearchProviderTokens
            .Where(t => t.IsEnabled && t.SearchProvider == SearchProviderEnum.GitHub)
            .ToListAsync(cancellationToken);

        if (tokens.Count == 0)
        {
            Console.WriteLine("[red]No GitHub tokens configured. Use 'Configure Settings' to add one.[/]");
            return;
        }

        Console.WriteLine($"[dim]Loaded {tokens.Count} GitHub token(s).[/]");
        var tokenCursor = new TokenCursor { Index = 0 };

        while (!_cancellationTokenSource.Token.IsCancellationRequested)
        {
            try
            {
                // 1. Get all enabled queries
                var allQueries = await _dbContext.SearchQueries
                    .Where(q => q.IsEnabled)
                    .ToListAsync(cancellationToken);

                if (allQueries.Count == 0)
                {
                    Console.WriteLine("[yellow]No search queries defined.[/]");
                    return;
                }

                // 2. Group by "Provider" (inferred from common prefixes or generic)
                // We'll use a heuristic or just list them all if few.
                // Better: Group by inferred type.
                var groups = allQueries
                    .GroupBy(q => InferProviderFromQuery(q.Query))
                    .OrderBy(g => g.Key)
                    .ToList();

                // 3. Show Menu
                var choices = new List<string>();
                foreach (var group in groups)
                {
                    choices.Add($"{group.Key} ({group.Count()} queries)");
                }
                choices.Add("[red]Back to Main Menu[/]");

                var selection = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("[yellow]Select a provider group to scrape:[/]")
                        .PageSize(15)
                        .AddChoices(choices));

                if (selection == "[red]Back to Main Menu[/]")
                    break;

                // 4. Run Sequential Scraping for selected group
                var selectedGroupName = selection.Split('(')[0].Trim();
                
                // Ask for Search Mode
                var modeSelection = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title($"[yellow]Select search mode for {selectedGroupName}:[/]")
                        .AddChoices(new[]
                        {
                            "1. Lite Search (Fast, Max 1000 results/query)",
                            "2. Deep Search (Slow, Unlimited results, Uses Date Partitioning)",
                            "Back"
                        }));

                if (modeSelection == "Back") continue;

                bool isDeepSearch = modeSelection.StartsWith("2");
                var queriesToRun = groups.First(g => g.Key == selectedGroupName).ToList();

                Console.WriteLine($"\n[green]Starting {(isDeepSearch ? "DEEP" : "LITE")} scrape for {queriesToRun.Count} queries...[/]");
                
                foreach (var query in queriesToRun)
                {
                    if (_cancellationTokenSource.Token.IsCancellationRequested) break;

                    if (isDeepSearch)
                    {
                        await RunDeepSearchAsync(tokens, query, tokenCursor, discoveredBy);
                    }
                    else
                    {
                        await RunScrapingCycleUtilsAsync(tokens, query, tokenCursor, null, discoveredBy);
                    }

                    // Delay between queries (if not the last one)
                    if (query != queriesToRun.Last())
                    {
                        Console.WriteLine($"[dim]Waiting {LiteLimits.SEARCH_DELAY_MS / 1000}s before next query...[/]");
                        await Task.Delay(LiteLimits.SEARCH_DELAY_MS, _cancellationTokenSource.Token);
                    }
                }

                Console.WriteLine($"[green]Completed scraping for {selectedGroupName}.[/]");
                Console.WriteLine("[dim]Press any key to continue to menu...[/]");
                if (!Console.IsInputRedirected) Console.ReadKey(true);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[red]Error during scraping: {Markup.Escape(ex.Message)}[/]");
                _logger?.LogError(ex, "Scraping cycle error");
                await Task.Delay(2000, _cancellationTokenSource.Token);
            }
        }
    }

    private string InferProviderFromQuery(string query)
    {
        var q = query.ToLower();
        // Check specific prefixes BEFORE generic sk- to avoid misclassification
        if (q.Contains("sk-ant") || q.Contains("anthropic") || q.Contains("claude")) return "Anthropic";
        if (q.Contains("sk-or-v1") || q.Contains("openrouter")) return "OpenRouter";
        if (q.Contains("openai") || q.Contains("sk-proj") || q.Contains("sk-svcacct") || q.Contains("gpt")) return "OpenAI";
        if (q.Contains("google") || q.Contains("gemini") || q.Contains("aizasy") || q.Contains("aiza")) return "Google";
        if (q.Contains("groq") || q.Contains("gsk_")) return "Groq";
        if (q.Contains("perplexity") || q.Contains("pplx")) return "Perplexity";
        if (q.Contains("cerebras") || q.Contains("csk-")) return "Cerebras";
        if (q.Contains("voyage")) return "Voyage AI";
        if (q.Contains("bedrock") || q.Contains("aws_bearer")) return "AWS Bedrock";
        if (q.Contains("akia") || q.Contains("asia") || q.Contains("aws")) return "AWS IAM";
        if (q.Contains("azure")) return "Azure OpenAI";
        if (q.Contains("mistral")) return "Mistral AI";
        if (q.Contains("kling")) return "Kling AI";
        if (q.Contains("pollo")) return "Pollo AI";
        if (q.Contains("runway") || q.Contains("runwayml")) return "Runway";
        if (q.Contains("deepseek")) return "DeepSeek";
        if (q.Contains("cohere") || q.Contains("co_api")) return "Cohere";
        if (q.Contains("eleven") || q.Contains("xi-api")) return "ElevenLabs";
        if (q.Contains("stability")) return "Stability AI";
        if (q.Contains("together")) return "Together AI";
        if (q.Contains("xai") || q.Contains("grok")) return "xAI";
        if (q.Contains("replicate") || q.StartsWith("r8_")) return "Replicate";
        if (q.Contains("fireworks") || q.StartsWith("fw_")) return "Fireworks AI";
        if (q.Contains("hugging") || q.Contains("hf_token") || q.StartsWith("hf_")) return "Hugging Face";
        if (q.Contains("a2e")) return "A2E AI";
        if (q.Contains("facebook") || q.Contains("fb_access_token") || q.Contains("eaaq") || q.Contains("eaaf")) return "Facebook";
        if (q.Contains("gocspx") || q.Contains("googleusercontent") || q.Contains("google_oauth")) return "Google OAuth";
        if (q.Contains("stripe") || q.Contains("rk_live") || q.Contains("whsec")) return "Stripe";
        if (q.Contains("tiktok")) return "TikTok";
        if (q.Contains("goog1") || q.Contains("hmac_access_key")) return "Google Cloud HMAC";
        return "Other";
    }

    private async Task RunDeepSearchAsync(List<SearchProviderToken> tokens, SearchQuery query, TokenCursor cursor, long? discoveredBy)
    {
        // Deep search strategy using language and file extension filters with progress tracking
        
        Console.WriteLine("\n[cyan]═══ Deep Search Configuration ═══[/]");
        Console.WriteLine("[yellow]Deep Search will partition results by language and file type to bypass the 1000-result limit.[/]");
        Console.WriteLine("[dim]Note: GitHub Code Search doesn't support date filtering.[/]\n");
        
        // Define search partitions
        var languages = new[] { "python", "javascript", "typescript", "go", "java", "csharp", "ruby", "php", "shell" };
        var extensions = new[] { "env", "yml", "yaml", "json", "config", "txt", "md", "properties" };
        
        // Load existing progress
        var existingProgress = await _dbContext.DeepSearchProgress
            .Where(p => p.SearchQueryId == query.Id)
            .ToListAsync(_cancellationTokenSource!.Token);
        
        // Display progress if any exists
        if (existingProgress.Any())
        {
            Console.WriteLine("[yellow]Found existing progress for this query:[/]\n");
            
            var progressTable = new Table().Border(TableBorder.Rounded);
            progressTable.AddColumn("[bold]Partition[/]");
            progressTable.AddColumn("[bold]Type[/]");
            progressTable.AddColumn("[bold]Last Page[/]");
            progressTable.AddColumn("[bold]Results[/]");
            progressTable.AddColumn("[bold]Status[/]");
            
            foreach (var prog in existingProgress.OrderBy(p => p.PartitionType).ThenBy(p => p.PartitionValue))
            {
                string status = prog.IsCompleted ? "[green]✓ Complete[/]" : "[yellow]In Progress[/]";
                progressTable.AddRow(
                    prog.PartitionValue,
                    prog.PartitionType,
                    prog.LastPageSearched.ToString(),
                    prog.TotalResultsFound.ToString(),
                    status
                );
            }
            
            AnsiConsole.Write(progressTable);
            AnsiConsole.WriteLine();
            
            var resumeChoice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[yellow]What would you like to do?[/]")
                    .AddChoices(new[]
                    {
                        "Resume from last position",
                        "Start fresh (clear all progress)",
                        "Cancel"
                    }));
            
            if (resumeChoice == "Cancel") return;
            
            if (resumeChoice == "Start fresh (clear all progress)")
            {
                if (!IsWorkerMode)
                {
                    _dbContext.DeepSearchProgress.RemoveRange(existingProgress);
                    await _dbContext.SaveChangesAsync(_cancellationTokenSource.Token);
                }
                existingProgress.Clear();
                Console.WriteLine("[green]Progress cleared. Starting fresh...[/]\n");
            }
            else
            {
                Console.WriteLine("[green]Resuming from last position...[/]\n");
            }
        }
        
        int totalExistingResults = 0;
        foreach (var prog in existingProgress)
        {
            totalExistingResults += prog.TotalResultsFound;
        }
        
        var stats = new DeepSearchStats
        {
            SearchStartDate = DateTime.MinValue,
            SearchEndDate = DateTime.UtcNow,
            TotalRangesSearched = 0,
            TotalResultsFound = totalExistingResults
        };
        
        Console.WriteLine($"[yellow]Starting deep search for: {Markup.Escape(query.Query)}[/]");
        Console.WriteLine($"[dim]Will search across {languages.Length} languages and {extensions.Length} file types[/]\n");
        
        // Search by language
        foreach (var language in languages)
        {
            if (_cancellationTokenSource.Token.IsCancellationRequested) break;
            
            await SearchPartitionAsync(tokens, query, cursor, "language", language, stats, discoveredBy);
        }
        
        // Search by file extension
        foreach (var extension in extensions)
        {
            if (_cancellationTokenSource.Token.IsCancellationRequested) break;
            
            await SearchPartitionAsync(tokens, query, cursor, "extension", extension, stats, discoveredBy);
        }
        
        // Display summary
        Console.WriteLine("\n[cyan]═══ Deep Search Summary ═══[/]");
        var summaryTable = new Table().Border(TableBorder.Rounded);
        summaryTable.AddColumn("[bold]Metric[/]");
        summaryTable.AddColumn("[bold]Value[/]");
        
        summaryTable.AddRow("Query", Markup.Escape(query.Query));
        summaryTable.AddRow("Search Partitions", $"{languages.Length} languages + {extensions.Length} extensions");
        summaryTable.AddRow("Total Searches", stats.TotalRangesSearched.ToString());
        summaryTable.AddRow("Total Results Found", $"[green]{stats.TotalResultsFound}[/]");
        summaryTable.AddRow("New Keys Discovered", $"[green]{stats.NewKeysFound}[/]");
        summaryTable.AddRow("Duplicates Found", $"[dim]{stats.DuplicateKeysFound}[/]");
        
        AnsiConsole.Write(summaryTable);
        AnsiConsole.WriteLine();
    }

    private async Task SearchPartitionAsync(List<SearchProviderToken> tokens, SearchQuery query, TokenCursor cursor, string partitionType, string partitionValue, DeepSearchStats stats, long? discoveredBy)
    {
        // Get or create progress record
        var progress = await _dbContext.DeepSearchProgress
            .FirstOrDefaultAsync(p => 
                p.SearchQueryId == query.Id && 
                p.PartitionType == partitionType && 
                p.PartitionValue == partitionValue,
                _cancellationTokenSource!.Token);
        
        if (progress == null)
        {
            progress = new DeepSearchProgress
            {
                SearchQueryId = query.Id,
                PartitionType = partitionType,
                PartitionValue = partitionValue,
                LastPageSearched = 0,
                TotalResultsFound = 0,
                IsCompleted = false,
                LastSearchedUTC = DateTime.UtcNow
            };

            if (!IsWorkerMode)
            {
                _dbContext.DeepSearchProgress.Add(progress);
                await _dbContext.SaveChangesAsync(_cancellationTokenSource.Token);
            }
        }
        
        // Skip if already completed
        if (progress.IsCompleted)
        {
            Console.WriteLine($"[dim]→ Skipping {partitionValue} ({partitionType}) - already completed[/]");
            return;
        }
        
        string filter = $"{partitionType}:{partitionValue}";
        int startPage = progress.LastPageSearched + 1;
        
        Console.WriteLine($"[dim]→ Searching {partitionValue} ({partitionType}) from page {startPage}...[/]");
        
        stats.TotalRangesSearched++;
        var response = await RunScrapingCycleUtilsAsync(tokens, query, cursor, filter, discoveredBy, startPage);
        int resultCount = response?.Results?.Count() ?? 0;
        
        // Update progress
        progress.TotalResultsFound += resultCount;
        progress.LastPageSearched = response?.LastPageReached ?? progress.LastPageSearched;
        progress.LastSearchedUTC = DateTime.UtcNow;
        
        stats.TotalResultsFound += resultCount;
        stats.NewKeysFound += _newKeysFound;
        stats.DuplicateKeysFound += _duplicateKeysFound;

        if (!IsWorkerMode)
        {
            await _dbContext.SaveChangesAsync(_cancellationTokenSource.Token);
        }
        
        // Handle "Hit Wall" logic
        if (response != null && response.HitLimit)
        {
            Console.WriteLine($"[yellow]⚠ Partition '{partitionValue}' hit the 1,000 result limit. Triggering sub-partitions...[/]");
            
            // Subdivision Strategy 1: File Sizes
            var sizeBuckets = new[] { "0..500", "501..2000", "2001..5000", "5001..15000", ">15000" };
            foreach (var bucket in sizeBuckets)
            {
                if (_cancellationTokenSource.Token.IsCancellationRequested) break;
                string subFilter = $"{filter} size:{bucket}";
                await SearchPartitionAsync(tokens, query, cursor, "sub-partition", subFilter, stats, discoveredBy);
            }

            // Subdivision Strategy 2: Common Paths (as requested by user)
            var paths = new[] { "config", "src", ".env", "keys", "deploy", "setup" };
            foreach (var path in paths)
            {
                if (_cancellationTokenSource.Token.IsCancellationRequested) break;
                string subFilter = $"{filter} path:{path}";
                await SearchPartitionAsync(tokens, query, cursor, "sub-partition", subFilter, stats, discoveredBy);
            }

            progress.IsCompleted = true; // The parent partition is effectively "managed" by sub-partitions now
        }
        else if (resultCount < 1000)
        {
            progress.IsCompleted = true;
        }
        
        if (!IsWorkerMode)
        {
            await _dbContext.SaveChangesAsync(_cancellationTokenSource.Token);
        }

        // Small delay between searches to be polite
        await Task.Delay(1000, _cancellationTokenSource.Token);

        // MEMORY OPTIMIZATION
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }

    private async Task<SearchResponse?> RunScrapingCycleUtilsAsync(List<SearchProviderToken> tokens, SearchQuery query, TokenCursor cursor, string? extraParams, long? discoveredBy, int startPage = 1)
    {
        int retryCount = 0;
        bool querySuccess = false;
        SearchResponse? finalResponse = null;
        var depletedTokens = new Dictionary<int, DateTime>();

        while (!querySuccess && retryCount < (tokens.Count * 2)) // Allow orbiting tokens once
        {
            if (depletedTokens.ContainsKey(cursor.Index))
            {
                if (DateTime.UtcNow < depletedTokens[cursor.Index])
                {
                    // Still depleted, move to next
                    cursor.Index = (cursor.Index + 1) % tokens.Count;
                    retryCount++;
                    
                    if (depletedTokens.Count == tokens.Count)
                    {
                        // ALL tokens are rate limited
                        var nextReset = depletedTokens.Values.Min();
                        var waitTime = nextReset - DateTime.UtcNow;
                        if (waitTime > TimeSpan.Zero)
                        {
                            Console.WriteLine($"[yellow]All {tokens.Count} tokens are currently rate-limited.[/]");
                            Console.WriteLine($"[yellow]Waiting {Math.Ceiling(waitTime.TotalMinutes)} minutes for first token to reset (at {nextReset.ToIst():HH:mm:ss} IST)...[/]");
                            await Task.Delay(waitTime, _cancellationTokenSource!.Token);
                            depletedTokens.Remove(depletedTokens.First(x => x.Value == nextReset).Key);
                        }
                    }
                    continue;
                }
                else
                {
                    depletedTokens.Remove(cursor.Index);
                }
            }

            var currentToken = tokens[cursor.Index];
            try
            {
               finalResponse = await RunScrapingCycleAsync(currentToken, query, extraParams, discoveredBy, startPage);
               querySuccess = true;
            }
            catch (Octokit.RateLimitExceededException ex)
            {
                Console.WriteLine($"[yellow]Token {cursor.Index + 1} hit GitHub rate limit. Reset at {ex.Reset.LocalDateTime.ToIst():HH:mm:ss} IST.[/]");
                MetricsService.Instance.RecordGitHubRateLimit();
                depletedTokens[cursor.Index] = ex.Reset.UtcDateTime.AddSeconds(5); // Add buffer
                cursor.Index = (cursor.Index + 1) % tokens.Count;
                retryCount++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[red]Error with token {cursor.Index + 1}: {Markup.Escape(ex.Message)}. Switching token...[/]");
                cursor.Index = (cursor.Index + 1) % tokens.Count;
                retryCount++;

                if (retryCount >= tokens.Count && !querySuccess)
                {
                    Console.WriteLine("[red]Many tokens failed for this query. Moving on...[/]");
                    break;
                }
            }
        }
        return finalResponse;
    }

    private async Task<SearchResponse?> RunScrapingCycleAsync(SearchProviderToken token, SearchQuery query, string? extraParams, long? discoveredBy, int startPage = 1)
    {
        // Reset counters for this cycle
        _newKeysFound = 0;
        _duplicateKeysFound = 0;

        string displayQuery = query.Query + (extraParams != null ? $" {extraParams}" : "");
        Console.WriteLine($"[cyan]Searching: {Markup.Escape(displayQuery)}[/]");

        // Update last search time (Only for Master)
        if (!IsWorkerMode)
        {
            query.LastSearchUTC = DateTime.UtcNow;
            _dbContext.SearchQueries.Update(query);
            await _dbContext.SaveChangesAsync(_cancellationTokenSource!.Token);
        }

        // Search GitHub
        SearchResponse? response;

        try
        {
            response = await _searchProvider.SearchAsync(query, token, extraParams, startPage);
            
            // Update results count stat on Master if we got a response
            if (!IsWorkerMode && response != null && startPage == 1 && string.IsNullOrEmpty(extraParams))
            {
                query.SearchResultsCount = response.TotalResultsCount;
                _dbContext.SearchQueries.Update(query);
                await _dbContext.SaveChangesAsync(_cancellationTokenSource!.Token);
            }
        }
        catch (Exception)
        {
            // Re-throw so the main loop can handle token rotation
            throw;
        }

        if (response?.Results == null)
        {
            Console.WriteLine("[yellow]No results from search.[/]");
            return response;
        }

        var resultsList = response.Results.ToList();
        
        Console.WriteLine($"[dim]Fetched {resultsList.Count} matches[/]");

        // Process results in parallel with a concurrency limit
        var discoveries = new System.Collections.Concurrent.ConcurrentBag<NodeReportDto>();
        
        // Use a more memory-efficient approach by processing in chunks
        const int chunkSize = 50;
        var chunks = resultsList.Chunk(chunkSize);
        
        foreach (var chunk in chunks)
        {
            if (_cancellationTokenSource!.Token.IsCancellationRequested) break;

            var processingTasks = chunk.Select(async repoRef =>
            {
                await _parallelSemaphore.WaitAsync(_cancellationTokenSource!.Token);
                try
                {
                    if (_cancellationTokenSource!.Token.IsCancellationRequested) return;
                    
                    // Use a factory-created context per result to avoid thread-safety issues
                    await using var localDb = await _dbContextFactory.CreateDbContextAsync(_cancellationTokenSource!.Token);
                    var found = await ProcessResultAndCollectAsync(localDb, repoRef, token, query, discoveredBy);
                    if (found != null && found.Any())
                    {
                        foreach (var discovery in found) discoveries.Add(discovery);
                    }
                }
                finally
                {
                    _parallelSemaphore.Release();
                }
            });

            await Task.WhenAll(processingTasks);
            
            // Memory conservation: yield control and check for cancellation
            await Task.Yield();
        }

        // Bulk Report Findings (Worker Mode)
        if (IsWorkerMode && discoveries.Any())
        {
            await ReportBulkDiscoveryAsync(discoveries.ToList());
        }

        // Summary
        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("[bold]Metric[/]")
            .AddColumn("[bold]Value[/]");

        table.AddRow("Query", Markup.Escape(displayQuery));
        table.AddRow("Results", resultsList.Count.ToString());
        table.AddRow("New Keys", $"[green]{_newKeysFound}[/]");
        table.AddRow("Duplicates", $"[dim]{_duplicateKeysFound}[/]");

        AnsiConsole.Write(table);

        // MEMORY OPTIMIZATION: Clear the change tracker
        _dbContext.ChangeTracker.Clear();

        return response;
    }

    private async Task<List<NodeReportDto>> ProcessResultAndCollectAsync(DBContext db, RepoReference repoRef, SearchProviderToken token, SearchQuery query, long? discoveredBy)
    {
        var discoveredKeys = new List<NodeReportDto>();
        try
        {
            // Get file content
            var content = await FetchFileContentAsync(repoRef, token);
            MetricsService.Instance.RecordGitHubRequest();

            if (string.IsNullOrEmpty(content))
                return discoveredKeys;

            MetricsService.Instance.RecordFileScanned();

            // Search for API keys using all provider patterns (pre-compiled for performance)
            foreach (var (provider, regex) in _compiledPatterns)
            {
                System.Text.RegularExpressions.MatchCollection matches;
                try { matches = regex.Matches(content); }
                catch (System.Text.RegularExpressions.RegexMatchTimeoutException) { continue; }

                foreach (System.Text.RegularExpressions.Match match in matches)
                {
                    var apiKey = match.Groups.Count > 1 ? match.Groups[1].Value : match.Value;

                        // Special handling for Kling AI (Access Key + Secret Key pairing)
                        if (provider.ApiType == ApiTypeEnum.KlingAI && !apiKey.Contains(':'))
                        {
                            var secretKey = await ExtractKlingSecretFromContextAsync(content, match.Index);
                            if (!string.IsNullOrEmpty(secretKey))
                            {
                                apiKey = $"{apiKey}:{secretKey}";
                            }
                        }

                        // Special handling for AWS IAM (AKIA/ASIA + Secret + optional Session Token context pairing)
                        if (provider.ApiType == ApiTypeEnum.AWSIAM && !apiKey.Contains(":::") && !apiKey.Contains("|"))
                        {
                            var (secretKey, sessionToken) = await ExtractAwsSecretAndTokenFromContextAsync(content, apiKey, match.Index);
                            if (!string.IsNullOrEmpty(secretKey))
                            {
                                apiKey = string.IsNullOrEmpty(sessionToken) 
                                    ? $"{apiKey}:::{secretKey}"
                                    : $"{apiKey}:::{secretKey}:::{sessionToken}";
                            }
                        }

                        // Special handling for Azure OpenAI (Key + Endpoint pairing)
                        if (provider.ApiType == ApiTypeEnum.AzureOpenAI && !apiKey.Contains('|'))
                        {
                            var azureEndpoint = await ExtractAzureOpenAiEndpointFromContextAsync(content, match.Index);
                            if (!string.IsNullOrEmpty(azureEndpoint))
                            {
                                apiKey = $"{apiKey}|{azureEndpoint}";
                            }
                        }

                        if (IsWorkerMode)
                        {
                            discoveredKeys.Add(new NodeReportDto
                            {
                                 ApiKey = apiKey,
                                 ApiType = provider.ApiType,
                                 Metadata = $"[Worker {(!string.IsNullOrEmpty(NodeToken) && NodeToken.Length > 5 ? NodeToken.Substring(0, 5) : "Unknown")}]",
                                 RepoName = repoRef.RepoName ?? string.Empty,
                                 RepoOwner = repoRef.RepoOwner ?? string.Empty,
                                 FilePath = repoRef.FilePath ?? string.Empty,
                                 FileUrl = repoRef.FileURL ?? string.Empty
                             });
                            Interlocked.Increment(ref _newKeysFound);
                            MetricsService.Instance.RecordKeyFound();
                        }
                        else
                        {
                            var exists = await db.APIKeys
                                .AnyAsync(k => k.ApiKey == apiKey, _cancellationTokenSource!.Token);

                            if (exists)
                            {
                                Interlocked.Increment(ref _duplicateKeysFound);
                                MetricsService.Instance.RecordDuplicate();
                                continue;
                            }

                        // Add new key
                        var newKey = new APIKey
                        {
                            ApiKey = apiKey,
                            ApiType = provider.ApiType,
                            Status = ApiStatusEnum.Unverified,
                            SearchProvider = SearchProviderEnum.GitHub,
                            FirstFoundUTC = DateTime.UtcNow,
                            LastFoundUTC = DateTime.UtcNow,
                            DiscoveredByTelegramId = discoveredBy
                        };

                        // Add repo reference (clone to avoid EF tracking conflicts if same file has multiple keys)
                        var newRepoRef = new RepoReference
                        {
                            RepoURL = repoRef.RepoURL,
                            RepoOwner = repoRef.RepoOwner,
                            RepoName = repoRef.RepoName,
                            RepoDescription = repoRef.RepoDescription,
                            RepoId = repoRef.RepoId,
                            FileURL = repoRef.FileURL,
                            FileName = repoRef.FileName,
                            FilePath = repoRef.FilePath,
                            FileSHA = repoRef.FileSHA,
                            ApiContentUrl = repoRef.ApiContentUrl,
                            CodeContext = repoRef.CodeContext,
                            LineNumber = repoRef.LineNumber,
                            Branch = repoRef.Branch,
                            SearchQueryId = query.Id,
                            FoundUTC = DateTime.UtcNow,
                            Provider = "GitHub"
                        };
                        newKey.References.Add(newRepoRef);

                            try
                            {
                                db.APIKeys.Add(newKey);
                                await db.SaveChangesAsync(_cancellationTokenSource!.Token);

                                Interlocked.Increment(ref _newKeysFound);
                                MetricsService.Instance.RecordKeyFound();
                                Console.WriteLine($"[green]+ New {Markup.Escape(provider.ProviderName)} key found![/]");
                                Console.WriteLine($"  [dim]Source: {Markup.Escape(repoRef.FileURL ?? "Unknown")}[/]");
                                Console.WriteLine($"  [dim]Repo: {Markup.Escape(repoRef.RepoURL ?? "Unknown")}[/]");
                            }
                            catch (DbUpdateException) // Likely a unique constraint violation
                            {
                                db.Entry(newKey).State = EntityState.Detached; // Remove from tracker
                                Interlocked.Increment(ref _duplicateKeysFound);
                            }
                        }
                    }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error processing result: {Url}", repoRef.FileURL);
        }
        return discoveredKeys;
    }

    private async Task ReportBulkDiscoveryAsync(List<NodeReportDto> discoveries)
    {
        if (string.IsNullOrEmpty(MasterApiUrl) || string.IsNullOrEmpty(NodeToken) || !discoveries.Any())
            return;

        try
        {
            using var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Add("X-Node-Token", NodeToken);

            var report = new NodeBulkReportDto { Discoveries = discoveries };
            var response = await client.PostAsJsonAsync($"{MasterApiUrl.TrimEnd('/')}/api/v1/nodes/report", report);

            if (response.IsSuccessStatusCode)
            {
                _logger?.LogInformation("Successfully reported {Count} keys to Master.", discoveries.Count);
            }
            else
            {
                _logger?.LogWarning("Failed to report bulk discoveries: {Status}", response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Exception in bulk reporting");
        }
    }

    private async Task ReportDiscoveryAsync(NodeReportDto discovery)
    {
        if (string.IsNullOrEmpty(MasterApiUrl) || string.IsNullOrEmpty(NodeToken))
            return;

        try
        {
            using var client = _httpClientFactory.CreateClient(); // use 'using' to ensure disposal
            client.DefaultRequestHeaders.Add("X-Node-Token", NodeToken);

            var report = new NodeBulkReportDto { Discoveries = new List<NodeReportDto> { discovery } };
            var response = await client.PostAsJsonAsync($"{MasterApiUrl.TrimEnd('/')}/api/v1/nodes/report", report);

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<dynamic>();
                // We could track successful reports here
            }
            else
            {
                _logger?.LogWarning("Failed to report discovery to master: {Status}", response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Exception reporting discovery to master");
        }
    }

    private async Task<string?> FetchFileContentAsync(RepoReference repoRef, SearchProviderToken token)
    {
        try
        {
            using var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("UnsecuredAPIKeys-Lite/1.1");
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Token);

            // Build raw content URL from repo info
            // Format: https://raw.githubusercontent.com/{owner}/{repo}/{branch}/{path}
            string? url = null;

            if (!string.IsNullOrEmpty(repoRef.RepoOwner) &&
                !string.IsNullOrEmpty(repoRef.RepoName) &&
                !string.IsNullOrEmpty(repoRef.FilePath))
            {
                var branch = repoRef.Branch ?? "main";
                url = $"https://raw.githubusercontent.com/{repoRef.RepoOwner}/{repoRef.RepoName}/{branch}/{repoRef.FilePath}";
            }

            if (string.IsNullOrEmpty(url))
                return null;

            var response = await client.GetAsync(url, _cancellationTokenSource!.Token);

            // Try 'master' if 'main' fails
            if (!response.IsSuccessStatusCode && repoRef.Branch == null)
            {
                url = $"https://raw.githubusercontent.com/{repoRef.RepoOwner}/{repoRef.RepoName}/master/{repoRef.FilePath}";
                response = await client.GetAsync(url, _cancellationTokenSource!.Token);
            }

            if (!response.IsSuccessStatusCode)
                return null;

            return await response.Content.ReadAsStringAsync(_cancellationTokenSource.Token);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<(string? secretKey, string? sessionToken)> ExtractAwsSecretAndTokenFromContextAsync(string content, string accessKeyId, int matchIndex)
    {
        var contextExtractor = new UnsecuredAPIKeys.Providers.ServerProviders.Services.ContextExtractor();
        var credentialContext = await contextExtractor.ExtractContextAsync(content, matchIndex, 15);
        var context = credentialContext.FullContext;
        if (string.IsNullOrEmpty(context)) return (null, null);

        string? secretKey = null;
        string? sessionToken = null;

        // Named secret key assignment pattern (prevents false positive matches against arbitrary 40-char strings)
        var secretMatch = System.Text.RegularExpressions.Regex.Match(context,
            @"\b(?:AWS_SECRET_ACCESS_KEY|aws_secret_access_key|SecretAccessKey|secret_key|secret)\s*[:=]\s*['""]?([A-Za-z0-9/+=]{20,})['""]?",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(2));

        if (secretMatch.Success)
        {
            secretKey = secretMatch.Groups[1].Value.Trim('\'', '"', ';', ',');
        }

        // Session Token pattern
        var tokenMatch = System.Text.RegularExpressions.Regex.Match(context,
            @"\b(?:AWS_SESSION_TOKEN|aws_session_token|SessionToken|security_token)\s*[:=]\s*['""]?([A-Za-z0-9/+=]{50,})['""]?",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(2));

        if (tokenMatch.Success)
        {
            sessionToken = tokenMatch.Groups[1].Value.Trim('\'', '"', ';', ',');
        }

        return (secretKey, sessionToken);
    }

    private static async Task<string?> ExtractAzureOpenAiEndpointFromContextAsync(string content, int matchIndex)
    {
        var contextExtractor = new UnsecuredAPIKeys.Providers.ServerProviders.Services.ContextExtractor();
        var credentialContext = await contextExtractor.ExtractContextAsync(content, matchIndex, 15);
        var context = credentialContext.FullContext;
        if (string.IsNullOrEmpty(context)) return null;

        var endpointMatch = System.Text.RegularExpressions.Regex.Match(context,
            @"\b(?:AZURE_OPENAI_ENDPOINT|AZURE_ENDPOINT|AZURE_OPENAI_BASE_PATH)\s*[:=]\s*['""]?(https://[a-zA-Z0-9_\-]+\.(?:openai\.azure\.com|cognitiveservices\.azure\.com|ai\.azure\.com))['""]?",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(2));

        if (endpointMatch.Success)
        {
            return endpointMatch.Groups[1].Value.Trim('\'', '"', ';', ',');
        }

        var rawUrlMatch = System.Text.RegularExpressions.Regex.Match(context,
            @"\b(https://[a-zA-Z0-9_\-]+\.(?:openai\.azure\.com|cognitiveservices\.azure\.com|ai\.azure\.com))\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(2));

        if (rawUrlMatch.Success)
        {
            return rawUrlMatch.Groups[1].Value.Trim('\'', '"', ';', ',');
        }

        return null;
    }

    private static async Task<string?> ExtractKlingSecretFromContextAsync(string content, int matchIndex)
    {
        var contextExtractor = new UnsecuredAPIKeys.Providers.ServerProviders.Services.ContextExtractor();
        var credentialContext = await contextExtractor.ExtractContextAsync(content, matchIndex, 15);
        var context = credentialContext.FullContext;
        if (string.IsNullOrEmpty(context)) return null;

        var secretMatch = System.Text.RegularExpressions.Regex.Match(context,
            @"\b(?:KLING_SECRET_KEY|kling_secret_key|KLING_SK|kling_sk|secret_key|secret)\s*[:=]\s*['""]?([A-Za-z0-9]{16,})['""]?",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(2));

        if (secretMatch.Success)
        {
            return secretMatch.Groups[1].Value.Trim('\'', '"', ';', ',');
        }

        return null;
    }
}

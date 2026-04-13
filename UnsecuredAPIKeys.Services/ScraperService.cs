using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using System.Linq;
using UnsecuredAPIKeys.Data;
using UnsecuredAPIKeys.Data.Common;
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
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ScraperService>? _logger;
    private readonly IReadOnlyList<IApiKeyProvider> _providers;
    private CancellationTokenSource? _cancellationTokenSource;

    private int _newKeysFound;
    private int _duplicateKeysFound;

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
        public DateTime SearchStartDate { get; set; }
        public DateTime SearchEndDate { get; set; }
    }

    public ScraperService(DBContext dbContext, IHttpClientFactory httpClientFactory, ILogger<ScraperService>? logger = null)
    {
        _dbContext = dbContext;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _providers = ApiProviderRegistry.ScraperProviders;
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

    public async Task RunScrapeByGroupAsync(string selectedGroupName, bool isDeepSearch, CancellationToken cancellationToken)
    {
        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        
        var tokens = await _dbContext.SearchProviderTokens
            .Where(t => t.IsEnabled && t.SearchProvider == SearchProviderEnum.GitHub)
            .ToListAsync(cancellationToken);

        if (tokens.Count == 0)
        {
            _logger?.LogWarning("No GitHub tokens configured.");
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
            return;
        }

        _logger?.LogInformation("Starting {Mode} scrape for {Count} queries in group {Group}...", 
            isDeepSearch ? "DEEP" : "LITE", queriesToRun.Count, selectedGroupName);

        foreach (var query in queriesToRun)
        {
            if (_cancellationTokenSource.Token.IsCancellationRequested) break;

            if (isDeepSearch)
            {
                await RunDeepSearchAsync(tokens, query, tokenCursor);
            }
            else
            {
                await RunScrapingCycleUtilsAsync(tokens, query, tokenCursor, null);
            }

            if (query != queriesToRun.Last())
            {
                await Task.Delay(LiteLimits.SEARCH_DELAY_MS, _cancellationTokenSource.Token);
            }
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        AnsiConsole.MarkupLine("[cyan]Starting GitHub scraper...[/]");

        // Get GitHub tokens
        var tokens = await _dbContext.SearchProviderTokens
            .Where(t => t.IsEnabled && t.SearchProvider == SearchProviderEnum.GitHub)
            .ToListAsync(cancellationToken);

        if (tokens.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]No GitHub tokens configured. Use 'Configure Settings' to add one.[/]");
            return;
        }

        AnsiConsole.MarkupLine($"[dim]Loaded {tokens.Count} GitHub token(s).[/]");
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
                    AnsiConsole.MarkupLine("[yellow]No search queries defined.[/]");
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

                AnsiConsole.MarkupLine($"\n[green]Starting {(isDeepSearch ? "DEEP" : "LITE")} scrape for {queriesToRun.Count} queries...[/]");
                
                foreach (var query in queriesToRun)
                {
                    if (_cancellationTokenSource.Token.IsCancellationRequested) break;

                    if (isDeepSearch)
                    {
                        await RunDeepSearchAsync(tokens, query, tokenCursor);
                    }
                    else
                    {
                        await RunScrapingCycleUtilsAsync(tokens, query, tokenCursor, null);
                    }

                    // Delay between queries (if not the last one)
                    if (query != queriesToRun.Last())
                    {
                        AnsiConsole.MarkupLine($"[dim]Waiting {LiteLimits.SEARCH_DELAY_MS / 1000}s before next query...[/]");
                        await Task.Delay(LiteLimits.SEARCH_DELAY_MS, _cancellationTokenSource.Token);
                    }
                }

                AnsiConsole.MarkupLine($"[green]Completed scraping for {selectedGroupName}.[/]");
                AnsiConsole.MarkupLine("[dim]Press any key to continue to menu...[/]");
                if (!Console.IsInputRedirected) Console.ReadKey(true);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error during scraping: {Markup.Escape(ex.Message)}[/]");
                _logger?.LogError(ex, "Scraping cycle error");
                await Task.Delay(2000, _cancellationTokenSource.Token);
            }
        }
    }

    private string InferProviderFromQuery(string query)
    {
        var q = query.ToLower();
        if (q.Contains("openai") || q.Contains("sk-") || q.Contains("gpt")) return "OpenAI";
        if (q.Contains("anthropic") || q.Contains("claude")) return "Anthropic";
        if (q.Contains("google") || q.Contains("gemini") || q.Contains("aizasy")) return "Google";
        if (q.Contains("kling")) return "KlingAI";
        if (q.Contains("pollo")) return "PolloAI";
        if (q.Contains("runway") || q.Contains("key_")) return "RunwayML";
        if (q.Contains("deepseek")) return "DeepSeek";
        if (q.Contains("cohere")) return "Cohere";
        if (q.Contains("eleven") || q.Contains("xi-")) return "ElevenLabs";
        if (q.Contains("stability")) return "StabilityAI";
        if (q.Contains("together")) return "TogetherAI";
        if (q.Contains("xai")) return "XAI";
        if (q.Contains("replicate") || q.Contains("r8_")) return "Replicate";
        if (q.Contains("fireworks") || q.Contains("fw_")) return "Fireworks";
        if (q.Contains("hugging") || q.Contains("hf_")) return "HuggingFace";
        return "Other";
    }

    private async Task RunDeepSearchAsync(List<SearchProviderToken> tokens, SearchQuery query, TokenCursor cursor)
    {
        // Deep search strategy using language and file extension filters with progress tracking
        
        AnsiConsole.MarkupLine("\n[cyan]═══ Deep Search Configuration ═══[/]");
        AnsiConsole.MarkupLine("[yellow]Deep Search will partition results by language and file type to bypass the 1000-result limit.[/]");
        AnsiConsole.MarkupLine("[dim]Note: GitHub Code Search doesn't support date filtering.[/]\n");
        
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
            AnsiConsole.MarkupLine("[yellow]Found existing progress for this query:[/]\n");
            
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
                _dbContext.DeepSearchProgress.RemoveRange(existingProgress);
                await _dbContext.SaveChangesAsync(_cancellationTokenSource.Token);
                existingProgress.Clear();
                AnsiConsole.MarkupLine("[green]Progress cleared. Starting fresh...[/]\n");
            }
            else
            {
                AnsiConsole.MarkupLine("[green]Resuming from last position...[/]\n");
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
        
        AnsiConsole.MarkupLine($"[yellow]Starting deep search for: {Markup.Escape(query.Query)}[/]");
        AnsiConsole.MarkupLine($"[dim]Will search across {languages.Length} languages and {extensions.Length} file types[/]\n");
        
        // Search by language
        foreach (var language in languages)
        {
            if (_cancellationTokenSource.Token.IsCancellationRequested) break;
            
            await SearchPartitionAsync(tokens, query, cursor, "language", language, stats);
        }
        
        // Search by file extension
        foreach (var extension in extensions)
        {
            if (_cancellationTokenSource.Token.IsCancellationRequested) break;
            
            await SearchPartitionAsync(tokens, query, cursor, "extension", extension, stats);
        }
        
        // Display summary
        AnsiConsole.MarkupLine("\n[cyan]═══ Deep Search Summary ═══[/]");
        var summaryTable = new Table().Border(TableBorder.Rounded);
        summaryTable.AddColumn("[bold]Metric[/]");
        summaryTable.AddColumn("[bold]Value[/]");
        
        summaryTable.AddRow("Query", Markup.Escape(query.Query));
        summaryTable.AddRow("Search Partitions", $"{languages.Length} languages + {extensions.Length} extensions");
        summaryTable.AddRow("Total Searches", stats.TotalRangesSearched.ToString());
        summaryTable.AddRow("Total Results Found", $"[green]{stats.TotalResultsFound}[/]");
        summaryTable.AddRow("New Keys Discovered", $"[green]{_newKeysFound}[/]");
        
        AnsiConsole.Write(summaryTable);
        AnsiConsole.WriteLine();
    }

    private async Task SearchPartitionAsync(List<SearchProviderToken> tokens, SearchQuery query, TokenCursor cursor, string partitionType, string partitionValue, DeepSearchStats stats)
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
            _dbContext.DeepSearchProgress.Add(progress);
            await _dbContext.SaveChangesAsync(_cancellationTokenSource.Token);
        }
        
        // Skip if already completed
        if (progress.IsCompleted)
        {
            AnsiConsole.MarkupLine($"[dim]→ Skipping {partitionValue} ({partitionType}) - already completed[/]");
            return;
        }
        
        string filter = $"{partitionType}:{partitionValue}";
        int startPage = progress.LastPageSearched + 1;
        
        AnsiConsole.MarkupLine($"[dim]→ Searching {partitionValue} ({partitionType}) from page {startPage}...[/]");
        
        stats.TotalRangesSearched++;
        int resultCount = await RunScrapingCycleUtilsAsync(tokens, query, cursor, filter, startPage);
        
        // Update progress
        progress.TotalResultsFound += resultCount;
        progress.LastSearchedUTC = DateTime.UtcNow;
        
        // Mark as completed if we got less than 1000 results (hit the end)
        if (resultCount < 1000)
        {
            progress.IsCompleted = true;
            progress.LastPageSearched = startPage + (resultCount / 100); // Approximate last page
        }
        else
        {
            progress.LastPageSearched = startPage + 9; // Searched 10 pages (1000 results)
        }
        
        stats.TotalResultsFound += resultCount;
        await _dbContext.SaveChangesAsync(_cancellationTokenSource.Token);
        
        // Small delay between searches
        if (resultCount > 0)
            await Task.Delay(1000, _cancellationTokenSource.Token);
    }

    private async Task<int> RunScrapingCycleUtilsAsync(List<SearchProviderToken> tokens, SearchQuery query, TokenCursor cursor, string? extraParams, int startPage = 1)
    {
        int retryCount = 0;
        bool querySuccess = false;
        int totalResults = 0;

        while (!querySuccess && retryCount < tokens.Count)
        {
            var currentToken = tokens[cursor.Index];
            try
            {
               totalResults = await RunScrapingCycleAsync(currentToken, query, extraParams, startPage);
               querySuccess = true;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[yellow]Error with current token: {Markup.Escape(ex.Message)}. Switching token...[/]");
                cursor.Index = (cursor.Index + 1) % tokens.Count;
                retryCount++;

                if (retryCount >= tokens.Count)
                {
                    AnsiConsole.MarkupLine("[red]All tokens exhausted or failed for this query.[/]");
                }
            }
        }
        return totalResults;
    }

    private async Task<int> RunScrapingCycleAsync(SearchProviderToken token, SearchQuery query, string? extraParams, int startPage = 1)
    {
        // Reset counters for this cycle
        _newKeysFound = 0;
        _duplicateKeysFound = 0;

        string displayQuery = query.Query + (extraParams != null ? $" {extraParams}" : "");
        AnsiConsole.MarkupLine($"[cyan]Searching: {Markup.Escape(displayQuery)}[/]");

        // Update last search time
        query.LastSearchUTC = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(_cancellationTokenSource!.Token);

        // Search GitHub
        var searchProvider = new GitHubSearchProvider(_dbContext);
        IEnumerable<RepoReference>? results;

        try
        {
            results = await searchProvider.SearchAsync(query, token, extraParams, startPage);
        }
        catch (Exception)
        {
            // Re-throw so the main loop can handle token rotation
            throw;
        }

        if (results == null)
        {
            AnsiConsole.MarkupLine("[yellow]No results from search.[/]");
            return 0;
        }

        var resultsList = results.ToList();
        
        AnsiConsole.MarkupLine($"[dim]Fetched {resultsList.Count} matches[/]");

        // Process each result
        await AnsiConsole.Progress()
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask($"[cyan]Processing results[/]", maxValue: resultsList.Count);

                foreach (var repoRef in resultsList)
                {
                    if (_cancellationTokenSource!.Token.IsCancellationRequested)
                        break;

                    await ProcessResultAsync(repoRef, token, query);
                    task.Increment(1);
                }
            });

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
        return resultsList.Count;
    }

    private async Task ProcessResultAsync(RepoReference repoRef, SearchProviderToken token, SearchQuery query)
    {
        try
        {
            // Get file content
            var content = await FetchFileContentAsync(repoRef, token);
            if (string.IsNullOrEmpty(content))
                return;

            // Search for API keys using all provider patterns
            foreach (var provider in _providers)
            {
                foreach (var pattern in provider.RegexPatterns)
                {
                    var regex = new System.Text.RegularExpressions.Regex(pattern);
                    var matches = regex.Matches(content);

                    foreach (System.Text.RegularExpressions.Match match in matches)
                    {
                        var apiKey = match.Groups.Count > 1 ? match.Groups[1].Value : match.Value;

                        // Special handling for Kling AI to find Access and Secret keys together
                        if (provider.ApiType == ApiTypeEnum.KlingAI && !apiKey.Contains(':'))
                        {
                            var secretMatch = System.Text.RegularExpressions.Regex.Match(content, 
                                @"(?:KLING|kling).*?(?:SECRET|secret|sk).*?['""]([a-zA-Z0-9]{16,})['""]", 
                                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                                
                            if (secretMatch.Success)
                            {
                                apiKey = $"{apiKey}:{secretMatch.Groups[1].Value}";
                            }
                        }

                        // Check if already exists
                        var exists = await _dbContext.APIKeys
                            .AnyAsync(k => k.ApiKey == apiKey, _cancellationTokenSource!.Token);

                        if (exists)
                        {
                            Interlocked.Increment(ref _duplicateKeysFound);
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
                            LastFoundUTC = DateTime.UtcNow
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

                        _dbContext.APIKeys.Add(newKey);
                        await _dbContext.SaveChangesAsync(_cancellationTokenSource!.Token);

                        Interlocked.Increment(ref _newKeysFound);
                        AnsiConsole.MarkupLine($"[green]+ New {Markup.Escape(provider.ProviderName)} key found![/]");
                        AnsiConsole.MarkupLine($"  [dim]Source: {Markup.Escape(repoRef.FileURL ?? "Unknown")}[/]");
                        AnsiConsole.MarkupLine($"  [dim]Repo: {Markup.Escape(repoRef.RepoURL ?? "Unknown")}[/]");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error processing result: {Url}", repoRef.FileURL);
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
}

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using Spectre.Console;
using System.Text.Json;

using UnsecuredAPIKeys.Services;
using UnsecuredAPIKeys.Data;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Data.Models;

// Initialize services
var services = new ServiceCollection();
services.AddLogging(builder => builder
    .SetMinimumLevel(LogLevel.Warning)
    .AddConsole());
services.AddHttpClient();
services.AddMemoryCache();

// Server Credential Services
services.AddSingleton<UnsecuredAPIKeys.Providers.ServerProviders.Services.IContextExtractor, UnsecuredAPIKeys.Providers.ServerProviders.Services.ContextExtractor>();
services.AddSingleton<UnsecuredAPIKeys.Providers.ServerProviders.Services.IEntropyAnalyzer, UnsecuredAPIKeys.Providers.ServerProviders.Services.EntropyAnalyzer>();
services.AddSingleton<UnsecuredAPIKeys.Providers.ServerProviders.Services.INetworkVerifier, UnsecuredAPIKeys.Providers.ServerProviders.Services.NetworkVerifier>();
services.AddSingleton<UnsecuredAPIKeys.Providers.ServerProviders.Services.IAuthenticationVerifier, UnsecuredAPIKeys.Providers.ServerProviders.Services.AuthenticationVerifier>();
services.AddSingleton<UnsecuredAPIKeys.Providers.ServerProviders.Services.IOSINTService, UnsecuredAPIKeys.Providers.ServerProviders.Services.OSINTService>();
services.AddSingleton<UnsecuredAPIKeys.Providers.ServerProviders.Services.IGeolocationService, UnsecuredAPIKeys.Providers.ServerProviders.Services.GeolocationService>();
services.AddSingleton<UnsecuredAPIKeys.Providers.ServerProviders.Services.AdaptiveIOManager>();
services.AddSingleton<UnsecuredAPIKeys.Providers.ServerProviders.Services.RenderOptimizer>();
services.AddSingleton<UnsecuredAPIKeys.Providers.ServerProviders.Services.VerificationQueue>();
services.AddSingleton<UnsecuredAPIKeys.Providers.ServerProviders.Services.HostCircuitBreaker>();

await using var serviceProvider = services.BuildServiceProvider();
var httpClientFactory = serviceProvider.GetRequiredService<IHttpClientFactory>();

// Initialize database
var dbService = new DatabaseService(AppInfo.DatabaseName);
DBContext? dbContext = null;

try
{
    dbContext = await dbService.InitializeDatabaseAsync();
}
catch (Exception ex)
{
    AnsiConsole.MarkupLine($"[red]Failed to initialize database: {Markup.Escape(ex.Message)}[/]");
    return;
}

// Display banner
DisplayBanner();

// Main menu loop
var running = true;
while (running)
{
    var choice = AnsiConsole.Prompt(
        new SelectionPrompt<string>()
            .Title("[yellow]What would you like to do?[/]")
            .PageSize(10)
            .AddChoices(new[]
            {
                "1. Start Scraper (search GitHub for keys)",
                "2. Start Verifier (maintain valid keys)",
                "3. View Status",
                "4. Configure Settings",
                "5. Export Keys",
                "6. Exit"
            }));

    AnsiConsole.WriteLine();

    switch (choice[0])
    {
        case '1':
            await RunScraperAsync(dbContext, httpClientFactory);
            break;
        case '2':
            await RunVerifierAsync(dbContext, httpClientFactory);
            break;
        case '3':
            await ShowStatusMenuAsync(dbContext, dbService);
            break;
        case '4':

            if (dbContext != null)
            {
                var shouldReset = await ConfigureSettingsAsync(dbContext, dbService);
                if (shouldReset)
                {
                    // Dispose context to release file lock
                    dbContext.Dispose();
                    dbContext = null;
                    
                    // Perform reset
                    await AnsiConsole.Status()
                        .Spinner(Spinner.Known.Dots)
                        .SpinnerStyle(Style.Parse("red"))
                        .StartAsync("Resetting database...", async ctx =>
                        {
                            await dbService.ResetDatabaseAsync();
                        });

                    // Re-initialize
                    dbContext = await dbService.InitializeDatabaseAsync();
                    AnsiConsole.MarkupLine("[green]Database reset complete.[/]");
                }
            }
            break;
        case '5':
            await ExportKeysAsync(dbContext, dbService);
            break;
        case '6':
            running = false;
            break;
    }

    if (running)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[dim]Press any key to continue...[/]");
        Console.ReadKey(true);
        AnsiConsole.Clear();
        DisplayBanner();
    }
}

AnsiConsole.MarkupLine("[green]Goodbye![/]");
dbContext?.Dispose();

// === Helper Methods ===

void DisplayBanner()
{
    AnsiConsole.Write(
        new FigletText(AppInfo.Name)
            .LeftJustified()
            .Color(Color.Cyan1));

    AnsiConsole.MarkupLine($"[dim]Version: {AppInfo.Version} [green](v1.3 Release)[/][/]");
    AnsiConsole.MarkupLine($"[dim]Modified by: [cyan]Rajiv[/][/]");
    AnsiConsole.MarkupLine($"[dim]Full Credit to: [cyan]https://github.com/TSCarterJr/UnsecuredAPIKeys-OpenSource[/][/]");
    AnsiConsole.MarkupLine($"[dim]Valid key limit: [yellow]{LiteLimits.MAX_VALID_KEYS}[/][/]");
    AnsiConsole.WriteLine();

    // Educational purpose notice
    var warningPanel = new Panel(
        "[yellow]This tool is for EDUCATIONAL PURPOSES ONLY.[/]\n\n" +
        "If you discover exposed API keys, please help secure them:\n" +
        "  [green]1.[/] Notify the owner\n" +
        "  [green]2.[/] Never use keys for unauthorized access\n" +
        "  [green]3.[/] Do NOT publish your results publicly\n\n" +
        "[dim]Help make the internet more secure by reporting, not exploiting.[/]")
        .Header("[yellow]Educational Use Only[/]")
        .Border(BoxBorder.Rounded)
        .BorderColor(Color.Yellow);

    AnsiConsole.Write(warningPanel);
    AnsiConsole.WriteLine();
}

async Task RunScraperAsync(DBContext db, IHttpClientFactory factory)
{
    AnsiConsole.Write(new Rule("[cyan]GitHub Scraper[/]").RuleStyle("cyan"));
    AnsiConsole.MarkupLine("[dim]Searches GitHub for exposed API keys. Runs continuously.[/]");
    AnsiConsole.MarkupLine("[dim]Press [yellow]Ctrl+C[/] to stop.[/]");
    AnsiConsole.WriteLine();

    using var cts = new CancellationTokenSource();
    ConsoleCancelEventHandler handler = (s, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
        AnsiConsole.MarkupLine("\n[yellow]Stopping scraper...[/]");
    };

    Console.CancelKeyPress += handler;
    try
    {
        var scraper = new ScraperService(db, factory);
        await scraper.RunAsync(cts.Token);
    }
    finally
    {
        Console.CancelKeyPress -= handler;
    }
}

async Task RunVerifierAsync(DBContext db, IHttpClientFactory factory)
{
    AnsiConsole.Write(new Rule("[green]Key Verifier[/]").RuleStyle("green"));
    AnsiConsole.MarkupLine($"[dim]Maintains up to [yellow]{LiteLimits.MAX_VALID_KEYS}[/] valid keys.[/]");
    AnsiConsole.MarkupLine("[dim]Re-checks valid keys and verifies new ones as needed.[/]");
    AnsiConsole.WriteLine();

    // Show categorized statistics first
    var catStats = await dbService.GetCategorizedStatisticsAsync(db);
    
    // Ask user to select API types to verify
    var choices = new List<string> { "[yellow]All API Types[/]" };

    // Get all supported verifier providers
    var allProviders = UnsecuredAPIKeys.Providers.ApiProviderRegistry.VerifierProviders;
    
    // Group by category
    var providersByCategory = allProviders
        .GroupBy(p => DatabaseService.GetCategoryForApiType(p.ApiType))
        .OrderBy(g => g.Key);

    foreach (var group in providersByCategory)
    {
        if (group.Key == ApiCategoryEnum.Unknown) continue;

        var categoryName = DatabaseService.GetCategoryName(group.Key);
        choices.Add($"[dim]--- {categoryName} ---[/]");

        foreach (var provider in group.OrderBy(p => p.ProviderName))
        {
            // Find stats for this provider
            var count = 0;
            if (catStats.Categories.TryGetValue(group.Key, out var catStat))
            {
                var typeStat = catStat.ApiTypes.FirstOrDefault(t => t.ApiType == provider.ApiType);
                if (typeStat != null)
                {
                    count = typeStat.KeyCount;
                }
            }

            choices.Add($"{provider.ApiType} ({count} keys)");
        }
    }

    var selectedChoices = AnsiConsole.Prompt(
        new MultiSelectionPrompt<string>()
            .Title("[yellow]Select API types to verify:[/]")
            .PageSize(20)
            .InstructionsText("[dim](Press [blue]space[/] to select, [green]enter[/] to confirm)[/]")
            .AddChoices(choices));

    HashSet<ApiTypeEnum>? selectedTypes = null;
    
    if (!selectedChoices.Contains("[yellow]All API Types[/]"))
    {
        selectedTypes = new HashSet<ApiTypeEnum>();
        foreach (var choice in selectedChoices)
        {
            // Skip category headers
            if (choice.Contains("---")) continue;
            
            // Extract API type name from "TypeName (X keys)"
            var typeName = choice.Split('(')[0].Trim();
            if (Enum.TryParse<ApiTypeEnum>(typeName, out var apiType))
            {
                selectedTypes.Add(apiType);
            }
        }
    }

    AnsiConsole.WriteLine();
    var verifierMode = AnsiConsole.Prompt(
        new SelectionPrompt<string>()
            .Title("[yellow]Select verifier operation mode:[/]")
            .AddChoices(new[]
            {
                "1. Verify Unverified Keys (Scan & find new valid keys)",
                "2. Re-verify Valid Keys (Re-check if current valid keys still work/exist)"
            }));

    bool reVerifyOnly = verifierMode.StartsWith("2");

    AnsiConsole.WriteLine();
    AnsiConsole.MarkupLine("[dim]Press [yellow]Ctrl+C[/] to stop.[/]");
    AnsiConsole.WriteLine();

    using var cts = new CancellationTokenSource();
    ConsoleCancelEventHandler handler = (s, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
        AnsiConsole.MarkupLine("\n[yellow]Stopping verifier...[/]");
    };

    Console.CancelKeyPress += handler;
    try
    {
        var verifier = new VerifierService(db, factory, selectedTypes, reVerifyOnly);
        await verifier.RunAsync(cts.Token);
    }
    finally
    {
        Console.CancelKeyPress -= handler;
    }
}

async Task ShowStatusAsync(DBContext db, DatabaseService dbService)
{
    AnsiConsole.Write(new Rule("[blue]Current Status[/]").RuleStyle("blue"));

    var catStats = await AnsiConsole.Status()
        .Spinner(Spinner.Known.Dots)
        .SpinnerStyle(Style.Parse("blue"))
        .StartAsync("Loading statistics...", async ctx =>
        {
            return await dbService.GetCategorizedStatisticsAsync(db);
        });

    // Create summary table
    var summaryTable = new Table()
        .Border(TableBorder.Rounded)
        .BorderColor(Color.Grey)
        .AddColumn(new TableColumn("[bold]Metric[/]").LeftAligned())
        .AddColumn(new TableColumn("[bold]Value[/]").RightAligned());

    summaryTable.AddRow("Total Keys Found", catStats.TotalKeys.ToString());
    summaryTable.AddRow("Valid Keys", $"[green]{catStats.ValidKeys}[/] / [yellow]{LiteLimits.MAX_VALID_KEYS}[/]");
    summaryTable.AddRow("Valid (No Credits)", $"[yellow]{catStats.ValidNoCreditsKeys}[/]");
    summaryTable.AddRow("Invalid Keys", $"[red]{catStats.InvalidKeys}[/]");
    summaryTable.AddRow("Pending Verification", $"[blue]{catStats.UnverifiedKeys}[/]");
    summaryTable.AddRow(new Rule().RuleStyle("dim"));

    // Server Credentials counts
    var totalCreds = await db.ServerCredentials.CountAsync();
    var validCreds = await db.ServerCredentials.CountAsync(c => c.AuthenticationStatus == "Valid");
    var invalidCreds = await db.ServerCredentials.CountAsync(c => c.AuthenticationStatus == "Invalid");
    var untestedCreds = await db.ServerCredentials.CountAsync(c => c.AuthenticationStatus == "Untested");
    var honeypotsCount = await db.ServerCredentials.CountAsync(c => c.IsHoneypot);

    summaryTable.AddRow("Total Server Credentials", totalCreds.ToString());
    summaryTable.AddRow("  Valid Credentials", $"[green]{validCreds}[/]");
    summaryTable.AddRow("  Invalid Credentials", $"[red]{invalidCreds}[/]");
    summaryTable.AddRow("  Untested Credentials", $"[blue]{untestedCreds}[/]");
    summaryTable.AddRow("  Flagged Honeypots", $"[yellow]{honeypotsCount}[/]");
    summaryTable.AddRow(new Rule().RuleStyle("dim"));

    summaryTable.AddRow("Database", $"[dim]{Markup.Escape(AppInfo.DatabaseName)}[/]");
    summaryTable.AddRow("GitHub Tokens", catStats.GitHubTokensCount > 0 ? $"[green]{catStats.GitHubTokensCount} Configured[/]" : "[red]Not configured[/]");

    AnsiConsole.Write(summaryTable);

    // Show recent server credentials
    var recentCreds = await db.ServerCredentials
        .OrderByDescending(c => c.DiscoveredAt)
        .Take(20)
        .ToListAsync();

    if (recentCreds.Any())
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[cyan]Recent Server Credentials[/]").LeftJustified().RuleStyle("cyan"));
        DisplayServerCredentials(recentCreds);
    }

    // Show valid keys list
    var validKeysList = await db.APIKeys
        .Where(k => k.Status == ApiStatusEnum.Valid || k.Status == ApiStatusEnum.ValidNoCredits)
        .OrderByDescending(k => k.LastCheckedUTC)
        .Take(20)
        .ToListAsync();

    if (validKeysList.Any())
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[green]Valid Keys[/]").LeftJustified().RuleStyle("green"));
        DisplayValidKeys(validKeysList);
    }

    // Show top keys with balance
    var richKeys = await db.APIKeys
        .Where(k => !string.IsNullOrEmpty(k.Balance))
        .OrderByDescending(k => k.LastCheckedUTC)
        .Take(5)
        .ToListAsync();

    if (richKeys.Any())
    {
        AnsiConsole.WriteLine();
        var richTable = new Table().Border(TableBorder.Rounded).BorderColor(Color.Green);
        richTable.Title("[bold green]Top Detected Balances[/]");
        richTable.AddColumn("Type");
        richTable.AddColumn("Tier");
        richTable.AddColumn("Balance");

        foreach (var key in richKeys)
        {
            richTable.AddRow(key.ApiType.ToString(), key.AccountTier ?? "N/A", $"[bold green]{key.Balance}[/]");
        }
        AnsiConsole.Write(richTable);
    }

    AnsiConsole.WriteLine();

    // ── Session Metrics ──────────────────────────────────────────────────────
    var metrics = UnsecuredAPIKeys.Services.MetricsService.Instance.GetSnapshot();
    if (metrics.TotalFilesScanned > 0 || metrics.TotalKeysVerified > 0)
    {
        AnsiConsole.Write(new Rule("[dim]Session Metrics[/]").LeftJustified().RuleStyle("dim"));

        var metricsTable = new Table()
            .Border(TableBorder.None)
            .HideHeaders()
            .AddColumn(new TableColumn("").LeftAligned())
            .AddColumn(new TableColumn("").RightAligned());

        metricsTable.AddRow("[dim]Uptime[/]",            $"[dim]{metrics.SessionUptime:hh\\:mm\\:ss}[/]");
        metricsTable.AddRow("[dim]Files Scanned[/]",     $"[dim]{metrics.TotalFilesScanned:N0}[/]");
        metricsTable.AddRow("[dim]Keys Found[/]",        $"[green]{metrics.TotalKeysFound:N0}[/]");
        metricsTable.AddRow("[dim]Duplicates Skipped[/]",$"[dim]{metrics.TotalDuplicatesSkipped:N0}[/]");
        metricsTable.AddRow("[dim]GitHub Requests[/]",   $"[dim]{metrics.TotalGitHubRequests:N0}[/]");
        metricsTable.AddRow("[dim]Rate Limit Hits[/]",   metrics.TotalGitHubRateLimitHits > 0
            ? $"[yellow]{metrics.TotalGitHubRateLimitHits}[/]" : "[dim]0[/]");
        metricsTable.AddRow("[dim]Keys Verified[/]",     $"[dim]{metrics.TotalKeysVerified:N0}[/]");
        metricsTable.AddRow("[dim]Valid Found[/]",       $"[green]{metrics.TotalValidFound:N0}[/]");
        metricsTable.AddRow("[dim]Invalid Found[/]",     $"[red]{metrics.TotalInvalidFound:N0}[/]");
        metricsTable.AddRow("[dim]Network Errors[/]",    metrics.TotalNetworkErrors > 0
            ? $"[red]{metrics.TotalNetworkErrors}[/]" : "[dim]0[/]");

        AnsiConsole.Write(metricsTable);

        if (metrics.ProviderLatencies.Count > 0)
        {
            AnsiConsole.WriteLine();
            var latencyTable = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey);
            latencyTable.Title("[dim]Provider Avg. Latency (this session)[/]");
            latencyTable.AddColumn("Provider");
            latencyTable.AddColumn("Avg (ms)");
            latencyTable.AddColumn("Calls");

            foreach (var p in metrics.ProviderLatencies.Take(8))
            {
                var color = p.AverageMs < 1000 ? "green" : p.AverageMs < 3000 ? "yellow" : "red";
                latencyTable.AddRow(
                    p.ProviderName,
                    $"[{color}]{p.AverageMs}[/]",
                    p.TotalCalls.ToString());
            }
            AnsiConsole.Write(latencyTable);
        }

        AnsiConsole.WriteLine();
    }

    // Display categorized breakdown
    foreach (var category in catStats.Categories.OrderBy(c => c.Key))
    {
        if (category.Key == ApiCategoryEnum.Unknown) continue;
        
        var categoryStats = category.Value;
        if (categoryStats.TotalKeys == 0) continue;

        AnsiConsole.Write(new Rule($"[cyan]{categoryStats.CategoryName}[/]").LeftJustified().RuleStyle("cyan dim"));
        
        var categoryTable = new Table()
            .Border(TableBorder.None)
            .HideHeaders()
            .AddColumn(new TableColumn("").LeftAligned())
            .AddColumn(new TableColumn("").RightAligned());

        foreach (var apiType in categoryStats.ApiTypes)
        {
            categoryTable.AddRow(
                $"  [dim]{apiType.ApiTypeName}[/]",
                $"[yellow]{apiType.KeyCount}[/] keys"
            );
        }

        AnsiConsole.Write(categoryTable);
        AnsiConsole.WriteLine();
    }
}

async Task<bool> ConfigureSettingsAsync(DBContext db, DatabaseService dbService)
{
    AnsiConsole.Write(new Rule("[magenta]Configuration[/]").RuleStyle("magenta"));

    var configChoice = AnsiConsole.Prompt(
        new SelectionPrompt<string>()
            .Title("[yellow]What would you like to configure?[/]")
            .AddChoices(new[]
            {
                "1. Manage GitHub Tokens",
                "2. View Current Settings",
                "3. Reset Database",
                "4. Back to Main Menu"
            }));

    switch (configChoice[0])
    {
        case '1':
            await ManageGitHubTokensAsync(db, dbService);
            return false;
        case '2':
            await ShowCurrentSettingsAsync(db, dbService);
            return false;
        case '3':
            return await ConfirmResetAsync();
    }
    return false;
}

async Task ManageGitHubTokensAsync(DBContext db, DatabaseService dbService)
{
    while (true)
    {
        AnsiConsole.Clear();
        AnsiConsole.Write(new Rule("[cyan]Manage GitHub Tokens[/]").RuleStyle("cyan"));
        
        var tokens = await dbService.GetGitHubTokensAsync(db);
        
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("ID");
        table.AddColumn("Token (Masked)");
        table.AddColumn("Last Used");
        
        foreach (var t in tokens)
        {
            var masked = t.Token.Length > 8 
                ? $"{t.Token.Substring(0, 4)}...{t.Token.Substring(t.Token.Length - 4)}" 
                : "****";
            table.AddRow(t.Id.ToString(), masked, t.LastUsedUTC?.ToString("g") ?? "Never");
        }
        
        if (tokens.Count == 0)
        {
            table.AddRow("-", "[dim]No tokens found[/]", "-");
        }
        
        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();

        var choice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[yellow]Token Options:[/]")
                .AddChoices(new[]
                {
                    "1. Add New Token",
                    "2. Remove Token",
                    "3. Back to Configuration"
                }));

        if (choice.StartsWith("3")) break;

        if (choice.StartsWith("1"))
        {
            await SetGitHubTokenAsync(db, dbService);
        }
        else if (choice.StartsWith("2"))
        {
             if (tokens.Count == 0)
             {
                 AnsiConsole.MarkupLine("[red]No tokens to remove.[/]");
                 await Task.Delay(1000);
                 continue;
             }

             var tokenChoices = tokens.Select(t => $"{t.Id}").ToList();
             tokenChoices.Add("Cancel");

             var selectedId = AnsiConsole.Prompt(
                 new SelectionPrompt<string>()
                     .Title("Select Token ID to remove:")
                     .AddChoices(tokenChoices));

             if (selectedId != "Cancel" && int.TryParse(selectedId, out int id))
             {
                 await dbService.DeleteGitHubTokenAsync(db, id);
                 AnsiConsole.MarkupLine($"[green]Token {id} removed.[/]");
                 await Task.Delay(1000);
             }
        }
    }
}

async Task SetGitHubTokenAsync(DBContext db, DatabaseService dbService)
{
    AnsiConsole.MarkupLine("[dim]Enter your GitHub Personal Access Token.[/]");
    AnsiConsole.MarkupLine("[dim]Create one at: https:[[//]]github.com[[/]]settings[[/]]tokens[/]");
    AnsiConsole.MarkupLine("[dim]Required scopes: [yellow]public_repo[/] (for searching public repos)[/]");
    AnsiConsole.WriteLine();

    var token = AnsiConsole.Prompt(
        new TextPrompt<string>("[green]GitHub Token:[/]")
            .Secret());

    if (string.IsNullOrWhiteSpace(token))
    {
        AnsiConsole.MarkupLine("[red]Token cannot be empty.[/]");
        return;
    }

    // Validate token format
    if (!token.StartsWith("ghp_") && !token.StartsWith("github_pat_"))
    {
        var proceed = AnsiConsole.Confirm(
            "[yellow]Token doesn't match expected GitHub token format. Save anyway?[/]",
            false);

        if (!proceed) return;
    }

    await dbService.AddGitHubTokenAsync(db, token);
    AnsiConsole.MarkupLine("[green]GitHub token added successfully![/]");
    await Task.Delay(1000);
}

async Task ShowCurrentSettingsAsync(DBContext db, DatabaseService dbService)
{
    var stats = await dbService.GetStatisticsAsync(db);

    var table = new Table()
        .Border(TableBorder.Rounded)
        .BorderColor(Color.Grey)
        .AddColumn("[bold]Setting[/]")
        .AddColumn("[bold]Value[/]");

    var dbPath = Path.Combine(Environment.CurrentDirectory, AppInfo.DatabaseName);
    table.AddRow("Database Path", Markup.Escape(dbPath));
    table.AddRow("GitHub Tokens", stats.GitHubTokensCount > 0 ? $"[green]{stats.GitHubTokensCount} Configured[/]" : "[red]Not configured[/]");
    table.AddRow("Max Valid Keys", LiteLimits.MAX_VALID_KEYS.ToString());
    table.AddRow("Supported Providers", "OpenAI, Anthropic, Google, Replicate, Fireworks, HuggingFace");

    AnsiConsole.Write(table);
}

async Task<bool> ConfirmResetAsync()
{
    var confirm = AnsiConsole.Confirm(
        "[red]Are you sure you want to reset the database? All data will be lost![/]",
        false);

    if (!confirm)
    {
        AnsiConsole.MarkupLine("[dim]Database reset cancelled.[/]");
        return false;
    }

    var doubleConfirm = AnsiConsole.Confirm(
        "[red]This action is irreversible. Are you absolutely sure?[/]",
        false);

    if (!doubleConfirm)
    {
        AnsiConsole.MarkupLine("[dim]Database reset cancelled.[/]");
        return false;
    }

    return true;
}

async Task ExportKeysAsync(DBContext db, DatabaseService dbService)
{
    AnsiConsole.Write(new Rule("[yellow]Export Data[/]").RuleStyle("yellow"));

    var exportChoice = AnsiConsole.Prompt(
        new SelectionPrompt<string>()
            .Title("[yellow]Select what and how to export:[/]")
            .AddChoices(new[]
            {
                "1. API Keys (JSON)",
                "2. API Keys (CSV)",
                "3. Server Credentials (CSV)",
                "4. Server Credentials (JSON)",
                "5. Back to Main Menu"
            }));

    if (exportChoice.StartsWith("5")) return;

    if (exportChoice.StartsWith("3") || exportChoice.StartsWith("4"))
    {
        var format = exportChoice.StartsWith("3") ? "csv" : "json";
        var defaultFileName = exportChoice.StartsWith("3") ? "credentials.csv" : "credentials.json";

        var filterType = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[yellow]Filter by Credential Type:[/]")
                .AddChoices(new[] { "All", "SSH", "FTP", "SFTP", "RDP", "SMTP", "MySQL", "PostgreSQL", "MongoDB", "Redis", "MSSQL", "cPanel_HTTPS", "WHM_HTTPS", "Plesk" }));

        var filterRisk = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[yellow]Filter by Risk Level:[/]")
                .AddChoices(new[] { "All", "Critical", "High", "Medium", "Low" }));

        var filterAuth = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[yellow]Filter by Auth Status:[/]")
                .AddChoices(new[] { "All", "Valid", "Invalid", "RateLimited", "Untested" }));

        var fileName = AnsiConsole.Prompt(
            new TextPrompt<string>("[green]Output file name:[/]")
                .DefaultValue(defaultFileName));

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("yellow"))
            .StartAsync($"Exporting to {Markup.Escape(fileName)}...", async ctx =>
            {
                await dbService.ExportServerCredentialsAsync(db, fileName, format, filterType, filterRisk, filterAuth);
            });

        AnsiConsole.MarkupLine($"[green]Exported to [bold]{Markup.Escape(fileName)}[/][/]");
        return;
    }

    var filterChoice = AnsiConsole.Prompt(
        new SelectionPrompt<string>()
            .Title("[yellow]Filter keys:[/]")
            .AddChoices(new[]
            {
                "1. Valid Only (With Credits)",
                "2. Valid (No Credits) Only",
                "3. Both (All working keys)",
                "4. All (Including Invalid/Unverified)"
            }));

    ApiStatusEnum? statusFilter = null;
    switch (filterChoice[0])
    {
        case '1': statusFilter = ApiStatusEnum.Valid; break;
        case '2': statusFilter = ApiStatusEnum.ValidNoCredits; break;
        case '3': statusFilter = null; break; // null in DatabaseService means BOTH Valid and ValidNoCredits
        case '4': statusFilter = (ApiStatusEnum)(-1); break; // Special case for ALL
    }

    var formatKeys = exportChoice.StartsWith("1") ? "json" : "csv";
    var defaultFileNameKeys = exportChoice.StartsWith("1") ? "keys.json" : "keys.csv";
    var fileNameKeys = AnsiConsole.Prompt(
        new TextPrompt<string>("[green]Output file name:[/]")
            .DefaultValue(defaultFileNameKeys));

    await AnsiConsole.Status()
        .Spinner(Spinner.Known.Dots)
        .SpinnerStyle(Style.Parse("yellow"))
        .StartAsync($"Exporting to {Markup.Escape(fileNameKeys)}...", async ctx =>
        {
            if (statusFilter == (ApiStatusEnum)(-1))
            {
                await dbService.ExportKeysAsync(db, fileNameKeys, formatKeys, null); 
            }
            else
            {
                await dbService.ExportKeysAsync(db, fileNameKeys, formatKeys, statusFilter);
            }
        });

    AnsiConsole.MarkupLine($"[green]Exported to [bold]{Markup.Escape(fileNameKeys)}[/][/]");
}

// === Display Helpers ===

static void DisplayValidKeys(IEnumerable<APIKey> keys)
{
    var table = new Table()
        .Border(TableBorder.Rounded)
        .BorderColor(Color.Green)
        .AddColumn(new TableColumn("[bold]ID[/]").RightAligned())
        .AddColumn(new TableColumn("[bold]Type[/]").LeftAligned())
        .AddColumn(new TableColumn("[bold]Key (Masked)[/]").LeftAligned())
        .AddColumn(new TableColumn("[bold]Status[/]").LeftAligned())
        .AddColumn(new TableColumn("[bold]AWS Account[/]").LeftAligned())
        .AddColumn(new TableColumn("[bold]Risk[/]").LeftAligned())
        .AddColumn(new TableColumn("[bold]Last Checked[/]").LeftAligned());

    foreach (var key in keys)
    {
        var awsAccount = key.AwsAccountId != null
            ? $"[cyan]{Markup.Escape(key.AwsAccountId)}[/]"
            : "[dim]N/A[/]";

        var riskMarkup = key.AwsRiskLevel switch
        {
            "Critical" => "[red]Critical[/]",
            "High"     => "[darkorange]High[/]",
            "Medium"   => "[yellow]Medium[/]",
            "Low"      => "[green]Low[/]",
            _          => "[dim]N/A[/]"
        };

        table.AddRow(
            key.Id.ToString(),
            $"[cyan]{key.ApiType}[/]",
            $"[dim]{Markup.Escape(MaskKey(key.ApiKey))}[/]",
            GetStatusMarkup(key.Status),
            awsAccount,
            riskMarkup,
            key.LastCheckedUTC.HasValue
                ? $"[dim]{key.LastCheckedUTC.Value:yyyy-MM-dd HH:mm}[/]"
                : "[dim]Never[/]"
        );
    }

    AnsiConsole.Write(table);
}

static void DisplayKeyDetails(APIKey key)
{
    var table = new Table()
        .Border(TableBorder.Rounded)
        .AddColumn("[bold]Property[/]")
        .AddColumn("[bold]Value[/]");

    table.AddRow("ID", key.Id.ToString());
    table.AddRow("API Key", $"[dim]{Markup.Escape(MaskKey(key.ApiKey))}[/]");
    table.AddRow("Type", $"[cyan]{key.ApiType}[/]");
    table.AddRow("Status", GetStatusMarkup(key.Status));

    if (!string.IsNullOrEmpty(key.Balance))
        table.AddRow("Balance", $"[green]{Markup.Escape(key.Balance)}[/]");

    if (!string.IsNullOrEmpty(key.AccountTier))
        table.AddRow("Tier", $"[yellow]{Markup.Escape(key.AccountTier)}[/]");

    // AWS-specific metadata (shown when key is AWS IAM or AWS metadata is present)
    if (key.ApiType == ApiTypeEnum.AWSIAM || key.AwsAccountId != null)
    {
        table.AddRow("[bold cyan]AWS Account ID[/]",
            $"[cyan]{Markup.Escape(key.AwsAccountId ?? "N/A")}[/]");

        table.AddRow("[bold cyan]AWS User ARN[/]",
            $"[dim]{Markup.Escape(key.AwsUserArn ?? "N/A")}[/]");

        table.AddRow("[bold cyan]AWS Credential Type[/]",
            $"[yellow]{Markup.Escape(key.AwsCredentialType ?? "N/A")}[/]");

        // Risk level with color coding
        var riskMarkup = key.AwsRiskLevel switch
        {
            "Critical" => "[red]Critical[/]",
            "High"     => "[darkorange]High[/]",
            "Medium"   => "[yellow]Medium[/]",
            "Low"      => "[green]Low[/]",
            _          => "[dim]N/A[/]"
        };
        table.AddRow("[bold cyan]AWS Risk Level[/]", riskMarkup);

        // Root account warning
        if (key.AwsIsRootAccount)
        {
            table.AddRow("[bold red]⚠ ROOT ACCOUNT[/]",
                "[bold red]⚠ ROOT ACCOUNT - CRITICAL RISK[/]");
        }

        // Attached policies as bullet list
        if (!string.IsNullOrEmpty(key.AwsAttachedPolicies))
        {
            try
            {
                var policies = JsonSerializer.Deserialize<List<string>>(key.AwsAttachedPolicies);
                if (policies != null && policies.Count > 0)
                {
                    var policyText = string.Join("\n", policies.Select(p => $"• {Markup.Escape(p)}"));
                    table.AddRow("[bold cyan]AWS Attached Policies[/]", $"[dim]{policyText}[/]");
                }
                else
                {
                    table.AddRow("[bold cyan]AWS Attached Policies[/]", "[dim]N/A[/]");
                }
            }
            catch
            {
                table.AddRow("[bold cyan]AWS Attached Policies[/]",
                    $"[dim]{Markup.Escape(key.AwsAttachedPolicies)}[/]");
            }
        }
        else
        {
            table.AddRow("[bold cyan]AWS Attached Policies[/]", "[dim]N/A[/]");
        }
    }

    if (!string.IsNullOrEmpty(key.ValidationResponse))
        table.AddRow("Validation", $"[dim]{Markup.Escape(key.ValidationResponse)}[/]");

    table.AddRow("First Found", key.FirstFoundUTC.ToString("yyyy-MM-dd HH:mm:ss UTC"));

    if (key.LastCheckedUTC.HasValue)
        table.AddRow("Last Checked", key.LastCheckedUTC.Value.ToString("yyyy-MM-dd HH:mm:ss UTC"));

    AnsiConsole.Write(table);
}

static string GetStatusMarkup(ApiStatusEnum status)
{
    return status switch
    {
        ApiStatusEnum.Valid          => "[green]Valid[/]",
        ApiStatusEnum.ValidNoCredits => "[yellow]Valid (No Credits)[/]",
        ApiStatusEnum.Invalid        => "[red]Invalid[/]",
        ApiStatusEnum.Unverified     => "[grey]Unverified[/]",
        ApiStatusEnum.Error          => "[orange1]Error[/]",
        _                            => "[white]Unknown[/]"
    };
}

static string MaskKey(string apiKey)
{
    if (string.IsNullOrEmpty(apiKey) || apiKey.Length <= 8)
        return "****";
    return $"{apiKey[..4]}...{apiKey[^4..]}";
}

async Task ShowStatusMenuAsync(DBContext db, DatabaseService dbService)
{
    var statusChoice = AnsiConsole.Prompt(
        new SelectionPrompt<string>()
            .Title("[yellow]Select Status View:[/]")
            .AddChoices(new[]
            {
                "1. General API Keys Status",
                "2. Server Credentials Status (With Filtering)",
                "3. Back to Main Menu"
            }));

    if (statusChoice.StartsWith("1"))
    {
        await ShowStatusAsync(db, dbService);
    }
    else if (statusChoice.StartsWith("2"))
    {
        await ShowServerCredentialsStatusAsync(db);
    }
}

async Task ShowServerCredentialsStatusAsync(DBContext db)
{
    AnsiConsole.Clear();
    AnsiConsole.Write(new Rule("[cyan]Server Credentials Status[/]").RuleStyle("cyan"));

    var total = await db.ServerCredentials.CountAsync();
    if (total == 0)
    {
        AnsiConsole.MarkupLine("[yellow]No server credentials discovered yet.[/]");
        return;
    }

    var filterChoice = AnsiConsole.Prompt(
        new SelectionPrompt<string>()
            .Title("[yellow]Select Filter Option:[/]")
            .AddChoices(new[]
            {
                "1. View All",
                "2. Filter by Credential Type",
                "3. Filter by Risk Level",
                "4. Filter by Auth Status",
                "5. Back"
            }));

    if (filterChoice.StartsWith("5")) return;

    var query = db.ServerCredentials.AsQueryable();

    if (filterChoice.StartsWith("2"))
    {
        var types = await db.ServerCredentials.Select(c => c.CredentialType).Distinct().ToListAsync();
        var selectedType = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Select Credential Type:")
                .AddChoices(types));
        query = query.Where(c => c.CredentialType == selectedType);
    }
    else if (filterChoice.StartsWith("3"))
    {
        var selectedRisk = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Select Risk Level:")
                .AddChoices(new[] { "Critical", "High", "Medium", "Low" }));
        query = query.Where(c => c.RiskLevel == selectedRisk);
    }
    else if (filterChoice.StartsWith("4"))
    {
        var selectedStatus = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Select Auth Status:")
                .AddChoices(new[] { "Valid", "Invalid", "RateLimited", "Untested" }));
        query = query.Where(c => c.AuthenticationStatus == selectedStatus);
    }

    var results = await query.OrderByDescending(c => c.DiscoveredAt).Take(50).ToListAsync();
    AnsiConsole.WriteLine();
    AnsiConsole.MarkupLine($"[green]Found {results.Count} matching server credentials:[/]");
    DisplayServerCredentials(results);
}

static void DisplayServerCredentials(IEnumerable<ServerCredential> credentials)
{
    var table = new Table()
        .Border(TableBorder.Rounded)
        .BorderColor(Color.Cyan1)
        .AddColumn(new TableColumn("[bold]ID[/]").RightAligned())
        .AddColumn(new TableColumn("[bold]Type[/]").LeftAligned())
        .AddColumn(new TableColumn("[bold]Host:Port[/]").LeftAligned())
        .AddColumn(new TableColumn("[bold]Username[/]").LeftAligned())
        .AddColumn(new TableColumn("[bold]Network Status[/]").LeftAligned())
        .AddColumn(new TableColumn("[bold]Auth Status[/]").LeftAligned())
        .AddColumn(new TableColumn("[bold]Risk Level[/]").LeftAligned())
        .AddColumn(new TableColumn("[bold]Honeypot[/]").LeftAligned())
        .AddColumn(new TableColumn("[bold]Country / ISP[/]").LeftAligned())
        .AddColumn(new TableColumn("[bold]Discovered[/]").LeftAligned());

    foreach (var cred in credentials)
    {
        var netMarkup = cred.NetworkStatus switch
        {
            "Accessible" => "[green]Accessible[/]",
            "Unreachable" => "[red]Unreachable[/]",
            "Timeout" => "[yellow]Timeout[/]",
            _ => $"[dim]{Markup.Escape(cred.NetworkStatus)}[/]"
        };

        var authMarkup = cred.AuthenticationStatus switch
        {
            "Valid" => "[green]Valid[/]",
            "Invalid" => "[red]Invalid[/]",
            "RateLimited" => "[yellow]RateLimited[/]",
            "Untested" => "[grey]Untested[/]",
            _ => $"[dim]{Markup.Escape(cred.AuthenticationStatus)}[/]"
        };

        var riskMarkup = cred.RiskLevel switch
        {
            "Critical" => "[red]Critical[/]",
            "High" => "[darkorange]High[/]",
            "Medium" => "[yellow]Medium[/]",
            "Low" => "[green]Low[/]",
            _ => $"[dim]{Markup.Escape(cred.RiskLevel)}[/]"
        };

        var honeypotMarkup = cred.IsHoneypot ? "[yellow]⚠ HONEYPOT[/]" : "[dim]No[/]";

        var countryIsp = "N/A";
        try
        {
            if (!string.IsNullOrEmpty(cred.GeolocationData) && cred.GeolocationData != "{}")
            {
                var geo = JsonSerializer.Deserialize<UnsecuredAPIKeys.Providers.ServerProviders.Services.GeolocationResult>(cred.GeolocationData);
                if (geo != null)
                {
                    countryIsp = $"{geo.Country} ({geo.ISP})";
                }
            }
        }
        catch
        {
            countryIsp = "N/A";
        }

        table.AddRow(
            cred.Id.ToString(),
            $"[cyan]{cred.CredentialType}[/]",
            $"{cred.Host}:{cred.Port}",
            string.IsNullOrEmpty(cred.Username) ? "[dim]N/A[/]" : cred.Username,
            netMarkup,
            authMarkup,
            riskMarkup,
            honeypotMarkup,
            countryIsp,
            cred.DiscoveredAt.ToString("yyyy-MM-dd HH:mm")
        );
    }

    AnsiConsole.Write(table);
}

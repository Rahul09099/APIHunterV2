using Microsoft.EntityFrameworkCore;
using Spectre.Console;
using UnsecuredAPIKeys.Data;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Data.Models;
using UnsecuredAPIKeys.Providers;

namespace UnsecuredAPIKeys.Services;

/// <summary>
/// Service for database initialization and common operations.
/// </summary>
public class DatabaseService(string dbPath = "unsecuredapikeys.db")
{
    public async Task<DBContext> InitializeDatabaseAsync()
    {
        var dbContext = new DBContext(dbPath);

        // Ensure database is created
        await dbContext.Database.EnsureCreatedAsync();
        
        // Re-classify any keys that might be misclassified or have legacy IDs
        await FixLegacyKeysAsync(dbContext);
 
        // Seed default data if needed
        await SeedDefaultDataAsync(dbContext);
 
        return dbContext;
    }
    
    private async Task FixLegacyKeysAsync(DBContext dbContext)
    {
        var providers = ApiProviderRegistry.Providers;
        var keysToFix = await dbContext.APIKeys
            .Where(k => k.ApiType == ApiTypeEnum.Unknown || (int)k.ApiType < 100)
            .ToListAsync();
            
        if (keysToFix.Count == 0) return;
        
        AnsiConsole.MarkupLine($"[dim]Checking {keysToFix.Count} legacy/unknown keys for re-classification...[/]");
        int fixedCount = 0;
        
        foreach (var key in keysToFix)
        {
            foreach (var provider in providers)
            {
                if (provider.RegexPatterns.Any(p => System.Text.RegularExpressions.Regex.IsMatch(key.ApiKey, p)))
                {
                    if (key.ApiType != provider.ApiType)
                    {
                        key.ApiType = provider.ApiType;
                        fixedCount++;
                    }
                    break;
                }
            }
        }
        
        if (fixedCount > 0)
        {
            await dbContext.SaveChangesAsync();
            AnsiConsole.MarkupLine($"[green]Successfully re-classified {fixedCount} keys.[/]");
        }
    }

    private async Task SeedDefaultDataAsync(DBContext dbContext)
    {
        // Seed default search queries
        var defaultQueries = new[]
        {
            // OpenAI patterns
            "sk-proj-",
            "sk-or-v1-",
            "sk-",
            "openai.api_key",
            "chatgpt api key",
            "gpt-4 api key",

            // Anthropic patterns
            "sk-ant-api",
            "ANTHROPIC_API_KEY",
            "anthropic_api_key",
            "claude api key",

            // Google AI patterns
            "AIzaSy",
            "GOOGLE_API_KEY",
            "gemini_api_key",

            // Other AI providers
            "r8_",           // Replicate
            "fw_",           // Fireworks
            "hf_",           // HuggingFace
            "AI_API_KEY",    // Generic

            // KlingAI patterns
            "KLING_API_KEY",
            "klingai_key",
            "KLING_ACCESS_KEY",

            // DeepSeek
            "sk-",
            "DEEPSEEK_API_KEY",
        

            // Cohere
            "COHERE_API_KEY",

            // ElevenLabs
            "xi-api-key",
            "ELEVEN_API_KEY",
            "ELEVENLABS_API_KEY",

            // StabilityAI
            "STABILITY_API_KEY",

            // TogetherAI
            "TOGETHER_API_KEY",

            // XAI
            "xai-",
            "XAI_API_KEY"
        };

        bool addedAny = false;
        foreach (var query in defaultQueries)
        {
            if (!await dbContext.SearchQueries.AnyAsync(q => q.Query == query))
            {
                dbContext.SearchQueries.Add(new SearchQuery
                {
                    Query = query,
                    IsEnabled = true,
                    LastSearchUTC = DateTime.UtcNow.AddDays(-1)
                });
                addedAny = true;
            }
        }

        if (addedAny)
        {
            await dbContext.SaveChangesAsync();
            AnsiConsole.MarkupLine($"[dim]Updated default search queries.[/]");
        }
    }

    public async Task<Statistics> GetStatisticsAsync(DBContext dbContext)
    {
        var stats = new Statistics
        {
            TotalKeys = await dbContext.APIKeys.CountAsync(),
            ValidKeys = await dbContext.APIKeys.CountAsync(k => k.Status == ApiStatusEnum.Valid),
            InvalidKeys = await dbContext.APIKeys.CountAsync(k => k.Status == ApiStatusEnum.Invalid),
            UnverifiedKeys = await dbContext.APIKeys.CountAsync(k => k.Status == ApiStatusEnum.Unverified),
            ValidNoCreditsKeys = await dbContext.APIKeys.CountAsync(k => k.Status == ApiStatusEnum.ValidNoCredits),
            OpenAIKeys = await dbContext.APIKeys.CountAsync(k => k.ApiType == ApiTypeEnum.OpenAI),
            AnthropicKeys = await dbContext.APIKeys.CountAsync(k => k.ApiType == ApiTypeEnum.AnthropicClaude),
            GoogleKeys = await dbContext.APIKeys.CountAsync(k => k.ApiType == ApiTypeEnum.GoogleAI),
            GitHubTokensCount = await dbContext.SearchProviderTokens
                .CountAsync(t => t.IsEnabled && t.SearchProvider == SearchProviderEnum.GitHub)
        };

        return stats;
    }

    public async Task<CategorizedStatistics> GetCategorizedStatisticsAsync(DBContext dbContext)
    {
        var allKeys = await dbContext.APIKeys.ToListAsync();
        
        var categorized = new CategorizedStatistics
        {
            TotalKeys = allKeys.Count,
            ValidKeys = allKeys.Count(k => k.Status == ApiStatusEnum.Valid),
            InvalidKeys = allKeys.Count(k => k.Status == ApiStatusEnum.Invalid),
            UnverifiedKeys = allKeys.Count(k => k.Status == ApiStatusEnum.Unverified),
            ValidNoCreditsKeys = allKeys.Count(k => k.Status == ApiStatusEnum.ValidNoCredits),
            GitHubTokensCount = await dbContext.SearchProviderTokens
                .CountAsync(t => t.IsEnabled && t.SearchProvider == SearchProviderEnum.GitHub),
            Categories = new Dictionary<ApiCategoryEnum, CategoryStats>()
        };

        // Group by category
        var categoryGroups = allKeys.GroupBy(k => GetCategoryForApiType(k.ApiType));

        foreach (var categoryGroup in categoryGroups)
        {
            var category = categoryGroup.Key;
            var categoryKeys = categoryGroup.ToList();

            var categoryStats = new CategoryStats
            {
                CategoryName = GetCategoryName(category),
                TotalKeys = categoryKeys.Count,
                ApiTypes = new List<ApiTypeStats>()
            };

            // Group by API type within category
            var typeGroups = categoryKeys.GroupBy(k => k.ApiType);
            foreach (var typeGroup in typeGroups)
            {
                categoryStats.ApiTypes.Add(new ApiTypeStats
                {
                    ApiType = typeGroup.Key,
                    ApiTypeName = typeGroup.Key.ToString(),
                    KeyCount = typeGroup.Count()
                });
            }

            // Sort by key count descending
            categoryStats.ApiTypes = categoryStats.ApiTypes.OrderByDescending(t => t.KeyCount).ToList();
            categorized.Categories[category] = categoryStats;
        }

        return categorized;
    }

    public static ApiCategoryEnum GetCategoryForApiType(ApiTypeEnum apiType)
    {
        return apiType switch
        {
            ApiTypeEnum.OpenAI or ApiTypeEnum.AnthropicClaude or ApiTypeEnum.GoogleAI or
            ApiTypeEnum.Cohere or ApiTypeEnum.HuggingFace or ApiTypeEnum.StabilityAI or
            ApiTypeEnum.Replicate or ApiTypeEnum.TogetherAI or ApiTypeEnum.DeepSeek or
            ApiTypeEnum.ElevenLabs or ApiTypeEnum.XAI or ApiTypeEnum.FireworksAI
                => ApiCategoryEnum.AIAndLLM,

            ApiTypeEnum.SendGrid or ApiTypeEnum.Mailgun or ApiTypeEnum.Slack
                => ApiCategoryEnum.Communication,

            ApiTypeEnum.Mapbox
                => ApiCategoryEnum.MapsAndLocation,

            _ => ApiCategoryEnum.Unknown
        };
    }

    public static string GetCategoryName(ApiCategoryEnum category)
    {
        return category switch
        {
            ApiCategoryEnum.AIAndLLM => "AI & LLM",
            ApiCategoryEnum.Communication => "Communication",
            ApiCategoryEnum.MapsAndLocation => "Maps & Location",
            _ => "Unknown"
        };
    }

    public async Task AddGitHubTokenAsync(DBContext dbContext, string token)
    {
        // Check if token already exists to prevent duplicates
        var exists = await dbContext.SearchProviderTokens
            .AnyAsync(t => t.Token == token && t.SearchProvider == SearchProviderEnum.GitHub);

        if (!exists)
        {
            dbContext.SearchProviderTokens.Add(new SearchProviderToken
            {
                Token = token,
                SearchProvider = SearchProviderEnum.GitHub,
                IsEnabled = true
            });
            await dbContext.SaveChangesAsync();
        }
    }

    public async Task<List<SearchProviderToken>> GetGitHubTokensAsync(DBContext dbContext)
    {
        return await dbContext.SearchProviderTokens
            .Where(t => t.SearchProvider == SearchProviderEnum.GitHub && t.IsEnabled)
            .OrderBy(t => t.Id)
            .ToListAsync();
    }

    public async Task DeleteGitHubTokenAsync(DBContext dbContext, int tokenId)
    {
        var token = await dbContext.SearchProviderTokens.FindAsync(tokenId);
        if (token != null)
        {
            dbContext.SearchProviderTokens.Remove(token);
            await dbContext.SaveChangesAsync();
        }
    }

    // Legacy method wrapper for backward compatibility or simple update
    public async Task SaveGitHubTokenAsync(DBContext dbContext, string token)
    {
       await AddGitHubTokenAsync(dbContext, token);
    }

    public async Task ResetDatabaseAsync()
    {
        // Clear all connection pools to ensure the file is not locked by SQLite
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        if (File.Exists(dbPath))
        {
            File.Delete(dbPath);
        }

        // Reinitialize
        await InitializeDatabaseAsync();
    }

    public async Task ExportKeysAsync(DBContext dbContext, string filePath, bool validOnly, string format)
    {
        var query = dbContext.APIKeys.AsQueryable();

        if (validOnly)
        {
            // Only export keys with credits (truly working keys)
            query = query.Where(k => k.Status == ApiStatusEnum.Valid);
        }
        else
        {
            // Export all valid keys (with and without credits)
            query = query.Where(k => k.Status == ApiStatusEnum.Valid || k.Status == ApiStatusEnum.ValidNoCredits);
        }

        var keys = await query
            .Include(k => k.References)
            .ToListAsync();

        if (format.ToLower() == "json")
        {
            await ExportAsJsonAsync(keys, filePath);
        }
        else
        {
            await ExportAsCsvAsync(keys, filePath);
        }
    }

    private async Task ExportAsJsonAsync(List<APIKey> keys, string filePath)
    {
        var exportData = keys.Select(k => new
        {
            k.Id,
            k.ApiKey,
            Type = k.ApiType.ToString(),
            Status = k.Status.ToString(),
            k.FirstFoundUTC,
            k.LastCheckedUTC,
            k.ValidationResponse,
            Sources = k.References.Select(r => new
            {
                Source = r.FileURL ?? $"{r.RepoURL}/blob/{r.Branch ?? "main"}/{r.FilePath}",
                FoundUTC = r.FoundUTC
            })
        });

        var json = System.Text.Json.JsonSerializer.Serialize(exportData, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true
        });

        await File.WriteAllTextAsync(filePath, json);
    }

    private async Task ExportAsCsvAsync(List<APIKey> keys, string filePath)
    {
        var lines = new List<string>
        {
            "Id,ApiKey,Type,Status,ValidationResponse,FirstFoundUTC,LastCheckedUTC,Source,SourceFoundUTC"
        };

        foreach (var key in keys)
        {
            var firstRef = key.References.OrderByDescending(r => r.FoundUTC).FirstOrDefault();
            var source = firstRef?.FileURL ?? (firstRef != null ? $"{firstRef.RepoURL}/blob/{firstRef.Branch ?? "main"}/{firstRef.FilePath}" : "");
            var sourceFoundUTC = firstRef != null ? firstRef.FoundUTC.ToString("O") : "";
            
            // Escape quotes and handle newlines in validation response
            var valResponse = key.ValidationResponse?.Replace("\"", "\"\"").Replace("\n", " ") ?? "";
            
            lines.Add($"{key.Id},\"{key.ApiKey}\",{key.ApiType},{key.Status},\"{valResponse}\",{key.FirstFoundUTC:O},{key.LastCheckedUTC:O},\"{source}\",\"{sourceFoundUTC}\"");
        }

        await File.WriteAllLinesAsync(filePath, lines);
    }
}

public class Statistics
{
    public int TotalKeys { get; set; }
    public int ValidKeys { get; set; }
    public int InvalidKeys { get; set; }
    public int UnverifiedKeys { get; set; }
    public int ValidNoCreditsKeys { get; set; }
    public int OpenAIKeys { get; set; }
    public int AnthropicKeys { get; set; }
    public int GoogleKeys { get; set; }
    public int GitHubTokensCount { get; set; }
}

public class CategorizedStatistics
{
    public int TotalKeys { get; set; }
    public int ValidKeys { get; set; }
    public int InvalidKeys { get; set; }
    public int UnverifiedKeys { get; set; }
    public int ValidNoCreditsKeys { get; set; }
    public int GitHubTokensCount { get; set; }
    public Dictionary<ApiCategoryEnum, CategoryStats> Categories { get; set; } = new();
}

public class CategoryStats
{
    public string CategoryName { get; set; } = "";
    public int TotalKeys { get; set; }
    public List<ApiTypeStats> ApiTypes { get; set; } = new();
}

public class ApiTypeStats
{
    public ApiTypeEnum ApiType { get; set; }
    public string ApiTypeName { get; set; } = "";
    public int KeyCount { get; set; }
}


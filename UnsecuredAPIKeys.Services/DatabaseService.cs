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
public class DatabaseService(DBContext dbContext)
{
    private readonly string _dbPath = Environment.GetEnvironmentVariable("DATABASE_PATH") ?? "unsecuredapikeys.db";

    public DatabaseService(string dbPath) : this(new DBContext(dbPath))
    {
        _dbPath = dbPath;
    }

    public async Task<DBContext> InitializeDatabaseAsync()
    {
        // Ensure database is created (Bypassed for stability on Render Free tier)
        // await dbContext.Database.EnsureCreatedAsync();
        
        // Skip legacy processing at startup to save memory on Render Free tier
        // await FixLegacyKeysAsync(dbContext);
        
        // Skip seeding at startup to prevent timeouts on Supabase pooler
        // await SeedDefaultDataAsync(dbContext);
 
        return dbContext;
    }
    
    private async Task FixLegacyKeysAsync(DBContext dbContext)
    {
        var providers = ApiProviderRegistry.Providers;
        var keysToFix = await dbContext.APIKeys
            .Where(k => k.ApiType == ApiTypeEnum.Unknown || (int)k.ApiType < 100)
            .ToListAsync();
            
        if (keysToFix.Count == 0) return;
        
        Console.WriteLine($"[DB] Checking {keysToFix.Count} legacy/unknown keys for re-classification...");
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
            Console.WriteLine($"[DB] Successfully re-classified {fixedCount} keys.");
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
            "XAI_API_KEY",
            "GROK_API_KEY",
            "xai_api_key",
            "XAI_SECRET",
            "grok-",

            // Pollo AI patterns
            "POLLO_API_KEY",
            "pollo_api_key",
            "POLLO_SECRET",
            "pollo_",

            // Runway ML
            "key_",
            "RUNWAYML_API_SECRET",
            "RUNWAY_API_KEY",
        };

        bool addedAny = false;
        var existingQueries = await dbContext.SearchQueries.Select(q => q.Query).ToListAsync();
        var existingSet = new HashSet<string>(existingQueries);

        foreach (var query in defaultQueries)
        {
            if (!existingSet.Contains(query))
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
            Console.WriteLine("[DB] Updated default search queries.");
        }
    }

    public async Task<Statistics> GetStatisticsAsync(DBContext dbContext, long? filterByTelegramId = null)
    {
        var query = dbContext.APIKeys.AsQueryable();
        if (filterByTelegramId.HasValue)
        {
            query = query.Where(k => k.DiscoveredByTelegramId == filterByTelegramId.Value);
        }

        var stats = new Statistics
        {
            TotalKeys = await query.CountAsync(),
            ValidKeys = await query.CountAsync(k => k.Status == ApiStatusEnum.Valid),
            InvalidKeys = await query.CountAsync(k => k.Status == ApiStatusEnum.Invalid),
            UnverifiedKeys = await query.CountAsync(k => k.Status == ApiStatusEnum.Unverified),
            ValidNoCreditsKeys = await query.CountAsync(k => k.Status == ApiStatusEnum.ValidNoCredits),
            OpenAIKeys = await query.CountAsync(k => k.ApiType == ApiTypeEnum.OpenAI),
            AnthropicKeys = await query.CountAsync(k => k.ApiType == ApiTypeEnum.AnthropicClaude),
            GoogleKeys = await query.CountAsync(k => k.ApiType == ApiTypeEnum.GoogleAI),
            GitHubTokensCount = await dbContext.SearchProviderTokens
                .CountAsync(t => t.IsEnabled && t.SearchProvider == SearchProviderEnum.GitHub)
        };

        return stats;
    }

    public async Task<CategorizedStatistics> GetCategorizedStatisticsAsync(DBContext dbContext, long? filterByTelegramId = null)
    {
        var query = dbContext.APIKeys.AsQueryable();
        if (filterByTelegramId.HasValue)
        {
            query = query.Where(k => k.DiscoveredByTelegramId == filterByTelegramId.Value);
        }

        var allKeys = await query.ToListAsync();
        
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
            ApiTypeEnum.ElevenLabs or ApiTypeEnum.XAI or ApiTypeEnum.FireworksAI or
            ApiTypeEnum.KlingAI or ApiTypeEnum.PolloAI or ApiTypeEnum.RunwayML
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

    public async Task AddGitHubTokenAsync(DBContext dbContext, string token, long? addedBy = null)
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
                IsEnabled = true,
                AddedByTelegramId = addedBy
            });
            await dbContext.SaveChangesAsync();
        }
    }

    public async Task<List<SearchProviderToken>> GetGitHubTokensAsync(DBContext dbContext, long? filterByTelegramId = null)
    {
        var query = dbContext.SearchProviderTokens
            .Where(t => t.SearchProvider == SearchProviderEnum.GitHub && t.IsEnabled);
            
        if (filterByTelegramId.HasValue)
        {
            query = query.Where(t => t.AddedByTelegramId == filterByTelegramId.Value);
        }

        return await query.OrderBy(t => t.Id).ToListAsync();
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

        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }

        // Reinitialize
        await InitializeDatabaseAsync();
    }

    public async Task ExportKeysAsync(DBContext dbContext, string filePath, bool validOnly, string format, long? filterByTelegramId = null)
    {
        var query = dbContext.APIKeys.AsQueryable();

        if (filterByTelegramId.HasValue)
        {
            query = query.Where(k => k.DiscoveredByTelegramId == filterByTelegramId.Value);
        }

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
            k.ApiType,
            k.Status,
            k.Balance,
            k.AccountTier,
            k.FirstFoundUTC,
            k.LastCheckedUTC,
            k.TimesDisplayed,
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
            "Id,ApiKey,Type,Status,Balance,Tier,ValidationResponse,FirstFoundUTC,LastCheckedUTC,Source,SourceFoundUTC"
        };

        foreach (var key in keys)
        {
            foreach (var r in key.References)
            {
                var source = r.FileURL ?? $"{r.RepoURL}/blob/{r.Branch ?? "main"}/{r.FilePath}";
                var valResponse = key.ValidationResponse?.Replace("\"", "\"\"").Replace("\n", " ") ?? "";
                lines.Add($"{key.Id},{key.ApiKey},{key.ApiType},{key.Status},{key.Balance},{key.AccountTier},\"{valResponse}\",{key.FirstFoundUTC:O},{key.LastCheckedUTC:O},\"{source}\",{r.FoundUTC:O}");
            }
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


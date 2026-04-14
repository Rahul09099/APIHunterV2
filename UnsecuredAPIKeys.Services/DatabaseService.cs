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
        Console.WriteLine("[DB] Checking database migrations...");
        
        try 
        {
            // Apply any pending migrations automatically
            await dbContext.Database.MigrateAsync();
            Console.WriteLine("[DB] Migrations applied successfully.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DB] Migration check failed (Expected if using SQLite Lite version): {ex.Message}");
            
            // Fallback for Lite version or environments where migrations aren't initialized yet
            if (dbContext.Database.IsSqlite())
            {
                await dbContext.Database.EnsureCreatedAsync();
            }
        }
        
        // Manual column check for all tables (Full Robustness Layer)
        await EnsureAllTableColumnsExistAsync(dbContext);
        
        // Seed default queries if database is empty or queries are missing
        await SeedDefaultQueriesAsync(dbContext);

        return dbContext;
    }

    private async Task EnsureAllTableColumnsExistAsync(DBContext context)
    {
        if (!context.Database.IsNpgsql()) return;

        try 
        {
            Console.WriteLine("[DB] Running exhaustive schema parity check...");

            // 1. TelegramSubscribers
            await context.Database.ExecuteSqlRawAsync(@"
                CREATE TABLE IF NOT EXISTS ""TelegramSubscribers"" (""TelegramId"" BIGINT PRIMARY KEY);
                ALTER TABLE ""TelegramSubscribers"" DROP COLUMN IF EXISTS ""SubscribedAtUTC"";
                ALTER TABLE ""TelegramSubscribers"" ADD COLUMN IF NOT EXISTS ""Username"" TEXT;
                ALTER TABLE ""TelegramSubscribers"" ADD COLUMN IF NOT EXISTS ""SubscriptionExpiryUtc"" TIMESTAMP WITH TIME ZONE DEFAULT '1970-01-01 00:00:00+00';
                ALTER TABLE ""TelegramSubscribers"" ADD COLUMN IF NOT EXISTS ""IsAdmin"" BOOLEAN DEFAULT FALSE;
                ALTER TABLE ""TelegramSubscribers"" ADD COLUMN IF NOT EXISTS ""CreatedAtUtc"" TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP;
                ALTER TABLE ""TelegramSubscribers"" ADD COLUMN IF NOT EXISTS ""NodeToken"" TEXT;
                ALTER TABLE ""TelegramSubscribers"" ADD COLUMN IF NOT EXISTS ""LastNodeHeartbeatUtc"" TIMESTAMP WITH TIME ZONE;
                CREATE UNIQUE INDEX IF NOT EXISTS ""IX_TelegramSubscribers_NodeToken"" ON ""TelegramSubscribers"" (""NodeToken"") WHERE ""NodeToken"" IS NOT NULL;");

            // 2. SearchQueries
            await context.Database.ExecuteSqlRawAsync(@"
                CREATE TABLE IF NOT EXISTS ""SearchQueries"" (""Id"" SERIAL PRIMARY KEY);
                ALTER TABLE ""SearchQueries"" ADD COLUMN IF NOT EXISTS ""Query"" TEXT NOT NULL DEFAULT '';
                ALTER TABLE ""SearchQueries"" ADD COLUMN IF NOT EXISTS ""IsEnabled"" BOOLEAN DEFAULT TRUE;
                ALTER TABLE ""SearchQueries"" ADD COLUMN IF NOT EXISTS ""SearchResultsCount"" INTEGER DEFAULT 0;
                ALTER TABLE ""SearchQueries"" ADD COLUMN IF NOT EXISTS ""LastSearchUTC"" TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP;
                ALTER TABLE ""SearchQueries"" ADD COLUMN IF NOT EXISTS ""LastDeepSearchDateUTC"" TIMESTAMP WITH TIME ZONE;
                CREATE INDEX IF NOT EXISTS ""IX_SearchQueries_IsEnabled_LastSearchUTC"" ON ""SearchQueries"" (""IsEnabled"", ""LastSearchUTC"");");

            // 3. SearchProviderTokens
            await context.Database.ExecuteSqlRawAsync(@"
                CREATE TABLE IF NOT EXISTS ""SearchProviderTokens"" (""Id"" SERIAL PRIMARY KEY);
                ALTER TABLE ""SearchProviderTokens"" ADD COLUMN IF NOT EXISTS ""Token"" TEXT NOT NULL DEFAULT '';
                ALTER TABLE ""SearchProviderTokens"" ADD COLUMN IF NOT EXISTS ""SearchProvider"" INTEGER DEFAULT 0;
                ALTER TABLE ""SearchProviderTokens"" ADD COLUMN IF NOT EXISTS ""IsEnabled"" BOOLEAN DEFAULT TRUE;
                ALTER TABLE ""SearchProviderTokens"" ADD COLUMN IF NOT EXISTS ""AddedByTelegramId"" BIGINT;
                ALTER TABLE ""SearchProviderTokens"" ADD COLUMN IF NOT EXISTS ""LastUsedUTC"" TIMESTAMP WITH TIME ZONE;
                CREATE INDEX IF NOT EXISTS ""IX_SearchProviderTokens_SearchProvider"" ON ""SearchProviderTokens"" (""SearchProvider"");
                CREATE INDEX IF NOT EXISTS ""IX_SearchProviderTokens_AddedByTelegramId"" ON ""SearchProviderTokens"" (""AddedByTelegramId"");");

            // 4. APIKeys
            await context.Database.ExecuteSqlRawAsync(@"
                CREATE TABLE IF NOT EXISTS ""APIKeys"" (""Id"" SERIAL PRIMARY KEY);
                ALTER TABLE ""APIKeys"" ADD COLUMN IF NOT EXISTS ""ApiKey"" TEXT NOT NULL DEFAULT '';
                ALTER TABLE ""APIKeys"" ADD COLUMN IF NOT EXISTS ""Status"" INTEGER DEFAULT 0;
                ALTER TABLE ""APIKeys"" ADD COLUMN IF NOT EXISTS ""ApiType"" INTEGER DEFAULT 0;
                ALTER TABLE ""APIKeys"" ADD COLUMN IF NOT EXISTS ""SearchProvider"" INTEGER DEFAULT 0;
                ALTER TABLE ""APIKeys"" ADD COLUMN IF NOT EXISTS ""LastCheckedUTC"" TIMESTAMP WITH TIME ZONE;
                ALTER TABLE ""APIKeys"" ADD COLUMN IF NOT EXISTS ""FirstFoundUTC"" TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP;
                ALTER TABLE ""APIKeys"" ADD COLUMN IF NOT EXISTS ""LastFoundUTC"" TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP;
                ALTER TABLE ""APIKeys"" ADD COLUMN IF NOT EXISTS ""TimesDisplayed"" INTEGER DEFAULT 0;
                ALTER TABLE ""APIKeys"" ADD COLUMN IF NOT EXISTS ""ErrorCount"" INTEGER DEFAULT 0;
                ALTER TABLE ""APIKeys"" ADD COLUMN IF NOT EXISTS ""ValidationResponse"" TEXT;
                ALTER TABLE ""APIKeys"" ADD COLUMN IF NOT EXISTS ""Balance"" TEXT;
                ALTER TABLE ""APIKeys"" ADD COLUMN IF NOT EXISTS ""AccountTier"" TEXT;
                ALTER TABLE ""APIKeys"" ADD COLUMN IF NOT EXISTS ""DiscoveredByTelegramId"" BIGINT;
                ALTER TABLE ""APIKeys"" ADD COLUMN IF NOT EXISTS ""Metadata"" TEXT;
                CREATE UNIQUE INDEX IF NOT EXISTS ""IX_APIKeys_ApiKey"" ON ""APIKeys"" (""ApiKey"");");

            // 5. RepoReferences
            await context.Database.ExecuteSqlRawAsync(@"
                CREATE TABLE IF NOT EXISTS ""RepoReferences"" (""Id"" SERIAL PRIMARY KEY);
                ALTER TABLE ""RepoReferences"" ADD COLUMN IF NOT EXISTS ""APIKeyId"" BIGINT NOT NULL DEFAULT 0;
                ALTER TABLE ""RepoReferences"" ADD COLUMN IF NOT EXISTS ""RepoURL"" TEXT;
                ALTER TABLE ""RepoReferences"" ADD COLUMN IF NOT EXISTS ""RepoOwner"" TEXT;
                ALTER TABLE ""RepoReferences"" ADD COLUMN IF NOT EXISTS ""RepoName"" TEXT;
                ALTER TABLE ""RepoReferences"" ADD COLUMN IF NOT EXISTS ""RepoId"" BIGINT DEFAULT 0;
                ALTER TABLE ""RepoReferences"" ADD COLUMN IF NOT EXISTS ""FileURL"" TEXT;
                ALTER TABLE ""RepoReferences"" ADD COLUMN IF NOT EXISTS ""FileName"" TEXT;
                ALTER TABLE ""RepoReferences"" ADD COLUMN IF NOT EXISTS ""FilePath"" TEXT;
                ALTER TABLE ""RepoReferences"" ADD COLUMN IF NOT EXISTS ""FileSHA"" TEXT;
                ALTER TABLE ""RepoReferences"" ADD COLUMN IF NOT EXISTS ""ApiContentUrl"" TEXT;
                ALTER TABLE ""RepoReferences"" ADD COLUMN IF NOT EXISTS ""CodeContext"" TEXT;
                ALTER TABLE ""RepoReferences"" ADD COLUMN IF NOT EXISTS ""LineNumber"" INTEGER DEFAULT 0;
                ALTER TABLE ""RepoReferences"" ADD COLUMN IF NOT EXISTS ""SearchQueryId"" BIGINT DEFAULT 0;
                ALTER TABLE ""RepoReferences"" ADD COLUMN IF NOT EXISTS ""FoundUTC"" TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP;
                ALTER TABLE ""RepoReferences"" ADD COLUMN IF NOT EXISTS ""Provider"" TEXT;
                ALTER TABLE ""RepoReferences"" ADD COLUMN IF NOT EXISTS ""Branch"" TEXT DEFAULT 'main';
                CREATE INDEX IF NOT EXISTS ""IX_RepoReferences_ApiKeyId"" ON ""RepoReferences"" (""APIKeyId"");");

            // 6. DeepSearchProgress (Aggressive Reset for Stability)
            await context.Database.ExecuteSqlRawAsync(@"
                DROP TABLE IF EXISTS ""DeepSearchProgress"" CASCADE;
                CREATE TABLE ""DeepSearchProgress"" (
                    ""Id"" SERIAL PRIMARY KEY,
                    ""SearchQueryId"" BIGINT NOT NULL,
                    ""PartitionType"" TEXT NOT NULL,
                    ""PartitionValue"" TEXT NOT NULL,
                    ""LastPageSearched"" INTEGER DEFAULT 0,
                    ""TotalResultsFound"" INTEGER DEFAULT 0,
                    ""IsCompleted"" BOOLEAN DEFAULT FALSE,
                    ""LastSearchedUTC"" TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP
                );
                CREATE UNIQUE INDEX ""IX_DeepSearchProgress_Query_Partition"" ON ""DeepSearchProgress"" (""SearchQueryId"", ""PartitionType"", ""PartitionValue"");");

            // 7. ApplicationSettings
            await context.Database.ExecuteSqlRawAsync(@"
                CREATE TABLE IF NOT EXISTS ""ApplicationSettings"" (
                    ""Key"" TEXT PRIMARY KEY,
                    ""Value"" TEXT NOT NULL,
                    ""Description"" TEXT
                );");

            Console.WriteLine("[DB] Full schema stabilization completed successfully.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DB] CRITICAL: Schema stabilization failed: {ex.Message}");
        }
    }

    private async Task SeedDefaultQueriesAsync(DBContext context)
    {
        try 
        {
            var count = await context.SearchQueries.CountAsync();
            if (count > 0) return;

            Console.WriteLine("[DB] Seeding default search targets...");
            var now = DateTime.UtcNow;
            var defaults = new List<SearchQuery>
            {
                new() { Query = "sk- OpenAI", IsEnabled = true, LastSearchUTC = now },
                new() { Query = "anthropic Claude", IsEnabled = true, LastSearchUTC = now },
                new() { Query = "aizasy Gemini", IsEnabled = true, LastSearchUTC = now },
                new() { Query = "deepseek", IsEnabled = true, LastSearchUTC = now },
                new() { Query = "kling AI", IsEnabled = true, LastSearchUTC = now },
                new() { Query = "pollo AI", IsEnabled = true, LastSearchUTC = now },
                new() { Query = "runway ML", IsEnabled = true, LastSearchUTC = now },
                new() { Query = "cohere", IsEnabled = true, LastSearchUTC = now },
                new() { Query = "elevenlabs", IsEnabled = true, LastSearchUTC = now },
                new() { Query = "stability AI", IsEnabled = true, LastSearchUTC = now },
                new() { Query = "together AI", IsEnabled = true, LastSearchUTC = now },
                new() { Query = "grok XAI", IsEnabled = true, LastSearchUTC = now },
                new() { Query = "replicate r8_", IsEnabled = true, LastSearchUTC = now },
                new() { Query = "fireworks fw_", IsEnabled = true, LastSearchUTC = now },
                new() { Query = "hf_ HuggingFace", IsEnabled = true, LastSearchUTC = now }
            };

            context.SearchQueries.AddRange(defaults);
            await context.SaveChangesAsync();
            Console.WriteLine($"[DB] Seeded {defaults.Count} default search targets.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DB] Warning: Could not seed default queries: {ex.Message}");
        }
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


using Microsoft.EntityFrameworkCore;
using Spectre.Console;
using System.Text.Json;
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
        
        // Clean up repo references for any keys already marked Invalid
        // (handles keys that were invalidated before auto-purge was implemented)
        await PurgeJunkSourcesAsync(dbContext);

        // Seed default queries if database is empty or queries are missing
        await SeedDefaultQueriesAsync(dbContext);

        // Seed default admin subscriber node token for testing/dashboard access
        await SeedDefaultAdminNodeTokenAsync(dbContext);

        return dbContext;
    }

    private async Task SeedDefaultAdminNodeTokenAsync(DBContext context)
    {
        try
        {
            var adminUser = await context.TelegramSubscribers.FirstOrDefaultAsync(s => s.TelegramId == 12345678);
            if (adminUser == null)
            {
                var defaultAdmin = new TelegramSubscriber
                {
                    TelegramId = 12345678,
                    Username = "admin_test",
                    IsAdmin = true,
                    NodeToken = "default_admin_token_2026",
                    SubscriptionExpiryUtc = DateTime.UtcNow.AddYears(10),
                    CreatedAtUtc = DateTime.UtcNow
                };
                context.TelegramSubscribers.Add(defaultAdmin);
                await context.SaveChangesAsync();
                Console.WriteLine("[DB] Seeded default admin subscriber with node token: default_admin_token_2026");
            }
            else if (adminUser.NodeToken != "default_admin_token_2026")
            {
                adminUser.NodeToken = "default_admin_token_2026";
                adminUser.IsAdmin = true;
                await context.SaveChangesAsync();
                Console.WriteLine("[DB] Updated existing admin subscriber 12345678 with node token: default_admin_token_2026");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DB] Failed to seed default admin subscriber: {ex.Message}");
        }
    }

    private async Task EnsureAllTableColumnsExistAsync(DBContext context)
    {
        if (context.Database.IsNpgsql())
        {
            await EnsurePostgresSchemaAsync(context);
        }
        else if (context.Database.IsSqlite())
        {
            await EnsureSQLiteSchemaAsync(context);
        }
    }

    private async Task EnsureSQLiteSchemaAsync(DBContext context)
    {
        try
        {
            Console.WriteLine("[DB] Running SQLite schema health check...");

            // 1. TelegramSubscribers
            await context.Database.ExecuteSqlRawAsync(@"
                CREATE TABLE IF NOT EXISTS ""TelegramSubscribers"" (
                    ""TelegramId"" INTEGER PRIMARY KEY,
                    ""Username"" TEXT,
                    ""SubscriptionExpiryUtc"" DATETIME DEFAULT '1970-01-01 00:00:00',
                    ""IsAdmin"" BOOLEAN DEFAULT 0,
                    ""CreatedAtUtc"" DATETIME DEFAULT CURRENT_TIMESTAMP,
                    ""NodeToken"" TEXT,
                    ""NodeUrl"" TEXT,
                    ""LastNodeHeartbeatUtc"" DATETIME,
                    ""DeployHook"" TEXT
                );");

            // Migration step for existing SQLite database
            try
            {
                await context.Database.ExecuteSqlRawAsync(@"ALTER TABLE ""TelegramSubscribers"" ADD COLUMN ""DeployHook"" TEXT");
            }
            catch
            {
                // Already exists
            }

            // 2. SearchQueries
            await context.Database.ExecuteSqlRawAsync(@"
                CREATE TABLE IF NOT EXISTS ""SearchQueries"" (
                    ""Id"" INTEGER PRIMARY KEY AUTOINCREMENT,
                    ""Query"" TEXT NOT NULL DEFAULT '',
                    ""IsEnabled"" BOOLEAN DEFAULT 1,
                    ""SearchResultsCount"" INTEGER DEFAULT 0,
                    ""LastSearchUTC"" DATETIME DEFAULT CURRENT_TIMESTAMP,
                    ""LastDeepSearchDateUTC"" DATETIME
                );");

            // 3. SearchProviderTokens
            await context.Database.ExecuteSqlRawAsync(@"
                CREATE TABLE IF NOT EXISTS ""SearchProviderTokens"" (
                    ""Id"" INTEGER PRIMARY KEY AUTOINCREMENT,
                    ""Token"" TEXT NOT NULL DEFAULT '',
                    ""SearchProvider"" INTEGER DEFAULT 0,
                    ""IsEnabled"" BOOLEAN DEFAULT 1,
                    ""AddedByTelegramId"" INTEGER,
                    ""LastUsedUTC"" DATETIME
                );");

            // 4. APIKeys
            await context.Database.ExecuteSqlRawAsync(@"
                CREATE TABLE IF NOT EXISTS ""APIKeys"" (
                    ""Id"" INTEGER PRIMARY KEY AUTOINCREMENT,
                    ""ApiKey"" TEXT NOT NULL DEFAULT '',
                    ""Status"" INTEGER DEFAULT 0,
                    ""ApiType"" INTEGER DEFAULT 0,
                    ""SearchProvider"" INTEGER DEFAULT 0,
                    ""LastCheckedUTC"" DATETIME,
                    ""FirstFoundUTC"" DATETIME DEFAULT CURRENT_TIMESTAMP,
                    ""LastFoundUTC"" DATETIME DEFAULT CURRENT_TIMESTAMP,
                    ""TimesDisplayed"" INTEGER DEFAULT 0,
                    ""ErrorCount"" INTEGER DEFAULT 0,
                    ""ValidationResponse"" TEXT,
                    ""Balance"" TEXT,
                    ""AccountTier"" TEXT,
                    ""DiscoveredByTelegramId"" INTEGER,
                    ""Metadata"" TEXT
                );
                CREATE UNIQUE INDEX IF NOT EXISTS ""IX_APIKeys_ApiKey"" ON ""APIKeys"" (""ApiKey"");");

            // 4b. AWS IAM columns for APIKeys (SQLite doesn't support ADD COLUMN IF NOT EXISTS in older versions,
            //     so we use a try/catch per column)
            var awsColumns = new[]
            {
                (@"ALTER TABLE ""APIKeys"" ADD COLUMN ""AwsAccountId"" TEXT", "AwsAccountId"),
                (@"ALTER TABLE ""APIKeys"" ADD COLUMN ""AwsUserArn"" TEXT", "AwsUserArn"),
                (@"ALTER TABLE ""APIKeys"" ADD COLUMN ""AwsUserId"" TEXT", "AwsUserId"),
                (@"ALTER TABLE ""APIKeys"" ADD COLUMN ""AwsCredentialType"" TEXT", "AwsCredentialType"),
                (@"ALTER TABLE ""APIKeys"" ADD COLUMN ""AwsAttachedPolicies"" TEXT", "AwsAttachedPolicies"),
                (@"ALTER TABLE ""APIKeys"" ADD COLUMN ""AwsRiskLevel"" TEXT", "AwsRiskLevel"),
                (@"ALTER TABLE ""APIKeys"" ADD COLUMN ""AwsIsRootAccount"" INTEGER DEFAULT 0", "AwsIsRootAccount"),
            };
            foreach (var (sql, colName) in awsColumns)
            {
                try { await context.Database.ExecuteSqlRawAsync(sql); }
                catch { /* Column already exists — safe to ignore */ }
            }
            // Dummy statement to satisfy the compiler (the above block replaces the original single call)
            await Task.CompletedTask;;

            // 5. RepoReferences
            await context.Database.ExecuteSqlRawAsync(@"
                CREATE TABLE IF NOT EXISTS ""RepoReferences"" (
                    ""Id"" INTEGER PRIMARY KEY AUTOINCREMENT,
                    ""APIKeyId"" INTEGER NOT NULL DEFAULT 0,
                    ""RepoURL"" TEXT,
                    ""RepoOwner"" TEXT,
                    ""RepoName"" TEXT,
                    ""RepoId"" INTEGER DEFAULT 0,
                    ""FileURL"" TEXT,
                    ""FileName"" TEXT,
                    ""FilePath"" TEXT,
                    ""FileSHA"" TEXT,
                    ""ApiContentUrl"" TEXT,
                    ""CodeContext"" TEXT,
                    ""LineNumber"" INTEGER DEFAULT 0,
                    ""SearchQueryId"" INTEGER DEFAULT 0,
                    ""FoundUTC"" DATETIME DEFAULT CURRENT_TIMESTAMP,
                    ""Provider"" TEXT,
                    ""Branch"" TEXT DEFAULT 'main'
                );");

            // 6. DeepSearchProgress
            await context.Database.ExecuteSqlRawAsync(@"
                CREATE TABLE IF NOT EXISTS ""DeepSearchProgress"" (
                    ""Id"" INTEGER PRIMARY KEY AUTOINCREMENT,
                    ""SearchQueryId"" INTEGER NOT NULL,
                    ""PartitionType"" TEXT NOT NULL,
                    ""PartitionValue"" TEXT NOT NULL,
                    ""LastPageSearched"" INTEGER DEFAULT 0,
                    ""TotalResultsFound"" INTEGER DEFAULT 0,
                    ""IsCompleted"" BOOLEAN DEFAULT 0,
                    ""LastSearchedUTC"" DATETIME DEFAULT CURRENT_TIMESTAMP
                );
                CREATE UNIQUE INDEX IF NOT EXISTS ""IX_DeepSearchProgress_Query_Partition"" ON ""DeepSearchProgress"" (""SearchQueryId"", ""PartitionType"", ""PartitionValue"");");

            // 7. ApplicationSettings
            await context.Database.ExecuteSqlRawAsync(@"
                CREATE TABLE IF NOT EXISTS ""ApplicationSettings"" (
                    ""Key"" TEXT PRIMARY KEY,
                    ""Value"" TEXT NOT NULL,
                    ""Description"" TEXT
                );");

            // 8. ServerCredentials
            await context.Database.ExecuteSqlRawAsync(@"
                CREATE TABLE IF NOT EXISTS ""ServerCredentials"" (
                    ""Id"" INTEGER PRIMARY KEY AUTOINCREMENT,
                    ""CredentialType"" TEXT NOT NULL,
                    ""Host"" TEXT NOT NULL,
                    ""Port"" INTEGER NOT NULL DEFAULT 0,
                    ""Username"" TEXT,
                    ""PasswordHash"" TEXT,
                    ""Domain"" TEXT,
                    ""NetworkStatus"" TEXT NOT NULL DEFAULT 'Unknown',
                    ""AuthenticationStatus"" TEXT NOT NULL DEFAULT 'Untested',
                    ""ServerMetadata"" TEXT NOT NULL DEFAULT '{{}}',
                    ""GeolocationData"" TEXT NOT NULL DEFAULT '{{}}',
                    ""OSINTData"" TEXT NOT NULL DEFAULT '{{}}',
                    ""RiskLevel"" TEXT NOT NULL DEFAULT 'Low',
                    ""IsHoneypot"" BOOLEAN NOT NULL DEFAULT 0,
                    ""SourceRepository"" TEXT,
                    ""SourceFilePath"" TEXT,
                    ""SurroundingContext"" TEXT,
                    ""EntropyScore"" DOUBLE DEFAULT 0,
                    ""DiscoveredAt"" DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    ""LastVerifiedAt"" DATETIME,
                    CONSTRAINT ""uq_server_cred"" UNIQUE (""Host"", ""Port"", ""Username"", ""CredentialType"")
                );
                CREATE INDEX IF NOT EXISTS ""idx_sc_type"" ON ""ServerCredentials"" (""CredentialType"");
                CREATE INDEX IF NOT EXISTS ""idx_sc_risk"" ON ""ServerCredentials"" (""RiskLevel"");
                CREATE INDEX IF NOT EXISTS ""idx_sc_auth_status"" ON ""ServerCredentials"" (""AuthenticationStatus"");
                CREATE INDEX IF NOT EXISTS ""idx_sc_honeypot"" ON ""ServerCredentials"" (""IsHoneypot"");");

            Console.WriteLine("[DB] SQLite schema stabilization completed successfully.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DB] ERROR: SQLite schema stabilization failed: {ex.Message}");
        }
    }

    private async Task EnsurePostgresSchemaAsync(DBContext context)
    {
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
                ALTER TABLE ""TelegramSubscribers"" ADD COLUMN IF NOT EXISTS ""NodeUrl"" TEXT;
                ALTER TABLE ""TelegramSubscribers"" ADD COLUMN IF NOT EXISTS ""LastNodeHeartbeatUtc"" TIMESTAMP WITH TIME ZONE;
                ALTER TABLE ""TelegramSubscribers"" ADD COLUMN IF NOT EXISTS ""DeployHook"" TEXT;
                CREATE UNIQUE INDEX IF NOT EXISTS ""IX_TelegramSubscribers_NodeToken"" ON ""TelegramSubscribers"" (""NodeToken"") WHERE ""NodeToken"" IS NOT NULL;");

            // 2. SearchQueries
            await context.Database.ExecuteSqlRawAsync(@"
                CREATE TABLE IF NOT EXISTS ""SearchQueries"" (""Id"" SERIAL PRIMARY KEY);
                ALTER TABLE ""SearchQueries"" ADD COLUMN IF NOT EXISTS ""Query"" TEXT NOT NULL DEFAULT '';
                ALTER TABLE ""SearchQueries"" ADD COLUMN IF NOT EXISTS ""IsEnabled"" BOOLEAN DEFAULT TRUE;
                ALTER TABLE ""SearchQueries"" ADD COLUMN IF NOT EXISTS ""SearchResultsCount"" INTEGER DEFAULT 0;
                ALTER TABLE ""SearchQueries"" ADD COLUMN IF NOT EXISTS ""LastSearchUTC"" TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP;
                ALTER TABLE ""SearchQueries"" ADD COLUMN IF NOT EXISTS ""LastDeepSearchDateUTC"" TIMESTAMP WITH TIME ZONE;
                ALTER TABLE ""SearchQueries"" ADD COLUMN IF NOT EXISTS ""LastSuccessfulSearchUTC"" TIMESTAMP WITH TIME ZONE;
                ALTER TABLE ""SearchQueries"" ADD COLUMN IF NOT EXISTS ""LastRepoPushedSeenUTC"" TIMESTAMP WITH TIME ZONE;
                CREATE INDEX IF NOT EXISTS ""IX_SearchQueries_IsEnabled_LastSearchUTC"" ON ""SearchQueries"" (""IsEnabled"", ""LastSearchUTC"");
                CREATE INDEX IF NOT EXISTS ""IX_SearchQueries_IsEnabled_LastSuccessfulSearchUTC"" ON ""SearchQueries"" (""IsEnabled"", ""LastSuccessfulSearchUTC"");");

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
                ALTER TABLE ""APIKeys"" ADD COLUMN IF NOT EXISTS ""AwsAccountId"" TEXT;
                ALTER TABLE ""APIKeys"" ADD COLUMN IF NOT EXISTS ""AwsUserArn"" TEXT;
                ALTER TABLE ""APIKeys"" ADD COLUMN IF NOT EXISTS ""AwsUserId"" TEXT;
                ALTER TABLE ""APIKeys"" ADD COLUMN IF NOT EXISTS ""AwsCredentialType"" TEXT;
                ALTER TABLE ""APIKeys"" ADD COLUMN IF NOT EXISTS ""AwsAttachedPolicies"" TEXT;
                ALTER TABLE ""APIKeys"" ADD COLUMN IF NOT EXISTS ""AwsRiskLevel"" TEXT;
                ALTER TABLE ""APIKeys"" ADD COLUMN IF NOT EXISTS ""AwsIsRootAccount"" BOOLEAN DEFAULT FALSE;
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
                ALTER TABLE ""RepoReferences"" ADD COLUMN IF NOT EXISTS ""RepoPushedAt"" TIMESTAMP WITH TIME ZONE;
                ALTER TABLE ""RepoReferences"" ADD COLUMN IF NOT EXISTS ""RepoDescription"" TEXT;
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

            // 8. ServerCredentials
            await context.Database.ExecuteSqlRawAsync(@"
                CREATE TABLE IF NOT EXISTS ""ServerCredentials"" (
                    ""Id"" SERIAL PRIMARY KEY,
                    ""CredentialType"" VARCHAR(50)  NOT NULL,
                    ""Host"" VARCHAR(255) NOT NULL,
                    ""Port"" INTEGER      NOT NULL DEFAULT 0,
                    ""Username"" VARCHAR(255),
                    ""PasswordHash"" VARCHAR(64),
                    ""Domain"" VARCHAR(255),
                    ""NetworkStatus"" VARCHAR(50)  NOT NULL DEFAULT 'Unknown',
                    ""AuthenticationStatus"" VARCHAR(50)  NOT NULL DEFAULT 'Untested',
                    ""ServerMetadata"" JSONB        NOT NULL DEFAULT jsonb_build_object(),
                    ""GeolocationData"" JSONB        NOT NULL DEFAULT jsonb_build_object(),
                    ""OSINTData"" JSONB        NOT NULL DEFAULT jsonb_build_object(),
                    ""RiskLevel"" VARCHAR(20)  NOT NULL DEFAULT 'Low',
                    ""IsHoneypot"" BOOLEAN      NOT NULL DEFAULT FALSE,
                    ""SourceRepository"" VARCHAR(500),
                    ""SourceFilePath"" VARCHAR(500),
                    ""SurroundingContext"" TEXT,
                    ""EntropyScore"" DOUBLE PRECISION DEFAULT 0,
                    ""DiscoveredAt"" TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
                    ""LastVerifiedAt"" TIMESTAMPTZ,
                    CONSTRAINT ""uq_server_cred"" UNIQUE (""Host"", ""Port"", ""Username"", ""CredentialType"")
                );
                ALTER TABLE ""ServerCredentials"" ADD COLUMN IF NOT EXISTS ""CredentialType"" VARCHAR(50) NOT NULL DEFAULT 'Unknown';
                ALTER TABLE ""ServerCredentials"" ADD COLUMN IF NOT EXISTS ""Host"" VARCHAR(255) NOT NULL DEFAULT 'Unknown';
                ALTER TABLE ""ServerCredentials"" ADD COLUMN IF NOT EXISTS ""Port"" INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE ""ServerCredentials"" ADD COLUMN IF NOT EXISTS ""Username"" VARCHAR(255);
                ALTER TABLE ""ServerCredentials"" ADD COLUMN IF NOT EXISTS ""PasswordHash"" VARCHAR(64);
                ALTER TABLE ""ServerCredentials"" ADD COLUMN IF NOT EXISTS ""Domain"" VARCHAR(255);
                ALTER TABLE ""ServerCredentials"" ADD COLUMN IF NOT EXISTS ""NetworkStatus"" VARCHAR(50) NOT NULL DEFAULT 'Unknown';
                ALTER TABLE ""ServerCredentials"" ADD COLUMN IF NOT EXISTS ""AuthenticationStatus"" VARCHAR(50) NOT NULL DEFAULT 'Untested';
                ALTER TABLE ""ServerCredentials"" ADD COLUMN IF NOT EXISTS ""ServerMetadata"" JSONB NOT NULL DEFAULT jsonb_build_object();
                ALTER TABLE ""ServerCredentials"" ADD COLUMN IF NOT EXISTS ""GeolocationData"" JSONB NOT NULL DEFAULT jsonb_build_object();
                ALTER TABLE ""ServerCredentials"" ADD COLUMN IF NOT EXISTS ""OSINTData"" JSONB NOT NULL DEFAULT jsonb_build_object();
                ALTER TABLE ""ServerCredentials"" ADD COLUMN IF NOT EXISTS ""RiskLevel"" VARCHAR(20) NOT NULL DEFAULT 'Low';
                ALTER TABLE ""ServerCredentials"" ADD COLUMN IF NOT EXISTS ""IsHoneypot"" BOOLEAN NOT NULL DEFAULT FALSE;
                ALTER TABLE ""ServerCredentials"" ADD COLUMN IF NOT EXISTS ""SourceRepository"" VARCHAR(500);
                ALTER TABLE ""ServerCredentials"" ADD COLUMN IF NOT EXISTS ""SourceFilePath"" VARCHAR(500);
                ALTER TABLE ""ServerCredentials"" ADD COLUMN IF NOT EXISTS ""SurroundingContext"" TEXT;
                ALTER TABLE ""ServerCredentials"" ADD COLUMN IF NOT EXISTS ""EntropyScore"" DOUBLE PRECISION DEFAULT 0;
                ALTER TABLE ""ServerCredentials"" ADD COLUMN IF NOT EXISTS ""DiscoveredAt"" TIMESTAMPTZ NOT NULL DEFAULT NOW();
                ALTER TABLE ""ServerCredentials"" ADD COLUMN IF NOT EXISTS ""LastVerifiedAt"" TIMESTAMPTZ;
                CREATE INDEX IF NOT EXISTS ""idx_sc_type"" ON ""ServerCredentials"" (""CredentialType"");
                CREATE INDEX IF NOT EXISTS ""idx_sc_risk"" ON ""ServerCredentials"" (""RiskLevel"");
                CREATE INDEX IF NOT EXISTS ""idx_sc_auth_status"" ON ""ServerCredentials"" (""AuthenticationStatus"");
                CREATE INDEX IF NOT EXISTS ""idx_sc_honeypot"" ON ""ServerCredentials"" (""IsHoneypot"");");

            Console.WriteLine("[DB] Full PostgreSQL schema stabilization completed successfully.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DB] CRITICAL: PostgreSQL schema stabilization failed: {ex.Message}");
        }
    }

    private async Task SeedDefaultQueriesAsync(DBContext context)
    {
        try 
        {
            Console.WriteLine("[DB] Checking for default search targets...");
            var now = DateTime.UtcNow;
            var defaults = new List<string>
            {
                // OpenAI
                "sk-proj-", "sk-svcacct-", "OPENAI_API_KEY",
                // Anthropic
                "sk-ant-api", "ANTHROPIC_API_KEY",
                // Google
                "AIzaSy", "GOOGLE_API_KEY", "GEMINI_API_KEY",
                // DeepSeek
                "DEEPSEEK_API_KEY", "deepseek-chat",
                // Kling AI
                "KLING_ACCESS_KEY", "KLING_API_KEY",
                // Pollo AI
                "POLLO_API_KEY", "pollo_",
                // Runway ML
                "RUNWAYML_API_SECRET", "RUNWAY_API_KEY",
                // Cohere
                "COHERE_API_KEY", "CO_API_KEY",
                // ElevenLabs
                "ELEVENLABS_API_KEY", "ELEVEN_API_KEY", "xi-api-key",
                // Stability AI
                "STABILITY_API_KEY",
                // Together AI
                "TOGETHER_API_KEY",
                // xAI / Grok
                "XAI_API_KEY", "xai-",
                // Replicate
                "REPLICATE_API_TOKEN", "r8_",
                // Fireworks AI
                "FIREWORKS_API_KEY", "fw_",
                // HuggingFace
                "HUGGINGFACE_API_KEY", "HF_TOKEN", "hf_",
                // A2E AI
                "A2E_API_KEY", "A2E_SECRET",
                // PiAPI
                "PIAPI_KEY",
                // Groq
                "GROQ_API_KEY", "gsk_",
                // Mistral AI
                "MISTRAL_API_KEY",
                // OpenRouter
                "OPENROUTER_API_KEY", "sk-or-v1-",
                // Perplexity
                "PERPLEXITY_API_KEY", "PPLX_API_KEY", "pplx-",
                // Cerebras
                "CEREBRAS_API_KEY", "csk-",
                // Voyage AI
                "VOYAGE_API_KEY", "VOYAGEAI_API_KEY",
                // AWS Bedrock
                "AWS_BEARER_TOKEN_BEDROCK", "BEDROCK_API_KEY",
                // Azure OpenAI
                "AZURE_OPENAI_API_KEY", "AZURE_OPENAI_KEY",
                // AWS IAM
                "AKIA", "ASIA", "AWS_ACCESS_KEY_ID", "AWS_SECRET_ACCESS_KEY",
                "aws_access_key_id", "aws_secret_access_key",
                // Server Credentials
                "ssh ", "ftp://", "mysql://", "postgresql://", "mongodb://", "redis://",
                "-----BEGIN RSA PRIVATE KEY-----", "KUBERNETES_SERVICE_HOST", "DOCKER_HOST",
                "rdp://", "vnc://", "mstsc", "TeamViewer", "filename:.rdp", "WinRM",
                "smtp://", "SMTP_HOST", "imap://", "pop3://", "cPanel", "WHM_USER",
                "PLESK_", "filename:.bash_history", "filename:id_rsa", "extension:env"
            };

            var existingQueries = await context.SearchQueries.Select(q => q.Query).ToListAsync();
            var existingSet = new HashSet<string>(existingQueries);
            bool addedAny = false;

            foreach (var q in defaults)
            {
                if (!existingSet.Contains(q))
                {
                    context.SearchQueries.Add(new SearchQuery { Query = q, IsEnabled = true, LastSearchUTC = now });
                    addedAny = true;
                }
            }

            if (addedAny)
            {
                await context.SaveChangesAsync();
                Console.WriteLine("[DB] Seeded missing default search targets.");
            }
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
                if (provider.RegexPatterns.Any(p => 
                {
                    try { return System.Text.RegularExpressions.Regex.IsMatch(key.ApiKey, p, System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(2)); }
                    catch { return false; }
                }))
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
            "sk_",           // A2E
            "A2E_API_KEY",
            "A2E_SECRET",
            "PIAPI_KEY",
            "piapi.ai",
            "X-API-KEY",

            // AWS IAM
            "AKIA",
            "ASIA",
            "AWS_ACCESS_KEY_ID",
            "AWS_SECRET_ACCESS_KEY",
            "aws_access_key_id",
            "aws_secret_access_key",
            // Server Credentials
            "ssh ",
            "ftp://",
            "mysql://",
            "postgresql://",
            "mongodb://",
            "redis://",
            "-----BEGIN RSA PRIVATE KEY-----",
            "KUBERNETES_SERVICE_HOST",
            "DOCKER_HOST",
            "rdp://",
            "vnc://",
            "mstsc",
            "TeamViewer",
            "filename:.rdp",
            "WinRM",
            "smtp://",
            "SMTP_HOST",
            "imap://",
            "pop3://",
            "cPanel",
            "WHM_USER",
            "PLESK_",
            "filename:.bash_history",
            "filename:id_rsa",
            "extension:env",
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
            A2EKeys = await query.CountAsync(k => k.ApiType == ApiTypeEnum.A2E),
            PiAPIKeys = await query.CountAsync(k => k.ApiType == ApiTypeEnum.PiAPI),
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

        // Get status counts directly from DB
        var statusCounts = await query.GroupBy(k => k.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync();

        // Get type counts directly from DB
        var typeCounts = await query.GroupBy(k => k.ApiType)
            .Select(g => new { Type = g.Key, Count = g.Count() })
            .ToListAsync();

        var categorized = new CategorizedStatistics
        {
            TotalKeys = statusCounts.Sum(c => c.Count),
            ValidKeys = statusCounts.FirstOrDefault(c => c.Status == ApiStatusEnum.Valid)?.Count ?? 0,
            InvalidKeys = statusCounts.FirstOrDefault(c => c.Status == ApiStatusEnum.Invalid)?.Count ?? 0,
            UnverifiedKeys = statusCounts.FirstOrDefault(c => c.Status == ApiStatusEnum.Unverified)?.Count ?? 0,
            ValidNoCreditsKeys = statusCounts.FirstOrDefault(c => c.Status == ApiStatusEnum.ValidNoCredits)?.Count ?? 0,
            GitHubTokensCount = await dbContext.SearchProviderTokens
                .CountAsync(t => t.IsEnabled && t.SearchProvider == SearchProviderEnum.GitHub),
            Categories = new Dictionary<ApiCategoryEnum, CategoryStats>()
        };

        // Group by category in memory from the summary results
        var typeGroups = typeCounts.GroupBy(t => GetCategoryForApiType(t.Type));

        foreach (var typeGroup in typeGroups)
        {
            var category = typeGroup.Key;
            var categoryStats = new CategoryStats
            {
                CategoryName = GetCategoryName(category),
                TotalKeys = typeGroup.Sum(t => t.Count),
                ApiTypes = typeGroup.Select(t => new ApiTypeStats
                {
                    ApiType = t.Type,
                    ApiTypeName = t.Type.ToString(),
                    KeyCount = t.Count
                }).OrderByDescending(t => t.KeyCount).ToList()
            };

            categorized.Categories[category] = categoryStats;
        }

        categorized.DatabaseSizeBytes = await GetDatabaseSizeInBytesAsync();
        return categorized;
    }

    public async Task<long> GetDatabaseSizeInBytesAsync()
    {
        try
        {
            if (dbContext.Database.IsNpgsql())
            {
                // PostgreSQL/Supabase: Get size of current database
                var conn = dbContext.Database.GetDbConnection();
                var dbName = conn.Database;
                
                using var command = conn.CreateCommand();
                command.CommandText = $"SELECT pg_database_size('{dbName}');";
                
                if (conn.State != System.Data.ConnectionState.Open)
                    await conn.OpenAsync();
                
                var result = await command.ExecuteScalarAsync();
                return result != null ? Convert.ToInt64(result) : 0L;
            }
            else if (dbContext.Database.IsSqlite())
            {
                // SQLite: Get file size
                if (File.Exists(_dbPath))
                {
                    return new FileInfo(_dbPath).Length;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DB] Error getting database size: {ex.Message}");
        }
        
        return 0L;
    }

    public (string ProviderName, double StorageLimitMb) GetDatabaseProviderInfo()
    {
        // 1. Check for manual environment override
        if (double.TryParse(Environment.GetEnvironmentVariable("DATABASE_STORAGE_LIMIT_MB"), out double manualLimit) && manualLimit > 0)
        {
            var provider = GetDetectedProviderName();
            return (provider, manualLimit);
        }

        var connStr = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING") ?? "";
        if (string.IsNullOrEmpty(connStr))
        {
            return ("SQLite", 1024.0); // 1 GB local default
        }

        var lowerConn = connStr.ToLowerInvariant();
        if (lowerConn.Contains("aivencloud.com") || lowerConn.Contains("aiven"))
        {
            // Aiven plan default is 1024 MB (1 GB) for initial free plan or 5120 MB (5 GB)
            return ("Aiven", 1024.0);
        }
        if (lowerConn.Contains("supabase.co") || lowerConn.Contains("supabase.com"))
        {
            return ("Supabase", 500.0); // 500 MB Supabase Free Tier
        }
        if (lowerConn.Contains("cockroachlabs.cloud") || lowerConn.Contains("cockroach"))
        {
            return ("CockroachDB", 10240.0); // 10 GB Cockroach Serverless
        }
        if (lowerConn.Contains("neon.tech"))
        {
            return ("Neon", 500.0); // 500 MB Neon Free
        }
        if (lowerConn.Contains("railway.app") || lowerConn.Contains("railway.internal"))
        {
            return ("Railway", 5120.0); // 5 GB Railway
        }

        return ("PostgreSQL", 5120.0); // Generic Postgres Default 5 GB
    }

    private string GetDetectedProviderName()
    {
        var connStr = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING") ?? "";
        if (string.IsNullOrEmpty(connStr)) return "SQLite";
        
        var lowerConn = connStr.ToLowerInvariant();
        if (lowerConn.Contains("aivencloud.com") || lowerConn.Contains("aiven")) return "Aiven";
        if (lowerConn.Contains("supabase.co") || lowerConn.Contains("supabase.com")) return "Supabase";
        if (lowerConn.Contains("cockroachlabs.cloud") || lowerConn.Contains("cockroach")) return "CockroachDB";
        if (lowerConn.Contains("neon.tech")) return "Neon";
        if (lowerConn.Contains("railway.app") || lowerConn.Contains("railway.internal")) return "Railway";
        return "PostgreSQL";
    }

    public async Task<int> PurgeJunkSourcesAsync(DBContext context)
    {
        try
        {
            Console.WriteLine("[DB] Purging invalid API keys and junk references...");

            // 1. Delete all RepoReferences associated with invalid keys
            int deletedRefs = await context.RepoReferences
                .Where(r => context.APIKeys
                    .Any(k => k.Id == r.APIKeyId && k.Status == ApiStatusEnum.Invalid))
                .ExecuteDeleteAsync();

            // 2. Delete all APIKeys with Status = Invalid (0)
            int deletedKeys = await context.APIKeys
                .Where(k => k.Status == ApiStatusEnum.Invalid)
                .ExecuteDeleteAsync();

            if (deletedKeys > 0 || deletedRefs > 0)
                Console.WriteLine($"[DB] Purged {deletedKeys} invalid API key(s) and {deletedRefs} junk repo reference(s).");
            else
                Console.WriteLine("[DB] No invalid keys or junk references found.");

            return deletedKeys + deletedRefs;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DB] Error during purge: {ex.Message}");
            return 0;
        }
    }

    public static ApiCategoryEnum GetCategoryForApiType(ApiTypeEnum apiType)
    {
        return apiType switch
        {
            ApiTypeEnum.OpenAI or ApiTypeEnum.AnthropicClaude or ApiTypeEnum.GoogleAI or
            ApiTypeEnum.Cohere or ApiTypeEnum.HuggingFace or ApiTypeEnum.StabilityAI or
            ApiTypeEnum.Replicate or ApiTypeEnum.TogetherAI or ApiTypeEnum.DeepSeek or
            ApiTypeEnum.ElevenLabs or ApiTypeEnum.XAI or ApiTypeEnum.FireworksAI or
            ApiTypeEnum.KlingAI or ApiTypeEnum.PolloAI or ApiTypeEnum.RunwayML or
            ApiTypeEnum.A2E or ApiTypeEnum.PiAPI or ApiTypeEnum.Groq or
            ApiTypeEnum.MistralAI or ApiTypeEnum.OpenRouter or ApiTypeEnum.Perplexity or
            ApiTypeEnum.Cerebras or ApiTypeEnum.VoyageAI or ApiTypeEnum.AWSBedrock or
            ApiTypeEnum.AzureOpenAI or ApiTypeEnum.AWSIAM or
            ApiTypeEnum.AI21Labs or ApiTypeEnum.AssemblyAI or
            ApiTypeEnum.Deepgram or ApiTypeEnum.JinaAI or
            ApiTypeEnum.Upstage or ApiTypeEnum.LeonardoAI or ApiTypeEnum.FalAI or
            ApiTypeEnum.RunPod or ApiTypeEnum.Tavily or ApiTypeEnum.SarvamAI or ApiTypeEnum.Unsplash
                => ApiCategoryEnum.AIAndLLM,

            ApiTypeEnum.SendGrid or ApiTypeEnum.Mailgun or ApiTypeEnum.Slack or
            ApiTypeEnum.Facebook or ApiTypeEnum.GoogleOAuth or
            ApiTypeEnum.Stripe or ApiTypeEnum.TikTok or ApiTypeEnum.GcpHmac or
            ApiTypeEnum.GitHubToken
                => ApiCategoryEnum.Communication,

            ApiTypeEnum.ServerCredential
                => ApiCategoryEnum.ServerCredentials,

            ApiTypeEnum.Mapbox or ApiTypeEnum.WeatherApi
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
            ApiCategoryEnum.ServerCredentials => "Server Credentials",
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

    public async Task ExportKeysAsync(DBContext dbContext, string filePath, string format, ApiStatusEnum? statusFilter = null, long? filterByTelegramId = null)
    {
        var query = dbContext.APIKeys.AsNoTracking();

        if (filterByTelegramId.HasValue)
        {
            query = query.Where(k => k.DiscoveredByTelegramId == filterByTelegramId.Value);
        }

        if (statusFilter.HasValue)
        {
            if ((int)statusFilter.Value == -1)
            {
                // Export ALL (do nothing, no filter)
            }
            else
            {
                // Export specific status
                query = query.Where(k => k.Status == statusFilter.Value);
            }
        }
        else
        {
            // Default: Export all working keys (Valid and ValidNoCredits)
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
            ApiType = (int)k.ApiType,
            ApiTypeName = k.ApiType.ToString(),
            Status = (int)k.Status,
            StatusName = k.Status.ToString(),
            SearchProvider = k.SearchProvider.ToString(),
            k.Balance,
            k.AccountTier,
            k.FirstFoundUTC,
            k.LastFoundUTC,
            k.LastCheckedUTC,
            k.ErrorCount,
            FirstFoundIST = k.FirstFoundUTC.ToIst().ToString("yyyy-MM-dd HH:mm:ss"),
            LastCheckedIST = k.LastCheckedUTC?.ToIst().ToString("yyyy-MM-dd HH:mm:ss"),
            k.TimesDisplayed,
            k.ValidationResponse,
            k.Metadata,
            k.DiscoveredByTelegramId,
            AwsMetadata = k.AwsAccountId != null ? (object?)new
            {
                k.AwsAccountId,
                k.AwsUserArn,
                k.AwsUserId,
                k.AwsCredentialType,
                k.AwsRiskLevel,
                k.AwsIsRootAccount,
                AwsAttachedPolicies = !string.IsNullOrEmpty(k.AwsAttachedPolicies)
                    ? JsonSerializer.Deserialize<List<string>>(k.AwsAttachedPolicies)
                    : new List<string>()
            } : null,
            Sources = k.References.Select(r => new
            {
                Source = r.FileURL ?? (string.IsNullOrWhiteSpace(r.RepoURL) ? "" : $"{r.RepoURL}/blob/{r.Branch ?? "main"}/{r.FilePath}"),
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
            "Id,ApiKey,Type,TypeName,Status,StatusName,SearchProvider,Balance,Tier,ValidationResponse,Metadata," +
            "FirstFoundUTC,LastFoundUTC,LastCheckedUTC,TimesDisplayed,ErrorCount,DiscoveredByTelegramId," +
            "AwsAccountId,AwsUserArn,AwsUserId,AwsCredentialType,AwsRiskLevel,AwsIsRootAccount,AwsAttachedPolicies," +
            "FirstFoundIST,LastCheckedIST,Source,SourceFoundIST"
        };

        foreach (var key in keys)
        {
            // Format AWS attached policies as semicolon-separated string
            var policies = string.Empty;
            if (!string.IsNullOrEmpty(key.AwsAttachedPolicies))
            {
                try
                {
                    var policyList = JsonSerializer.Deserialize<List<string>>(key.AwsAttachedPolicies);
                    policies = policyList != null ? string.Join("; ", policyList) : "";
                }
                catch
                {
                    policies = key.AwsAttachedPolicies;
                }
            }

            if (key.References == null || !key.References.Any())
            {
                // Export at least one line even if no references exist
                lines.Add(string.Join(",", new[]
                {
                    key.Id.ToString(), CsvField(key.ApiKey), ((int)key.ApiType).ToString(), CsvField(key.ApiType.ToString()),
                    ((int)key.Status).ToString(), CsvField(key.Status.ToString()), CsvField(key.SearchProvider.ToString()),
                    CsvField(key.Balance), CsvField(key.AccountTier), CsvField(key.ValidationResponse), CsvField(key.Metadata),
                    CsvField(key.FirstFoundUTC.ToString("O")), CsvField(key.LastFoundUTC.ToString("O")), CsvField(key.LastCheckedUTC?.ToString("O")),
                    key.TimesDisplayed.ToString(), key.ErrorCount.ToString(), key.DiscoveredByTelegramId?.ToString() ?? "",
                    CsvField(key.AwsAccountId), CsvField(key.AwsUserArn), CsvField(key.AwsUserId), CsvField(key.AwsCredentialType),
                    CsvField(key.AwsRiskLevel), key.AwsIsRootAccount.ToString(), CsvField(policies),
                    CsvField(key.FirstFoundUTC.ToIst().ToString("yyyy-MM-dd HH:mm:ss")), CsvField(key.LastCheckedUTC?.ToIst().ToString("yyyy-MM-dd HH:mm:ss")), "\"\"", "\"\""
                }));
            }
            else
            {
                foreach (var r in key.References)
                {
                    var source = r.FileURL ?? (string.IsNullOrWhiteSpace(r.RepoURL) ? "" : $"{r.RepoURL}/blob/{r.Branch ?? "main"}/{r.FilePath}");
                    lines.Add(string.Join(",", new[]
                    {
                        key.Id.ToString(), CsvField(key.ApiKey), ((int)key.ApiType).ToString(), CsvField(key.ApiType.ToString()),
                        ((int)key.Status).ToString(), CsvField(key.Status.ToString()), CsvField(key.SearchProvider.ToString()),
                        CsvField(key.Balance), CsvField(key.AccountTier), CsvField(key.ValidationResponse), CsvField(key.Metadata),
                        CsvField(key.FirstFoundUTC.ToString("O")), CsvField(key.LastFoundUTC.ToString("O")), CsvField(key.LastCheckedUTC?.ToString("O")),
                        key.TimesDisplayed.ToString(), key.ErrorCount.ToString(), key.DiscoveredByTelegramId?.ToString() ?? "",
                        CsvField(key.AwsAccountId), CsvField(key.AwsUserArn), CsvField(key.AwsUserId), CsvField(key.AwsCredentialType),
                        CsvField(key.AwsRiskLevel), key.AwsIsRootAccount.ToString(), CsvField(policies),
                        CsvField(key.FirstFoundUTC.ToIst().ToString("yyyy-MM-dd HH:mm:ss")), CsvField(key.LastCheckedUTC?.ToString("yyyy-MM-dd HH:mm:ss")),
                        CsvField(source), CsvField(r.FoundUTC.ToIst().ToString("yyyy-MM-dd HH:mm:ss"))
                    }));
                }
            }
        }

        await File.WriteAllLinesAsync(filePath, lines);
    }

    private static string CsvField(string? value)
    {
        var sanitized = (value ?? string.Empty).Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " ");
        return $"\"{sanitized}\"";
    }

    public async Task ExportServerCredentialsAsync(DBContext dbContext, string filePath, string format, string? typeFilter = null, string? riskFilter = null, string? authStatusFilter = null)
    {
        var query = dbContext.ServerCredentials.AsNoTracking();

        if (!string.IsNullOrEmpty(typeFilter) && typeFilter != "All")
        {
            query = query.Where(sc => sc.CredentialType == typeFilter);
        }

        if (!string.IsNullOrEmpty(riskFilter) && riskFilter != "All")
        {
            query = query.Where(sc => sc.RiskLevel == riskFilter);
        }

        if (!string.IsNullOrEmpty(authStatusFilter) && authStatusFilter != "All")
        {
            query = query.Where(sc => sc.AuthenticationStatus == authStatusFilter);
        }

        var credentials = await query.ToListAsync();

        if (format.ToLower() == "json")
        {
            await ExportServerCredentialsAsJsonAsync(credentials, filePath);
        }
        else
        {
            await ExportServerCredentialsAsCsvAsync(credentials, filePath);
        }
    }

    private async Task ExportServerCredentialsAsJsonAsync(List<ServerCredential> credentials, string filePath)
    {
        var exportData = credentials.Select(sc => new
        {
            sc.Id,
            sc.CredentialType,
            sc.Host,
            sc.Port,
            sc.Username,
            sc.Password,
            sc.Domain,
            sc.NetworkStatus,
            sc.AuthenticationStatus,
            ServerMetadata = !string.IsNullOrEmpty(sc.ServerMetadata) ? JsonSerializer.Deserialize<object>(sc.ServerMetadata) : null,
            GeolocationData = !string.IsNullOrEmpty(sc.GeolocationData) ? JsonSerializer.Deserialize<object>(sc.GeolocationData) : null,
            OSINTData = !string.IsNullOrEmpty(sc.OSINTData) ? JsonSerializer.Deserialize<object>(sc.OSINTData) : null,
            sc.RiskLevel,
            sc.IsHoneypot,
            sc.SourceRepository,
            sc.SourceFilePath,
            sc.SurroundingContext,
            sc.EntropyScore,
            DiscoveredAtIST = sc.DiscoveredAt.ToIst().ToString("yyyy-MM-dd HH:mm:ss"),
            LastVerifiedAtIST = sc.LastVerifiedAt?.ToIst().ToString("yyyy-MM-dd HH:mm:ss")
        });

        var json = System.Text.Json.JsonSerializer.Serialize(exportData, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true
        });

        await File.WriteAllTextAsync(filePath, json);
    }

    private async Task ExportServerCredentialsAsCsvAsync(List<ServerCredential> credentials, string filePath)
    {
        var lines = new List<string>
        {
            "Id,CredentialType,Host,Port,Username,Password,Domain,NetworkStatus,AuthenticationStatus," +
            "RiskLevel,IsHoneypot,EntropyScore,SourceRepository,SourceFilePath," +
            "ServerMetadata,GeolocationData,OSINTData,DiscoveredAtIST,LastVerifiedAtIST"
        };

        foreach (var sc in credentials)
        {
            var host = sc.Host?.Replace("\"", "\"\"").Replace("\n", " ").Replace("\r", " ") ?? "";
            var username = sc.Username?.Replace("\"", "\"\"").Replace("\n", " ").Replace("\r", " ") ?? "";
            var domain = sc.Domain?.Replace("\"", "\"\"").Replace("\n", " ").Replace("\r", " ") ?? "";
            var sourceRepo = sc.SourceRepository?.Replace("\"", "\"\"").Replace("\n", " ").Replace("\r", " ") ?? "";
            var sourcePath = sc.SourceFilePath?.Replace("\"", "\"\"").Replace("\n", " ").Replace("\r", " ") ?? "";

            var flatMetadata = FlattenJson(sc.ServerMetadata).Replace("\"", "\"\"").Replace("\n", " ").Replace("\r", " ");
            var flatGeo = FlattenJson(sc.GeolocationData).Replace("\"", "\"\"").Replace("\n", " ").Replace("\r", " ");
            var flatOsint = FlattenJson(sc.OSINTData).Replace("\"", "\"\"").Replace("\n", " ").Replace("\r", " ");

            lines.Add($"{sc.Id},\"{sc.CredentialType}\",\"{host}\",{sc.Port},\"{username}\",\"{sc.Password}\",\"{domain}\"," +
                      $"\"{sc.NetworkStatus}\",\"{sc.AuthenticationStatus}\",\"{sc.RiskLevel}\",{sc.IsHoneypot}," +
                      $"{sc.EntropyScore},\"{sourceRepo}\",\"{sourcePath}\",\"{flatMetadata}\",\"{flatGeo}\",\"{flatOsint}\"," +
                      $"\"{sc.DiscoveredAt.ToIst():yyyy-MM-dd HH:mm:ss}\",\"{sc.LastVerifiedAt?.ToIst():yyyy-MM-dd HH:mm:ss}\"");
        }

        await File.WriteAllLinesAsync(filePath, lines);
    }

    private string FlattenJson(string? json)
    {
        if (string.IsNullOrEmpty(json) || json == "{}")
            return "";
        try
        {
            using var doc = JsonDocument.Parse(json);
            var pairs = new List<string>();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                var valStr = prop.Value.ValueKind switch
                {
                    JsonValueKind.Object => prop.Value.GetRawText(),
                    JsonValueKind.Array => prop.Value.GetRawText(),
                    _ => prop.Value.ToString()
                };
                pairs.Add($"{prop.Name}: {valStr}");
            }
            return string.Join(" | ", pairs);
        }
        catch
        {
            return json ?? "";
        }
    }


    public async Task<int> PurgeInvalidReferencesAsync(DBContext context)
    {
        return await PurgeJunkSourcesAsync(context);
    }

    public async Task VacuumDatabaseAsync(DBContext context)
    {
        if (context.Database.IsNpgsql())
        {
            // PostgreSQL non-blocking vacuum
            await context.Database.ExecuteSqlRawAsync("VACUUM ANALYZE;");
        }
        else if (context.Database.IsSqlite())
        {
            // SQLite full vacuum (reclaims file space)
            await context.Database.ExecuteSqlRawAsync("VACUUM;");
        }
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
    public int A2EKeys { get; set; }
    public int PiAPIKeys { get; set; }
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
    public long DatabaseSizeBytes { get; set; }
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

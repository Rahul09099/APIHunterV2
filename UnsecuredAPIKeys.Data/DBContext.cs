using Microsoft.EntityFrameworkCore;
using UnsecuredAPIKeys.Data.Models;

namespace UnsecuredAPIKeys.Data
{
    /// <summary>
    /// SQLite database context for UnsecuredAPIKeys Lite.
    /// Full version with PostgreSQL: www.UnsecuredAPIKeys.com
    /// </summary>
    public class DBContext : DbContext
    {
        private readonly string _dbPath;

        public DBContext(DbContextOptions<DBContext> options) : base(options)
        {
            _dbPath = Environment.GetEnvironmentVariable("DATABASE_PATH") ?? "unsecuredapikeys.db";
        }

        public DBContext()
        {
            _dbPath = Environment.GetEnvironmentVariable("DATABASE_PATH") ?? "unsecuredapikeys.db";
        }

        public DBContext(string dbPath)
        {
            _dbPath = dbPath;
        }

        // Core entities
        public DbSet<APIKey> APIKeys { get; set; } = null!;
        public DbSet<RepoReference> RepoReferences { get; set; } = null!;
        public DbSet<SearchQuery> SearchQueries { get; set; } = null!;
        public DbSet<SearchProviderToken> SearchProviderTokens { get; set; } = null!;
        public DbSet<ApplicationSetting> ApplicationSettings { get; set; } = null!;
        public DbSet<DeepSearchProgress> DeepSearchProgress { get; set; } = null!;
        public DbSet<TelegramSubscriber> TelegramSubscribers { get; set; } = null!;

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            if (!optionsBuilder.IsConfigured)
            {
                var connectionString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING");
                if (!string.IsNullOrEmpty(connectionString))
                {
                    optionsBuilder.UseNpgsql(ConvertPostgresUrl(connectionString));
                }
                else
                {
                    optionsBuilder.UseSqlite($"Data Source={_dbPath}");
                }
            }
        }

        public static string ConvertPostgresUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return url;
            
            // If it doesn't look like a URI, return it as is (might be a standard connection string)
            if (!url.Contains("://")) return url;

            try
            {
                // Ensure we support both postgres:// and postgresql://
                var uriString = url;
                if (url.StartsWith("postgres://"))
                {
                    uriString = "postgresql://" + url.Substring("postgres://".Length);
                }

                var uri = new Uri(uriString);
                var userInfo = uri.UserInfo.Split(':');
                var username = Uri.UnescapeDataString(userInfo[0]);
                var password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : "";
                var database = uri.AbsolutePath.TrimStart('/');
                var port = uri.Port == -1 ? 5432 : uri.Port;
                var connStr = $"Host={uri.Host};Port={port};Database={database};Username={username};Password={password};SSL Mode=Require;Trust Server Certificate=true";
                
                // If using the pooler (6543), disable prepared statements to prevent hangs during EnsureCreated
                if (port == 6543)
                {
                    connStr += ";Max Auto Prepare=0;";
                }
                
                return connStr;
            }
            catch 
            { 
                // Fallback to returning original string if parsing fails
                return url; 
            }
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // APIKey indexes for performance
            modelBuilder.Entity<APIKey>()
                .HasIndex(k => k.ApiKey)
                .IsUnique()
                .HasDatabaseName("IX_APIKeys_ApiKey");

            modelBuilder.Entity<APIKey>()
                .HasIndex(k => new { k.Status, k.ApiType })
                .HasDatabaseName("IX_APIKeys_Status_ApiType");

            modelBuilder.Entity<APIKey>()
                .HasIndex(k => k.LastCheckedUTC)
                .HasDatabaseName("IX_APIKeys_LastCheckedUTC");

            modelBuilder.Entity<APIKey>()
                .HasIndex(k => k.DiscoveredByTelegramId)
                .HasDatabaseName("IX_APIKeys_DiscoveredByTelegramId");

            modelBuilder.Entity<APIKey>()
                .HasIndex(k => k.Status)
                .HasDatabaseName("IX_APIKeys_Status");

            // RepoReference indexes
            modelBuilder.Entity<RepoReference>()
                .HasIndex(r => r.APIKeyId)
                .HasDatabaseName("IX_RepoReferences_ApiKeyId");

            // SearchQuery indexes
            modelBuilder.Entity<SearchQuery>()
                .HasIndex(q => new { q.IsEnabled, q.LastSearchUTC })
                .HasDatabaseName("IX_SearchQueries_IsEnabled_LastSearchUTC");

            // SearchProviderToken indexes
            modelBuilder.Entity<SearchProviderToken>()
                .HasIndex(t => t.SearchProvider)
                .HasDatabaseName("IX_SearchProviderTokens_SearchProvider");

            modelBuilder.Entity<SearchProviderToken>()
                .HasIndex(t => t.AddedByTelegramId)
                .HasDatabaseName("IX_SearchProviderTokens_AddedByTelegramId");

            // DeepSearchProgress indexes
            modelBuilder.Entity<DeepSearchProgress>()
                .HasIndex(p => new { p.SearchQueryId, p.PartitionType, p.PartitionValue })
                .IsUnique()
                .HasDatabaseName("IX_DeepSearchProgress_Query_Partition");

            modelBuilder.Entity<DeepSearchProgress>()
                .HasIndex(p => p.IsCompleted)
                .HasDatabaseName("IX_DeepSearchProgress_IsCompleted");

            modelBuilder.Entity<TelegramSubscriber>()
                .HasIndex(s => s.NodeToken)
                .IsUnique()
                .HasDatabaseName("IX_TelegramSubscribers_NodeToken");

            // Relationships
            modelBuilder.Entity<RepoReference>()
                .HasOne(r => r.APIKey)
                .WithMany(k => k.References)
                .HasForeignKey(r => r.APIKeyId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<DeepSearchProgress>()
                .HasOne(p => p.SearchQuery)
                .WithMany()
                .HasForeignKey(p => p.SearchQueryId)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }
}

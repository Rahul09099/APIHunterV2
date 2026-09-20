using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using UnsecuredAPIKeys.Data.Common;
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

        [ActivatorUtilitiesConstructor]
        public DBContext(DbContextOptions<DBContext> options) : base(options)
        {
            _dbPath = Environment.GetEnvironmentVariable("DATABASE_PATH") ?? "unsecuredapikeys.db";
        }

        public async Task InitializeDatabaseAsync()
        {
            // DatabaseService owns active schema initialization and readiness validation.
            await Task.CompletedTask;
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
        public DbSet<ServerCredential> ServerCredentials { get; set; } = null!;
        public DbSet<RepoReference> RepoReferences { get; set; } = null!;
        public DbSet<SearchQuery> SearchQueries { get; set; } = null!;
        public DbSet<SearchProviderToken> SearchProviderTokens { get; set; } = null!;
        public DbSet<CredentialGrant> CredentialGrants { get; set; } = null!;
        public DbSet<PublicSearchConsent> PublicSearchConsents { get; set; } = null!;
        public DbSet<PrivilegedAuditRecord> PrivilegedAuditRecords { get; set; } = null!;
        public DbSet<SearchProviderInstance> SearchProviderInstances { get; set; } = null!;
        public DbSet<PlatformSchemaVersion> SchemaVersions { get; set; } = null!;
        public DbSet<ReadinessMarker> ReadinessMarkers { get; set; } = null!;
        public DbSet<CutoverMarker> CutoverMarkers { get; set; } = null!;
        public DbSet<ApplicationSetting> ApplicationSettings { get; set; } = null!;
        public DbSet<DeepSearchProgress> DeepSearchProgress { get; set; } = null!;
        public DbSet<TelegramSubscriber> TelegramSubscribers { get; set; } = null!;

        // Work pipeline, result persistence, Scheduler claim, and Operation Slot entities (Task 7–8)
        public DbSet<WorkItem> WorkItems { get; set; } = null!;
        public DbSet<WorkPartition> WorkPartitions { get; set; } = null!;
        public DbSet<SearchQueryOverride> SearchQueryOverrides { get; set; } = null!;
        public DbSet<NormalizedResult> NormalizedResults { get; set; } = null!;
        public DbSet<ResultDeduplicationRecord> ResultDeduplicationRecords { get; set; } = null!;
        public DbSet<ResultOutboxRecord> ResultOutboxRecords { get; set; } = null!;
        public DbSet<CredentialClaimRecord> CredentialClaimRecords { get; set; } = null!;
        public DbSet<OperationSlot> OperationSlots { get; set; } = null!;

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            if (!optionsBuilder.IsConfigured)
            {
                var connectionString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING");
                if (!string.IsNullOrEmpty(connectionString))
                {
                    optionsBuilder.UseNpgsql(
                        ConvertPostgresUrl(connectionString),
                        npgsqlOptions => npgsqlOptions.EnableRetryOnFailure(
                            maxRetryCount: 5,
                            maxRetryDelay: TimeSpan.FromSeconds(10),
                            errorCodesToAdd: null));
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
                var connStr = $"Host={uri.Host};Port={port};Database={database};Username={username};Password={password};SSL Mode=Require;Trust Server Certificate=true;Timeout=30;Command Timeout=60;Keepalive=30;Maximum Pool Size=10;";

                // If using transaction pooler (6543) or Supabase pooler, disable auto prepare and prevent DISCARD ALL on close for PgBouncer compatibility
                if (port == 6543 || uri.Host.Contains("pooler.supabase.com"))
                {
                    connStr += "Max Auto Prepare=0;No Reset On Close=true;";
                }

                return connStr;
            }
            catch
            {
                // Fallback to returning original string if parsing fails
                return url;
            }
        }

        public override int SaveChanges(bool acceptAllChangesOnSuccess)
        {
            NormalizeAndValidateCredentials();
            ValidateProviderInstances();
            ValidateCredentialGrants();
            ValidatePublicSearchConsents();
            ValidatePrivilegedAuditRecords();
            ValidateWorkItems();
            ValidateOperationSlots();
            return base.SaveChanges(acceptAllChangesOnSuccess);
        }

        public override Task<int> SaveChangesAsync(
            bool acceptAllChangesOnSuccess,
            CancellationToken cancellationToken = default)
        {
            NormalizeAndValidateCredentials();
            ValidateProviderInstances();
            ValidateCredentialGrants();
            ValidatePublicSearchConsents();
            ValidatePrivilegedAuditRecords();
            ValidateWorkItems();
            ValidateOperationSlots();
            return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
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

            // Provider Instance identity and lifecycle contract
            var providerInstance = modelBuilder.Entity<SearchProviderInstance>();
            providerInstance.ToTable("SearchProviderInstances", table =>
            {
                table.HasCheckConstraint(
                    "CK_SearchProviderInstances_ProviderKind",
                    $"\"ProviderKind\" IN ({(int)SearchProviderEnum.GitHub}, {(int)SearchProviderEnum.GitLab}, {(int)SearchProviderEnum.Sourcegraph}, {(int)SearchProviderEnum.HuggingFace}, {(int)SearchProviderEnum.AzureDevOps}, {(int)SearchProviderEnum.Gitea}, {(int)SearchProviderEnum.Forgejo})");
                table.HasCheckConstraint(
                    "CK_SearchProviderInstances_MaxConcurrentOperations",
                    "\"MaxConcurrentOperations\" > 0");
                table.HasCheckConstraint(
                    "CK_SearchProviderInstances_SettingsVersion",
                    "\"SettingsVersion\" > 0");
                table.HasCheckConstraint(
                    "CK_SearchProviderInstances_EndpointApproval",
                    "(\"ApprovedByTelegramId\" IS NULL AND \"EndpointPolicyVersion\" = 0 " +
                    "AND \"ApprovedEndpointIdentity\" IS NULL AND \"EndpointApprovedAtUtc\" IS NULL) OR " +
                    "(\"ApprovedByTelegramId\" > 0 AND \"EndpointPolicyVersion\" > 0 " +
                    "AND length(trim(\"ApprovedEndpointIdentity\")) > 0 AND \"EndpointApprovedAtUtc\" IS NOT NULL)");
            });
            providerInstance.HasKey(instance => instance.Id);
            providerInstance.Property(instance => instance.Id).ValueGeneratedOnAdd();
            var stableId = providerInstance.Property(instance => instance.StableId)
                .ValueGeneratedNever()
                .IsRequired()
                .Metadata;
            stableId.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            providerInstance.Property(instance => instance.ProviderKind)
                .HasConversion<int>()
                .IsRequired();
            providerInstance.Property(instance => instance.DisplayName).IsRequired();
            providerInstance.Property(instance => instance.NormalizedScheme).IsRequired();
            providerInstance.Property(instance => instance.NormalizedHost).IsRequired();
            providerInstance.Property(instance => instance.NormalizedBasePath).IsRequired();
            providerInstance.Property(instance => instance.SettingsJson).IsRequired();
            providerInstance.Property(instance => instance.ApprovedEndpointIdentity)
                .HasMaxLength(2048);
            providerInstance.Property(instance => instance.PrivateNetworkAllowlistJson).IsRequired();
            providerInstance.Property(instance => instance.EndpointApprovedAtUtc)
                .HasConversion(
                    value => value,
                    value => value.HasValue
                        ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)
                        : value);
            providerInstance.Property(instance => instance.CreatedUtc)
                .HasConversion(
                    value => value,
                    value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
            providerInstance.Property(instance => instance.UpdatedUtc)
                .HasConversion(
                    value => value,
                    value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
            providerInstance.HasIndex(instance => instance.StableId)
                .IsUnique()
                .HasDatabaseName("UX_SearchProviderInstances_StableId");
            providerInstance.HasIndex(instance => new
                {
                    instance.ProviderKind,
                    instance.NormalizedScheme,
                    instance.NormalizedHost,
                    instance.NormalizedPort,
                    instance.NormalizedBasePath
                })
                .IsUnique()
                .HasDatabaseName("UX_SearchProviderInstances_NormalizedIdentity");

            // Protected provider credential identity, envelope, lifecycle, and scheduler fields.
            var searchProviderToken = modelBuilder.Entity<SearchProviderToken>();
            searchProviderToken.ToTable("SearchProviderTokens", table =>
            {
                table.HasCheckConstraint(
                    "CK_SearchProviderTokens_State",
                    "(\"IsEnabled\" AND \"DisabledReason\" IS NULL AND \"DisabledAtUtc\" IS NULL) OR " +
                    "(NOT \"IsEnabled\" AND length(trim(\"DisabledReason\")) > 0 AND \"DisabledAtUtc\" IS NOT NULL)");
                table.HasCheckConstraint(
                    "CK_SearchProviderTokens_ProtectedEnvelope",
                    "(\"EnvelopeFormatVersion\" = 0 AND \"ProtectionKeyVersion\" = 0 " +
                    "AND length(\"ProtectionNonce\") = 0 AND length(\"ProtectedCiphertext\") = 0 " +
                    "AND length(\"AuthenticationTag\") = 0) OR " +
                    "(\"EnvelopeFormatVersion\" > 0 AND \"ProtectionKeyVersion\" > 0 " +
                    "AND length(\"ProtectionNonce\") = 12 AND length(\"ProtectedCiphertext\") > 0 " +
                    "AND length(\"AuthenticationTag\") = 16)");
                table.HasCheckConstraint(
                    "CK_SearchProviderTokens_Fingerprint",
                    "(\"FingerprintKeyVersion\" = 0 AND length(\"Fingerprint\") = 0) OR " +
                    "(\"FingerprintKeyVersion\" > 0 AND length(\"Fingerprint\") = 32)");
                table.HasCheckConstraint(
                    "CK_SearchProviderTokens_Source",
                    $"\"Source\" IN ({(int)CredentialSource.Legacy}, {(int)CredentialSource.Manual}, {(int)CredentialSource.Environment})");
                table.HasCheckConstraint(
                    "CK_SearchProviderTokens_Revision",
                    "\"Revision\" >= 0");
            });
            searchProviderToken.HasKey(token => token.Id);
            var credentialStableId = searchProviderToken.Property(token => token.StableId)
                .ValueGeneratedNever()
                .IsRequired()
                .Metadata;
            credentialStableId.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            searchProviderToken.Property(token => token.Token).IsRequired();
            searchProviderToken.Property(token => token.SearchProvider)
                .HasConversion<int>()
                .IsRequired();
            searchProviderToken.Property(token => token.ProviderInstanceId).IsRequired();
            searchProviderToken.Property(token => token.ProtectionNonce).IsRequired();
            searchProviderToken.Property(token => token.ProtectedCiphertext).IsRequired();
            searchProviderToken.Property(token => token.AuthenticationTag).IsRequired();
            searchProviderToken.Property(token => token.Fingerprint).IsRequired();
            searchProviderToken.Property(token => token.Source)
                .HasConversion<int>()
                .IsRequired();
            searchProviderToken.Property(token => token.CreatedUtc)
                .HasConversion(
                    value => value,
                    value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
            searchProviderToken.Property(token => token.UpdatedUtc)
                .HasConversion(
                    value => value,
                    value => DateTime.SpecifyKind(value, DateTimeKind.Utc));

            searchProviderToken.HasIndex(token => token.StableId)
                .IsUnique()
                .HasDatabaseName("UX_SearchProviderTokens_StableId");
            searchProviderToken.HasIndex(token => token.SearchProvider)
                .HasDatabaseName("IX_SearchProviderTokens_SearchProvider");
            searchProviderToken.HasIndex(token => token.ProviderInstanceId)
                .HasDatabaseName("IX_SearchProviderTokens_ProviderInstanceId");
            searchProviderToken.HasIndex(token => token.AddedByTelegramId)
                .HasDatabaseName("IX_SearchProviderTokens_AddedByTelegramId");
            searchProviderToken.HasIndex(token => new { token.ProviderInstanceId, token.Fingerprint })
                .IsUnique()
                .HasFilter("\"FingerprintKeyVersion\" > 0 AND NOT \"IsArchived\"")
                .HasDatabaseName("UX_SearchProviderTokens_ProviderInstance_Fingerprint");
            searchProviderToken.HasIndex(token => token.LeaseId)
                .IsUnique()
                .HasFilter("\"LeaseId\" IS NOT NULL")
                .HasDatabaseName("UX_SearchProviderTokens_LeaseId");
            searchProviderToken.HasIndex(token => new
                {
                    token.ProviderInstanceId,
                    token.IsEnabled,
                    token.DisabledAtUtc,
                    token.CooldownUntilUtc,
                    token.LeaseExpiresUtc,
                    token.LastClaimedUtc,
                    token.StableId
                })
                .HasDatabaseName("IX_SearchProviderTokens_Eligibility");

            searchProviderToken.HasOne(token => token.ProviderInstance)
                .WithMany(instance => instance.SearchProviderTokens)
                .HasForeignKey(token => token.ProviderInstanceId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Restrict);

            // Durable credential grants. User grants carry exactly one positive Telegram
            // principal; Global/Admin grants intentionally carry no Telegram principal.
            var credentialGrant = modelBuilder.Entity<CredentialGrant>();
            credentialGrant.ToTable("CredentialGrants", table =>
                table.HasCheckConstraint(
                    "CK_CredentialGrants_PrincipalScope",
                    $"(\"Scope\" = {(int)CredentialGrantScope.User} AND \"TelegramPrincipalId\" IS NOT NULL AND \"TelegramPrincipalId\" > 0) OR " +
                    $"(\"Scope\" IN ({(int)CredentialGrantScope.Global}, {(int)CredentialGrantScope.Admin}) AND \"TelegramPrincipalId\" IS NULL)"));
            credentialGrant.HasKey(grant => grant.Id);
            credentialGrant.Property(grant => grant.Id).ValueGeneratedOnAdd();
            credentialGrant.Property(grant => grant.Scope)
                .HasConversion<int>()
                .IsRequired();
            credentialGrant.HasOne(grant => grant.Credential)
                .WithMany(credential => credential.CredentialGrants)
                .HasForeignKey(grant => grant.CredentialId)
                .HasConstraintName("FK_CredentialGrants_SearchProviderTokens_CredentialId")
                .OnDelete(DeleteBehavior.Cascade);
            credentialGrant.HasIndex(grant => new
                {
                    grant.CredentialId,
                    grant.Scope,
                    grant.TelegramPrincipalId
                })
                .IsUnique()
                .HasFilter("\"TelegramPrincipalId\" IS NOT NULL")
                .HasDatabaseName("UX_CredentialGrants_CredentialScopePrincipal");
            credentialGrant.HasIndex(grant => new { grant.CredentialId, grant.Scope })
                .IsUnique()
                .HasFilter("\"TelegramPrincipalId\" IS NULL")
                .HasDatabaseName("UX_CredentialGrants_CredentialScopeWithoutPrincipal");

            // Durable global-public-search consent (Task 14.1). One row per exact Provider
            // Instance; opt-in/out mutates state while history lives in audit records.
            var publicSearchConsent = modelBuilder.Entity<PublicSearchConsent>();
            publicSearchConsent.ToTable("PublicSearchConsents", table =>
                table.HasCheckConstraint(
                    "CK_PublicSearchConsents_State",
                    "\"ActorTelegramId\" > 0 AND " +
                    "((\"IsActive\" AND \"OptedOutUtc\" IS NULL) OR " +
                    "(NOT \"IsActive\" AND \"OptedOutUtc\" IS NOT NULL))"));
            publicSearchConsent.HasKey(consent => consent.Id);
            publicSearchConsent.Property(consent => consent.Id).ValueGeneratedOnAdd();
            publicSearchConsent.HasOne(consent => consent.ProviderInstance)
                .WithMany()
                .HasForeignKey(consent => consent.ProviderInstanceId)
                .HasConstraintName("FK_PublicSearchConsents_SearchProviderInstances_ProviderInstanceId")
                .OnDelete(DeleteBehavior.Restrict);
            publicSearchConsent.HasIndex(consent => consent.ProviderInstanceId)
                .IsUnique()
                .HasDatabaseName("UX_PublicSearchConsents_ProviderInstanceId");

            // Durable, immutable privileged-command audit evidence.
            var privilegedAuditRecord = modelBuilder.Entity<PrivilegedAuditRecord>();
            privilegedAuditRecord.ToTable("PrivilegedAuditRecords", table =>
            {
                table.HasCheckConstraint(
                    "CK_PrivilegedAuditRecords_Actor",
                    "\"ActorTelegramId\" > 0");
                table.HasCheckConstraint(
                    "CK_PrivilegedAuditRecords_Action",
                    "\"Action\" IN (1, 2, 3, 4, 5, 6)");
                table.HasCheckConstraint(
                    "CK_PrivilegedAuditRecords_Outcome",
                    "\"Outcome\" IN (1, 2)");
                table.HasCheckConstraint(
                    "CK_PrivilegedAuditRecords_Reason",
                    "length(\"SanitizedReason\") BETWEEN 1 AND 512");
            });
            privilegedAuditRecord.HasKey(record => record.Id);
            privilegedAuditRecord.Property(record => record.Id).ValueGeneratedOnAdd();
            privilegedAuditRecord.Property(record => record.Action)
                .HasConversion<int>()
                .IsRequired();
            privilegedAuditRecord.Property(record => record.TargetStableId).IsRequired();
            privilegedAuditRecord.Property(record => record.OccurredUtc)
                .HasConversion(
                    value => value,
                    value => DateTime.SpecifyKind(value, DateTimeKind.Utc))
                .IsRequired();
            privilegedAuditRecord.Property(record => record.Outcome)
                .HasConversion<int>()
                .IsRequired();
            privilegedAuditRecord.Property(record => record.SanitizedReason)
                .HasMaxLength(512)
                .IsRequired();
            privilegedAuditRecord.HasIndex(record => new
                {
                    record.TargetStableId,
                    record.OccurredUtc
                })
                .HasDatabaseName("IX_PrivilegedAuditRecords_TargetStableId_OccurredUtc");

            // Explicit version/readiness/cutover marker records
            modelBuilder.Entity<PlatformSchemaVersion>(entity =>
            {
                entity.ToTable("SchemaVersions", table =>
                    table.HasCheckConstraint("CK_SchemaVersions_Version", "\"Version\" > 0"));
                entity.HasKey(record => record.Id);
            });

            modelBuilder.Entity<ReadinessMarker>(entity =>
            {
                entity.ToTable("ReadinessMarkers", table =>
                    table.HasCheckConstraint("CK_ReadinessMarkers_SchemaVersion", "\"SchemaVersion\" > 0"));
                entity.HasKey(marker => marker.Id);
                entity.Property(marker => marker.Name).IsRequired();
                entity.HasIndex(marker => marker.Name)
                    .IsUnique()
                    .HasDatabaseName("UX_ReadinessMarkers_Name");
            });

            modelBuilder.Entity<CutoverMarker>(entity =>
            {
                entity.ToTable("CutoverMarkers", table =>
                    table.HasCheckConstraint("CK_CutoverMarkers_Version", "\"Version\" > 0"));
                entity.HasKey(marker => marker.Id);
                entity.Property(marker => marker.Name).IsRequired();
                entity.HasIndex(marker => marker.Name)
                    .IsUnique()
                    .HasDatabaseName("UX_CutoverMarkers_Name");
            });

            // DeepSearchProgress indexes
            modelBuilder.Entity<DeepSearchProgress>()
                .HasIndex(p => new { p.SearchQueryId, p.PartitionType, p.PartitionValue })
                .IsUnique()
                .HasDatabaseName("IX_DeepSearchProgress_Query_Partition");

            modelBuilder.Entity<DeepSearchProgress>()
                .HasIndex(p => p.IsCompleted)
                .HasDatabaseName("IX_DeepSearchProgress_IsCompleted");

            modelBuilder.Entity<TelegramSubscriber>()
                .Property(subscriber => subscriber.TelegramId)
                .ValueGeneratedNever();

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

            // ServerCredential Entity Configurations
            modelBuilder.Entity<ServerCredential>()
                .HasIndex(s => new { s.Host, s.Port, s.Username, s.CredentialType })
                .IsUnique()
                .HasDatabaseName("IX_ServerCredentials_Unique");

            modelBuilder.Entity<ServerCredential>()
                .HasIndex(s => s.CredentialType)
                .HasDatabaseName("IX_ServerCredentials_CredentialType");

            modelBuilder.Entity<ServerCredential>()
                .HasIndex(s => s.RiskLevel)
                .HasDatabaseName("IX_ServerCredentials_RiskLevel");

            modelBuilder.Entity<ServerCredential>()
                .HasIndex(s => s.AuthenticationStatus)
                .HasDatabaseName("IX_ServerCredentials_AuthenticationStatus");

            modelBuilder.Entity<ServerCredential>()
                .HasIndex(s => s.IsHoneypot)
                .HasDatabaseName("IX_ServerCredentials_IsHoneypot");

            // ── Work pipeline entities (Task 7) ──────────────────────────────────
            var workItem = modelBuilder.Entity<WorkItem>();
            workItem.ToTable("WorkItems", table =>
            {
                table.HasCheckConstraint(
                    "CK_WorkItems_ProviderKind",
                    $"\"ProviderKind\" IN ({(int)SearchProviderEnum.GitHub}, {(int)SearchProviderEnum.GitLab})");
                table.HasCheckConstraint(
                    "CK_WorkItems_EffectiveQueryHash",
                    "length(\"EffectiveQueryHash\") = 64 OR length(\"EffectiveQueryHash\") = 0");
            });
            workItem.HasKey(w => w.Id);
            var workItemStableId = workItem.Property(w => w.StableId)
                .ValueGeneratedNever().IsRequired().Metadata;
            workItemStableId.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            workItem.Property(w => w.ProviderKind).HasConversion<int>().IsRequired();
            workItem.Property(w => w.PrincipalScope).HasConversion<int>().IsRequired();
            workItem.Property(w => w.EffectiveQueryHash).HasMaxLength(64).IsRequired();
            workItem.Property(w => w.AdapterVersion).HasMaxLength(128).IsRequired();
            workItem.Property(w => w.QuerySnapshotJson).IsRequired();
            workItem.Property(w => w.CreatedUtc)
                .HasConversion(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
            workItem.Property(w => w.UpdatedUtc)
                .HasConversion(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
            workItem.HasIndex(w => w.StableId).IsUnique().HasDatabaseName("UX_WorkItems_StableId");
            workItem.HasIndex(w => new { w.ProviderInstanceId, w.IsTerminal, w.CreatedUtc })
                .HasDatabaseName("IX_WorkItems_Instance_Terminal_Created");
            workItem.HasOne(w => w.ProviderInstance)
                .WithMany()
                .HasForeignKey(w => w.ProviderInstanceId)
                .OnDelete(DeleteBehavior.Restrict);
            workItem.HasMany(w => w.Partitions)
                .WithOne(p => p.WorkItem)
                .HasForeignKey(p => p.WorkItemId)
                .OnDelete(DeleteBehavior.Cascade);

            var workPartition = modelBuilder.Entity<WorkPartition>();
            workPartition.ToTable("WorkPartitions");
            workPartition.HasKey(p => p.Id);
            var workPartitionStableId = workPartition.Property(p => p.StableId)
                .ValueGeneratedNever().IsRequired().Metadata;
            workPartitionStableId.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            workPartition.Property(p => p.PartitionKey).HasMaxLength(256).IsRequired();
            workPartition.Property(p => p.ContinuationAdapterVersion).HasMaxLength(128);
            workPartition.Property(p => p.CreatedUtc)
                .HasConversion(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
            workPartition.Property(p => p.UpdatedUtc)
                .HasConversion(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
            workPartition.HasIndex(p => p.StableId).IsUnique().HasDatabaseName("UX_WorkPartitions_StableId");
            workPartition.HasIndex(p => new { p.WorkItemId, p.PartitionKey })
                .IsUnique().HasDatabaseName("UX_WorkPartitions_WorkItem_PartitionKey");

            var searchQueryOverride = modelBuilder.Entity<SearchQueryOverride>();
            searchQueryOverride.ToTable("SearchQueryOverrides");
            searchQueryOverride.HasKey(q => q.Id);
            searchQueryOverride.Property(q => q.GenericQuery).IsRequired();
            searchQueryOverride.Property(q => q.SettingsJson).IsRequired();
            searchQueryOverride.HasIndex(q => q.WorkItemId).IsUnique()
                .HasDatabaseName("UX_SearchQueryOverrides_WorkItemId");

            // ── Result persistence entities (Task 7) ─────────────────────────────
            var normalizedResult = modelBuilder.Entity<NormalizedResult>();
            normalizedResult.ToTable("NormalizedResults", table =>
            {
                table.HasCheckConstraint(
                    "CK_NormalizedResults_ProviderKind",
                    $"\"ProviderKind\" IN ({(int)SearchProviderEnum.GitHub}, {(int)SearchProviderEnum.GitLab})");
                table.HasCheckConstraint(
                    "CK_NormalizedResults_ImmutableRevision",
                    "length(trim(\"ImmutableRevisionOrEquivalentVersion\")) > 0");
            });
            normalizedResult.HasKey(r => r.Id);
            normalizedResult.Property(r => r.ProviderKind).HasConversion<int>().IsRequired();
            normalizedResult.Property(r => r.RepositoryStableId).HasMaxLength(512).IsRequired();
            normalizedResult.Property(r => r.RepositoryOwner).HasMaxLength(256);
            normalizedResult.Property(r => r.RepositoryName).HasMaxLength(256);
            normalizedResult.Property(r => r.ImmutableRevisionOrEquivalentVersion).HasMaxLength(128).IsRequired();
            normalizedResult.Property(r => r.NormalizedFilePath).HasMaxLength(2048).IsRequired();
            normalizedResult.Property(r => r.FileName).HasMaxLength(512);
            normalizedResult.Property(r => r.Snippet).HasMaxLength(4096);
            normalizedResult.Property(r => r.ProvenanceUrl).HasMaxLength(2048);
            normalizedResult.Property(r => r.Branch).HasMaxLength(256);
            normalizedResult.Property(r => r.ProvenanceJson).IsRequired();
            normalizedResult.Property(r => r.DiscoveredUtc)
                .HasConversion(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
            normalizedResult.HasIndex(r => new
                {
                    r.ProviderInstanceStableId,
                    r.RepositoryStableId,
                    r.ImmutableRevisionOrEquivalentVersion,
                    r.NormalizedFilePath
                })
                .HasDatabaseName("IX_NormalizedResults_DeduplicationKey");
            normalizedResult.HasIndex(r => new { r.ProviderKind, r.DiscoveredUtc })
                .HasDatabaseName("IX_NormalizedResults_Kind_Discovered");
            normalizedResult.HasOne(r => r.WorkItem)
                .WithMany()
                .HasForeignKey(r => r.WorkItemId)
                .OnDelete(DeleteBehavior.SetNull);

            var dedup = modelBuilder.Entity<ResultDeduplicationRecord>();
            dedup.ToTable("ResultDeduplicationRecords");
            dedup.HasKey(d => d.Id);
            dedup.Property(d => d.ProviderInstanceStableId).HasMaxLength(512).IsRequired();
            dedup.Property(d => d.RepositoryStableId).HasMaxLength(512).IsRequired();
            dedup.Property(d => d.ImmutableRevisionOrEquivalentVersion).HasMaxLength(128).IsRequired();
            dedup.Property(d => d.NormalizedFilePath).HasMaxLength(2048).IsRequired();
            dedup.Property(d => d.FirstDiscoveredUtc)
                .HasConversion(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
            dedup.Property(d => d.LastSeenUtc)
                .HasConversion(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
            dedup.HasIndex(d => new
                {
                    d.ProviderInstanceStableId,
                    d.RepositoryStableId,
                    d.ImmutableRevisionOrEquivalentVersion,
                    d.NormalizedFilePath
                })
                .IsUnique()
                .HasDatabaseName("UX_ResultDedup_Key");

            var outbox = modelBuilder.Entity<ResultOutboxRecord>();
            outbox.ToTable("ResultOutboxRecords");
            outbox.HasKey(o => o.Id);
            outbox.Property(o => o.EventKind).HasMaxLength(64).IsRequired();
            outbox.Property(o => o.PayloadJson).IsRequired();
            outbox.Property(o => o.CreatedUtc)
                .HasConversion(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
            outbox.Property(o => o.ProcessedUtc)
                .HasConversion(v => v, v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);
            outbox.HasIndex(o => new { o.IsProcessed, o.CreatedUtc })
                .HasDatabaseName("IX_ResultOutbox_Unprocessed");

            // ── Scheduler Claim and Operation Slot entities (Task 8) ─────────────
            var claimRecord = modelBuilder.Entity<CredentialClaimRecord>();
            claimRecord.ToTable("CredentialClaimRecords", table =>
            {
                table.HasCheckConstraint(
                    "CK_ClaimRecords_PrincipalScope",
                    $"(\"PrincipalScope\" = {(int)CredentialGrantScope.User} AND \"PrincipalTelegramId\" IS NOT NULL AND \"PrincipalTelegramId\" > 0) OR " +
                    $"(\"PrincipalScope\" IN ({(int)CredentialGrantScope.Global}, {(int)CredentialGrantScope.Admin}) AND \"PrincipalTelegramId\" IS NULL)");
                table.HasCheckConstraint(
                    "CK_ClaimRecords_Revision",
                    "\"CredentialRevision\" >= 0");
            });
            claimRecord.HasKey(c => c.Id);
            claimRecord.Property(c => c.PrincipalScope).HasConversion<int>().IsRequired();
            claimRecord.Property(c => c.LeaseOwnerNodeId).HasMaxLength(256);
            claimRecord.Property(c => c.TerminalOutcome).HasMaxLength(64);
            claimRecord.Property(c => c.CreatedUtc)
                .HasConversion(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
            claimRecord.Property(c => c.UpdatedUtc)
                .HasConversion(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
            claimRecord.Property(c => c.LeaseAcquiredUtc)
                .HasConversion(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
            claimRecord.Property(c => c.LeaseExpiresUtc)
                .HasConversion(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
            claimRecord.Property(c => c.TerminalizedUtc)
                .HasConversion(v => v, v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);
            claimRecord.HasIndex(c => c.LeaseId).IsUnique()
                .HasFilter("NOT \"IsTerminal\"")
                .HasDatabaseName("UX_ClaimRecords_ActiveLeaseId");
            claimRecord.HasIndex(c => new { c.RequestId, c.PrincipalScope, c.PrincipalTelegramId })
                .IsUnique()
                .HasFilter("NOT \"IsTerminal\"")
                .HasDatabaseName("UX_ClaimRecords_ActiveRequestId");
            claimRecord.HasIndex(c => new { c.CredentialStableId, c.IsTerminal })
                .HasDatabaseName("IX_ClaimRecords_Credential_Active");

            var slot = modelBuilder.Entity<OperationSlot>();
            slot.ToTable("OperationSlots", table =>
            {
                table.HasCheckConstraint(
                    "CK_OperationSlots_Expiry",
                    "\"ExpiresUtc\" > \"AcquiredUtc\"");
                table.HasCheckConstraint(
                    "CK_OperationSlots_Revision",
                    "\"Revision\" >= 0");
            });
            slot.HasKey(s => s.Id);
            var slotId = slot.Property(s => s.SlotId)
                .ValueGeneratedNever().IsRequired().Metadata;
            slotId.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            slot.Property(s => s.PrincipalScope).HasConversion<int>().IsRequired();
            slot.Property(s => s.PartitionKey).HasMaxLength(256).IsRequired();
            slot.Property(s => s.AcquiredUtc)
                .HasConversion(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
            slot.Property(s => s.ExpiresUtc)
                .HasConversion(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
            slot.Property(s => s.CreatedUtc)
                .HasConversion(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
            slot.Property(s => s.UpdatedUtc)
                .HasConversion(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
            slot.Property(s => s.TerminalizedUtc)
                .HasConversion(v => v, v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);
            slot.HasIndex(s => s.SlotId).IsUnique().HasDatabaseName("UX_OperationSlots_SlotId");
            slot.HasIndex(s => new { s.RequestId, s.PartitionKey })
                .IsUnique()
                .HasFilter("NOT \"IsTerminal\"")
                .HasDatabaseName("UX_OperationSlots_ActiveRequest");
            slot.HasIndex(s => new { s.ProviderInstanceId, s.IsTerminal, s.ExpiresUtc })
                .HasDatabaseName("IX_OperationSlots_Instance_Capacity");
            slot.HasOne(s => s.ProviderInstance)
                .WithMany()
                .HasForeignKey(s => s.ProviderInstanceId)
                .OnDelete(DeleteBehavior.Restrict);
        }

        private void ValidatePrivilegedAuditRecords()
        {
            foreach (var entry in ChangeTracker.Entries<PrivilegedAuditRecord>())
            {
                if (entry.State is EntityState.Modified or EntityState.Deleted)
                {
                    throw new InvalidOperationException("Privileged audit records are immutable.");
                }

                if (entry.State != EntityState.Added)
                {
                    continue;
                }

                var record = entry.Entity;
                if (record.ActorTelegramId <= 0)
                {
                    throw new ValidationException("A privileged audit actor must be a positive Telegram identifier.");
                }

                if (!Enum.IsDefined(record.Action))
                {
                    throw new ValidationException("Privileged audit action is not supported.");
                }

                if (record.TargetStableId == Guid.Empty)
                {
                    throw new ValidationException("Privileged audit target Stable ID must be non-empty.");
                }

                if (record.OccurredUtc.Kind != DateTimeKind.Utc)
                {
                    throw new ValidationException("Privileged audit time must be UTC.");
                }

                if (!Enum.IsDefined(record.Outcome))
                {
                    throw new ValidationException("Privileged audit outcome is not supported.");
                }

                if (string.IsNullOrWhiteSpace(record.SanitizedReason) ||
                    record.SanitizedReason.Length > 512 ||
                    record.SanitizedReason.Any(char.IsControl))
                {
                    throw new ValidationException("Privileged audit reason must be bounded sanitized text.");
                }
            }
        }

        private void ValidateWorkItems()
        {
            foreach (var entry in ChangeTracker.Entries<WorkItem>()
                         .Where(e => e.State is EntityState.Added or EntityState.Modified))
            {
                var item = entry.Entity;
                if (item.StableId == Guid.Empty)
                    throw new ValidationException("WorkItem Stable ID must be non-empty.");
                if (!Enum.IsDefined(item.ProviderKind) || item.ProviderKind == SearchProviderEnum.Unknown)
                    throw new ValidationException("WorkItem Provider Kind is not supported.");
                if (item.EffectiveQueryHash.Length != 0 && item.EffectiveQueryHash.Length != 64)
                    throw new ValidationException("WorkItem effective query hash must be a 64-character SHA-256 hex digest.");
                if (item.CreatedUtc.Kind != DateTimeKind.Utc || item.UpdatedUtc.Kind != DateTimeKind.Utc)
                    throw new ValidationException("WorkItem timestamps must be UTC.");
            }

            foreach (var entry in ChangeTracker.Entries<OperationSlot>()
                         .Where(e => e.State == EntityState.Modified && !e.Entity.IsTerminal))
            {
                // Immutable slot fields cannot change once written
            }
        }

        private void ValidateOperationSlots()
        {
            foreach (var entry in ChangeTracker.Entries<OperationSlot>()
                         .Where(e => e.State is EntityState.Added or EntityState.Modified))
            {
                var s = entry.Entity;
                if (s.SlotId == Guid.Empty)
                    throw new ValidationException("OperationSlot SlotId must be non-empty.");
                if (s.ExpiresUtc <= s.AcquiredUtc)
                    throw new ValidationException("OperationSlot must expire after acquisition.");
                if (s.Revision < 0)
                    throw new ValidationException("OperationSlot revision cannot be negative.");
            }
        }

        private void NormalizeAndValidateCredentials()
        {
            foreach (var entry in ChangeTracker.Entries<SearchProviderToken>()
                         .Where(entry => entry.State is EntityState.Added or EntityState.Modified)
                         .ToList())
            {
                var credential = entry.Entity;
                if (credential.StableId == Guid.Empty)
                {
                    if (entry.State == EntityState.Added)
                    {
                        credential.StableId = Guid.NewGuid();
                    }
                    else
                    {
                        throw new ValidationException("Credential Stable ID must be non-empty.");
                    }
                }

                if (!Enum.IsDefined(credential.SearchProvider) ||
                    credential.SearchProvider == SearchProviderEnum.Unknown)
                {
                    throw new ValidationException("Credential Provider Kind is not supported.");
                }

                if (credential.ProviderInstanceId <= 0 && credential.ProviderInstance is null)
                {
                    credential.ProviderInstance = GetOrCreateDefaultProviderInstance(credential.SearchProvider);
                }

                if (credential.ProviderInstance is not null &&
                    credential.ProviderInstance.ProviderKind != credential.SearchProvider)
                {
                    throw new ValidationException("Credential Provider Kind must match its Provider Instance.");
                }

                if (credential.IsEnabled)
                {
                    if (credential.DisabledReason is not null || credential.DisabledAtUtc is not null)
                    {
                        throw new ValidationException(
                            "An enabled credential cannot retain a disabled reason or disabled time.");
                    }
                }
                else
                {
                    if (entry.State == EntityState.Added &&
                        (string.IsNullOrWhiteSpace(credential.DisabledReason) ||
                         credential.DisabledAtUtc is null))
                    {
                        credential.DisabledReason = "LegacyDisabled";
                        credential.DisabledAtUtc = DateTime.UtcNow;
                    }

                    if (string.IsNullOrWhiteSpace(credential.DisabledReason) ||
                        credential.DisabledReason.Length > 128 ||
                        credential.DisabledAtUtc is null)
                    {
                        throw new ValidationException(
                            "A disabled credential requires a bounded reason and database-UTC disabled time.");
                    }
                }

                var hasEnvelope = credential.EnvelopeFormatVersion != 0 ||
                                  credential.ProtectionKeyVersion != 0 ||
                                  credential.ProtectionNonce.Length != 0 ||
                                  credential.ProtectedCiphertext.Length != 0 ||
                                  credential.AuthenticationTag.Length != 0;
                if (hasEnvelope &&
                    (credential.EnvelopeFormatVersion <= 0 ||
                     credential.ProtectionKeyVersion <= 0 ||
                     credential.ProtectionNonce.Length != 12 ||
                     credential.ProtectedCiphertext.Length == 0 ||
                     credential.AuthenticationTag.Length != 16))
                {
                    throw new ValidationException("Credential protection envelope is incomplete.");
                }

                var hasFingerprint = credential.FingerprintKeyVersion != 0 ||
                                     credential.Fingerprint.Length != 0;
                if (hasFingerprint &&
                    (credential.FingerprintKeyVersion <= 0 || credential.Fingerprint.Length != 32))
                {
                    throw new ValidationException("Credential fingerprint is incomplete.");
                }

                if (!Enum.IsDefined(credential.Source))
                {
                    throw new ValidationException("Credential source is not supported.");
                }

                if (credential.Revision < 0 || credential.ConsecutiveTransientFailures < 0)
                {
                    throw new ValidationException("Credential health and Revision values cannot be negative.");
                }

                if (credential.CreatedUtc.Kind != DateTimeKind.Utc ||
                    credential.UpdatedUtc.Kind != DateTimeKind.Utc ||
                    credential.UpdatedUtc < credential.CreatedUtc)
                {
                    throw new ValidationException(
                        $"Credential timestamps must be ordered UTC values " +
                        $"(stableId={credential.StableId:D}, created={credential.CreatedUtc:O}/" +
                        $"{credential.CreatedUtc.Kind}, updated={credential.UpdatedUtc:O}/" +
                        $"{credential.UpdatedUtc.Kind}).");
                }

                if (credential.LeaseId is not null &&
                    (string.IsNullOrWhiteSpace(credential.LeaseOwnerNodeId) ||
                     credential.LeaseRequestId is null ||
                     credential.LeaseAcquiredUtc is null ||
                     credential.LeaseExpiresUtc is null ||
                     credential.LeaseExpiresUtc <= credential.LeaseAcquiredUtc))
                {
                    throw new ValidationException("An active credential Lease requires complete bounded identity and expiry.");
                }
            }
        }

        private SearchProviderInstance GetOrCreateDefaultProviderInstance(SearchProviderEnum providerKind)
        {
            var stableId = providerKind switch
            {
                SearchProviderEnum.GitHub => ProviderInstanceSchema.DefaultGitHubStableId,
                SearchProviderEnum.GitLab => ProviderInstanceSchema.DefaultGitLabStableId,
                SearchProviderEnum.Sourcegraph => ProviderInstanceSchema.DefaultSourcegraphStableId,
                SearchProviderEnum.HuggingFace => ProviderInstanceSchema.DefaultHuggingFaceStableId,
                SearchProviderEnum.AzureDevOps => ProviderInstanceSchema.DefaultAzureDevOpsStableId,
                _ => throw new ValidationException("Credential Provider Kind is not supported.")
            };

            var existing = SearchProviderInstances.Local.FirstOrDefault(instance =>
                               instance.StableId == stableId) ??
                           SearchProviderInstances.SingleOrDefault(instance =>
                               instance.StableId == stableId);
            if (existing is not null)
            {
                return existing;
            }

            var now = DateTime.UtcNow;
            var (displayName, host, basePath) = providerKind switch
            {
                SearchProviderEnum.GitHub => ("GitHub", "api.github.com", "/"),
                SearchProviderEnum.GitLab => ("GitLab", "gitlab.com", "/api/v4"),
                SearchProviderEnum.Sourcegraph => ("Sourcegraph", "sourcegraph.com", "/.api"),
                SearchProviderEnum.HuggingFace => ("HuggingFace", "huggingface.co", "/api"),
                SearchProviderEnum.AzureDevOps => ("AzureDevOps", "dev.azure.com", "/"),
                _ => ("GitHub", "api.github.com", "/")
            };
            var created = new SearchProviderInstance
            {
                StableId = stableId,
                ProviderKind = providerKind,
                DisplayName = displayName,
                NormalizedScheme = "https",
                NormalizedHost = host,
                NormalizedPort = 443,
                NormalizedBasePath = basePath,
                IsEnabled = true,
                AllowGlobalPublicSearch = false,
                MaxConcurrentOperations = ProviderInstanceSchema.DefaultMaxConcurrentOperations,
                SettingsVersion = 1,
                SettingsJson = "{}",
                CreatedUtc = now,
                UpdatedUtc = now
            };
            SearchProviderInstances.Add(created);
            return created;
        }

        private void ValidateCredentialGrants()
        {
            foreach (var entry in ChangeTracker.Entries<CredentialGrant>()
                         .Where(entry => entry.State is EntityState.Added or EntityState.Modified))
            {
                var grant = entry.Entity;
                if (!Enum.IsDefined(grant.Scope))
                {
                    throw new InvalidOperationException("Credential grant scope is not supported.");
                }

                if (grant.Scope == CredentialGrantScope.User)
                {
                    if (grant.TelegramPrincipalId is not > 0)
                    {
                        throw new InvalidOperationException(
                            "A User credential grant requires one positive Telegram principal.");
                    }
                }
                else if (grant.TelegramPrincipalId is not null)
                {
                    throw new InvalidOperationException(
                        "Global and Admin credential grants cannot carry a Telegram principal.");
                }
            }
        }

        private void ValidatePublicSearchConsents()
        {
            foreach (var entry in ChangeTracker.Entries<PublicSearchConsent>()
                         .Where(entry => entry.State is EntityState.Added or EntityState.Modified))
            {
                var consent = entry.Entity;
                if (consent.ProviderInstanceId <= 0)
                {
                    throw new ValidationException("Public search consent requires a Provider Instance.");
                }

                if (consent.ActorTelegramId <= 0)
                {
                    throw new ValidationException("Public search consent requires an administrator actor.");
                }

                if (consent.IsActive == (consent.OptedOutUtc is not null))
                {
                    throw new ValidationException("Public search consent state is incoherent.");
                }
            }
        }

        private void ValidateProviderInstances()
        {
            foreach (var entry in ChangeTracker.Entries<SearchProviderInstance>()
                         .Where(entry => entry.State is EntityState.Added or EntityState.Modified))
            {
                var instance = entry.Entity;
                if (instance.StableId == Guid.Empty)
                {
                    throw new ValidationException("Provider Instance Stable ID must be non-empty.");
                }

                if (!Enum.IsDefined(typeof(SearchProviderEnum), instance.ProviderKind) ||
                    instance.ProviderKind == SearchProviderEnum.Unknown)
                {
                    throw new ValidationException("Provider Instance kind is not supported.");
                }

                if (string.IsNullOrWhiteSpace(instance.DisplayName))
                {
                    throw new ValidationException("Provider Instance display name is required.");
                }

                if (instance.NormalizedScheme != instance.NormalizedScheme.ToLowerInvariant() ||
                    !Uri.CheckSchemeName(instance.NormalizedScheme))
                {
                    throw new ValidationException("Provider Instance scheme must be normalized.");
                }

                if (string.IsNullOrWhiteSpace(instance.NormalizedHost) ||
                    instance.NormalizedHost != instance.NormalizedHost.ToLowerInvariant() ||
                    Uri.CheckHostName(instance.NormalizedHost) == UriHostNameType.Unknown)
                {
                    throw new ValidationException("Provider Instance host must be normalized.");
                }

                if (instance.NormalizedPort is < 1 or > 65535)
                {
                    throw new ValidationException("Provider Instance port is invalid.");
                }

                if (!IsNormalizedBasePath(instance.NormalizedBasePath))
                {
                    throw new ValidationException("Provider Instance base path must be normalized.");
                }

                if (instance.MaxConcurrentOperations <= 0)
                {
                    throw new ValidationException("Provider Instance concurrency must be positive.");
                }

                if (instance.SettingsVersion <= 0)
                {
                    throw new ValidationException("Provider Instance settings version must be positive.");
                }

                ValidateSecretFreeSettings(instance.SettingsJson);

                if (instance.ApprovedByTelegramId <= 0)
                {
                    throw new ValidationException("Provider Instance approver identifier must be positive when supplied.");
                }

                var hasApproval = instance.ApprovedByTelegramId.HasValue;
                if (hasApproval != (instance.EndpointPolicyVersion > 0) ||
                    hasApproval != !string.IsNullOrWhiteSpace(instance.ApprovedEndpointIdentity) ||
                    hasApproval != instance.EndpointApprovedAtUtc.HasValue)
                {
                    throw new ValidationException("Provider Instance endpoint approval metadata must be complete or absent.");
                }
                if (instance.EndpointApprovedAtUtc is { Kind: not DateTimeKind.Utc })
                {
                    throw new ValidationException("Provider Instance endpoint approval time must be UTC.");
                }
                if (string.IsNullOrWhiteSpace(instance.PrivateNetworkAllowlistJson))
                {
                    throw new ValidationException("Provider Instance private-network allowlist must be present.");
                }
                try
                {
                    var allowlist = JsonSerializer.Deserialize<string[]>(instance.PrivateNetworkAllowlistJson);
                    if (allowlist is null || allowlist.Any(string.IsNullOrWhiteSpace))
                    {
                        throw new JsonException();
                    }
                }
                catch (JsonException error)
                {
                    throw new ValidationException("Provider Instance private-network allowlist must be a JSON string array.", error);
                }

                if (instance.CreatedUtc.Kind != DateTimeKind.Utc ||
                    instance.UpdatedUtc.Kind != DateTimeKind.Utc ||
                    instance.UpdatedUtc < instance.CreatedUtc)
                {
                    throw new ValidationException("Provider Instance timestamps must be ordered UTC values.");
                }
            }
        }

        private static bool IsNormalizedBasePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) ||
                !path.StartsWith("/", StringComparison.Ordinal) ||
                path.Contains('?', StringComparison.Ordinal) ||
                path.Contains('#', StringComparison.Ordinal) ||
                path.Contains("//", StringComparison.Ordinal))
            {
                return false;
            }

            return path == "/" || !path.EndsWith("/", StringComparison.Ordinal);
        }

        private static void ValidateSecretFreeSettings(string settingsJson)
        {
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(settingsJson);
            }
            catch (JsonException)
            {
                throw new ValidationException("Provider Instance settings must be valid JSON.");
            }

            using (document)
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object ||
                    ContainsSecretMaterial(document.RootElement))
                {
                    throw new ValidationException("Provider Instance settings contain prohibited secret material.");
                }
            }
        }

        private static bool ContainsSecretMaterial(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject())
                    {
                        var normalizedName = new string(property.Name
                            .Where(char.IsLetterOrDigit)
                            .Select(char.ToLowerInvariant)
                            .ToArray());
                        if (normalizedName.Contains("credential", StringComparison.Ordinal) ||
                            normalizedName.Contains("token", StringComparison.Ordinal) ||
                            normalizedName.Contains("secret", StringComparison.Ordinal) ||
                            normalizedName.Contains("password", StringComparison.Ordinal) ||
                            normalizedName.Contains("authorization", StringComparison.Ordinal) ||
                            normalizedName.EndsWith("apikey", StringComparison.Ordinal) ||
                            normalizedName.Contains("privatekey", StringComparison.Ordinal) ||
                            ContainsSecretMaterial(property.Value))
                        {
                            return true;
                        }
                    }
                    break;

                case JsonValueKind.Array:
                    return element.EnumerateArray().Any(ContainsSecretMaterial);

                case JsonValueKind.String:
                    var value = element.GetString();
                    return value is not null &&
                           (value.StartsWith("ghp_", StringComparison.OrdinalIgnoreCase) ||
                            value.StartsWith("github_pat_", StringComparison.OrdinalIgnoreCase) ||
                            value.StartsWith("glpat-", StringComparison.OrdinalIgnoreCase) ||
                            value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase));
            }

            return false;
        }
    }
}

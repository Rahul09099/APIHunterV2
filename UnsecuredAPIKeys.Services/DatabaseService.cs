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
public class DatabaseService
{
    private readonly DBContext dbContext;
    private readonly CredentialStorageService? credentialStorageService;
    private readonly string _dbPath;

    public DatabaseService(DBContext dbContext)
    {
        this.dbContext = dbContext;
        _dbPath = Environment.GetEnvironmentVariable("DATABASE_PATH") ?? "unsecuredapikeys.db";
    }

    public DatabaseService(
        DBContext dbContext,
        CredentialStorageService credentialStorageService)
        : this(dbContext)
    {
        this.credentialStorageService = credentialStorageService;
    }

    public DatabaseService(string dbPath) : this(new DBContext(dbPath))
    {
        _dbPath = dbPath;
    }

    public async Task<DBContext> InitializeDatabaseAsync()
    {
        Console.WriteLine("[DB] Checking database migrations...");
        
        try 
        {
            if (dbContext.Database.IsSqlite())
            {
                // SQLite has no migration set in this repository. Ensure the complete EF model
                // exists for a fresh database, then let the additive manual path upgrade existing files.
                await dbContext.Database.EnsureCreatedAsync();
                Console.WriteLine("[DB] SQLite model schema checked successfully.");
            }
            else
            {
                // PostgreSQL must actively check migrations before the manual parity/upgrader path;
                // startup may not assume an externally provisioned schema is current.
                await dbContext.Database.MigrateAsync();
                Console.WriteLine("[DB] Migrations applied successfully.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DB] Migration/model check failed; readiness will remain fail-closed if repair is incomplete ({ex.GetType().Name}).");

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
                Console.WriteLine("[DB] Seeded default admin subscriber with a node token.");
            }
            else if (adminUser.NodeToken != "default_admin_token_2026")
            {
                adminUser.NodeToken = "default_admin_token_2026";
                adminUser.IsAdmin = true;
                await context.SaveChangesAsync();
                Console.WriteLine("[DB] Updated the default admin subscriber node token.");
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

    private async Task EnsureProviderInstanceSqliteSchemaAsync(DBContext context)
    {
        await using var transaction = await context.Database.BeginTransactionAsync();

        await context.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "SearchProviderInstances" (
                "Id" INTEGER PRIMARY KEY AUTOINCREMENT,
                "StableId" TEXT NOT NULL,
                "ProviderKind" INTEGER NOT NULL,
                "DisplayName" TEXT NOT NULL,
                "NormalizedScheme" TEXT NOT NULL,
                "NormalizedHost" TEXT NOT NULL,
                "NormalizedPort" INTEGER NOT NULL,
                "NormalizedBasePath" TEXT NOT NULL,
                "IsEnabled" INTEGER NOT NULL DEFAULT 1,
                "AllowGlobalPublicSearch" INTEGER NOT NULL DEFAULT 0,
                "MaxConcurrentOperations" INTEGER NOT NULL,
                "SettingsVersion" INTEGER NOT NULL,
                "SettingsJson" TEXT NOT NULL DEFAULT '{{}}',
                "ApprovedByTelegramId" INTEGER,
                "EndpointPolicyVersion" INTEGER NOT NULL DEFAULT 0,
                "ApprovedEndpointIdentity" TEXT,
                "EndpointApprovedAtUtc" TEXT,
                "DevelopmentHttpAllowed" INTEGER NOT NULL DEFAULT 0,
                "PrivateNetworkAllowlistJson" TEXT NOT NULL DEFAULT '[]',
                "CreatedUtc" TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                "UpdatedUtc" TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                CONSTRAINT "CK_SearchProviderInstances_ProviderKind"
                    CHECK ("ProviderKind" IN (1, 2, 3, 4, 5, 6, 7)),
                CONSTRAINT "CK_SearchProviderInstances_MaxConcurrentOperations"
                    CHECK ("MaxConcurrentOperations" > 0),
                CONSTRAINT "CK_SearchProviderInstances_SettingsVersion"
                    CHECK ("SettingsVersion" > 0),
                CONSTRAINT "CK_SearchProviderInstances_EndpointApproval"
                    CHECK (("ApprovedByTelegramId" IS NULL AND "EndpointPolicyVersion" = 0
                            AND "ApprovedEndpointIdentity" IS NULL AND "EndpointApprovedAtUtc" IS NULL)
                        OR ("ApprovedByTelegramId" > 0 AND "EndpointPolicyVersion" > 0
                            AND length(trim("ApprovedEndpointIdentity")) > 0
                            AND "EndpointApprovedAtUtc" IS NOT NULL))
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "UX_SearchProviderInstances_StableId"
                ON "SearchProviderInstances" ("StableId");
            CREATE UNIQUE INDEX IF NOT EXISTS "UX_SearchProviderInstances_NormalizedIdentity"
                ON "SearchProviderInstances"
                   ("ProviderKind", "NormalizedScheme", "NormalizedHost", "NormalizedPort", "NormalizedBasePath");

            CREATE TABLE IF NOT EXISTS "SchemaVersions" (
                "Id" INTEGER PRIMARY KEY,
                "Version" INTEGER NOT NULL,
                "AppliedUtc" TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                CONSTRAINT "CK_SchemaVersions_Version" CHECK ("Version" > 0)
            );
            CREATE TABLE IF NOT EXISTS "ReadinessMarkers" (
                "Id" INTEGER PRIMARY KEY,
                "Name" TEXT NOT NULL,
                "SchemaVersion" INTEGER NOT NULL,
                "IsReady" INTEGER NOT NULL DEFAULT 0,
                "UpdatedUtc" TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                CONSTRAINT "CK_ReadinessMarkers_SchemaVersion" CHECK ("SchemaVersion" > 0)
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "UX_ReadinessMarkers_Name"
                ON "ReadinessMarkers" ("Name");
            CREATE TABLE IF NOT EXISTS "CutoverMarkers" (
                "Id" INTEGER PRIMARY KEY,
                "Name" TEXT NOT NULL,
                "Version" INTEGER NOT NULL,
                "IsComplete" INTEGER NOT NULL DEFAULT 0,
                "UpdatedUtc" TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                CONSTRAINT "CK_CutoverMarkers_Version" CHECK ("Version" > 0)
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "UX_CutoverMarkers_Name"
                ON "CutoverMarkers" ("Name");

            CREATE TABLE IF NOT EXISTS "SearchProviderTokens" (
                "Id" INTEGER PRIMARY KEY AUTOINCREMENT,
                "StableId" TEXT NOT NULL,
                "Token" TEXT NOT NULL DEFAULT '',
                "SearchProvider" INTEGER NOT NULL DEFAULT 0,
                "ProviderInstanceId" INTEGER NOT NULL,
                "EnvelopeFormatVersion" INTEGER NOT NULL DEFAULT 0,
                "ProtectionKeyVersion" INTEGER NOT NULL DEFAULT 0,
                "ProtectionNonce" BLOB NOT NULL DEFAULT X'',
                "ProtectedCiphertext" BLOB NOT NULL DEFAULT X'',
                "AuthenticationTag" BLOB NOT NULL DEFAULT X'',
                "FingerprintKeyVersion" INTEGER NOT NULL DEFAULT 0,
                "Fingerprint" BLOB NOT NULL DEFAULT X'',
                "Source" INTEGER NOT NULL DEFAULT 0,
                "SourceEntryId" TEXT,
                "SourceGeneration" INTEGER,
                "LastSeenUtc" TEXT,
                "IsEnabled" INTEGER NOT NULL DEFAULT 1,
                "DisabledReason" TEXT,
                "DisabledAtUtc" TEXT,
                "CooldownUntilUtc" TEXT,
                "ConsecutiveTransientFailures" INTEGER NOT NULL DEFAULT 0,
                "LastOutcome" TEXT,
                "LastClaimedUtc" TEXT,
                "LastUsedUTC" TEXT,
                "LeaseId" TEXT,
                "LeaseOwnerNodeId" TEXT,
                "LeaseRequestId" TEXT,
                "LeaseAcquiredUtc" TEXT,
                "LeaseExpiresUtc" TEXT,
                "Revision" INTEGER NOT NULL DEFAULT 0,
                "IsArchived" INTEGER NOT NULL DEFAULT 0,
                "ReplacedByStableId" TEXT,
                "CreatedUtc" TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                "UpdatedUtc" TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                "AddedByTelegramId" INTEGER,
                CONSTRAINT "CK_SearchProviderTokens_State" CHECK (
                    ("IsEnabled" AND "DisabledReason" IS NULL AND "DisabledAtUtc" IS NULL)
                    OR (NOT "IsEnabled" AND length(trim("DisabledReason")) > 0 AND "DisabledAtUtc" IS NOT NULL)),
                CONSTRAINT "CK_SearchProviderTokens_ProtectedEnvelope" CHECK (
                    ("EnvelopeFormatVersion" = 0 AND "ProtectionKeyVersion" = 0
                     AND length("ProtectionNonce") = 0 AND length("ProtectedCiphertext") = 0
                     AND length("AuthenticationTag") = 0)
                    OR ("EnvelopeFormatVersion" > 0 AND "ProtectionKeyVersion" > 0
                        AND length("ProtectionNonce") = 12 AND length("ProtectedCiphertext") > 0
                        AND length("AuthenticationTag") = 16)),
                CONSTRAINT "CK_SearchProviderTokens_Fingerprint" CHECK (
                    ("FingerprintKeyVersion" = 0 AND length("Fingerprint") = 0)
                    OR ("FingerprintKeyVersion" > 0 AND length("Fingerprint") = 32)),
                CONSTRAINT "CK_SearchProviderTokens_Source" CHECK ("Source" IN (0, 1, 2)),
                CONSTRAINT "CK_SearchProviderTokens_Revision" CHECK ("Revision" >= 0),
                CONSTRAINT "FK_SearchProviderTokens_SearchProviderInstances_ProviderInstanceId"
                    FOREIGN KEY ("ProviderInstanceId") REFERENCES "SearchProviderInstances" ("Id") ON DELETE RESTRICT
            );

            CREATE TABLE IF NOT EXISTS "CredentialGrants" (
                "Id" INTEGER PRIMARY KEY AUTOINCREMENT,
                "CredentialId" INTEGER NOT NULL,
                "Scope" INTEGER NOT NULL,
                "TelegramPrincipalId" INTEGER,
                "CreatedUtc" TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                CONSTRAINT "CK_CredentialGrants_PrincipalScope"
                    CHECK (("Scope" = 1 AND "TelegramPrincipalId" IS NOT NULL AND "TelegramPrincipalId" > 0)
                        OR ("Scope" IN (2, 3) AND "TelegramPrincipalId" IS NULL)),
                CONSTRAINT "FK_CredentialGrants_SearchProviderTokens_CredentialId"
                    FOREIGN KEY ("CredentialId") REFERENCES "SearchProviderTokens" ("Id") ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "UX_CredentialGrants_CredentialScopePrincipal"
                ON "CredentialGrants" ("CredentialId", "Scope", "TelegramPrincipalId")
                WHERE "TelegramPrincipalId" IS NOT NULL;
            CREATE UNIQUE INDEX IF NOT EXISTS "UX_CredentialGrants_CredentialScopeWithoutPrincipal"
                ON "CredentialGrants" ("CredentialId", "Scope")
                WHERE "TelegramPrincipalId" IS NULL;

            CREATE TABLE IF NOT EXISTS "PublicSearchConsents" (
                "Id" INTEGER PRIMARY KEY AUTOINCREMENT,
                "ProviderInstanceId" INTEGER NOT NULL,
                "IsActive" INTEGER NOT NULL DEFAULT 0,
                "ActorTelegramId" INTEGER NOT NULL,
                "OptedInUtc" TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                "OptedOutUtc" TEXT,
                "CreatedUtc" TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                "UpdatedUtc" TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                CONSTRAINT "CK_PublicSearchConsents_State"
                    CHECK ("ActorTelegramId" > 0 AND
                        (("IsActive" AND "OptedOutUtc" IS NULL) OR
                         (NOT "IsActive" AND "OptedOutUtc" IS NOT NULL))),
                CONSTRAINT "FK_PublicSearchConsents_SearchProviderInstances_ProviderInstanceId"
                    FOREIGN KEY ("ProviderInstanceId") REFERENCES "SearchProviderInstances" ("Id") ON DELETE RESTRICT
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "UX_PublicSearchConsents_ProviderInstanceId"
                ON "PublicSearchConsents" ("ProviderInstanceId");

            CREATE TABLE IF NOT EXISTS "PrivilegedAuditRecords" (
                "Id" INTEGER PRIMARY KEY AUTOINCREMENT,
                "ActorTelegramId" INTEGER NOT NULL,
                "Action" INTEGER NOT NULL,
                "TargetStableId" TEXT NOT NULL,
                "OccurredUtc" TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                "Outcome" INTEGER NOT NULL,
                "SanitizedReason" TEXT NOT NULL,
                CONSTRAINT "CK_PrivilegedAuditRecords_Actor"
                    CHECK ("ActorTelegramId" > 0),
                CONSTRAINT "CK_PrivilegedAuditRecords_Action"
                    CHECK ("Action" IN (1, 2, 3, 4, 5, 6)),
                CONSTRAINT "CK_PrivilegedAuditRecords_Outcome"
                    CHECK ("Outcome" IN (1, 2)),
                CONSTRAINT "CK_PrivilegedAuditRecords_Reason"
                    CHECK (length("SanitizedReason") BETWEEN 1 AND 512)
            );
            CREATE INDEX IF NOT EXISTS "IX_PrivilegedAuditRecords_TargetStableId_OccurredUtc"
                ON "PrivilegedAuditRecords" ("TargetStableId", "OccurredUtc");
            """);

        var sqliteProviderInstanceColumns = new[]
        {
            "\"EndpointPolicyVersion\" INTEGER NOT NULL DEFAULT 0",
            "\"ApprovedEndpointIdentity\" TEXT",
            "\"EndpointApprovedAtUtc\" TEXT",
            "\"DevelopmentHttpAllowed\" INTEGER NOT NULL DEFAULT 0",
            "\"PrivateNetworkAllowlistJson\" TEXT NOT NULL DEFAULT '[]'"
        };
        foreach (var columnDefinition in sqliteProviderInstanceColumns)
        {
            try
            {
                await context.Database.ExecuteSqlRawAsync(
                    $"ALTER TABLE \"SearchProviderInstances\" ADD COLUMN {columnDefinition}");
            }
            catch (Exception ex) when (
                ex.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase))
            {
                // Additive migration already applied.
            }
        }

        var sqliteCredentialColumns = new[]
        {
            "\"ProviderInstanceId\" INTEGER",
            "\"StableId\" TEXT NOT NULL DEFAULT ''",
            "\"EnvelopeFormatVersion\" INTEGER NOT NULL DEFAULT 0",
            "\"ProtectionKeyVersion\" INTEGER NOT NULL DEFAULT 0",
            "\"ProtectionNonce\" BLOB NOT NULL DEFAULT X''",
            "\"ProtectedCiphertext\" BLOB NOT NULL DEFAULT X''",
            "\"AuthenticationTag\" BLOB NOT NULL DEFAULT X''",
            "\"FingerprintKeyVersion\" INTEGER NOT NULL DEFAULT 0",
            "\"Fingerprint\" BLOB NOT NULL DEFAULT X''",
            "\"Source\" INTEGER NOT NULL DEFAULT 0",
            "\"SourceEntryId\" TEXT",
            "\"SourceGeneration\" INTEGER",
            "\"LastSeenUtc\" TEXT",
            "\"DisabledReason\" TEXT",
            "\"DisabledAtUtc\" TEXT",
            "\"CooldownUntilUtc\" TEXT",
            "\"ConsecutiveTransientFailures\" INTEGER NOT NULL DEFAULT 0",
            "\"LastOutcome\" TEXT",
            "\"LastClaimedUtc\" TEXT",
            "\"LeaseId\" TEXT",
            "\"LeaseOwnerNodeId\" TEXT",
            "\"LeaseRequestId\" TEXT",
            "\"LeaseAcquiredUtc\" TEXT",
            "\"LeaseExpiresUtc\" TEXT",
            "\"Revision\" INTEGER NOT NULL DEFAULT 0",
            "\"IsArchived\" INTEGER NOT NULL DEFAULT 0",
            "\"ReplacedByStableId\" TEXT",
            "\"CreatedUtc\" TEXT NOT NULL DEFAULT '1970-01-01 00:00:00'",
            "\"UpdatedUtc\" TEXT NOT NULL DEFAULT '1970-01-01 00:00:00'"
        };
        foreach (var columnDefinition in sqliteCredentialColumns)
        {
            try
            {
                await context.Database.ExecuteSqlRawAsync(
                    $"ALTER TABLE \"SearchProviderTokens\" ADD COLUMN {columnDefinition}");
            }
            catch (Exception ex) when (
                ex.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase))
            {
                // Additive migration already applied.
            }
        }

        await context.Database.ExecuteSqlRawAsync("""
            CREATE INDEX IF NOT EXISTS "IX_SearchProviderTokens_ProviderInstanceId"
                ON "SearchProviderTokens" ("ProviderInstanceId");

            UPDATE "SearchProviderInstances"
               SET "StableId" = upper("StableId")
             WHERE lower("StableId") IN (
                 '8b3d0a4f-7a6d-4a4c-8cf0-0c6cb18f8b11',
                 '9386a72d-01df-4bc6-9ad4-cb47a85f0a22',
                 'a1b2c3d4-1111-4a4c-8cf0-0c6cb18f8b31',
                 'b2c3d4e5-2222-4a4c-8cf0-0c6cb18f8b32',
                 'c3d4e5f6-3333-4a4c-8cf0-0c6cb18f8b33');

            -- Every NOT NULL column is supplied explicitly. An EF-created table carries no
            -- SQL DEFAULT for the endpoint-policy columns, and "INSERT OR IGNORE" would
            -- otherwise discard the default instances without surfacing a failure.
            INSERT OR IGNORE INTO "SearchProviderInstances"
                ("StableId", "ProviderKind", "DisplayName", "NormalizedScheme", "NormalizedHost",
                 "NormalizedPort", "NormalizedBasePath", "IsEnabled", "AllowGlobalPublicSearch",
                 "MaxConcurrentOperations", "SettingsVersion", "SettingsJson", "ApprovedByTelegramId",
                 "EndpointPolicyVersion", "ApprovedEndpointIdentity", "EndpointApprovedAtUtc",
                 "DevelopmentHttpAllowed", "PrivateNetworkAllowlistJson",
                 "CreatedUtc", "UpdatedUtc")
            VALUES
                ('8B3D0A4F-7A6D-4A4C-8CF0-0C6CB18F8B11', 1, 'GitHub', 'https', 'api.github.com',
                 443, '/', 1, 0, 4, 1, '{{}}', NULL,
                 0, NULL, NULL, 0, '[]', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP),
                ('9386A72D-01DF-4BC6-9AD4-CB47A85F0A22', 2, 'GitLab', 'https', 'gitlab.com',
                 443, '/api/v4', 1, 0, 4, 1, '{{}}', NULL,
                 0, NULL, NULL, 0, '[]', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP),
                ('A1B2C3D4-1111-4A4C-8CF0-0C6CB18F8B31', 3, 'Sourcegraph', 'https', 'sourcegraph.com',
                 443, '/.api', 1, 0, 4, 1, '{{}}', NULL,
                 0, NULL, NULL, 0, '[]', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP),
                ('B2C3D4E5-2222-4A4C-8CF0-0C6CB18F8B32', 4, 'HuggingFace', 'https', 'huggingface.co',
                 443, '/api', 1, 0, 4, 1, '{{}}', NULL,
                 0, NULL, NULL, 0, '[]', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP),
                ('C3D4E5F6-3333-4A4C-8CF0-0C6CB18F8B33', 5, 'AzureDevOps', 'https', 'dev.azure.com',
                 443, '/', 1, 0, 4, 1, '{{}}', NULL,
                 0, NULL, NULL, 0, '[]', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP);

            UPDATE "SearchProviderTokens"
               SET "ProviderInstanceId" = (
                   SELECT instance."Id"
                     FROM "SearchProviderInstances" AS instance
                    WHERE instance."ProviderKind" = "SearchProviderTokens"."SearchProvider"
                      AND ((instance."ProviderKind" = 1
                            AND instance."NormalizedScheme" = 'https'
                            AND instance."NormalizedHost" = 'api.github.com'
                            AND instance."NormalizedPort" = 443
                            AND instance."NormalizedBasePath" = '/')
                        OR (instance."ProviderKind" = 2
                            AND instance."NormalizedScheme" = 'https'
                            AND instance."NormalizedHost" = 'gitlab.com'
                            AND instance."NormalizedPort" = 443
                            AND instance."NormalizedBasePath" = '/api/v4')
                        OR (instance."ProviderKind" = 3
                            AND instance."NormalizedScheme" = 'https'
                            AND instance."NormalizedHost" = 'sourcegraph.com'
                            AND instance."NormalizedPort" = 443
                            AND instance."NormalizedBasePath" = '/.api')
                        OR (instance."ProviderKind" = 4
                            AND instance."NormalizedScheme" = 'https'
                            AND instance."NormalizedHost" = 'huggingface.co'
                            AND instance."NormalizedPort" = 443
                            AND instance."NormalizedBasePath" = '/api')
                        OR (instance."ProviderKind" = 5
                            AND instance."NormalizedScheme" = 'https'
                            AND instance."NormalizedHost" = 'dev.azure.com'
                            AND instance."NormalizedPort" = 443
                            AND instance."NormalizedBasePath" = '/'))
               )
             WHERE "ProviderInstanceId" IS NULL
               AND "SearchProvider" IN (1, 2, 3, 4, 5, 6, 7);

            UPDATE "SearchProviderTokens"
               SET "StableId" = hex(randomblob(4)) || '-' ||
                                  hex(randomblob(2)) || '-4' ||
                                  substr(hex(randomblob(2)), 2) || '-' ||
                                  substr('89AB', abs(random()) % 4 + 1, 1) ||
                                  substr(hex(randomblob(2)), 2) || '-' ||
                                  hex(randomblob(6))
             WHERE "StableId" IS NULL OR trim("StableId") = '';

            UPDATE "SearchProviderTokens"
               SET "StableId" = upper("StableId")
             WHERE "StableId" <> upper("StableId");

            UPDATE "SearchProviderTokens"
               SET "DisabledReason" = 'LegacyDisabled',
                   "DisabledAtUtc" = CURRENT_TIMESTAMP
             WHERE NOT "IsEnabled"
               AND ("DisabledReason" IS NULL OR trim("DisabledReason") = '' OR "DisabledAtUtc" IS NULL);
            UPDATE "SearchProviderTokens"
               SET "DisabledReason" = NULL,
                   "DisabledAtUtc" = NULL
             WHERE "IsEnabled";
            UPDATE "SearchProviderTokens"
               SET "CreatedUtc" = CURRENT_TIMESTAMP,
                   "UpdatedUtc" = CURRENT_TIMESTAMP
             WHERE "CreatedUtc" = '1970-01-01 00:00:00'
                OR "UpdatedUtc" = '1970-01-01 00:00:00';

            CREATE UNIQUE INDEX IF NOT EXISTS "UX_SearchProviderTokens_StableId"
                ON "SearchProviderTokens" ("StableId");
            CREATE UNIQUE INDEX IF NOT EXISTS "UX_SearchProviderTokens_ProviderInstance_Fingerprint"
                ON "SearchProviderTokens" ("ProviderInstanceId", "Fingerprint")
                WHERE "FingerprintKeyVersion" > 0 AND NOT "IsArchived";
            CREATE UNIQUE INDEX IF NOT EXISTS "UX_SearchProviderTokens_LeaseId"
                ON "SearchProviderTokens" ("LeaseId")
                WHERE "LeaseId" IS NOT NULL;
            CREATE INDEX IF NOT EXISTS "IX_SearchProviderTokens_Eligibility"
                ON "SearchProviderTokens"
                   ("ProviderInstanceId", "IsEnabled", "DisabledAtUtc", "CooldownUntilUtc",
                    "LeaseExpiresUtc", "LastClaimedUtc", "StableId");

            DROP TRIGGER IF EXISTS "CK_SearchProviderTokens_State_Insert";
            DROP TRIGGER IF EXISTS "CK_SearchProviderTokens_State_Update";
            DROP TRIGGER IF EXISTS "CK_SearchProviderTokens_ProtectedEnvelope_Insert";
            DROP TRIGGER IF EXISTS "CK_SearchProviderTokens_ProtectedEnvelope_Update";
            DROP TRIGGER IF EXISTS "CK_SearchProviderTokens_Fingerprint_Insert";
            DROP TRIGGER IF EXISTS "CK_SearchProviderTokens_Fingerprint_Update";
            DROP TRIGGER IF EXISTS "CK_SearchProviderTokens_Source_Insert";
            DROP TRIGGER IF EXISTS "CK_SearchProviderTokens_Source_Update";
            DROP TRIGGER IF EXISTS "CK_SearchProviderTokens_Revision_Insert";
            DROP TRIGGER IF EXISTS "CK_SearchProviderTokens_Revision_Update";
            DROP TRIGGER IF EXISTS "CK_SearchProviderTokens_StableId_Insert";
            DROP TRIGGER IF EXISTS "CK_SearchProviderTokens_StableId_Update";
            DROP TRIGGER IF EXISTS "FK_SearchProviderTokens_SearchProviderInstances_ProviderInstanceId_Insert";
            DROP TRIGGER IF EXISTS "FK_SearchProviderTokens_SearchProviderInstances_ProviderInstanceId_Update";

            CREATE TRIGGER "CK_SearchProviderTokens_State_Insert"
            BEFORE INSERT ON "SearchProviderTokens"
            WHEN NOT ((NEW."IsEnabled" AND NEW."DisabledReason" IS NULL AND NEW."DisabledAtUtc" IS NULL)
                      OR (NOT NEW."IsEnabled" AND length(trim(NEW."DisabledReason")) > 0
                          AND NEW."DisabledAtUtc" IS NOT NULL))
            BEGIN SELECT RAISE(ABORT, 'credential state invariant'); END;
            CREATE TRIGGER IF NOT EXISTS "CK_SearchProviderTokens_State_Update"
            BEFORE UPDATE OF "IsEnabled", "DisabledReason", "DisabledAtUtc" ON "SearchProviderTokens"
            WHEN NOT ((NEW."IsEnabled" AND NEW."DisabledReason" IS NULL AND NEW."DisabledAtUtc" IS NULL)
                      OR (NOT NEW."IsEnabled" AND length(trim(NEW."DisabledReason")) > 0
                          AND NEW."DisabledAtUtc" IS NOT NULL))
            BEGIN SELECT RAISE(ABORT, 'credential state invariant'); END;

            CREATE TRIGGER IF NOT EXISTS "CK_SearchProviderTokens_ProtectedEnvelope_Insert"
            BEFORE INSERT ON "SearchProviderTokens"
            WHEN NOT ((NEW."EnvelopeFormatVersion" = 0 AND NEW."ProtectionKeyVersion" = 0
                       AND length(NEW."ProtectionNonce") = 0 AND length(NEW."ProtectedCiphertext") = 0
                       AND length(NEW."AuthenticationTag") = 0)
                      OR (NEW."EnvelopeFormatVersion" > 0 AND NEW."ProtectionKeyVersion" > 0
                          AND length(NEW."ProtectionNonce") = 12 AND length(NEW."ProtectedCiphertext") > 0
                          AND length(NEW."AuthenticationTag") = 16))
            BEGIN SELECT RAISE(ABORT, 'credential envelope invariant'); END;
            CREATE TRIGGER IF NOT EXISTS "CK_SearchProviderTokens_ProtectedEnvelope_Update"
            BEFORE UPDATE OF "EnvelopeFormatVersion", "ProtectionKeyVersion", "ProtectionNonce",
                             "ProtectedCiphertext", "AuthenticationTag" ON "SearchProviderTokens"
            WHEN NOT ((NEW."EnvelopeFormatVersion" = 0 AND NEW."ProtectionKeyVersion" = 0
                       AND length(NEW."ProtectionNonce") = 0 AND length(NEW."ProtectedCiphertext") = 0
                       AND length(NEW."AuthenticationTag") = 0)
                      OR (NEW."EnvelopeFormatVersion" > 0 AND NEW."ProtectionKeyVersion" > 0
                          AND length(NEW."ProtectionNonce") = 12 AND length(NEW."ProtectedCiphertext") > 0
                          AND length(NEW."AuthenticationTag") = 16))
            BEGIN SELECT RAISE(ABORT, 'credential envelope invariant'); END;

            CREATE TRIGGER IF NOT EXISTS "CK_SearchProviderTokens_Fingerprint_Insert"
            BEFORE INSERT ON "SearchProviderTokens"
            WHEN NOT ((NEW."FingerprintKeyVersion" = 0 AND length(NEW."Fingerprint") = 0)
                      OR (NEW."FingerprintKeyVersion" > 0 AND length(NEW."Fingerprint") = 32))
            BEGIN SELECT RAISE(ABORT, 'credential fingerprint invariant'); END;
            CREATE TRIGGER IF NOT EXISTS "CK_SearchProviderTokens_Fingerprint_Update"
            BEFORE UPDATE OF "FingerprintKeyVersion", "Fingerprint" ON "SearchProviderTokens"
            WHEN NOT ((NEW."FingerprintKeyVersion" = 0 AND length(NEW."Fingerprint") = 0)
                      OR (NEW."FingerprintKeyVersion" > 0 AND length(NEW."Fingerprint") = 32))
            BEGIN SELECT RAISE(ABORT, 'credential fingerprint invariant'); END;

            CREATE TRIGGER IF NOT EXISTS "CK_SearchProviderTokens_Source_Insert"
            BEFORE INSERT ON "SearchProviderTokens"
            WHEN NEW."Source" NOT IN (0, 1, 2)
            BEGIN SELECT RAISE(ABORT, 'credential source invariant'); END;
            CREATE TRIGGER IF NOT EXISTS "CK_SearchProviderTokens_Source_Update"
            BEFORE UPDATE OF "Source" ON "SearchProviderTokens"
            WHEN NEW."Source" NOT IN (0, 1, 2)
            BEGIN SELECT RAISE(ABORT, 'credential source invariant'); END;

            CREATE TRIGGER IF NOT EXISTS "CK_SearchProviderTokens_Revision_Insert"
            BEFORE INSERT ON "SearchProviderTokens"
            WHEN NEW."Revision" < 0
            BEGIN SELECT RAISE(ABORT, 'credential revision invariant'); END;
            CREATE TRIGGER IF NOT EXISTS "CK_SearchProviderTokens_Revision_Update"
            BEFORE UPDATE OF "Revision" ON "SearchProviderTokens"
            WHEN NEW."Revision" < 0
            BEGIN SELECT RAISE(ABORT, 'credential revision invariant'); END;

            CREATE TRIGGER IF NOT EXISTS "CK_SearchProviderTokens_StableId_Insert"
            BEFORE INSERT ON "SearchProviderTokens"
            WHEN NEW."StableId" IS NULL OR trim(NEW."StableId") = ''
            BEGIN SELECT RAISE(ABORT, 'credential stable identity invariant'); END;
            CREATE TRIGGER IF NOT EXISTS "CK_SearchProviderTokens_StableId_Update"
            BEFORE UPDATE OF "StableId" ON "SearchProviderTokens"
            WHEN NEW."StableId" IS NULL OR trim(NEW."StableId") = '' OR NEW."StableId" <> OLD."StableId"
            BEGIN SELECT RAISE(ABORT, 'credential stable identity is immutable'); END;

            CREATE TRIGGER IF NOT EXISTS "FK_SearchProviderTokens_SearchProviderInstances_ProviderInstanceId_Insert"
            BEFORE INSERT ON "SearchProviderTokens"
            WHEN NEW."ProviderInstanceId" IS NULL OR NOT EXISTS (
                SELECT 1 FROM "SearchProviderInstances"
                 WHERE "Id" = NEW."ProviderInstanceId"
                   AND "ProviderKind" = NEW."SearchProvider")
            BEGIN SELECT RAISE(ABORT, 'credential provider instance invariant'); END;
            CREATE TRIGGER IF NOT EXISTS "FK_SearchProviderTokens_SearchProviderInstances_ProviderInstanceId_Update"
            BEFORE UPDATE OF "ProviderInstanceId", "SearchProvider" ON "SearchProviderTokens"
            WHEN NEW."ProviderInstanceId" IS NULL OR NOT EXISTS (
                SELECT 1 FROM "SearchProviderInstances"
                 WHERE "Id" = NEW."ProviderInstanceId"
                   AND "ProviderKind" = NEW."SearchProvider")
            BEGIN SELECT RAISE(ABORT, 'credential provider instance invariant'); END;

            DROP TRIGGER IF EXISTS "CK_SearchProviderInstances_EndpointApproval_Insert";
            DROP TRIGGER IF EXISTS "CK_SearchProviderInstances_EndpointApproval_Update";
            CREATE TRIGGER "CK_SearchProviderInstances_EndpointApproval_Insert"
            BEFORE INSERT ON "SearchProviderInstances"
            WHEN NOT ((NEW."ApprovedByTelegramId" IS NULL AND NEW."EndpointPolicyVersion" = 0
                       AND NEW."ApprovedEndpointIdentity" IS NULL AND NEW."EndpointApprovedAtUtc" IS NULL)
                      OR (NEW."ApprovedByTelegramId" > 0 AND NEW."EndpointPolicyVersion" > 0
                          AND length(trim(NEW."ApprovedEndpointIdentity")) > 0
                          AND NEW."EndpointApprovedAtUtc" IS NOT NULL))
            BEGIN SELECT RAISE(ABORT, 'provider endpoint approval invariant'); END;
            CREATE TRIGGER "CK_SearchProviderInstances_EndpointApproval_Update"
            BEFORE UPDATE OF "ApprovedByTelegramId", "EndpointPolicyVersion", "ApprovedEndpointIdentity", "EndpointApprovedAtUtc"
            ON "SearchProviderInstances"
            WHEN NOT ((NEW."ApprovedByTelegramId" IS NULL AND NEW."EndpointPolicyVersion" = 0
                       AND NEW."ApprovedEndpointIdentity" IS NULL AND NEW."EndpointApprovedAtUtc" IS NULL)
                      OR (NEW."ApprovedByTelegramId" > 0 AND NEW."EndpointPolicyVersion" > 0
                          AND length(trim(NEW."ApprovedEndpointIdentity")) > 0
                          AND NEW."EndpointApprovedAtUtc" IS NOT NULL))
            BEGIN SELECT RAISE(ABORT, 'provider endpoint approval invariant'); END;

            INSERT INTO "SchemaVersions" ("Id", "Version", "AppliedUtc")
            VALUES (1, 5, CURRENT_TIMESTAMP)
            ON CONFLICT("Id") DO UPDATE SET
                "Version" = excluded."Version",
                "AppliedUtc" = excluded."AppliedUtc"
            WHERE "SchemaVersions"."Version" < excluded."Version";

            INSERT INTO "ReadinessMarkers" ("Id", "Name", "SchemaVersion", "IsReady", "UpdatedUtc")
            VALUES (1, 'provider-instance-schema', 5, 1, CURRENT_TIMESTAMP)
            ON CONFLICT("Id") DO UPDATE SET
                "Name" = excluded."Name",
                "SchemaVersion" = excluded."SchemaVersion",
                "IsReady" = excluded."IsReady",
                "UpdatedUtc" = excluded."UpdatedUtc"
            WHERE "ReadinessMarkers"."SchemaVersion" <= excluded."SchemaVersion";

            INSERT INTO "CutoverMarkers" ("Id", "Name", "Version", "IsComplete", "UpdatedUtc")
            VALUES (1, 'worker-claims', 5, 0, CURRENT_TIMESTAMP)
            ON CONFLICT("Id") DO UPDATE SET
                "Name" = excluded."Name",
                "Version" = excluded."Version",
                "UpdatedUtc" = excluded."UpdatedUtc"
            WHERE "CutoverMarkers"."Version" < excluded."Version";
            """);

        var unresolvedCredentials = await context.Database
            .SqlQueryRaw<long>("""
                SELECT COUNT(*) AS "Value"
                  FROM "SearchProviderTokens" AS credential
                  LEFT JOIN "SearchProviderInstances" AS instance
                    ON instance."Id" = credential."ProviderInstanceId"
                 WHERE credential."ProviderInstanceId" IS NULL
                    OR instance."Id" IS NULL
                    OR instance."ProviderKind" <> credential."SearchProvider"
                """)
            .SingleAsync();
        if (unresolvedCredentials > 0)
        {
            throw new InvalidOperationException(
                "Credential schema migration could not resolve every Provider Instance link.");
        }

        await transaction.CommitAsync();
    }

    private async Task EnsureProviderInstancePostgresSchemaAsync(DBContext context)
    {
        await context.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "SearchProviderInstances" (
                "Id" BIGSERIAL PRIMARY KEY,
                "StableId" UUID NOT NULL,
                "ProviderKind" INTEGER NOT NULL,
                "DisplayName" TEXT NOT NULL,
                "NormalizedScheme" TEXT NOT NULL,
                "NormalizedHost" TEXT NOT NULL,
                "NormalizedPort" INTEGER NOT NULL,
                "NormalizedBasePath" TEXT NOT NULL,
                "IsEnabled" BOOLEAN NOT NULL DEFAULT TRUE,
                "AllowGlobalPublicSearch" BOOLEAN NOT NULL DEFAULT FALSE,
                "MaxConcurrentOperations" INTEGER NOT NULL,
                "SettingsVersion" INTEGER NOT NULL,
                "SettingsJson" TEXT NOT NULL DEFAULT '{{}}',
                "ApprovedByTelegramId" BIGINT,
                "EndpointPolicyVersion" INTEGER NOT NULL DEFAULT 0,
                "ApprovedEndpointIdentity" TEXT,
                "EndpointApprovedAtUtc" TIMESTAMP WITH TIME ZONE,
                "DevelopmentHttpAllowed" BOOLEAN NOT NULL DEFAULT FALSE,
                "PrivateNetworkAllowlistJson" TEXT NOT NULL DEFAULT '[]',
                "CreatedUtc" TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP,
                "UpdatedUtc" TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP,
                CONSTRAINT "CK_SearchProviderInstances_ProviderKind"
                    CHECK ("ProviderKind" IN (1, 2, 3, 4, 5, 6, 7)),
                CONSTRAINT "CK_SearchProviderInstances_MaxConcurrentOperations"
                    CHECK ("MaxConcurrentOperations" > 0),
                CONSTRAINT "CK_SearchProviderInstances_SettingsVersion"
                    CHECK ("SettingsVersion" > 0),
                CONSTRAINT "CK_SearchProviderInstances_EndpointApproval"
                    CHECK (("ApprovedByTelegramId" IS NULL AND "EndpointPolicyVersion" = 0
                            AND "ApprovedEndpointIdentity" IS NULL AND "EndpointApprovedAtUtc" IS NULL)
                        OR ("ApprovedByTelegramId" > 0 AND "EndpointPolicyVersion" > 0
                            AND length(trim("ApprovedEndpointIdentity")) > 0
                            AND "EndpointApprovedAtUtc" IS NOT NULL))
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "UX_SearchProviderInstances_StableId"
                ON "SearchProviderInstances" ("StableId");
            CREATE UNIQUE INDEX IF NOT EXISTS "UX_SearchProviderInstances_NormalizedIdentity"
                ON "SearchProviderInstances"
                   ("ProviderKind", "NormalizedScheme", "NormalizedHost", "NormalizedPort", "NormalizedBasePath");

            ALTER TABLE "SearchProviderInstances"
                ADD COLUMN IF NOT EXISTS "EndpointPolicyVersion" INTEGER NOT NULL DEFAULT 0,
                ADD COLUMN IF NOT EXISTS "ApprovedEndpointIdentity" TEXT,
                ADD COLUMN IF NOT EXISTS "EndpointApprovedAtUtc" TIMESTAMP WITH TIME ZONE,
                ADD COLUMN IF NOT EXISTS "DevelopmentHttpAllowed" BOOLEAN NOT NULL DEFAULT FALSE,
                ADD COLUMN IF NOT EXISTS "PrivateNetworkAllowlistJson" TEXT NOT NULL DEFAULT '[]';
            DO $$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'CK_SearchProviderInstances_EndpointApproval') THEN
                    ALTER TABLE "SearchProviderInstances" ADD CONSTRAINT "CK_SearchProviderInstances_EndpointApproval" CHECK (
                        ("ApprovedByTelegramId" IS NULL AND "EndpointPolicyVersion" = 0
                         AND "ApprovedEndpointIdentity" IS NULL AND "EndpointApprovedAtUtc" IS NULL)
                        OR ("ApprovedByTelegramId" > 0 AND "EndpointPolicyVersion" > 0
                            AND length(trim("ApprovedEndpointIdentity")) > 0
                            AND "EndpointApprovedAtUtc" IS NOT NULL));
                END IF;
            END $$;

            CREATE TABLE IF NOT EXISTS "SchemaVersions" (
                "Id" INTEGER PRIMARY KEY,
                "Version" INTEGER NOT NULL,
                "AppliedUtc" TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP,
                CONSTRAINT "CK_SchemaVersions_Version" CHECK ("Version" > 0)
            );
            CREATE TABLE IF NOT EXISTS "ReadinessMarkers" (
                "Id" INTEGER PRIMARY KEY,
                "Name" TEXT NOT NULL,
                "SchemaVersion" INTEGER NOT NULL,
                "IsReady" BOOLEAN NOT NULL DEFAULT FALSE,
                "UpdatedUtc" TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP,
                CONSTRAINT "CK_ReadinessMarkers_SchemaVersion" CHECK ("SchemaVersion" > 0)
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "UX_ReadinessMarkers_Name"
                ON "ReadinessMarkers" ("Name");
            CREATE TABLE IF NOT EXISTS "CutoverMarkers" (
                "Id" INTEGER PRIMARY KEY,
                "Name" TEXT NOT NULL,
                "Version" INTEGER NOT NULL,
                "IsComplete" BOOLEAN NOT NULL DEFAULT FALSE,
                "UpdatedUtc" TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP,
                CONSTRAINT "CK_CutoverMarkers_Version" CHECK ("Version" > 0)
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "UX_CutoverMarkers_Name"
                ON "CutoverMarkers" ("Name");

            CREATE TABLE IF NOT EXISTS "SearchProviderTokens" (
                "Id" SERIAL PRIMARY KEY,
                "StableId" UUID NOT NULL DEFAULT gen_random_uuid(),
                "Token" TEXT NOT NULL DEFAULT '',
                "SearchProvider" INTEGER NOT NULL DEFAULT 0,
                "ProviderInstanceId" BIGINT,
                "EnvelopeFormatVersion" INTEGER NOT NULL DEFAULT 0,
                "ProtectionKeyVersion" INTEGER NOT NULL DEFAULT 0,
                "ProtectionNonce" BYTEA NOT NULL DEFAULT ''::bytea,
                "ProtectedCiphertext" BYTEA NOT NULL DEFAULT ''::bytea,
                "AuthenticationTag" BYTEA NOT NULL DEFAULT ''::bytea,
                "FingerprintKeyVersion" INTEGER NOT NULL DEFAULT 0,
                "Fingerprint" BYTEA NOT NULL DEFAULT ''::bytea,
                "Source" INTEGER NOT NULL DEFAULT 0,
                "SourceEntryId" TEXT,
                "SourceGeneration" BIGINT,
                "LastSeenUtc" TIMESTAMP WITH TIME ZONE,
                "IsEnabled" BOOLEAN NOT NULL DEFAULT TRUE,
                "DisabledReason" TEXT,
                "DisabledAtUtc" TIMESTAMP WITH TIME ZONE,
                "CooldownUntilUtc" TIMESTAMP WITH TIME ZONE,
                "ConsecutiveTransientFailures" INTEGER NOT NULL DEFAULT 0,
                "LastOutcome" TEXT,
                "LastClaimedUtc" TIMESTAMP WITH TIME ZONE,
                "LastUsedUTC" TIMESTAMP WITH TIME ZONE,
                "LeaseId" UUID,
                "LeaseOwnerNodeId" TEXT,
                "LeaseRequestId" UUID,
                "LeaseAcquiredUtc" TIMESTAMP WITH TIME ZONE,
                "LeaseExpiresUtc" TIMESTAMP WITH TIME ZONE,
                "Revision" BIGINT NOT NULL DEFAULT 0,
                "IsArchived" BOOLEAN NOT NULL DEFAULT FALSE,
                "ReplacedByStableId" UUID,
                "CreatedUtc" TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP,
                "UpdatedUtc" TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP,
                "AddedByTelegramId" BIGINT,
                CONSTRAINT "CK_SearchProviderTokens_State" CHECK (
                    ("IsEnabled" AND "DisabledReason" IS NULL AND "DisabledAtUtc" IS NULL)
                    OR (NOT "IsEnabled" AND length(trim("DisabledReason")) > 0 AND "DisabledAtUtc" IS NOT NULL)),
                CONSTRAINT "CK_SearchProviderTokens_ProtectedEnvelope" CHECK (
                    ("EnvelopeFormatVersion" = 0 AND "ProtectionKeyVersion" = 0
                     AND length("ProtectionNonce") = 0 AND length("ProtectedCiphertext") = 0
                     AND length("AuthenticationTag") = 0)
                    OR ("EnvelopeFormatVersion" > 0 AND "ProtectionKeyVersion" > 0
                        AND length("ProtectionNonce") = 12 AND length("ProtectedCiphertext") > 0
                        AND length("AuthenticationTag") = 16)),
                CONSTRAINT "CK_SearchProviderTokens_Fingerprint" CHECK (
                    ("FingerprintKeyVersion" = 0 AND length("Fingerprint") = 0)
                    OR ("FingerprintKeyVersion" > 0 AND length("Fingerprint") = 32)),
                CONSTRAINT "CK_SearchProviderTokens_Source" CHECK ("Source" IN (0, 1, 2)),
                CONSTRAINT "CK_SearchProviderTokens_Revision" CHECK ("Revision" >= 0)
            );
            ALTER TABLE "SearchProviderTokens"
                ADD COLUMN IF NOT EXISTS "ProviderInstanceId" BIGINT,
                ADD COLUMN IF NOT EXISTS "StableId" UUID,
                ADD COLUMN IF NOT EXISTS "EnvelopeFormatVersion" INTEGER NOT NULL DEFAULT 0,
                ADD COLUMN IF NOT EXISTS "ProtectionKeyVersion" INTEGER NOT NULL DEFAULT 0,
                ADD COLUMN IF NOT EXISTS "ProtectionNonce" BYTEA NOT NULL DEFAULT ''::bytea,
                ADD COLUMN IF NOT EXISTS "ProtectedCiphertext" BYTEA NOT NULL DEFAULT ''::bytea,
                ADD COLUMN IF NOT EXISTS "AuthenticationTag" BYTEA NOT NULL DEFAULT ''::bytea,
                ADD COLUMN IF NOT EXISTS "FingerprintKeyVersion" INTEGER NOT NULL DEFAULT 0,
                ADD COLUMN IF NOT EXISTS "Fingerprint" BYTEA NOT NULL DEFAULT ''::bytea,
                ADD COLUMN IF NOT EXISTS "Source" INTEGER NOT NULL DEFAULT 0,
                ADD COLUMN IF NOT EXISTS "SourceEntryId" TEXT,
                ADD COLUMN IF NOT EXISTS "SourceGeneration" BIGINT,
                ADD COLUMN IF NOT EXISTS "LastSeenUtc" TIMESTAMP WITH TIME ZONE,
                ADD COLUMN IF NOT EXISTS "DisabledReason" TEXT,
                ADD COLUMN IF NOT EXISTS "DisabledAtUtc" TIMESTAMP WITH TIME ZONE,
                ADD COLUMN IF NOT EXISTS "CooldownUntilUtc" TIMESTAMP WITH TIME ZONE,
                ADD COLUMN IF NOT EXISTS "ConsecutiveTransientFailures" INTEGER NOT NULL DEFAULT 0,
                ADD COLUMN IF NOT EXISTS "LastOutcome" TEXT,
                ADD COLUMN IF NOT EXISTS "LastClaimedUtc" TIMESTAMP WITH TIME ZONE,
                ADD COLUMN IF NOT EXISTS "LeaseId" UUID,
                ADD COLUMN IF NOT EXISTS "LeaseOwnerNodeId" TEXT,
                ADD COLUMN IF NOT EXISTS "LeaseRequestId" UUID,
                ADD COLUMN IF NOT EXISTS "LeaseAcquiredUtc" TIMESTAMP WITH TIME ZONE,
                ADD COLUMN IF NOT EXISTS "LeaseExpiresUtc" TIMESTAMP WITH TIME ZONE,
                ADD COLUMN IF NOT EXISTS "Revision" BIGINT NOT NULL DEFAULT 0,
                ADD COLUMN IF NOT EXISTS "IsArchived" BOOLEAN NOT NULL DEFAULT FALSE,
                ADD COLUMN IF NOT EXISTS "ReplacedByStableId" UUID,
                ADD COLUMN IF NOT EXISTS "CreatedUtc" TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP,
                ADD COLUMN IF NOT EXISTS "UpdatedUtc" TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP;
            CREATE INDEX IF NOT EXISTS "IX_SearchProviderTokens_ProviderInstanceId"
                ON "SearchProviderTokens" ("ProviderInstanceId");

            CREATE TABLE IF NOT EXISTS "CredentialGrants" (
                "Id" BIGSERIAL PRIMARY KEY,
                "CredentialId" INTEGER NOT NULL,
                "Scope" INTEGER NOT NULL,
                "TelegramPrincipalId" BIGINT,
                "CreatedUtc" TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP,
                CONSTRAINT "CK_CredentialGrants_PrincipalScope"
                    CHECK (("Scope" = 1 AND "TelegramPrincipalId" IS NOT NULL AND "TelegramPrincipalId" > 0)
                        OR ("Scope" IN (2, 3) AND "TelegramPrincipalId" IS NULL)),
                CONSTRAINT "FK_CredentialGrants_SearchProviderTokens_CredentialId"
                    FOREIGN KEY ("CredentialId") REFERENCES "SearchProviderTokens" ("Id") ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "UX_CredentialGrants_CredentialScopePrincipal"
                ON "CredentialGrants" ("CredentialId", "Scope", "TelegramPrincipalId")
                WHERE "TelegramPrincipalId" IS NOT NULL;
            CREATE UNIQUE INDEX IF NOT EXISTS "UX_CredentialGrants_CredentialScopeWithoutPrincipal"
                ON "CredentialGrants" ("CredentialId", "Scope")
                WHERE "TelegramPrincipalId" IS NULL;

            CREATE TABLE IF NOT EXISTS "PublicSearchConsents" (
                "Id" BIGSERIAL PRIMARY KEY,
                "ProviderInstanceId" BIGINT NOT NULL,
                "IsActive" BOOLEAN NOT NULL DEFAULT FALSE,
                "ActorTelegramId" BIGINT NOT NULL,
                "OptedInUtc" TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP,
                "OptedOutUtc" TIMESTAMP WITH TIME ZONE,
                "CreatedUtc" TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP,
                "UpdatedUtc" TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP,
                CONSTRAINT "CK_PublicSearchConsents_State"
                    CHECK ("ActorTelegramId" > 0 AND
                        (("IsActive" AND "OptedOutUtc" IS NULL) OR
                         (NOT "IsActive" AND "OptedOutUtc" IS NOT NULL))),
                CONSTRAINT "FK_PublicSearchConsents_SearchProviderInstances_ProviderInstanceId"
                    FOREIGN KEY ("ProviderInstanceId") REFERENCES "SearchProviderInstances" ("Id") ON DELETE RESTRICT
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "UX_PublicSearchConsents_ProviderInstanceId"
                ON "PublicSearchConsents" ("ProviderInstanceId");

            CREATE TABLE IF NOT EXISTS "PrivilegedAuditRecords" (
                "Id" BIGSERIAL PRIMARY KEY,
                "ActorTelegramId" BIGINT NOT NULL,
                "Action" INTEGER NOT NULL,
                "TargetStableId" UUID NOT NULL,
                "OccurredUtc" TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP,
                "Outcome" INTEGER NOT NULL,
                "SanitizedReason" TEXT NOT NULL,
                CONSTRAINT "CK_PrivilegedAuditRecords_Actor"
                    CHECK ("ActorTelegramId" > 0),
                CONSTRAINT "CK_PrivilegedAuditRecords_Action"
                    CHECK ("Action" IN (1, 2, 3, 4, 5, 6)),
                CONSTRAINT "CK_PrivilegedAuditRecords_Outcome"
                    CHECK ("Outcome" IN (1, 2)),
                CONSTRAINT "CK_PrivilegedAuditRecords_Reason"
                    CHECK (length("SanitizedReason") BETWEEN 1 AND 512)
            );
            CREATE INDEX IF NOT EXISTS "IX_PrivilegedAuditRecords_TargetStableId_OccurredUtc"
                ON "PrivilegedAuditRecords" ("TargetStableId", "OccurredUtc");

            -- Every NOT NULL column is supplied explicitly so the seed does not depend on a
            -- SQL DEFAULT that an EF-created table never carries for the endpoint-policy columns.
            INSERT INTO "SearchProviderInstances"
                ("StableId", "ProviderKind", "DisplayName", "NormalizedScheme", "NormalizedHost",
                 "NormalizedPort", "NormalizedBasePath", "IsEnabled", "AllowGlobalPublicSearch",
                 "MaxConcurrentOperations", "SettingsVersion", "SettingsJson", "ApprovedByTelegramId",
                 "EndpointPolicyVersion", "ApprovedEndpointIdentity", "EndpointApprovedAtUtc",
                 "DevelopmentHttpAllowed", "PrivateNetworkAllowlistJson",
                 "CreatedUtc", "UpdatedUtc")
            VALUES
                ('8b3d0a4f-7a6d-4a4c-8cf0-0c6cb18f8b11', 1, 'GitHub', 'https', 'api.github.com',
                 443, '/', TRUE, FALSE, 4, 1, '{{}}', NULL,
                 0, NULL, NULL, FALSE, '[]', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP),
                ('9386a72d-01df-4bc6-9ad4-cb47a85f0a22', 2, 'GitLab', 'https', 'gitlab.com',
                 443, '/api/v4', TRUE, FALSE, 4, 1, '{{}}', NULL,
                 0, NULL, NULL, FALSE, '[]', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP),
                ('a1b2c3d4-1111-4a4c-8cf0-0c6cb18f8b31', 3, 'Sourcegraph', 'https', 'sourcegraph.com',
                 443, '/.api', TRUE, FALSE, 4, 1, '{{}}', NULL,
                 0, NULL, NULL, FALSE, '[]', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP),
                ('b2c3d4e5-2222-4a4c-8cf0-0c6cb18f8b32', 4, 'HuggingFace', 'https', 'huggingface.co',
                 443, '/api', TRUE, FALSE, 4, 1, '{{}}', NULL,
                 0, NULL, NULL, FALSE, '[]', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP),
                ('c3d4e5f6-3333-4a4c-8cf0-0c6cb18f8b33', 5, 'AzureDevOps', 'https', 'dev.azure.com',
                 443, '/', TRUE, FALSE, 4, 1, '{{}}', NULL,
                 0, NULL, NULL, FALSE, '[]', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP)
            ON CONFLICT DO NOTHING;

            UPDATE "SearchProviderTokens" AS token
               SET "ProviderInstanceId" = instance."Id"
              FROM "SearchProviderInstances" AS instance
             WHERE token."ProviderInstanceId" IS NULL
               AND token."SearchProvider" = instance."ProviderKind"
               AND ((instance."ProviderKind" = 1
                     AND instance."NormalizedScheme" = 'https'
                     AND instance."NormalizedHost" = 'api.github.com'
                     AND instance."NormalizedPort" = 443
                     AND instance."NormalizedBasePath" = '/')
                 OR (instance."ProviderKind" = 2
                     AND instance."NormalizedScheme" = 'https'
                     AND instance."NormalizedHost" = 'gitlab.com'
                     AND instance."NormalizedPort" = 443
                     AND instance."NormalizedBasePath" = '/api/v4')
                 OR (instance."ProviderKind" = 3
                     AND instance."NormalizedScheme" = 'https'
                     AND instance."NormalizedHost" = 'sourcegraph.com'
                     AND instance."NormalizedPort" = 443
                     AND instance."NormalizedBasePath" = '/.api')
                 OR (instance."ProviderKind" = 4
                     AND instance."NormalizedScheme" = 'https'
                     AND instance."NormalizedHost" = 'huggingface.co'
                     AND instance."NormalizedPort" = 443
                     AND instance."NormalizedBasePath" = '/api')
                 OR (instance."ProviderKind" = 5
                     AND instance."NormalizedScheme" = 'https'
                     AND instance."NormalizedHost" = 'dev.azure.com'
                     AND instance."NormalizedPort" = 443
                     AND instance."NormalizedBasePath" = '/'));

            DO $$
            BEGIN
                IF EXISTS (
                    SELECT 1
                      FROM "SearchProviderTokens" AS credential
                      LEFT JOIN "SearchProviderInstances" AS instance
                        ON instance."Id" = credential."ProviderInstanceId"
                     WHERE credential."ProviderInstanceId" IS NULL
                        OR instance."Id" IS NULL
                        OR instance."ProviderKind" <> credential."SearchProvider"
                ) THEN
                    RAISE EXCEPTION 'Credential Provider Instance backfill is incomplete.';
                END IF;
            END $$;
            ALTER TABLE "SearchProviderTokens"
                ALTER COLUMN "ProviderInstanceId" SET NOT NULL;

            UPDATE "SearchProviderTokens"
               SET "StableId" = gen_random_uuid()
             WHERE "StableId" IS NULL;
            ALTER TABLE "SearchProviderTokens"
                ALTER COLUMN "StableId" SET DEFAULT gen_random_uuid(),
                ALTER COLUMN "StableId" SET NOT NULL;

            UPDATE "SearchProviderTokens"
               SET "DisabledReason" = 'LegacyDisabled',
                   "DisabledAtUtc" = CURRENT_TIMESTAMP
             WHERE NOT "IsEnabled"
               AND ("DisabledReason" IS NULL OR trim("DisabledReason") = '' OR "DisabledAtUtc" IS NULL);
            UPDATE "SearchProviderTokens"
               SET "DisabledReason" = NULL,
                   "DisabledAtUtc" = NULL
             WHERE "IsEnabled";

            CREATE UNIQUE INDEX IF NOT EXISTS "UX_SearchProviderTokens_StableId"
                ON "SearchProviderTokens" ("StableId");
            CREATE UNIQUE INDEX IF NOT EXISTS "UX_SearchProviderTokens_ProviderInstance_Fingerprint"
                ON "SearchProviderTokens" ("ProviderInstanceId", "Fingerprint")
                WHERE "FingerprintKeyVersion" > 0 AND NOT "IsArchived";
            CREATE UNIQUE INDEX IF NOT EXISTS "UX_SearchProviderTokens_LeaseId"
                ON "SearchProviderTokens" ("LeaseId")
                WHERE "LeaseId" IS NOT NULL;
            CREATE INDEX IF NOT EXISTS "IX_SearchProviderTokens_Eligibility"
                ON "SearchProviderTokens"
                   ("ProviderInstanceId", "IsEnabled", "DisabledAtUtc", "CooldownUntilUtc",
                    "LeaseExpiresUtc", "LastClaimedUtc", "StableId");

            DO $$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'CK_SearchProviderTokens_State') THEN
                    ALTER TABLE "SearchProviderTokens" ADD CONSTRAINT "CK_SearchProviderTokens_State" CHECK (
                        ("IsEnabled" AND "DisabledReason" IS NULL AND "DisabledAtUtc" IS NULL)
                        OR (NOT "IsEnabled" AND length(trim("DisabledReason")) > 0 AND "DisabledAtUtc" IS NOT NULL));
                END IF;
                IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'CK_SearchProviderTokens_ProtectedEnvelope') THEN
                    ALTER TABLE "SearchProviderTokens" ADD CONSTRAINT "CK_SearchProviderTokens_ProtectedEnvelope" CHECK (
                        ("EnvelopeFormatVersion" = 0 AND "ProtectionKeyVersion" = 0
                         AND length("ProtectionNonce") = 0 AND length("ProtectedCiphertext") = 0
                         AND length("AuthenticationTag") = 0)
                        OR ("EnvelopeFormatVersion" > 0 AND "ProtectionKeyVersion" > 0
                            AND length("ProtectionNonce") = 12 AND length("ProtectedCiphertext") > 0
                            AND length("AuthenticationTag") = 16));
                END IF;
                IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'CK_SearchProviderTokens_Fingerprint') THEN
                    ALTER TABLE "SearchProviderTokens" ADD CONSTRAINT "CK_SearchProviderTokens_Fingerprint" CHECK (
                        ("FingerprintKeyVersion" = 0 AND length("Fingerprint") = 0)
                        OR ("FingerprintKeyVersion" > 0 AND length("Fingerprint") = 32));
                END IF;
                IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'CK_SearchProviderTokens_Source') THEN
                    ALTER TABLE "SearchProviderTokens" ADD CONSTRAINT "CK_SearchProviderTokens_Source"
                        CHECK ("Source" IN (0, 1, 2));
                END IF;
                IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'CK_SearchProviderTokens_Revision') THEN
                    ALTER TABLE "SearchProviderTokens" ADD CONSTRAINT "CK_SearchProviderTokens_Revision"
                        CHECK ("Revision" >= 0);
                END IF;
            END $$;

            DO $$
            BEGIN
                IF NOT EXISTS (
                    SELECT 1 FROM pg_constraint
                     WHERE conname = 'FK_SearchProviderTokens_SearchProviderInstances_ProviderInstanceId'
                ) THEN
                    ALTER TABLE "SearchProviderTokens"
                        ADD CONSTRAINT "FK_SearchProviderTokens_SearchProviderInstances_ProviderInstanceId"
                        FOREIGN KEY ("ProviderInstanceId") REFERENCES "SearchProviderInstances" ("Id")
                        ON DELETE RESTRICT;
                END IF;
            END $$;

            INSERT INTO "SchemaVersions" ("Id", "Version", "AppliedUtc")
            VALUES (1, 5, CURRENT_TIMESTAMP)
            ON CONFLICT ("Id") DO UPDATE SET
                "Version" = EXCLUDED."Version",
                "AppliedUtc" = EXCLUDED."AppliedUtc"
            WHERE "SchemaVersions"."Version" < EXCLUDED."Version";

            INSERT INTO "ReadinessMarkers" ("Id", "Name", "SchemaVersion", "IsReady", "UpdatedUtc")
            VALUES (1, 'provider-instance-schema', 5, TRUE, CURRENT_TIMESTAMP)
            ON CONFLICT ("Id") DO UPDATE SET
                "Name" = EXCLUDED."Name",
                "SchemaVersion" = EXCLUDED."SchemaVersion",
                "IsReady" = EXCLUDED."IsReady",
                "UpdatedUtc" = EXCLUDED."UpdatedUtc"
            WHERE "ReadinessMarkers"."SchemaVersion" <= EXCLUDED."SchemaVersion";

            INSERT INTO "CutoverMarkers" ("Id", "Name", "Version", "IsComplete", "UpdatedUtc")
            VALUES (1, 'worker-claims', 5, FALSE, CURRENT_TIMESTAMP)
            ON CONFLICT ("Id") DO UPDATE SET
                "Name" = EXCLUDED."Name",
                "Version" = EXCLUDED."Version",
                "UpdatedUtc" = EXCLUDED."UpdatedUtc"
            WHERE "CutoverMarkers"."Version" < EXCLUDED."Version";

            CREATE TABLE IF NOT EXISTS "WorkItems" (
                "Id" BIGSERIAL PRIMARY KEY,
                "StableId" UUID NOT NULL DEFAULT gen_random_uuid(),
                "PrincipalScope" INTEGER NOT NULL DEFAULT 0,
                "PrincipalTelegramId" BIGINT,
                "ProviderInstanceId" BIGINT NOT NULL,
                "ProviderKind" INTEGER NOT NULL DEFAULT 0,
                "SearchQueryId" INTEGER,
                "EffectiveQueryHash" VARCHAR(64) NOT NULL DEFAULT '',
                "AdapterVersion" VARCHAR(128) NOT NULL DEFAULT '',
                "QuerySnapshotJson" TEXT NOT NULL DEFAULT '{}',
                "IsTerminal" BOOLEAN NOT NULL DEFAULT FALSE,
                "IsComplete" BOOLEAN NOT NULL DEFAULT FALSE,
                "CreatedUtc" TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP,
                "UpdatedUtc" TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "UX_WorkItems_StableId" ON "WorkItems" ("StableId");
            CREATE INDEX IF NOT EXISTS "IX_WorkItems_ProviderInstanceId" ON "WorkItems" ("ProviderInstanceId");

            CREATE TABLE IF NOT EXISTS "WorkPartitions" (
                "Id" BIGSERIAL PRIMARY KEY,
                "StableId" UUID NOT NULL DEFAULT gen_random_uuid(),
                "WorkItemId" BIGINT NOT NULL,
                "PartitionKey" VARCHAR(256) NOT NULL DEFAULT '',
                "Continuation" TEXT,
                "ContinuationAdapterVersion" VARCHAR(128),
                "LastSafeCheckpoint" TEXT,
                "IsTerminal" BOOLEAN NOT NULL DEFAULT FALSE,
                "IsComplete" BOOLEAN NOT NULL DEFAULT FALSE,
                "CreatedUtc" TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP,
                "UpdatedUtc" TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "UX_WorkPartitions_StableId" ON "WorkPartitions" ("StableId");
            CREATE INDEX IF NOT EXISTS "IX_WorkPartitions_WorkItemId" ON "WorkPartitions" ("WorkItemId");

            CREATE TABLE IF NOT EXISTS "SearchQueryOverrides" (
                "Id" BIGSERIAL PRIMARY KEY,
                "WorkItemId" BIGINT NOT NULL,
                "GenericQuery" TEXT NOT NULL DEFAULT '',
                "NativeOverride" TEXT,
                "SettingsJson" TEXT NOT NULL DEFAULT '{}'
            );
            CREATE INDEX IF NOT EXISTS "IX_SearchQueryOverrides_WorkItemId" ON "SearchQueryOverrides" ("WorkItemId");

            CREATE TABLE IF NOT EXISTS "NormalizedResults" (
                "Id" BIGSERIAL PRIMARY KEY,
                "ProviderKind" INTEGER NOT NULL DEFAULT 0,
                "ProviderInstanceStableId" UUID NOT NULL,
                "RepositoryStableId" VARCHAR(512) NOT NULL DEFAULT '',
                "RepositoryOwner" VARCHAR(256),
                "RepositoryName" VARCHAR(256),
                "ImmutableRevisionOrEquivalentVersion" VARCHAR(128) NOT NULL DEFAULT '',
                "NormalizedFilePath" VARCHAR(2048) NOT NULL DEFAULT '',
                "FileName" VARCHAR(512),
                "LineNumber" INTEGER,
                "Snippet" VARCHAR(4096),
                "ProvenanceUrl" VARCHAR(2048),
                "Branch" VARCHAR(256),
                "SearchQueryId" INTEGER,
                "WorkItemId" BIGINT,
                "WorkPartitionId" BIGINT,
                "DiscoveredUtc" TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP,
                "ProvenanceJson" TEXT NOT NULL DEFAULT '{}',
                "ApiKeyId" INTEGER
            );
            CREATE INDEX IF NOT EXISTS "IX_NormalizedResults_WorkItemId" ON "NormalizedResults" ("WorkItemId");

            CREATE TABLE IF NOT EXISTS "ResultDeduplicationRecords" (
                "Id" BIGSERIAL PRIMARY KEY,
                "ProviderInstanceStableId" VARCHAR(512) NOT NULL DEFAULT '',
                "RepositoryStableId" VARCHAR(512) NOT NULL DEFAULT '',
                "ImmutableRevisionOrEquivalentVersion" VARCHAR(128) NOT NULL DEFAULT '',
                "NormalizedFilePath" VARCHAR(2048) NOT NULL DEFAULT '',
                "NormalizedResultId" BIGINT NOT NULL,
                "FirstDiscoveredUtc" TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP,
                "LastSeenUtc" TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP
            );
            CREATE INDEX IF NOT EXISTS "IX_ResultDeduplicationRecords_Lookup"
                ON "ResultDeduplicationRecords" ("ProviderInstanceStableId", "RepositoryStableId", "ImmutableRevisionOrEquivalentVersion", "NormalizedFilePath");

            CREATE TABLE IF NOT EXISTS "ResultOutboxRecords" (
                "Id" BIGSERIAL PRIMARY KEY,
                "NormalizedResultId" BIGINT NOT NULL,
                "EventKind" VARCHAR(64) NOT NULL DEFAULT '',
                "PayloadJson" TEXT NOT NULL DEFAULT '{}',
                "IsProcessed" BOOLEAN NOT NULL DEFAULT FALSE,
                "CreatedUtc" TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP,
                "ProcessedUtc" TIMESTAMP WITH TIME ZONE
            );
            CREATE INDEX IF NOT EXISTS "IX_ResultOutboxRecords_IsProcessed" ON "ResultOutboxRecords" ("IsProcessed");

            CREATE TABLE IF NOT EXISTS "CredentialClaimRecords" (
                "Id" BIGSERIAL PRIMARY KEY,
                "PrincipalScope" INTEGER NOT NULL DEFAULT 0,
                "PrincipalTelegramId" BIGINT,
                "RequestId" UUID NOT NULL,
                "CredentialStableId" UUID NOT NULL,
                "ProviderInstanceStableId" UUID NOT NULL,
                "WorkItemId" BIGINT NOT NULL,
                "LeaseId" UUID NOT NULL,
                "LeaseOwnerNodeId" VARCHAR(256),
                "LeaseAcquiredUtc" TIMESTAMP WITH TIME ZONE NOT NULL,
                "LeaseExpiresUtc" TIMESTAMP WITH TIME ZONE NOT NULL,
                "CredentialRevision" BIGINT NOT NULL DEFAULT 0,
                "TerminalOutcome" VARCHAR(64),
                "IsTerminal" BOOLEAN NOT NULL DEFAULT FALSE,
                "TerminalizedUtc" TIMESTAMP WITH TIME ZONE,
                "CreatedUtc" TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP,
                "UpdatedUtc" TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP
            );
            CREATE INDEX IF NOT EXISTS "IX_CredentialClaimRecords_RequestId" ON "CredentialClaimRecords" ("RequestId");
            CREATE INDEX IF NOT EXISTS "IX_CredentialClaimRecords_LeaseId" ON "CredentialClaimRecords" ("LeaseId");
            CREATE INDEX IF NOT EXISTS "IX_CredentialClaimRecords_PrincipalScope"
                ON "CredentialClaimRecords" ("PrincipalScope", "PrincipalTelegramId", "RequestId");

            CREATE TABLE IF NOT EXISTS "OperationSlots" (
                "Id" BIGSERIAL PRIMARY KEY,
                "SlotId" UUID NOT NULL DEFAULT gen_random_uuid(),
                "ProviderInstanceId" BIGINT NOT NULL,
                "RequestId" UUID NOT NULL,
                "PrincipalScope" INTEGER NOT NULL DEFAULT 0,
                "PrincipalTelegramId" BIGINT,
                "WorkItemId" BIGINT NOT NULL,
                "PartitionKey" VARCHAR(256) NOT NULL DEFAULT '',
                "AcquiredUtc" TIMESTAMP WITH TIME ZONE NOT NULL,
                "ExpiresUtc" TIMESTAMP WITH TIME ZONE NOT NULL,
                "Revision" BIGINT NOT NULL DEFAULT 0,
                "IsTerminal" BOOLEAN NOT NULL DEFAULT FALSE,
                "TerminalizedUtc" TIMESTAMP WITH TIME ZONE,
                "CreatedUtc" TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP,
                "UpdatedUtc" TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "UX_OperationSlots_SlotId" ON "OperationSlots" ("SlotId");
            CREATE INDEX IF NOT EXISTS "IX_OperationSlots_ActiveCapacity"
                ON "OperationSlots" ("ProviderInstanceId", "ExpiresUtc", "IsTerminal");
            """);
    }

    private async Task EnsureSQLiteSchemaAsync(DBContext context)
    {
        try
        {
            Console.WriteLine("[DB] Running SQLite schema health check...");
            // Task 3.2 parity: SearchProviderInstance ProviderInstanceId StableId ProviderKind DisplayName
            // NormalizedScheme NormalizedHost NormalizedPort NormalizedBasePath IsEnabled
            // AllowGlobalPublicSearch MaxConcurrentOperations SettingsVersion SettingsJson
            // ApprovedByTelegramId CreatedUtc UpdatedUtc SchemaVersion ReadinessMarker CutoverMarker
            // CredentialGrant CredentialId Scope TelegramPrincipalId unique grant identity.
            await EnsureProviderInstanceSqliteSchemaAsync(context);

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
            // Task 3.2 parity: SearchProviderInstance ProviderInstanceId StableId ProviderKind DisplayName
            // NormalizedScheme NormalizedHost NormalizedPort NormalizedBasePath IsEnabled
            // AllowGlobalPublicSearch MaxConcurrentOperations SettingsVersion SettingsJson
            // ApprovedByTelegramId CreatedUtc UpdatedUtc SchemaVersion ReadinessMarker CutoverMarker
            // CredentialGrant CredentialId Scope TelegramPrincipalId unique grant identity.
            await EnsureProviderInstancePostgresSchemaAsync(context);

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
            Console.WriteLine("[DB] Purging junk repo references for invalid API keys...");

            // 1. Delete all RepoReferences associated with invalid keys (reclaims 90%+ disk space)
            int deletedRefs = await context.RepoReferences
                .Where(r => context.APIKeys
                    .Any(k => k.Id == r.APIKeyId && k.Status == ApiStatusEnum.Invalid))
                .ExecuteDeleteAsync();

            // 2. Strip heavy text payloads from invalid keys, keeping the slim row for deduplication
            await context.APIKeys
                .Where(k => k.Status == ApiStatusEnum.Invalid && (k.ValidationResponse != null || k.Metadata != null))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(k => k.ValidationResponse, (string?)null)
                    .SetProperty(k => k.Metadata, (string?)null)
                    .SetProperty(k => k.Balance, (string?)null)
                    .SetProperty(k => k.AccountTier, (string?)null)
                    .SetProperty(k => k.AwsAttachedPolicies, (string?)null));

            if (deletedRefs > 0)
                Console.WriteLine($"[DB] Purged {deletedRefs} junk repo reference(s). Invalid keys retained as tombstones for deduplication.");
            else
                Console.WriteLine("[DB] No junk repo references found.");

            return deletedRefs;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DB] Error during purge: {ex.Message}");
            return 0;
        }
    }

    /// <summary>
    /// Purges very old dead key tombstones (older than N days, default 90) to ensure bounded storage growth.
    /// </summary>
    public async Task<int> PurgeOldInvalidKeysAsync(DBContext context, int olderThanDays = 90)
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-olderThanDays);
            
            // Delete references first
            await context.RepoReferences
                .Where(r => context.APIKeys
                    .Any(k => k.Id == r.APIKeyId && k.Status == ApiStatusEnum.Invalid && (k.LastCheckedUTC == null || k.LastCheckedUTC < cutoff)))
                .ExecuteDeleteAsync();

            // Delete stale tombstone keys
            int deleted = await context.APIKeys
                .Where(k => k.Status == ApiStatusEnum.Invalid && (k.LastCheckedUTC == null || k.LastCheckedUTC < cutoff))
                .ExecuteDeleteAsync();

            if (deleted > 0)
                Console.WriteLine($"[DB] Cleaned up {deleted} stale invalid key tombstones older than {olderThanDays} days.");

            return deleted;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DB] Error cleaning up stale tombstones: {ex.Message}");
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

    public async Task<CredentialWriteResult> AddGitHubTokenAsync(
        DBContext dbContext,
        string token,
        long? addedBy = null)
    {
        return await AddSearchProviderTokenAsync(
            dbContext,
            token,
            SearchProviderEnum.GitHub,
            addedBy);
    }

    public async Task<CredentialWriteResult> AddGitLabTokenAsync(
        DBContext dbContext,
        string token,
        long? addedBy = null)
    {
        return await AddSearchProviderTokenAsync(
            dbContext,
            token,
            SearchProviderEnum.GitLab,
            addedBy);
    }

    public async Task<CredentialWriteResult> AddSearchProviderTokenAsync(
        DBContext dbContext,
        string token,
        SearchProviderEnum provider,
        long? addedBy = null)
    {
        if (credentialStorageService is null)
        {
            throw new CredentialProtectionException(
                "Protected credential writes are unavailable because credential protection is not configured.");
        }
        if (provider is SearchProviderEnum.Unknown)
        {
            throw new ArgumentOutOfRangeException(nameof(provider), "Provider Kind is not supported.");
        }

        var providerInstance = provider switch
        {
            SearchProviderEnum.GitHub => await dbContext.SearchProviderInstances
                .SingleAsync(instance => instance.StableId == ProviderInstanceSchema.DefaultGitHubStableId),
            SearchProviderEnum.GitLab => await dbContext.SearchProviderInstances
                .SingleAsync(instance => instance.StableId == ProviderInstanceSchema.DefaultGitLabStableId),
            SearchProviderEnum.Sourcegraph => await dbContext.SearchProviderInstances
                .SingleAsync(instance => instance.StableId == ProviderInstanceSchema.DefaultSourcegraphStableId),
            SearchProviderEnum.HuggingFace => await dbContext.SearchProviderInstances
                .SingleAsync(instance => instance.StableId == ProviderInstanceSchema.DefaultHuggingFaceStableId),
            SearchProviderEnum.AzureDevOps => await dbContext.SearchProviderInstances
                .SingleAsync(instance => instance.StableId == ProviderInstanceSchema.DefaultAzureDevOpsStableId),
            SearchProviderEnum.Gitea or SearchProviderEnum.Forgejo => await dbContext.SearchProviderInstances
                .Where(instance => instance.ProviderKind == provider)
                .OrderBy(instance => instance.Id)
                .FirstAsync(),
            _ => throw new ArgumentOutOfRangeException(nameof(provider), "Provider Kind is not supported.")
        };
        var fingerprint = credentialStorageService.ComputeFingerprint(
            token,
            provider,
            providerInstance.StableId);
        var existing = await dbContext.SearchProviderTokens
            .AsNoTracking()
            .Where(credential =>
                credential.ProviderInstanceId == providerInstance.Id &&
                credential.FingerprintKeyVersion == fingerprint.FingerprintKeyVersion &&
                credential.Fingerprint == fingerprint.Fingerprint)
            .Select(credential => new
            {
                credential.Id,
                credential.StableId
            })
            .SingleOrDefaultAsync();
        if (existing is not null)
        {
            return new CredentialWriteResult(
                Created: false,
                existing.Id,
                existing.StableId);
        }

        var credential = credentialStorageService.CreateProtectedCredential(
            token,
            providerInstance,
            CredentialSource.Manual,
            addedByTelegramId: addedBy);
        credential.CredentialGrants.Add(new CredentialGrant
        {
            Scope = addedBy is > 0
                ? CredentialGrantScope.User
                : CredentialGrantScope.Admin,
            TelegramPrincipalId = addedBy is > 0 ? addedBy : null,
            CreatedUtc = DateTime.UtcNow
        });

        dbContext.SearchProviderTokens.Add(credential);
        await dbContext.SaveChangesAsync();
        await MarkProtectedReadVerificationPendingAsync(dbContext);
        return new CredentialWriteResult(
            Created: true,
            credential.Id,
            credential.StableId);
    }

    public Task<List<CredentialSummary>> GetGitHubTokensAsync(
        DBContext dbContext,
        long? filterByTelegramId = null) =>
        GetSearchTokensAsync(dbContext, SearchProviderEnum.GitHub, filterByTelegramId);

    public Task<List<CredentialSummary>> GetGitLabTokensAsync(
        DBContext dbContext,
        long? filterByTelegramId = null) =>
        GetSearchTokensAsync(dbContext, SearchProviderEnum.GitLab, filterByTelegramId);

    public async Task<List<CredentialSummary>> GetSearchTokensAsync(
        DBContext dbContext,
        SearchProviderEnum? provider = null,
        long? filterByTelegramId = null)
    {
        var query = dbContext.SearchProviderTokens.AsNoTracking();
        if (provider.HasValue)
        {
            query = query.Where(credential => credential.SearchProvider == provider.Value);
        }

        if (filterByTelegramId.HasValue)
        {
            var telegramPrincipalId = filterByTelegramId.Value;
            query = query.Where(credential => credential.CredentialGrants.Any(grant =>
                grant.Scope == CredentialGrantScope.Global ||
                (grant.Scope == CredentialGrantScope.User &&
                 grant.TelegramPrincipalId == telegramPrincipalId)));
        }

        var rows = await query
            .OrderBy(credential => credential.Id)
            .Select(credential => new
            {
                credential.Id,
                credential.StableId,
                credential.SearchProvider,
                credential.Source,
                credential.IsEnabled,
                credential.LastClaimedUtc,
                credential.LastUsedUTC,
                credential.CooldownUntilUtc,
                credential.DisabledReason,
                credential.DisabledAtUtc,
                Grants = credential.CredentialGrants
                    .OrderBy(grant => grant.Scope)
                    .ThenBy(grant => grant.TelegramPrincipalId)
                    .Select(grant => new CredentialGrantSummary(
                        grant.Scope,
                        grant.TelegramPrincipalId))
                    .ToList()
            })
            .ToListAsync();

        return rows.Select(row => new CredentialSummary(
                row.Id,
                row.StableId,
                row.SearchProvider,
                row.Source,
                row.IsEnabled,
                row.LastClaimedUtc,
                row.LastUsedUTC,
                row.CooldownUntilUtc,
                row.DisabledReason,
                row.DisabledAtUtc,
                row.Grants))
            .ToList();
    }

    private static async Task MarkProtectedReadVerificationPendingAsync(DBContext context)
    {
        var marker = await context.ReadinessMarkers.SingleOrDefaultAsync(candidate =>
            candidate.Name == ProviderInstanceSchema.ProtectedReadsReadinessMarkerName);
        var now = await ReadDatabaseUtcAsync(context);
        if (marker is null)
        {
            context.ReadinessMarkers.Add(new ReadinessMarker
            {
                Id = ProviderInstanceSchema.ProtectedReadsMarkerRecordId,
                Name = ProviderInstanceSchema.ProtectedReadsReadinessMarkerName,
                SchemaVersion = ProviderInstanceSchema.CurrentVersion,
                IsReady = false,
                UpdatedUtc = now
            });
        }
        else
        {
            marker.SchemaVersion = ProviderInstanceSchema.CurrentVersion;
            marker.IsReady = false;
            marker.UpdatedUtc = now;
        }
        await context.SaveChangesAsync();
    }

    private static async Task<DateTime> ReadDatabaseUtcAsync(DBContext context)
    {
        var commandText = context.Database.IsSqlite()
            ? "SELECT strftime('%Y-%m-%dT%H:%M:%fZ', 'now') AS \"Value\""
            : "SELECT CURRENT_TIMESTAMP AS \"Value\"";
        var value = await context.Database.SqlQueryRaw<DateTime>(commandText).SingleAsync();
        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
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

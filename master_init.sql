-- =============================================================================
-- 🚀 APIHunterV2 MASTER INITIALIZATION SCRIPT
-- Target: Supabase / PostgreSQL
-- Safe to run multiple times (fully idempotent).
-- Last updated: April 2026 — Schema parity restored for ApplicationSettings.
-- =============================================================================

-- =============================================================================
-- SECTION 1: TELEGRAM SUBSCRIBERS
-- =============================================================================
CREATE TABLE IF NOT EXISTS "TelegramSubscribers" (
    "TelegramId"            BIGINT PRIMARY KEY,
    "Username"              TEXT,
    "SubscriptionExpiryUtc" TIMESTAMP WITH TIME ZONE DEFAULT '1970-01-01 00:00:00+00',
    "IsAdmin"               BOOLEAN DEFAULT FALSE,
    "CreatedAtUtc"          TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP,
    "NodeToken"             TEXT,
    "NodeUrl"               TEXT,
    "LastNodeHeartbeatUtc"  TIMESTAMP WITH TIME ZONE,
    "DeployHook"            TEXT
);

-- Idempotent column additions (safe on existing databases)
ALTER TABLE "TelegramSubscribers" DROP COLUMN IF EXISTS "SubscribedAtUTC";
ALTER TABLE "TelegramSubscribers" ADD COLUMN IF NOT EXISTS "Username"              TEXT;
ALTER TABLE "TelegramSubscribers" ADD COLUMN IF NOT EXISTS "SubscriptionExpiryUtc" TIMESTAMP WITH TIME ZONE DEFAULT '1970-01-01 00:00:00+00';
ALTER TABLE "TelegramSubscribers" ADD COLUMN IF NOT EXISTS "IsAdmin"               BOOLEAN DEFAULT FALSE;
ALTER TABLE "TelegramSubscribers" ADD COLUMN IF NOT EXISTS "CreatedAtUtc"          TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP;
ALTER TABLE "TelegramSubscribers" ADD COLUMN IF NOT EXISTS "NodeToken"             TEXT;
ALTER TABLE "TelegramSubscribers" ADD COLUMN IF NOT EXISTS "NodeUrl"               TEXT;
ALTER TABLE "TelegramSubscribers" ADD COLUMN IF NOT EXISTS "LastNodeHeartbeatUtc"  TIMESTAMP WITH TIME ZONE;
ALTER TABLE "TelegramSubscribers" ADD COLUMN IF NOT EXISTS "DeployHook"            TEXT;

CREATE UNIQUE INDEX IF NOT EXISTS "IX_TelegramSubscribers_NodeToken"
    ON "TelegramSubscribers" ("NodeToken") WHERE "NodeToken" IS NOT NULL;

-- =============================================================================
-- SECTION 2: SEARCH QUERIES
-- =============================================================================
CREATE TABLE IF NOT EXISTS "SearchQueries" (
    "Id"                      SERIAL PRIMARY KEY,
    "Query"                   TEXT NOT NULL DEFAULT '',
    "IsEnabled"               BOOLEAN DEFAULT TRUE,
    "SearchResultsCount"      INTEGER DEFAULT 0,
    "LastSearchUTC"           TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP,
    "LastDeepSearchDateUTC"   TIMESTAMP WITH TIME ZONE,
    "LastSuccessfulSearchUTC" TIMESTAMP WITH TIME ZONE,
    "LastRepoPushedSeenUTC"   TIMESTAMP WITH TIME ZONE
);

ALTER TABLE "SearchQueries" ADD COLUMN IF NOT EXISTS "Query"                   TEXT NOT NULL DEFAULT '';
ALTER TABLE "SearchQueries" ADD COLUMN IF NOT EXISTS "IsEnabled"               BOOLEAN DEFAULT TRUE;
ALTER TABLE "SearchQueries" ADD COLUMN IF NOT EXISTS "SearchResultsCount"      INTEGER DEFAULT 0;
ALTER TABLE "SearchQueries" ADD COLUMN IF NOT EXISTS "LastSearchUTC"           TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP;
ALTER TABLE "SearchQueries" ADD COLUMN IF NOT EXISTS "LastDeepSearchDateUTC"   TIMESTAMP WITH TIME ZONE;
ALTER TABLE "SearchQueries" ADD COLUMN IF NOT EXISTS "LastSuccessfulSearchUTC" TIMESTAMP WITH TIME ZONE;
ALTER TABLE "SearchQueries" ADD COLUMN IF NOT EXISTS "LastRepoPushedSeenUTC"   TIMESTAMP WITH TIME ZONE;

CREATE INDEX IF NOT EXISTS "IX_SearchQueries_IsEnabled_LastSearchUTC"
    ON "SearchQueries" ("IsEnabled", "LastSearchUTC");
CREATE INDEX IF NOT EXISTS "IX_SearchQueries_IsEnabled_LastSuccessfulSearchUTC"
    ON "SearchQueries" ("IsEnabled", "LastSuccessfulSearchUTC");

-- =============================================================================
-- SECTION 3: SEARCH PROVIDER TOKENS
-- Additive protected-credential and endpoint-policy schema v5. The legacy Token column remains only for
-- the explicitly guarded pre-scrub migration window.
-- =============================================================================
CREATE TABLE IF NOT EXISTS "SearchProviderTokens" (
    "Id"                           SERIAL PRIMARY KEY,
    "StableId"                     UUID NOT NULL DEFAULT gen_random_uuid(),
    "Token"                        TEXT NOT NULL DEFAULT '',
    "SearchProvider"               INTEGER NOT NULL DEFAULT 0,
    "ProviderInstanceId"           BIGINT,
    "EnvelopeFormatVersion"        INTEGER NOT NULL DEFAULT 0,
    "ProtectionKeyVersion"         INTEGER NOT NULL DEFAULT 0,
    "ProtectionNonce"              BYTEA NOT NULL DEFAULT ''::bytea,
    "ProtectedCiphertext"          BYTEA NOT NULL DEFAULT ''::bytea,
    "AuthenticationTag"            BYTEA NOT NULL DEFAULT ''::bytea,
    "FingerprintKeyVersion"        INTEGER NOT NULL DEFAULT 0,
    "Fingerprint"                  BYTEA NOT NULL DEFAULT ''::bytea,
    "Source"                       INTEGER NOT NULL DEFAULT 0,
    "SourceEntryId"                TEXT,
    "SourceGeneration"             BIGINT,
    "LastSeenUtc"                  TIMESTAMP WITH TIME ZONE,
    "IsEnabled"                    BOOLEAN NOT NULL DEFAULT TRUE,
    "DisabledReason"               TEXT,
    "DisabledAtUtc"                TIMESTAMP WITH TIME ZONE,
    "CooldownUntilUtc"             TIMESTAMP WITH TIME ZONE,
    "ConsecutiveTransientFailures" INTEGER NOT NULL DEFAULT 0,
    "LastOutcome"                  TEXT,
    "LastClaimedUtc"               TIMESTAMP WITH TIME ZONE,
    "LastUsedUTC"                  TIMESTAMP WITH TIME ZONE,
    "LeaseId"                      UUID,
    "LeaseOwnerNodeId"             TEXT,
    "LeaseRequestId"               UUID,
    "LeaseAcquiredUtc"             TIMESTAMP WITH TIME ZONE,
    "LeaseExpiresUtc"              TIMESTAMP WITH TIME ZONE,
    "Revision"                     BIGINT NOT NULL DEFAULT 0,
    "IsArchived"                   BOOLEAN NOT NULL DEFAULT FALSE,
    "ReplacedByStableId"           UUID,
    "CreatedUtc"                   TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP,
    "UpdatedUtc"                   TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP,
    "AddedByTelegramId"            BIGINT
);

ALTER TABLE "SearchProviderTokens"
    ADD COLUMN IF NOT EXISTS "StableId" UUID,
    ADD COLUMN IF NOT EXISTS "Token" TEXT NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS "SearchProvider" INTEGER NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS "ProviderInstanceId" BIGINT,
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
    ADD COLUMN IF NOT EXISTS "IsEnabled" BOOLEAN NOT NULL DEFAULT TRUE,
    ADD COLUMN IF NOT EXISTS "DisabledReason" TEXT,
    ADD COLUMN IF NOT EXISTS "DisabledAtUtc" TIMESTAMP WITH TIME ZONE,
    ADD COLUMN IF NOT EXISTS "CooldownUntilUtc" TIMESTAMP WITH TIME ZONE,
    ADD COLUMN IF NOT EXISTS "ConsecutiveTransientFailures" INTEGER NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS "LastOutcome" TEXT,
    ADD COLUMN IF NOT EXISTS "LastClaimedUtc" TIMESTAMP WITH TIME ZONE,
    ADD COLUMN IF NOT EXISTS "LastUsedUTC" TIMESTAMP WITH TIME ZONE,
    ADD COLUMN IF NOT EXISTS "LeaseId" UUID,
    ADD COLUMN IF NOT EXISTS "LeaseOwnerNodeId" TEXT,
    ADD COLUMN IF NOT EXISTS "LeaseRequestId" UUID,
    ADD COLUMN IF NOT EXISTS "LeaseAcquiredUtc" TIMESTAMP WITH TIME ZONE,
    ADD COLUMN IF NOT EXISTS "LeaseExpiresUtc" TIMESTAMP WITH TIME ZONE,
    ADD COLUMN IF NOT EXISTS "Revision" BIGINT NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS "IsArchived" BOOLEAN NOT NULL DEFAULT FALSE,
    ADD COLUMN IF NOT EXISTS "ReplacedByStableId" UUID,
    ADD COLUMN IF NOT EXISTS "CreatedUtc" TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP,
    ADD COLUMN IF NOT EXISTS "UpdatedUtc" TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP,
    ADD COLUMN IF NOT EXISTS "AddedByTelegramId" BIGINT;

CREATE INDEX IF NOT EXISTS "IX_SearchProviderTokens_SearchProvider"
    ON "SearchProviderTokens" ("SearchProvider");
CREATE INDEX IF NOT EXISTS "IX_SearchProviderTokens_AddedByTelegramId"
    ON "SearchProviderTokens" ("AddedByTelegramId");

-- =============================================================================
-- SECTION 3A: PROVIDER INSTANCES, SCHEMA VERSION, READINESS/CUTOVER MARKERS
-- Additive multi-provider control-plane schema v5.
-- =============================================================================
CREATE TABLE IF NOT EXISTS "SearchProviderInstances" (
    "Id"                      BIGSERIAL PRIMARY KEY,
    "StableId"                UUID NOT NULL,
    "ProviderKind"             INTEGER NOT NULL,
    "DisplayName"              TEXT NOT NULL,
    "NormalizedScheme"         TEXT NOT NULL,
    "NormalizedHost"           TEXT NOT NULL,
    "NormalizedPort"           INTEGER NOT NULL,
    "NormalizedBasePath"       TEXT NOT NULL,
    "IsEnabled"                BOOLEAN NOT NULL DEFAULT TRUE,
    "AllowGlobalPublicSearch"  BOOLEAN NOT NULL DEFAULT FALSE,
    "MaxConcurrentOperations"  INTEGER NOT NULL,
    "SettingsVersion"          INTEGER NOT NULL,
    "SettingsJson"             TEXT NOT NULL DEFAULT '{}',
    "ApprovedByTelegramId"     BIGINT,
    "EndpointPolicyVersion"     INTEGER NOT NULL DEFAULT 0,
    "ApprovedEndpointIdentity"  TEXT,
    "EndpointApprovedAtUtc"     TIMESTAMP WITH TIME ZONE,
    "DevelopmentHttpAllowed"    BOOLEAN NOT NULL DEFAULT FALSE,
    "PrivateNetworkAllowlistJson" TEXT NOT NULL DEFAULT '[]',
    "CreatedUtc"               TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP,
    "UpdatedUtc"               TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP,
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

CREATE UNIQUE INDEX IF NOT EXISTS "UX_SearchProviderInstances_StableId"
    ON "SearchProviderInstances" ("StableId");
CREATE UNIQUE INDEX IF NOT EXISTS "UX_SearchProviderInstances_NormalizedIdentity"
    ON "SearchProviderInstances"
       ("ProviderKind", "NormalizedScheme", "NormalizedHost", "NormalizedPort", "NormalizedBasePath");

CREATE INDEX IF NOT EXISTS "IX_SearchProviderTokens_ProviderInstanceId"
    ON "SearchProviderTokens" ("ProviderInstanceId");

-- Every NOT NULL column is supplied explicitly so the seed never depends on a SQL DEFAULT
-- that an EF-created table does not carry for the endpoint-policy columns.
INSERT INTO "SearchProviderInstances"
    ("StableId", "ProviderKind", "DisplayName", "NormalizedScheme", "NormalizedHost",
     "NormalizedPort", "NormalizedBasePath", "IsEnabled", "AllowGlobalPublicSearch",
     "MaxConcurrentOperations", "SettingsVersion", "SettingsJson", "ApprovedByTelegramId",
     "EndpointPolicyVersion", "ApprovedEndpointIdentity", "EndpointApprovedAtUtc",
     "DevelopmentHttpAllowed", "PrivateNetworkAllowlistJson",
     "CreatedUtc", "UpdatedUtc")
VALUES
    ('8b3d0a4f-7a6d-4a4c-8cf0-0c6cb18f8b11', 1, 'GitHub', 'https', 'api.github.com',
     443, '/', TRUE, FALSE, 4, 1, '{}', NULL,
     0, NULL, NULL, FALSE, '[]', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP),
    ('9386a72d-01df-4bc6-9ad4-cb47a85f0a22', 2, 'GitLab', 'https', 'gitlab.com',
     443, '/api/v4', TRUE, FALSE, 4, 1, '{}', NULL,
     0, NULL, NULL, FALSE, '[]', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP),
    ('a1b2c3d4-1111-4a4c-8cf0-0c6cb18f8b31', 3, 'Sourcegraph', 'https', 'sourcegraph.com',
     443, '/.api', TRUE, FALSE, 4, 1, '{}', NULL,
     0, NULL, NULL, FALSE, '[]', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP),
    ('b2c3d4e5-2222-4a4c-8cf0-0c6cb18f8b32', 4, 'HuggingFace', 'https', 'huggingface.co',
     443, '/api', TRUE, FALSE, 4, 1, '{}', NULL,
     0, NULL, NULL, FALSE, '[]', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP),
    ('c3d4e5f6-3333-4a4c-8cf0-0c6cb18f8b33', 5, 'AzureDevOps', 'https', 'dev.azure.com',
     443, '/', TRUE, FALSE, 4, 1, '{}', NULL,
     0, NULL, NULL, FALSE, '[]', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP)
ON CONFLICT DO NOTHING;
 
UPDATE "SearchProviderTokens"
   SET "SearchProvider" = 1
 WHERE "SearchProvider" = 0;

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

-- Durable credential authorization (Task 3.2). AddedByTelegramId remains migration metadata only.
CREATE TABLE IF NOT EXISTS "CredentialGrants" (
    "Id"                  BIGSERIAL PRIMARY KEY,
    "CredentialId"        INTEGER NOT NULL,
    "Scope"               INTEGER NOT NULL,
    "TelegramPrincipalId" BIGINT,
    "CreatedUtc"          TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP,
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

-- Durable global-public-search consent, one row per exact Provider Instance (Task 14.1).
CREATE TABLE IF NOT EXISTS "PublicSearchConsents" (
    "Id"                  BIGSERIAL PRIMARY KEY,
    "ProviderInstanceId"  BIGINT NOT NULL,
    "IsActive"            BOOLEAN NOT NULL DEFAULT FALSE,
    "ActorTelegramId"     BIGINT NOT NULL,
    "OptedInUtc"          TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP,
    "OptedOutUtc"         TIMESTAMP WITH TIME ZONE,
    "CreatedUtc"          TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP,
    "UpdatedUtc"          TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT "CK_PublicSearchConsents_State"
        CHECK ("ActorTelegramId" > 0 AND
            (("IsActive" AND "OptedOutUtc" IS NULL) OR
             (NOT "IsActive" AND "OptedOutUtc" IS NOT NULL))),
    CONSTRAINT "FK_PublicSearchConsents_SearchProviderInstances_ProviderInstanceId"
        FOREIGN KEY ("ProviderInstanceId") REFERENCES "SearchProviderInstances" ("Id")
        ON DELETE RESTRICT
);

CREATE UNIQUE INDEX IF NOT EXISTS "UX_PublicSearchConsents_ProviderInstanceId"
    ON "PublicSearchConsents" ("ProviderInstanceId");

-- Immutable privileged-command audit evidence (Task 3.3).
CREATE TABLE IF NOT EXISTS "PrivilegedAuditRecords" (
    "Id"                BIGSERIAL PRIMARY KEY,
    "ActorTelegramId"   BIGINT NOT NULL,
    "Action"            INTEGER NOT NULL,
    "TargetStableId"    UUID NOT NULL,
    "OccurredUtc"       TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP,
    "Outcome"           INTEGER NOT NULL,
    "SanitizedReason"   TEXT NOT NULL,
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

CREATE TABLE IF NOT EXISTS "SchemaVersions" (
    "Id"         INTEGER PRIMARY KEY,
    "Version"    INTEGER NOT NULL,
    "AppliedUtc" TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT "CK_SchemaVersions_Version" CHECK ("Version" > 0)
);

CREATE TABLE IF NOT EXISTS "ReadinessMarkers" (
    "Id"            INTEGER PRIMARY KEY,
    "Name"          TEXT NOT NULL,
    "SchemaVersion" INTEGER NOT NULL,
    "IsReady"       BOOLEAN NOT NULL DEFAULT FALSE,
    "UpdatedUtc"    TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT "CK_ReadinessMarkers_SchemaVersion" CHECK ("SchemaVersion" > 0)
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_ReadinessMarkers_Name"
    ON "ReadinessMarkers" ("Name");

CREATE TABLE IF NOT EXISTS "CutoverMarkers" (
    "Id"         INTEGER PRIMARY KEY,
    "Name"       TEXT NOT NULL,
    "Version"    INTEGER NOT NULL,
    "IsComplete" BOOLEAN NOT NULL DEFAULT FALSE,
    "UpdatedUtc" TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT "CK_CutoverMarkers_Version" CHECK ("Version" > 0)
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_CutoverMarkers_Name"
    ON "CutoverMarkers" ("Name");

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

-- =============================================================================
-- SECTION 3B: WORK PIPELINE, PARTITIONS, AND QUERY OVERRIDES (Wave 7)
-- =============================================================================
CREATE TABLE IF NOT EXISTS "WorkItems" (
    "Id"                          BIGSERIAL PRIMARY KEY,
    "StableId"                    UUID NOT NULL DEFAULT gen_random_uuid(),
    "PrincipalScope"              INTEGER NOT NULL DEFAULT 0,
    "PrincipalTelegramId"         BIGINT,
    "ProviderInstanceId"          BIGINT NOT NULL,
    "ProviderKind"                INTEGER NOT NULL DEFAULT 0,
    "SearchQueryId"               INTEGER,
    "EffectiveQueryHash"          VARCHAR(64) NOT NULL DEFAULT '',
    "AdapterVersion"              VARCHAR(128) NOT NULL DEFAULT '',
    "QuerySnapshotJson"           TEXT NOT NULL DEFAULT '{}',
    "IsTerminal"                  BOOLEAN NOT NULL DEFAULT FALSE,
    "IsComplete"                  BOOLEAN NOT NULL DEFAULT FALSE,
    "CreatedUtc"                  TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP,
    "UpdatedUtc"                  TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE UNIQUE INDEX IF NOT EXISTS "UX_WorkItems_StableId"
    ON "WorkItems" ("StableId");
CREATE INDEX IF NOT EXISTS "IX_WorkItems_ProviderInstanceId"
    ON "WorkItems" ("ProviderInstanceId");

CREATE TABLE IF NOT EXISTS "WorkPartitions" (
    "Id"                          BIGSERIAL PRIMARY KEY,
    "StableId"                    UUID NOT NULL DEFAULT gen_random_uuid(),
    "WorkItemId"                  BIGINT NOT NULL,
    "PartitionKey"                VARCHAR(256) NOT NULL DEFAULT '',
    "Continuation"                TEXT,
    "ContinuationAdapterVersion"  VARCHAR(128),
    "LastSafeCheckpoint"          TEXT,
    "IsTerminal"                  BOOLEAN NOT NULL DEFAULT FALSE,
    "IsComplete"                  BOOLEAN NOT NULL DEFAULT FALSE,
    "CreatedUtc"                  TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP,
    "UpdatedUtc"                  TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE UNIQUE INDEX IF NOT EXISTS "UX_WorkPartitions_StableId"
    ON "WorkPartitions" ("StableId");
CREATE INDEX IF NOT EXISTS "IX_WorkPartitions_WorkItemId"
    ON "WorkPartitions" ("WorkItemId");

CREATE TABLE IF NOT EXISTS "SearchQueryOverrides" (
    "Id"                          BIGSERIAL PRIMARY KEY,
    "WorkItemId"                  BIGINT NOT NULL,
    "GenericQuery"                TEXT NOT NULL DEFAULT '',
    "NativeOverride"              TEXT,
    "SettingsJson"                TEXT NOT NULL DEFAULT '{}'
);

CREATE INDEX IF NOT EXISTS "IX_SearchQueryOverrides_WorkItemId"
    ON "SearchQueryOverrides" ("WorkItemId");

-- =============================================================================
-- SECTION 3C: NORMALIZED RESULTS, DEDUPLICATION, AND OUTBOX (Wave 7)
-- =============================================================================
CREATE TABLE IF NOT EXISTS "NormalizedResults" (
    "Id"                                    BIGSERIAL PRIMARY KEY,
    "ProviderKind"                          INTEGER NOT NULL DEFAULT 0,
    "ProviderInstanceStableId"              UUID NOT NULL,
    "RepositoryStableId"                    VARCHAR(512) NOT NULL DEFAULT '',
    "RepositoryOwner"                       VARCHAR(256),
    "RepositoryName"                        VARCHAR(256),
    "ImmutableRevisionOrEquivalentVersion"  VARCHAR(128) NOT NULL DEFAULT '',
    "NormalizedFilePath"                    VARCHAR(2048) NOT NULL DEFAULT '',
    "FileName"                              VARCHAR(512),
    "LineNumber"                            INTEGER,
    "Snippet"                               VARCHAR(4096),
    "ProvenanceUrl"                         VARCHAR(2048),
    "Branch"                                VARCHAR(256),
    "SearchQueryId"                         INTEGER,
    "WorkItemId"                            BIGINT,
    "WorkPartitionId"                       BIGINT,
    "DiscoveredUtc"                         TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP,
    "ProvenanceJson"                        TEXT NOT NULL DEFAULT '{}',
    "ApiKeyId"                              INTEGER
);

CREATE INDEX IF NOT EXISTS "IX_NormalizedResults_WorkItemId"
    ON "NormalizedResults" ("WorkItemId");

CREATE TABLE IF NOT EXISTS "ResultDeduplicationRecords" (
    "Id"                                    BIGSERIAL PRIMARY KEY,
    "ProviderInstanceStableId"              VARCHAR(512) NOT NULL DEFAULT '',
    "RepositoryStableId"                    VARCHAR(512) NOT NULL DEFAULT '',
    "ImmutableRevisionOrEquivalentVersion"  VARCHAR(128) NOT NULL DEFAULT '',
    "NormalizedFilePath"                    VARCHAR(2048) NOT NULL DEFAULT '',
    "NormalizedResultId"                    BIGINT NOT NULL,
    "FirstDiscoveredUtc"                    TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP,
    "LastSeenUtc"                           TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX IF NOT EXISTS "IX_ResultDeduplicationRecords_Lookup"
    ON "ResultDeduplicationRecords" ("ProviderInstanceStableId", "RepositoryStableId", "ImmutableRevisionOrEquivalentVersion", "NormalizedFilePath");

CREATE TABLE IF NOT EXISTS "ResultOutboxRecords" (
    "Id"                          BIGSERIAL PRIMARY KEY,
    "NormalizedResultId"          BIGINT NOT NULL,
    "EventKind"                   VARCHAR(64) NOT NULL DEFAULT '',
    "PayloadJson"                 TEXT NOT NULL DEFAULT '{}',
    "IsProcessed"                 BOOLEAN NOT NULL DEFAULT FALSE,
    "CreatedUtc"                  TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP,
    "ProcessedUtc"                TIMESTAMP WITH TIME ZONE
);

CREATE INDEX IF NOT EXISTS "IX_ResultOutboxRecords_IsProcessed"
    ON "ResultOutboxRecords" ("IsProcessed");

-- =============================================================================
-- SECTION 3D: CREDENTIAL CLAIM RECORDS AND OPERATION SLOTS (Wave 8 & 9)
-- =============================================================================
CREATE TABLE IF NOT EXISTS "CredentialClaimRecords" (
    "Id"                          BIGSERIAL PRIMARY KEY,
    "PrincipalScope"              INTEGER NOT NULL DEFAULT 0,
    "PrincipalTelegramId"         BIGINT,
    "RequestId"                   UUID NOT NULL,
    "CredentialStableId"          UUID NOT NULL,
    "ProviderInstanceStableId"    UUID NOT NULL,
    "WorkItemId"                  BIGINT NOT NULL,
    "LeaseId"                     UUID NOT NULL,
    "LeaseOwnerNodeId"            VARCHAR(256),
    "LeaseAcquiredUtc"            TIMESTAMP WITH TIME ZONE NOT NULL,
    "LeaseExpiresUtc"             TIMESTAMP WITH TIME ZONE NOT NULL,
    "CredentialRevision"          BIGINT NOT NULL DEFAULT 0,
    "TerminalOutcome"             VARCHAR(64),
    "IsTerminal"                  BOOLEAN NOT NULL DEFAULT FALSE,
    "TerminalizedUtc"             TIMESTAMP WITH TIME ZONE,
    "CreatedUtc"                  TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP,
    "UpdatedUtc"                  TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX IF NOT EXISTS "IX_CredentialClaimRecords_RequestId"
    ON "CredentialClaimRecords" ("RequestId");
CREATE INDEX IF NOT EXISTS "IX_CredentialClaimRecords_LeaseId"
    ON "CredentialClaimRecords" ("LeaseId");
CREATE INDEX IF NOT EXISTS "IX_CredentialClaimRecords_PrincipalScope"
    ON "CredentialClaimRecords" ("PrincipalScope", "PrincipalTelegramId", "RequestId");

CREATE TABLE IF NOT EXISTS "OperationSlots" (
    "Id"                          BIGSERIAL PRIMARY KEY,
    "SlotId"                      UUID NOT NULL DEFAULT gen_random_uuid(),
    "ProviderInstanceId"          BIGINT NOT NULL,
    "RequestId"                   UUID NOT NULL,
    "PrincipalScope"              INTEGER NOT NULL DEFAULT 0,
    "PrincipalTelegramId"         BIGINT,
    "WorkItemId"                  BIGINT NOT NULL,
    "PartitionKey"                VARCHAR(256) NOT NULL DEFAULT '',
    "AcquiredUtc"                 TIMESTAMP WITH TIME ZONE NOT NULL,
    "ExpiresUtc"                  TIMESTAMP WITH TIME ZONE NOT NULL,
    "Revision"                    BIGINT NOT NULL DEFAULT 0,
    "IsTerminal"                  BOOLEAN NOT NULL DEFAULT FALSE,
    "TerminalizedUtc"             TIMESTAMP WITH TIME ZONE,
    "CreatedUtc"                  TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP,
    "UpdatedUtc"                  TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE UNIQUE INDEX IF NOT EXISTS "UX_OperationSlots_SlotId"
    ON "OperationSlots" ("SlotId");
CREATE INDEX IF NOT EXISTS "IX_OperationSlots_ActiveCapacity"
    ON "OperationSlots" ("ProviderInstanceId", "ExpiresUtc", "IsTerminal");

-- =============================================================================
-- SECTION 4: API KEYS
-- =============================================================================
-- ApiType integer reference (matches ApiTypeEnum in CommonEnums.cs):
--   Unknown       = -99
--   OpenAI        = 100   AnthropicClaude = 120   GoogleAI      = 130
--   Cohere        = 140   HuggingFace     = 150   StabilityAI   = 160
--   Replicate     = 180   TogetherAI      = 190   DeepSeek      = 198
--   ElevenLabs    = 199   XAI             = 207   FireworksAI   = 208
--   KlingAI       = 210   PolloAI         = 215   RunwayML      = 220
--   A2E           = 230   PiAPI           = 240   Groq          = 250
--   MistralAI     = 260   OpenRouter      = 270   Perplexity    = 280
--   Cerebras      = 290   VoyageAI        = 300   AWSBedrock    = 310
--   AzureOpenAI   = 320   AWSIAM          = 330
--   AI21Labs      = 350   AssemblyAI      = 360
--   Deepgram      = 370   JinaAI          = 380   Anyscale      = 390
--   Upstage       = 400   LeonardoAI      = 405   FalAI         = 415
--   RunPod        = 420   Tavily          = 422   SarvamAI      = 424
--   Unsplash      = 426   SendGrid        = 410   Mailgun       = 425
--   Slack         = 430   Facebook        = 440   GoogleOAuth   = 450
--   Stripe        = 460   TikTok          = 470   GcpHmac       = 480
--   GitHubToken   = 490   ServerCredential= 500   Mapbox        = 600
--   WeatherApi    = 610
--
-- Status integer reference (matches ApiStatusEnum):
--   Unverified    = -99   Invalid = 0   Valid = 1   Error = 6   ValidNoCredits = 7
-- =============================================================================
CREATE TABLE IF NOT EXISTS "APIKeys" (
    "Id"                    SERIAL PRIMARY KEY,
    "ApiKey"                TEXT NOT NULL DEFAULT '',
    "Status"                INTEGER DEFAULT -99,
    "ApiType"               INTEGER DEFAULT -99,
    "SearchProvider"        INTEGER DEFAULT 0,
    "LastCheckedUTC"        TIMESTAMP WITH TIME ZONE,
    "FirstFoundUTC"         TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP,
    "LastFoundUTC"          TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP,
    "TimesDisplayed"        INTEGER DEFAULT 0,
    "ErrorCount"            INTEGER DEFAULT 0,
    "ValidationResponse"    TEXT,
    "Balance"               TEXT,
    "AccountTier"           TEXT,
    "DiscoveredByTelegramId" BIGINT,
    "Metadata"              TEXT
);

ALTER TABLE "APIKeys" ADD COLUMN IF NOT EXISTS "ApiKey"                TEXT NOT NULL DEFAULT '';
ALTER TABLE "APIKeys" ADD COLUMN IF NOT EXISTS "Status"                INTEGER DEFAULT -99;
ALTER TABLE "APIKeys" ADD COLUMN IF NOT EXISTS "ApiType"               INTEGER DEFAULT -99;
ALTER TABLE "APIKeys" ADD COLUMN IF NOT EXISTS "SearchProvider"        INTEGER DEFAULT 0;
ALTER TABLE "APIKeys" ADD COLUMN IF NOT EXISTS "LastCheckedUTC"        TIMESTAMP WITH TIME ZONE;
ALTER TABLE "APIKeys" ADD COLUMN IF NOT EXISTS "FirstFoundUTC"         TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP;
ALTER TABLE "APIKeys" ADD COLUMN IF NOT EXISTS "LastFoundUTC"          TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP;
ALTER TABLE "APIKeys" ADD COLUMN IF NOT EXISTS "TimesDisplayed"        INTEGER DEFAULT 0;
ALTER TABLE "APIKeys" ADD COLUMN IF NOT EXISTS "ErrorCount"            INTEGER DEFAULT 0;
ALTER TABLE "APIKeys" ADD COLUMN IF NOT EXISTS "ValidationResponse"    TEXT;
ALTER TABLE "APIKeys" ADD COLUMN IF NOT EXISTS "Balance"               TEXT;
ALTER TABLE "APIKeys" ADD COLUMN IF NOT EXISTS "AccountTier"           TEXT;
ALTER TABLE "APIKeys" ADD COLUMN IF NOT EXISTS "DiscoveredByTelegramId" BIGINT;
ALTER TABLE "APIKeys" ADD COLUMN IF NOT EXISTS "Metadata"              TEXT;

-- AWS IAM-specific metadata columns
ALTER TABLE "APIKeys" ADD COLUMN IF NOT EXISTS "AwsAccountId"          TEXT;
ALTER TABLE "APIKeys" ADD COLUMN IF NOT EXISTS "AwsUserArn"            TEXT;
ALTER TABLE "APIKeys" ADD COLUMN IF NOT EXISTS "AwsUserId"             TEXT;
ALTER TABLE "APIKeys" ADD COLUMN IF NOT EXISTS "AwsCredentialType"     TEXT;
ALTER TABLE "APIKeys" ADD COLUMN IF NOT EXISTS "AwsAttachedPolicies"   TEXT;
ALTER TABLE "APIKeys" ADD COLUMN IF NOT EXISTS "AwsRiskLevel"          TEXT;
ALTER TABLE "APIKeys" ADD COLUMN IF NOT EXISTS "AwsIsRootAccount"      BOOLEAN DEFAULT FALSE;

CREATE UNIQUE INDEX IF NOT EXISTS "IX_APIKeys_ApiKey"
    ON "APIKeys" ("ApiKey");
CREATE INDEX IF NOT EXISTS "IX_APIKeys_Status_ApiType"
    ON "APIKeys" ("Status", "ApiType");
CREATE INDEX IF NOT EXISTS "IX_APIKeys_Status"
    ON "APIKeys" ("Status");
CREATE INDEX IF NOT EXISTS "IX_APIKeys_LastCheckedUTC"
    ON "APIKeys" ("LastCheckedUTC");
CREATE INDEX IF NOT EXISTS "IX_APIKeys_DiscoveredByTelegramId"
    ON "APIKeys" ("DiscoveredByTelegramId");

-- AWS IAM-specific indexes
CREATE INDEX IF NOT EXISTS "IX_APIKeys_AwsAccountId"
    ON "APIKeys" ("AwsAccountId") WHERE "AwsAccountId" IS NOT NULL;
CREATE INDEX IF NOT EXISTS "IX_APIKeys_AwsRiskLevel"
    ON "APIKeys" ("AwsRiskLevel") WHERE "AwsRiskLevel" IS NOT NULL;

-- =============================================================================
-- SECTION 5: REPO REFERENCES
-- =============================================================================
CREATE TABLE IF NOT EXISTS "RepoReferences" (
    "Id"              SERIAL PRIMARY KEY,
    "APIKeyId"        BIGINT NOT NULL DEFAULT 0,
    "RepoURL"         TEXT,
    "RepoOwner"       TEXT,
    "RepoName"        TEXT,
    "RepoDescription" TEXT,
    "RepoId"          BIGINT DEFAULT 0,
    "FileURL"         TEXT,
    "FileName"        TEXT,
    "FilePath"        TEXT,
    "FileSHA"         TEXT,
    "ApiContentUrl"   TEXT,
    "CodeContext"     TEXT,
    "LineNumber"      INTEGER DEFAULT 0,
    "SearchQueryId"   BIGINT DEFAULT 0,
    "FoundUTC"        TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP,
    "Provider"        TEXT,
    "Branch"          TEXT DEFAULT 'main',
    "RepoPushedAt"    TIMESTAMP WITH TIME ZONE
);

ALTER TABLE "RepoReferences" ADD COLUMN IF NOT EXISTS "APIKeyId"       BIGINT NOT NULL DEFAULT 0;
ALTER TABLE "RepoReferences" ADD COLUMN IF NOT EXISTS "RepoURL"        TEXT;
ALTER TABLE "RepoReferences" ADD COLUMN IF NOT EXISTS "RepoOwner"      TEXT;
ALTER TABLE "RepoReferences" ADD COLUMN IF NOT EXISTS "RepoName"       TEXT;
ALTER TABLE "RepoReferences" ADD COLUMN IF NOT EXISTS "RepoDescription" TEXT;
ALTER TABLE "RepoReferences" ADD COLUMN IF NOT EXISTS "RepoId"         BIGINT DEFAULT 0;
ALTER TABLE "RepoReferences" ADD COLUMN IF NOT EXISTS "FileURL"        TEXT;
ALTER TABLE "RepoReferences" ADD COLUMN IF NOT EXISTS "FileName"       TEXT;
ALTER TABLE "RepoReferences" ADD COLUMN IF NOT EXISTS "FilePath"       TEXT;
ALTER TABLE "RepoReferences" ADD COLUMN IF NOT EXISTS "FileSHA"        TEXT;
ALTER TABLE "RepoReferences" ADD COLUMN IF NOT EXISTS "ApiContentUrl"  TEXT;
ALTER TABLE "RepoReferences" ADD COLUMN IF NOT EXISTS "CodeContext"    TEXT;
ALTER TABLE "RepoReferences" ADD COLUMN IF NOT EXISTS "LineNumber"     INTEGER DEFAULT 0;
ALTER TABLE "RepoReferences" ADD COLUMN IF NOT EXISTS "SearchQueryId"  BIGINT DEFAULT 0;
ALTER TABLE "RepoReferences" ADD COLUMN IF NOT EXISTS "FoundUTC"       TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP;
ALTER TABLE "RepoReferences" ADD COLUMN IF NOT EXISTS "Provider"       TEXT;
ALTER TABLE "RepoReferences" ADD COLUMN IF NOT EXISTS "Branch"         TEXT DEFAULT 'main';
ALTER TABLE "RepoReferences" ADD COLUMN IF NOT EXISTS "RepoPushedAt"    TIMESTAMP WITH TIME ZONE;

CREATE INDEX IF NOT EXISTS "IX_RepoReferences_ApiKeyId"
    ON "RepoReferences" ("APIKeyId");

-- =============================================================================
-- SECTION 6: DEEP SEARCH PROGRESS
-- Dropped and recreated to ensure clean state on fresh deployments.
-- =============================================================================
DROP TABLE IF EXISTS "DeepSearchProgress" CASCADE;

CREATE TABLE "DeepSearchProgress" (
    "Id"               SERIAL PRIMARY KEY,
    "SearchQueryId"    BIGINT NOT NULL,
    "PartitionType"    TEXT NOT NULL,
    "PartitionValue"   TEXT NOT NULL,
    "LastPageSearched" INTEGER DEFAULT 0,
    "TotalResultsFound" INTEGER DEFAULT 0,
    "IsCompleted"      BOOLEAN DEFAULT FALSE,
    "LastSearchedUTC"  TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP
);

CREATE UNIQUE INDEX "IX_DeepSearchProgress_Query_Partition"
    ON "DeepSearchProgress" ("SearchQueryId", "PartitionType", "PartitionValue");
CREATE INDEX "IX_DeepSearchProgress_IsCompleted"
    ON "DeepSearchProgress" ("IsCompleted");

-- =============================================================================
-- SECTION 7: APPLICATION SETTINGS
-- =============================================================================
CREATE TABLE IF NOT EXISTS "ApplicationSettings" (
    "Key"         TEXT PRIMARY KEY,
    "Value"       TEXT NOT NULL,
    "Description" TEXT
);

ALTER TABLE "ApplicationSettings" ADD COLUMN IF NOT EXISTS "Description" TEXT;

-- =============================================================================
-- SECTION 8: SEED DEFAULT SEARCH QUERIES (Idempotent)
-- These are the GitHub Code Search strings the scraper uses.
-- Each string must appear literally in source files on GitHub.
-- =============================================================================
INSERT INTO "SearchQueries" ("Query", "IsEnabled", "LastSearchUTC")
SELECT v.q, TRUE, CURRENT_TIMESTAMP
FROM (VALUES
    -- ── OpenAI ──────────────────────────────────────────────────────────────
    ('sk-proj-'),
    ('sk-svcacct-'),
    ('OPENAI_API_KEY'),

    -- ── Anthropic ────────────────────────────────────────────────────────────
    ('sk-ant-api'),
    ('ANTHROPIC_API_KEY'),

    -- ── Google AI / Gemini ───────────────────────────────────────────────────
    ('AIzaSy'),
    ('GOOGLE_API_KEY'),
    ('GEMINI_API_KEY'),
    ('AQ.Ab'),
    ('AQ.'),

    -- ── DeepSeek ─────────────────────────────────────────────────────────────
    ('DEEPSEEK_API_KEY'),

    -- ── Kling AI ─────────────────────────────────────────────────────────────
    ('KLING_ACCESS_KEY'),
    ('KLING_API_KEY'),

    -- ── Pollo AI ─────────────────────────────────────────────────────────────
    ('POLLO_API_KEY'),
    ('pollo_'),

    -- ── Runway ML ────────────────────────────────────────────────────────────
    ('RUNWAYML_API_SECRET'),
    ('RUNWAY_API_KEY'),

    -- ── Cohere ───────────────────────────────────────────────────────────────
    ('COHERE_API_KEY'),
    ('CO_API_KEY'),

    -- ── ElevenLabs ───────────────────────────────────────────────────────────
    ('ELEVENLABS_API_KEY'),
    ('ELEVEN_API_KEY'),
    ('xi-api-key'),

    -- ── Stability AI ─────────────────────────────────────────────────────────
    ('STABILITY_API_KEY'),

    -- ── Together AI ──────────────────────────────────────────────────────────
    ('TOGETHER_API_KEY'),

    -- ── xAI / Grok ───────────────────────────────────────────────────────────
    ('XAI_API_KEY'),
    ('xai-'),

    -- ── Replicate ────────────────────────────────────────────────────────────
    ('REPLICATE_API_TOKEN'),
    ('r8_'),

    -- ── Fireworks AI ─────────────────────────────────────────────────────────
    ('FIREWORKS_API_KEY'),
    ('fw_'),

    -- ── HuggingFace ──────────────────────────────────────────────────────────
    ('HUGGINGFACE_API_KEY'),
    ('HF_TOKEN'),
    ('hf_'),

    -- ── A2E AI ───────────────────────────────────────────────────────────────
    ('A2E_API_KEY'),
    ('A2E_SECRET'),

    -- ── PiAPI ────────────────────────────────────────────────────────────────
    ('PIAPI_KEY'),

    -- ── Groq ─────────────────────────────────────────────────────────────────
    ('GROQ_API_KEY'),
    ('gsk_'),

    -- ── Mistral AI ───────────────────────────────────────────────────────────
    ('MISTRAL_API_KEY'),

    -- ── OpenRouter ───────────────────────────────────────────────────────────
    ('OPENROUTER_API_KEY'),
    ('sk-or-v1-'),

    -- ── Perplexity ───────────────────────────────────────────────────────────
    ('PERPLEXITY_API_KEY'),
    ('PPLX_API_KEY'),
    ('pplx-'),

    -- ── Cerebras ─────────────────────────────────────────────────────────────
    ('CEREBRAS_API_KEY'),
    ('csk-'),

    -- ── Voyage AI ────────────────────────────────────────────────────────────
    ('VOYAGE_API_KEY'),
    ('VOYAGEAI_API_KEY'),

    -- ── AWS Bedrock ──────────────────────────────────────────────────────────
    ('AWS_BEARER_TOKEN_BEDROCK'),
    ('BEDROCK_API_KEY'),

    -- ── Azure OpenAI ─────────────────────────────────────────────────────────
    ('AZURE_OPENAI_API_KEY'),
    ('AZURE_OPENAI_KEY'),

    -- ── AWS IAM ──────────────────────────────────────────────────────────────
    ('AKIA'),
    ('ASIA'),
    ('AWS_ACCESS_KEY_ID'),
    ('AWS_SECRET_ACCESS_KEY'),
    ('aws_access_key_id'),
    ('aws_secret_access_key'),

    -- ── AI21 Labs ─────────────────────────────────────────────────────────────
    ('AI21_API_KEY'),
    ('AI21LABS_API_KEY'),

    -- ── AssemblyAI ────────────────────────────────────────────────────────────
    ('ASSEMBLYAI_API_KEY'),
    ('ASSEMBLY_AI_API_KEY'),

    -- ── Deepgram ──────────────────────────────────────────────────────────────
    ('DEEPGRAM_API_KEY'),
    ('DG_API_KEY'),

    -- ── Jina AI ───────────────────────────────────────────────────────────────
    ('JINA_API_KEY'),
    ('jina_'),

    -- ── Upstage (Solar) ───────────────────────────────────────────────────────
    ('UPSTAGE_API_KEY'),
    ('SOLAR_API_KEY'),
    ('up_'),

    -- ── Leonardo.ai ───────────────────────────────────────────────────────────
    ('LEONARDO_API_KEY'),
    ('LEONARDO_AI_API_KEY'),

    -- ── Fal.ai ────────────────────────────────────────────────────────────────
    ('FAL_KEY'),
    ('FAL_API_KEY'),

    -- ── RunPod ────────────────────────────────────────────────────────────────
    ('RUNPOD_API_KEY'),
    ('rpa_'),

    -- ── Tavily AI Search ──────────────────────────────────────────────────────
    ('TAVILY_API_KEY'),
    ('tvly-'),

    -- ── Sarvam AI (Indic GenAI) ───────────────────────────────────────────────
    ('SARVAM_API_KEY'),
    ('SARVAM_KEY'),
    ('api-subscription-key sarvam'),

    -- ── Unsplash (Stock Media / Visual API) ──────────────────────────────────
    ('UNSPLASH_ACCESS_KEY'),
    ('UNSPLASH_CLIENT_ID'),
    ('UNSPLASH_API_KEY'),

    -- ── WeatherAPI ────────────────────────────────────────────────────────────
    ('WEATHERAPI_KEY'),
    ('weatherapi.com'),

    -- ── Server Credentials (Requirement 17) ──────────────────────────────────
    ('ssh '),
    ('ftp://'),
    ('mysql://'),
    ('postgresql://'),
    ('mongodb://'),
    ('redis://'),
    ('-----BEGIN RSA PRIVATE KEY-----'),
    ('KUBERNETES_SERVICE_HOST'),
    ('DOCKER_HOST'),
    ('rdp://'),
    ('vnc://'),
    ('mstsc'),
    ('TeamViewer'),
    ('filename:.rdp'),
    ('WinRM'),
    ('smtp://'),
    ('SMTP_HOST'),
    ('imap://'),
    ('pop3://'),
    ('cPanel'),
    ('WHM_USER'),
    ('PLESK_'),
    ('filename:.bash_history'),
    ('filename:id_rsa'),
    ('extension:env'),
    ('filename:cloudbuild.yaml'),
    ('filename:Dockerfile'),
    ('filename:docker-compose.yml'),
    ('STRIPE_SECRET'),
    ('STRIPE_WEBHOOK_SECRET'),
    ('sk_live_'),
    ('sk_test_'),
    ('sk_org_'),
    ('rk_live_'),
    ('whsec_'),
    ('pk_live_'),
    ('TIKTOK_CLIENT_ID'),
    ('TIKTOK_CLIENT_SECRET'),
    ('GOOGLE_CLOUD_HMAC_ACCESS_KEY_ID'),
    ('GOOGLE_CLOUD_HMAC_SECRET_ACCESS_KEY'),
    ('GOOG1E')

) AS v(q)
WHERE NOT EXISTS (
    SELECT 1 FROM "SearchQueries" WHERE "Query" = v.q
);

-- =============================================================================
-- SECTION 9: FOREIGN KEY CONSTRAINTS
-- Added after all tables exist to avoid ordering issues.
-- =============================================================================
DO $$
BEGIN
    -- RepoReferences → APIKeys
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.table_constraints
        WHERE constraint_name = 'FK_RepoReferences_APIKeys_APIKeyId'
    ) THEN
        ALTER TABLE "RepoReferences"
            ADD CONSTRAINT "FK_RepoReferences_APIKeys_APIKeyId"
            FOREIGN KEY ("APIKeyId") REFERENCES "APIKeys" ("Id") ON DELETE CASCADE;
    END IF;

    -- DeepSearchProgress → SearchQueries
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.table_constraints
        WHERE constraint_name = 'FK_DeepSearchProgress_SearchQueries_SearchQueryId'
    ) THEN
        ALTER TABLE "DeepSearchProgress"
            ADD CONSTRAINT "FK_DeepSearchProgress_SearchQueries_SearchQueryId"
            FOREIGN KEY ("SearchQueryId") REFERENCES "SearchQueries" ("Id") ON DELETE CASCADE;
    END IF;
END $$;

-- =============================================================================
-- SECTION 10: SERVER CREDENTIALS
-- =============================================================================
CREATE TABLE IF NOT EXISTS "ServerCredentials" (
    "Id"                   SERIAL PRIMARY KEY,
    "CredentialType"       VARCHAR(50)  NOT NULL,
    "Host"                 VARCHAR(255) NOT NULL,
    "Port"                 INTEGER      NOT NULL DEFAULT 0,
    "Username"             VARCHAR(255),
    "Password"             TEXT,
    "Domain"               VARCHAR(255),
    "NetworkStatus"        VARCHAR(50)  NOT NULL DEFAULT 'Unknown',
    "AuthenticationStatus" VARCHAR(50)  NOT NULL DEFAULT 'Untested',
    "ServerMetadata"       JSONB        NOT NULL DEFAULT '{}',
    "GeolocationData"      JSONB        NOT NULL DEFAULT '{}',
    "OSINTData"            JSONB        NOT NULL DEFAULT '{}',
    "RiskLevel"            VARCHAR(20)  NOT NULL DEFAULT 'Low',
    "IsHoneypot"           BOOLEAN      NOT NULL DEFAULT FALSE,
    "SourceRepository"     VARCHAR(500),
    "SourceFilePath"       VARCHAR(500),
    "SurroundingContext"   TEXT,
    "EntropyScore"         DOUBLE PRECISION DEFAULT 0,
    "DiscoveredAt"         TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    "LastVerifiedAt"       TIMESTAMPTZ,
    CONSTRAINT "uq_server_cred" UNIQUE ("Host", "Port", "Username", "CredentialType")
);

-- Idempotent column additions
ALTER TABLE "ServerCredentials" ADD COLUMN IF NOT EXISTS "CredentialType"       VARCHAR(50)  NOT NULL DEFAULT 'Unknown';
ALTER TABLE "ServerCredentials" ADD COLUMN IF NOT EXISTS "Host"                 VARCHAR(255) NOT NULL DEFAULT 'Unknown';
ALTER TABLE "ServerCredentials" ADD COLUMN IF NOT EXISTS "Port"                 INTEGER      NOT NULL DEFAULT 0;
ALTER TABLE "ServerCredentials" ADD COLUMN IF NOT EXISTS "Username"             VARCHAR(255);
ALTER TABLE "ServerCredentials" ADD COLUMN IF NOT EXISTS "Password"             TEXT;
ALTER TABLE "ServerCredentials" ADD COLUMN IF NOT EXISTS "Domain"               VARCHAR(255);
ALTER TABLE "ServerCredentials" ADD COLUMN IF NOT EXISTS "NetworkStatus"        VARCHAR(50)  NOT NULL DEFAULT 'Unknown';
ALTER TABLE "ServerCredentials" ADD COLUMN IF NOT EXISTS "AuthenticationStatus" VARCHAR(50)  NOT NULL DEFAULT 'Untested';
ALTER TABLE "ServerCredentials" ADD COLUMN IF NOT EXISTS "ServerMetadata"       JSONB        NOT NULL DEFAULT '{}';
ALTER TABLE "ServerCredentials" ADD COLUMN IF NOT EXISTS "GeolocationData"      JSONB        NOT NULL DEFAULT '{}';
ALTER TABLE "ServerCredentials" ADD COLUMN IF NOT EXISTS "OSINTData"            JSONB        NOT NULL DEFAULT '{}';
ALTER TABLE "ServerCredentials" ADD COLUMN IF NOT EXISTS "RiskLevel"            VARCHAR(20)  NOT NULL DEFAULT 'Low';
ALTER TABLE "ServerCredentials" ADD COLUMN IF NOT EXISTS "IsHoneypot"           BOOLEAN      NOT NULL DEFAULT FALSE;
ALTER TABLE "ServerCredentials" ADD COLUMN IF NOT EXISTS "SourceRepository"     VARCHAR(500);
ALTER TABLE "ServerCredentials" ADD COLUMN IF NOT EXISTS "SourceFilePath"       VARCHAR(500);
ALTER TABLE "ServerCredentials" ADD COLUMN IF NOT EXISTS "SurroundingContext"   TEXT;
ALTER TABLE "ServerCredentials" ADD COLUMN IF NOT EXISTS "EntropyScore"         DOUBLE PRECISION DEFAULT 0;
ALTER TABLE "ServerCredentials" ADD COLUMN IF NOT EXISTS "DiscoveredAt"         TIMESTAMPTZ  NOT NULL DEFAULT NOW();
ALTER TABLE "ServerCredentials" ADD COLUMN IF NOT EXISTS "LastVerifiedAt"       TIMESTAMPTZ;

-- Indexes
CREATE INDEX IF NOT EXISTS "idx_sc_type"        ON "ServerCredentials" ("CredentialType");
CREATE INDEX IF NOT EXISTS "idx_sc_risk"        ON "ServerCredentials" ("RiskLevel");
CREATE INDEX IF NOT EXISTS "idx_sc_auth_status" ON "ServerCredentials" ("AuthenticationStatus");
CREATE INDEX IF NOT EXISTS "idx_sc_honeypot"    ON "ServerCredentials" ("IsHoneypot");

-- =============================================================================
-- DONE ✅
-- Run this script once on a fresh Supabase database.
-- The app's DatabaseService will handle any future schema migrations automatically.
-- =============================================================================

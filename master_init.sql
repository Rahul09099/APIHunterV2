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
    "Id"                   SERIAL PRIMARY KEY,
    "Query"                TEXT NOT NULL DEFAULT '',
    "IsEnabled"            BOOLEAN DEFAULT TRUE,
    "SearchResultsCount"   INTEGER DEFAULT 0,
    "LastSearchUTC"        TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP,
    "LastDeepSearchDateUTC" TIMESTAMP WITH TIME ZONE
);

ALTER TABLE "SearchQueries" ADD COLUMN IF NOT EXISTS "Query"                TEXT NOT NULL DEFAULT '';
ALTER TABLE "SearchQueries" ADD COLUMN IF NOT EXISTS "IsEnabled"            BOOLEAN DEFAULT TRUE;
ALTER TABLE "SearchQueries" ADD COLUMN IF NOT EXISTS "SearchResultsCount"   INTEGER DEFAULT 0;
ALTER TABLE "SearchQueries" ADD COLUMN IF NOT EXISTS "LastSearchUTC"        TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP;
ALTER TABLE "SearchQueries" ADD COLUMN IF NOT EXISTS "LastDeepSearchDateUTC" TIMESTAMP WITH TIME ZONE;

CREATE INDEX IF NOT EXISTS "IX_SearchQueries_IsEnabled_LastSearchUTC"
    ON "SearchQueries" ("IsEnabled", "LastSearchUTC");

-- =============================================================================
-- SECTION 3: SEARCH PROVIDER TOKENS
-- =============================================================================
CREATE TABLE IF NOT EXISTS "SearchProviderTokens" (
    "Id"                SERIAL PRIMARY KEY,
    "Token"             TEXT NOT NULL DEFAULT '',
    "SearchProvider"    INTEGER DEFAULT 0,
    "IsEnabled"         BOOLEAN DEFAULT TRUE,
    "AddedByTelegramId" BIGINT,
    "LastUsedUTC"       TIMESTAMP WITH TIME ZONE
);

ALTER TABLE "SearchProviderTokens" ADD COLUMN IF NOT EXISTS "Token"             TEXT NOT NULL DEFAULT '';
ALTER TABLE "SearchProviderTokens" ADD COLUMN IF NOT EXISTS "SearchProvider"    INTEGER DEFAULT 0;
ALTER TABLE "SearchProviderTokens" ADD COLUMN IF NOT EXISTS "IsEnabled"         BOOLEAN DEFAULT TRUE;
ALTER TABLE "SearchProviderTokens" ADD COLUMN IF NOT EXISTS "AddedByTelegramId" BIGINT;
ALTER TABLE "SearchProviderTokens" ADD COLUMN IF NOT EXISTS "LastUsedUTC"       TIMESTAMP WITH TIME ZONE;

CREATE INDEX IF NOT EXISTS "IX_SearchProviderTokens_SearchProvider"
    ON "SearchProviderTokens" ("SearchProvider");
CREATE INDEX IF NOT EXISTS "IX_SearchProviderTokens_AddedByTelegramId"
    ON "SearchProviderTokens" ("AddedByTelegramId");

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
--   RunPod        = 420
--   SendGrid      = 410   Mailgun         = 425   Slack         = 430
--   Mapbox        = 600
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
    "Id"            SERIAL PRIMARY KEY,
    "APIKeyId"      BIGINT NOT NULL DEFAULT 0,
    "RepoURL"       TEXT,
    "RepoOwner"     TEXT,
    "RepoName"      TEXT,
    "RepoDescription" TEXT,
    "RepoId"        BIGINT DEFAULT 0,
    "FileURL"       TEXT,
    "FileName"      TEXT,
    "FilePath"      TEXT,
    "FileSHA"       TEXT,
    "ApiContentUrl" TEXT,
    "CodeContext"   TEXT,
    "LineNumber"    INTEGER DEFAULT 0,
    "SearchQueryId" BIGINT DEFAULT 0,
    "FoundUTC"      TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP,
    "Provider"      TEXT,
    "Branch"        TEXT DEFAULT 'main'
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
    ('rpa_')

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
-- DONE ✅
-- Run this script once on a fresh Supabase database.
-- The app's DatabaseService will handle any future schema migrations automatically.
-- =============================================================================

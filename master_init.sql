-- 🚀 APIHunterV2 MASTER INITIALIZATION SCRIPT (FOR SUPABASE / POSTGRESQL)
-- This script creates the entire database schema and seeds default data from scratch.
-- It is safe to run multiple times (idempotent).

-- -----------------------------------------------------------------------------
-- 1. TELEGRAM SUBSCRIBERS
-- -----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS "TelegramSubscribers" (
    "TelegramId" BIGINT PRIMARY KEY,
    "Username" TEXT,
    "SubscriptionExpiryUtc" TIMESTAMP WITH TIME ZONE DEFAULT '1970-01-01 00:00:00+00',
    "IsAdmin" BOOLEAN DEFAULT FALSE,
    "CreatedAtUtc" TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP,
    "NodeToken" TEXT,
    "LastNodeHeartbeatUtc" TIMESTAMP WITH TIME ZONE
);

-- Ensure columns exist if table was partially created
ALTER TABLE "TelegramSubscribers" DROP COLUMN IF EXISTS "SubscribedAtUTC";
ALTER TABLE "TelegramSubscribers" ADD COLUMN IF NOT EXISTS "Username" TEXT;
ALTER TABLE "TelegramSubscribers" ADD COLUMN IF NOT EXISTS "SubscriptionExpiryUtc" TIMESTAMP WITH TIME ZONE DEFAULT '1970-01-01 00:00:00+00';
ALTER TABLE "TelegramSubscribers" ADD COLUMN IF NOT EXISTS "IsAdmin" BOOLEAN DEFAULT FALSE;
ALTER TABLE "TelegramSubscribers" ADD COLUMN IF NOT EXISTS "CreatedAtUtc" TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP;
ALTER TABLE "TelegramSubscribers" ADD COLUMN IF NOT EXISTS "NodeToken" TEXT;
ALTER TABLE "TelegramSubscribers" ADD COLUMN IF NOT EXISTS "LastNodeHeartbeatUtc" TIMESTAMP WITH TIME ZONE;

CREATE UNIQUE INDEX IF NOT EXISTS "IX_TelegramSubscribers_NodeToken" ON "TelegramSubscribers" ("NodeToken") WHERE "NodeToken" IS NOT NULL;

-- -----------------------------------------------------------------------------
-- 2. SEARCH QUERIES
-- -----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS "SearchQueries" (
    "Id" SERIAL PRIMARY KEY,
    "Query" TEXT NOT NULL DEFAULT '',
    "IsEnabled" BOOLEAN DEFAULT TRUE,
    "SearchResultsCount" INTEGER DEFAULT 0,
    "LastSearchUTC" TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP,
    "LastDeepSearchDateUTC" TIMESTAMP WITH TIME ZONE
);

ALTER TABLE "SearchQueries" ADD COLUMN IF NOT EXISTS "Query" TEXT NOT NULL DEFAULT '';
ALTER TABLE "SearchQueries" ADD COLUMN IF NOT EXISTS "IsEnabled" BOOLEAN DEFAULT TRUE;
ALTER TABLE "SearchQueries" ADD COLUMN IF NOT EXISTS "SearchResultsCount" INTEGER DEFAULT 0;
ALTER TABLE "SearchQueries" ADD COLUMN IF NOT EXISTS "LastSearchUTC" TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP;
ALTER TABLE "SearchQueries" ADD COLUMN IF NOT EXISTS "LastDeepSearchDateUTC" TIMESTAMP WITH TIME ZONE;

CREATE INDEX IF NOT EXISTS "IX_SearchQueries_IsEnabled_LastSearchUTC" ON "SearchQueries" ("IsEnabled", "LastSearchUTC");

-- -----------------------------------------------------------------------------
-- 3. SEARCH PROVIDER TOKENS
-- -----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS "SearchProviderTokens" (
    "Id" SERIAL PRIMARY KEY,
    "Token" TEXT NOT NULL DEFAULT '',
    "SearchProvider" INTEGER DEFAULT 0,
    "IsEnabled" BOOLEAN DEFAULT TRUE,
    "AddedByTelegramId" BIGINT,
    "LastUsedUTC" TIMESTAMP WITH TIME ZONE
);

ALTER TABLE "SearchProviderTokens" ADD COLUMN IF NOT EXISTS "Token" TEXT NOT NULL DEFAULT '';
ALTER TABLE "SearchProviderTokens" ADD COLUMN IF NOT EXISTS "SearchProvider" INTEGER DEFAULT 0;
ALTER TABLE "SearchProviderTokens" ADD COLUMN IF NOT EXISTS "IsEnabled" BOOLEAN DEFAULT TRUE;
ALTER TABLE "SearchProviderTokens" ADD COLUMN IF NOT EXISTS "AddedByTelegramId" BIGINT;
ALTER TABLE "SearchProviderTokens" ADD COLUMN IF NOT EXISTS "LastUsedUTC" TIMESTAMP WITH TIME ZONE;

CREATE INDEX IF NOT EXISTS "IX_SearchProviderTokens_SearchProvider" ON "SearchProviderTokens" ("SearchProvider");
CREATE INDEX IF NOT EXISTS "IX_SearchProviderTokens_AddedByTelegramId" ON "SearchProviderTokens" ("AddedByTelegramId");

-- -----------------------------------------------------------------------------
-- 4. API KEYS
-- -----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS "APIKeys" (
    "Id" SERIAL PRIMARY KEY,
    "ApiKey" TEXT NOT NULL DEFAULT '',
    "Status" INTEGER DEFAULT 0,
    "ApiType" INTEGER DEFAULT 0,
    "SearchProvider" INTEGER DEFAULT 0,
    "LastCheckedUTC" TIMESTAMP WITH TIME ZONE,
    "FirstFoundUTC" TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP,
    "LastFoundUTC" TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP,
    "TimesDisplayed" INTEGER DEFAULT 0,
    "ErrorCount" INTEGER DEFAULT 0,
    "ValidationResponse" TEXT,
    "Balance" TEXT,
    "AccountTier" TEXT,
    "DiscoveredByTelegramId" BIGINT,
    "Metadata" TEXT
);

ALTER TABLE "APIKeys" ADD COLUMN IF NOT EXISTS "ApiKey" TEXT NOT NULL DEFAULT '';
ALTER TABLE "APIKeys" ADD COLUMN IF NOT EXISTS "Status" INTEGER DEFAULT 0;
ALTER TABLE "APIKeys" ADD COLUMN IF NOT EXISTS "ApiType" INTEGER DEFAULT 0;
ALTER TABLE "APIKeys" ADD COLUMN IF NOT EXISTS "SearchProvider" INTEGER DEFAULT 0;
ALTER TABLE "APIKeys" ADD COLUMN IF NOT EXISTS "LastCheckedUTC" TIMESTAMP WITH TIME ZONE;
ALTER TABLE "APIKeys" ADD COLUMN IF NOT EXISTS "FirstFoundUTC" TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP;
ALTER TABLE "APIKeys" ADD COLUMN IF NOT EXISTS "LastFoundUTC" TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP;
ALTER TABLE "APIKeys" ADD COLUMN IF NOT EXISTS "TimesDisplayed" INTEGER DEFAULT 0;
ALTER TABLE "APIKeys" ADD COLUMN IF NOT EXISTS "ErrorCount" INTEGER DEFAULT 0;
ALTER TABLE "APIKeys" ADD COLUMN IF NOT EXISTS "ValidationResponse" TEXT;
ALTER TABLE "APIKeys" ADD COLUMN IF NOT EXISTS "Balance" TEXT;
ALTER TABLE "APIKeys" ADD COLUMN IF NOT EXISTS "AccountTier" TEXT;
ALTER TABLE "APIKeys" ADD COLUMN IF NOT EXISTS "DiscoveredByTelegramId" BIGINT;
ALTER TABLE "APIKeys" ADD COLUMN IF NOT EXISTS "Metadata" TEXT;

CREATE UNIQUE INDEX IF NOT EXISTS "IX_APIKeys_ApiKey" ON "APIKeys" ("ApiKey");

-- -----------------------------------------------------------------------------
-- 5. REPO REFERENCES
-- -----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS "RepoReferences" (
    "Id" SERIAL PRIMARY KEY,
    "APIKeyId" BIGINT NOT NULL DEFAULT 0,
    "RepoURL" TEXT,
    "RepoOwner" TEXT,
    "RepoName" TEXT,
    "RepoDescription" TEXT,
    "RepoId" BIGINT DEFAULT 0,
    "FileURL" TEXT,
    "FileName" TEXT,
    "FilePath" TEXT,
    "FileSHA" TEXT,
    "ApiContentUrl" TEXT,
    "CodeContext" TEXT,
    "LineNumber" INTEGER DEFAULT 0,
    "SearchQueryId" BIGINT DEFAULT 0,
    "FoundUTC" TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP,
    "Provider" TEXT,
    "Branch" TEXT DEFAULT 'main'
);

ALTER TABLE "RepoReferences" ADD COLUMN IF NOT EXISTS "APIKeyId" BIGINT NOT NULL DEFAULT 0;
ALTER TABLE "RepoReferences" ADD COLUMN IF NOT EXISTS "RepoURL" TEXT;
ALTER TABLE "RepoReferences" ADD COLUMN IF NOT EXISTS "RepoOwner" TEXT;
ALTER TABLE "RepoReferences" ADD COLUMN IF NOT EXISTS "RepoName" TEXT;
ALTER TABLE "RepoReferences" ADD COLUMN IF NOT EXISTS "RepoDescription" TEXT;
ALTER TABLE "RepoReferences" ADD COLUMN IF NOT EXISTS "RepoId" BIGINT DEFAULT 0;
ALTER TABLE "RepoReferences" ADD COLUMN IF NOT EXISTS "FileURL" TEXT;
ALTER TABLE "RepoReferences" ADD COLUMN IF NOT EXISTS "FileName" TEXT;
ALTER TABLE "RepoReferences" ADD COLUMN IF NOT EXISTS "FilePath" TEXT;
ALTER TABLE "RepoReferences" ADD COLUMN IF NOT EXISTS "FileSHA" TEXT;
ALTER TABLE "RepoReferences" ADD COLUMN IF NOT EXISTS "ApiContentUrl" TEXT;
ALTER TABLE "RepoReferences" ADD COLUMN IF NOT EXISTS "CodeContext" TEXT;
ALTER TABLE "RepoReferences" ADD COLUMN IF NOT EXISTS "LineNumber" INTEGER DEFAULT 0;
ALTER TABLE "RepoReferences" ADD COLUMN IF NOT EXISTS "SearchQueryId" BIGINT DEFAULT 0;
ALTER TABLE "RepoReferences" ADD COLUMN IF NOT EXISTS "FoundUTC" TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP;
ALTER TABLE "RepoReferences" ADD COLUMN IF NOT EXISTS "Provider" TEXT;
ALTER TABLE "RepoReferences" ADD COLUMN IF NOT EXISTS "Branch" TEXT DEFAULT 'main';

CREATE INDEX IF NOT EXISTS "IX_RepoReferences_ApiKeyId" ON "RepoReferences" ("APIKeyId");

-- -----------------------------------------------------------------------------
-- 6. DEEP SEARCH PROGRESS (Aggressive Reset)
-- -----------------------------------------------------------------------------
DROP TABLE IF EXISTS "DeepSearchProgress" CASCADE;

CREATE TABLE "DeepSearchProgress" (
    "Id" SERIAL PRIMARY KEY,
    "SearchQueryId" BIGINT NOT NULL,
    "PartitionType" TEXT NOT NULL,
    "PartitionValue" TEXT NOT NULL,
    "LastPageSearched" INTEGER DEFAULT 0,
    "TotalResultsFound" INTEGER DEFAULT 0,
    "IsCompleted" BOOLEAN DEFAULT FALSE,
    "LastSearchedUTC" TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP
);

CREATE UNIQUE INDEX "IX_DeepSearchProgress_Query_Partition" ON "DeepSearchProgress" ("SearchQueryId", "PartitionType", "PartitionValue");

-- -----------------------------------------------------------------------------
-- 7. APPLICATION SETTINGS
-- -----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS "ApplicationSettings" (
    "Key" TEXT PRIMARY KEY,
    "Value" TEXT NOT NULL,
    "Description" TEXT
);

-- -----------------------------------------------------------------------------
-- 🎯 SEED DEFAULT QUERIES
-- -----------------------------------------------------------------------------
INSERT INTO "SearchQueries" ("Query", "IsEnabled", "LastSearchUTC")
SELECT "Query", "IsEnabled", CURRENT_TIMESTAMP FROM (
    VALUES 
    ('sk- OpenAI', true),
    ('anthropic Claude', true),
    ('aizasy Gemini', true),
    ('deepseek', true),
    ('kling AI', true),
    ('pollo AI', true),
    ('runway ML', true),
    ('cohere', true),
    ('elevenlabs', true),
    ('stability AI', true),
    ('together AI', true),
    ('grok XAI', true),
    ('replicate r8_', true),
    ('fireworks fw_', true),
    ('hf_ HuggingFace', true)
) AS v("Query", "IsEnabled")
WHERE NOT EXISTS (SELECT 1 FROM "SearchQueries");

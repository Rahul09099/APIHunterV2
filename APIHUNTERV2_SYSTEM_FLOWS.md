# 🎯 APIHunterV2: Complete System Architecture, Working Flows & Verification Pipeline

This document provides an exhaustive, end-to-end technical explanation of how **APIHunterV2** operates. It covers the full lifecycle of **key scraping**, **key verification**, **server credentials scraping & deep validation**, and the **decentralized Master-Worker server architecture**.

---

## 📐 Table of Contents
1. [System Architecture Overview](#1-system-architecture-overview)
2. [Key Scraping Workflow (The Hunt)](#2-key-scraping-workflow-the-hunt)
3. [Key Verification Workflow (The Truth)](#3-key-verification-workflow-the-truth)
4. [Server Credentials Scraping & Validation System](#4-server-credentials-scraping--validation-system)
5. [Decentralized Ghost Node & Master Server Interaction](#5-decentralized-ghost-node--master-server-interaction)
6. [Database Schema & State Management](#6-database-schema--state-management)

---

## 1. System Architecture Overview

`APIHunterV2` is a C# .NET 9 high-performance multi-provider OSINT scanner and key validation framework designed to discover exposed API keys, cloud secrets, database connection strings, and server credentials across public sources (primarily GitHub).

### Project Layout
```
APIHunterV2/
├── UnsecuredAPIKeys.CLI/          # Interactive Console TUI & CLI entrypoint
├── UnsecuredAPIKeys.WebAPI/       # Master Server REST API & Telegram webhook controller
├── UnsecuredAPIKeys.Services/     # Core services: ScraperService, VerifierService, MetricsService
├── UnsecuredAPIKeys.Providers/    # Provider engines & verification logic
│   ├── AI Providers/              # OpenAI, DeepSeek, Anthropic, Gemini, Groq, etc. (33+ providers)
│   ├── Cloud Providers/           # AWS IAM (STS, Policy Enum), Azure, GCP
│   ├── Communication Providers/   # SendGrid, Mailgun, Slack
│   ├── Search & Maps Providers/   # Google Search, Jina AI
│   ├── Server Providers/          # SSH, FTP, DB (MySQL, Postgres, Mongo, Redis), SMTP, RDP
│   │   └── Services/              # AuthenticationVerifier, NetworkVerifier, OSINT, GeoLocation
│   ├── _Base/                     # BaseApiKeyProvider (retries, backoff, HTTP management)
│   └── ApiProviderRegistry.cs     # Reflection-based provider discovery & registration
└── UnsecuredAPIKeys.Data/         # EF Core DBContext, PostgreSQL/SQLite schemas, DTOs
```

---

## 2. Key Scraping Workflow (The Hunt)

The scraping engine is orchestrated by [ScraperService.cs](file:///c:/Users/rk170/Desktop/unsecureAPI%20project/APIHunterV2/UnsecuredAPIKeys.Services/ScraperService.cs) and [GitHubSearchProvider.cs](file:///c:/Users/rk170/Desktop/unsecureAPI%20project/APIHunterV2/UnsecuredAPIKeys.Providers/Search%20Providers/GitHubSearchProvider.cs).

```mermaid
flowchart TD
    A[Start Scraper] --> B{Is Worker Mode?}
    B -- Yes --> C[Call GET /api/v1/nodes/sync]
    B -- No --> D[Fetch Local DB Tokens & Queries]
    C --> E[Receive Partitioned Queries & GitHub Tokens]
    D --> F[Acquire Distributed Scrape Lock: scrape_lock:queryId]
    E --> F
    F --> G{Lock Acquired?}
    G -- No --> H[Skip Query - Already in progress by another node]
    G -- Yes --> I[Execute GitHub Search Lite / Deep]
    I --> J[Pre-compiled Regex Extraction Engine]
    J --> K[Clean & Normalize Extracted Keys]
    K --> L{Is Worker Mode?}
    L -- Yes --> M[POST /api/v1/nodes/report to Master]
    L -- No --> N[Deduplicate & Save to DB as Unverified]
    M --> O[Release Scrape Lock]
    N --> O
```

### Detailed Scraping Steps

1. **Token Allocation & Rotation**:
   - The scraper loads GitHub Personal Access Tokens (PATs) from the database or environment variables (`WORKER_GITHUB_TOKENS`).
   - GitHub API search has strict rate limits (30 requests/min for code search, 5000 requests/hr core limit).
   - The engine automatically rotates tokens when hitting rate limits or secondary limits (403 Abuse Detection).

2. **Distributed Query Locking (Mutex)**:
   - To prevent multiple worker nodes or the master from scraping the same query simultaneously, [ScraperService.cs](file:///c:/Users/rk170/Desktop/unsecureAPI%20project/APIHunterV2/UnsecuredAPIKeys.Services/ScraperService.cs) uses `scrape_lock:<queryId>` stored in the `ApplicationSettings` table.
   - Locks auto-expire after 15 minutes to recover from crashed nodes.

3. **Search Modes**:
   - **Lite Search**: Executes direct dork queries against the GitHub Code Search API (`/search/code?q=...`). Limited to 1,000 results per query by GitHub API constraints.
   - **Deep Search (Matrix Partitioning)**: Overcomes GitHub's 1,000 result limit by dynamically partitioning queries across combinations of **Languages** (Python, JavaScript, Go, PHP, Java, etc.) and **File Extensions** (`.env`, `.json`, `.yaml`, `.config`, `.txt`, `.py`, `.js`). This allows harvesting 15,000+ results per base query. State is resumable.

4. **Regex Extraction Engine**:
   - At startup, [ScraperService.cs](file:///c:/Users/rk170/Desktop/unsecureAPI%20project/APIHunterV2/UnsecuredAPIKeys.Services/ScraperService.cs) pre-compiles regex patterns from all registered providers (`ApiProviderRegistry.ScraperProviders`).
   - Extracted text snippets are matched against high-entropy regular expressions (e.g. `sk-proj-[A-Za-z0-9\-]{20,}`, `AKIA[0-9A-Z]{16}`, etc.).
   - Matches are stripped of whitespace and prefixes (`Bearer `, `x-api-key:`).

5. **Reporting / Persistence**:
   - **Standalone / Master Node**: Checks DB for existing keys using `SHA256` key hash. If new, inserts record with `Status = Unverified` and attaches `DiscoveredBy` subscriber ID.
   - **Worker Node**: Sends discovery report payload (`NodeBulkReportDto`) to Master endpoint `/api/v1/nodes/report`.

---

## 3. Key Verification Workflow (The Truth)

The verification process is driven by [VerifierService.cs](file:///c:/Users/rk170/Desktop/unsecureAPI%20project/APIHunterV2/UnsecuredAPIKeys.Services/VerifierService.cs) and concrete implementations inheriting from [BaseApiKeyProvider.cs](file:///c:/Users/rk170/Desktop/unsecureAPI%20project/APIHunterV2/UnsecuredAPIKeys.Providers/_Base/BaseApiKeyProvider.cs).

```mermaid
flowchart TD
    A[Verifier Service Loop] --> B[Fetch Unverified Keys from DB]
    B --> C[Group Keys by Provider]
    C --> D[Acquire Provider Semaphore Slim]
    D --> E[BaseApiKeyProvider.ValidateKeyAsync]
    E --> F[Clean Key Format & Validate Syntax]
    F --> G[Execute Live HTTP API Endpoint Verification]
    G --> H{HTTP Status Code Response}
    H -- 200 OK --> I[Status = Valid & Extract Available Models/Metadata]
    H -- 401 / 403 --> J[Status = Invalid / Unauthorized]
    H -- 429 Quota Exceeded --> K[Status = ValidNoCredits / QuotaExhausted]
    H -- 5xx / Network Timeout --> L[Exponential Backoff Retry up to 3x]
    L -- Max Retries Exceeded --> M[Status = Error]
    I --> N[Update DB Record & Record Latency Metric]
    J --> N
    K --> N
    M --> N
    N --> O[Release Provider Semaphore]
```

### Key Verification Architecture

1. **Provider Rate Limiting**:
   - `ProviderRateLimiter` enforces concurrent request caps per API provider using `SemaphoreSlim` (e.g., OpenAI = 5, Anthropic = 3, AWS IAM = 5, Default = 3). This avoids triggering 429 rate limits during validation.

2. **Retry & Backoff Logic ([BaseApiKeyProvider.cs](file:///c:/Users/rk170/Desktop/unsecureAPI%20project/APIHunterV2/UnsecuredAPIKeys.Providers/_Base/BaseApiKeyProvider.cs))**:
   - Retries up to 3 times for transient `HttpRequestException` and `TaskCanceledException` (timeouts).
   - Uses **Exponential Backoff with Jitter**: `delay = 500ms * 2^(retry-1) + random(0..300ms)`.
   - Parses HTTP `Retry-After` headers if returned by providers.

3. **Provider-Specific Validation Examples**:
   - **OpenAI ([OpenAIProvider.cs](file:///c:/Users/rk170/Desktop/unsecureAPI%20project/APIHunterV2/UnsecuredAPIKeys.Providers/AI%20Providers/OpenAIProvider.cs))**:
     1. Queries `GET https://api.openai.com/v1/models` using `Bearer <key>`.
     2. If successful, parses model list (`gpt-4o`, `gpt-4o-mini`, etc.).
     3. Performs a live lightweight test completion `POST https://api.openai.com/v1/chat/completions` (`max_tokens=5`) to verify active usage quota.
     4. If status is `429 insufficient_quota`, key is categorized as `ValidNoCredits`.
   - **AWS IAM ([AWSIAMProvider.cs](file:///c:/Users/rk170/Desktop/unsecureAPI%20project/APIHunterV2/UnsecuredAPIKeys.Providers/Cloud%20Providers/AWSIAMProvider.cs))**:
     1. Extracts Access Key ID (`AKIA...`) and Secret Access Key.
     2. Calls AWS STS `GetCallerIdentityAsync()` to get Account ID, User ARN, and User ID.
     3. Calls IAM `ListAttachedUserPoliciesAsync()` to enumerate attached admin/read permissions.
     4. Calculates Risk Level (`Critical` if Root account or `*` admin access).

---

## 4. Server Credentials Scraping & Validation System

Server credential hunting and validation is managed by [ServerCredentialProvider.cs](file:///c:/Users/rk170/Desktop/unsecureAPI%20project/APIHunterV2/UnsecuredAPIKeys.Providers/Server%20Providers/ServerCredentialProvider.cs) and specialized services in `UnsecuredAPIKeys.Providers/Server Providers/Services/`.

```mermaid
flowchart TD
    A[Extracted Server Credential] --> B[Parse Credential Type & Address]
    B --> C[Network Verification: Socket Ping & Port Check]
    C --> D{Port Accessible?}
    D -- No --> E[Mark NetworkStatus = Unreachable / ConnectionRefused]
    D -- Yes --> F[Extract Banner & SSL Certificate Details]
    F --> G[Perform Auth Verification Pipeline]
    G --> H[Query GreyNoise OSINT for Honeypot Classification]
    H --> I[Geolocate Host IP & Cloud Provider Lookup]
    I --> J[Save Complete Credential Audit Record to DB]
```

### 1. Regex & Credential Extraction Patterns
The system detects 10 categories of server access secrets using pre-compiled regex:
- **SSH Keys & Configs**: `ssh user@host`, `-----BEGIN OPENSSH PRIVATE KEY-----`, `Host ... User ...`.
- **FTP / SFTP**: `ftp://user:pass@host:port`, `FTP_PASS = ...`.
- **Database Connection Strings**:
  - `mysql://user:pass@host:3306/dbname`
  - `postgresql://user:pass@host:5432/dbname`
  - `mongodb://user:pass@host:27017/dbname`
  - `redis://:pass@host:6379`
  - `Server=host;Database=db;User Id=user;Password=pass;`
  - `jdbc:(mysql|postgresql|sqlserver)://...`
- **RDP / VNC / WinRM**: `rdp://user:pass@host`, `mstsc /v:host`, `TeamViewer ID:... Pass:...`, `WinRM host user pass`.
- **Mail Servers (SMTP / IMAP / POP3)**: `smtp://user:pass@host:587`, `SMTP_PASSWORD = ...`.
- **Control Panels**: cPanel (`CPANEL_PASS`), WHM (`WHM_PASS`), Plesk (`PLESK_PASS`).
- **Cloud & Container Infrastructure**: `DOCKER_HOST=tcp://host:2375`, `KUBERNETES_SERVICE_HOST`, `kubeconfig`.
- **Web Auth Files**: `AuthUserFile`, `.htpasswd`, `<user username="..." password="...">`.

### 2. Multi-Stage Validation Pipeline

| Validation Stage | Executed By | Operations Performed |
| :--- | :--- | :--- |
| **Stage 1: Network Connectivity** | [NetworkVerifier.cs](file:///c:/Users/rk170/Desktop/unsecureAPI%20project/APIHunterV2/UnsecuredAPIKeys.Providers/Server%20Providers/Services/NetworkVerifier.cs) | Opens `TcpClient` socket connection with 5s timeout. Checks port availability. Extracts server banner text (e.g. `SSH-2.0-OpenSSH_8.9p1`). Extracts SSL/TLS Subject, Issuer, and Certificate Thumbprint for HTTPS/IMAPS/SMTPS. |
| **Stage 2: Active Auth Verification** | [AuthenticationVerifier.cs](file:///c:/Users/rk170/Desktop/unsecureAPI%20project/APIHunterV2/UnsecuredAPIKeys.Providers/Server%20Providers/Services/AuthenticationVerifier.cs) | Performs protocol-level auth checks:<br>• **FTP**: Sends `USER` & `PASS` over socket, listens for `230 Logged in`.<br>• **SSH**: Verifies SSH service header.<br>• **Database / Control Panels**: Safe single-attempt login handshake.<br>• **24-Hour Cooldown**: Uses SHA256 `host:port:user` hash in memory cache (`auth_cooldown_hash`) to avoid brute-force lockout. |
| **Stage 3: OSINT & Honeypot Check** | [OSINTService.cs](file:///c:/Users/rk170/Desktop/unsecureAPI%20project/APIHunterV2/UnsecuredAPIKeys.Providers/Server%20Providers/Services/OSINTService.cs) | Queries GreyNoise API/Intelligence cache to identify if target host IP is a known scanner, bot, or honeypot (`IsHoneypot = true`). |
| **Stage 4: Geolocation & Infrastructure** | [GeolocationService.cs](file:///c:/Users/rk170/Desktop/unsecureAPI%20project/APIHunterV2/UnsecuredAPIKeys.Providers/Server%20Providers/Services/GeolocationService.cs) | Resolves host IP to Country, City, ISP, Autonomous System (ASN), and Cloud Provider tag (AWS, DigitalOcean, Hetzner, OVH, Azure). |
| **Stage 5: Circuit Breaker & Queue** | [HostCircuitBreaker.cs](file:///c:/Users/rk170/Desktop/unsecureAPI%20project/APIHunterV2/UnsecuredAPIKeys.Providers/Server%20Providers/Services/HostCircuitBreaker.cs) | Prevents flooding the same host IP with multiple credential checks within short time windows. |

---

## 5. Decentralized Ghost Node & Master Server Interaction

To support distributed scraping across free/cloud tiers (Render, Fly.io, VPS), `APIHunterV2` implements a **Stateless Ghost Node (Master-Worker)** architecture documented in [SYSTEM_ARCH_DECENTRALIZED.md](file:///c:/Users/rk170/Desktop/unsecureAPI%20project/APIHunterV2/SYSTEM_ARCH_DECENTRALIZED.md).

```mermaid
sequenceDiagram
    participant W as Ghost Worker Node
    participant M as Master WebAPI (NodesController)
    participant DB as Master Postgres Database
    
    W->>M: POST /api/v1/nodes/heartbeat (X-Node-Token, nodeUrl)
    M->>DB: Update LastNodeHeartbeatUtc & NodeUrl
    
    W->>M: GET /api/v1/nodes/sync (X-Node-Token)
    M->>DB: Fetch Active Subscribers & Enabled Search Queries
    M->>M: Round-Robin Query Partitioning (query_index % totalNodes == nodeIndex)
    M-->>W: JSON NodeSyncDTO (Assigned Queries & GitHub Tokens)
    
    W->>W: Execute GitHub Search for assigned queries
    
    W->>M: POST /api/v1/nodes/report (Discovered Keys Payload)
    M->>DB: Deduplicate (SHA256) & Save Valid/Unverified Key
    M-->>W: Http 200 OK (Report Processed)
    
    Note over M: Master Node Keep-Alive Loop (Every 14 Mins)
    M->>W: GET {nodeUrl}/health (Prevents Render Free-Tier Sleep)
```

### Ghost Node Lifecycle Details

1. **Authentication & Handshake (`/api/v1/nodes/sync`)**:
   - Worker passes unique token in `X-Node-Token` HTTP header.
   - Master counts active nodes (heartbeat within 10 mins) and assigns a **deterministic partition index**:
     $$\text{query\_index} \pmod{\text{totalNodes}} == \text{nodeIndex}$$
   - This ensures zero overlap between nodes without complex message queues.

2. **Stateless Worker Execution**:
   - Worker nodes set `EF Core` tracking to `NoTracking`.
   - Local DB writes are disabled when `IsWorkerMode = true`.
   - Keys found during scraping are instantly pushed via `/api/v1/nodes/report`.

3. **Render 14-Minute Keep-Alive (`NodeKeepAliveService`)**:
   - Cloud hosting services like Render free-tier put web services to sleep after 15 minutes of inactivity.
   - The Master API runs a background service that pings all active workers' `RENDER_EXTERNAL_URL` every 14 minutes to maintain 24/7 uptime.

---

## 6. Database Schema & State Management

Key entities managed in [DBContext.cs](file:///c:/Users/rk170/Desktop/unsecureAPI%20project/APIHunterV2/UnsecuredAPIKeys.Data/DBContext.cs):

| Entity / Table | Core Purpose | Key Attributes |
| :--- | :--- | :--- |
| `APIKeys` | Central registry of discovered keys | `KeyHash` (SHA256 indexed), `ApiType`, `Status` (Unverified, Valid, Invalid, ValidNoCredits, Error), `RawValue`, `DiscoveredBy`, `FirstFoundUTC`, `LastCheckedUTC`. |
| `ServerCredentials` | Detailed audit records for server secrets | `Host`, `Port`, `CredentialType`, `Username`, `EncryptedPassword`, `NetworkStatus`, `AuthenticationStatus`, `ServerMetadata`, `OSINTData`, `GeoLocationData`, `IsHoneypot`. |
| `SearchQueries` | GitHub dork queries | `Query`, `IsEnabled`, `Group`, `LastRunUTC`. |
| `SearchProviderTokens` | GitHub PAT pool | `Token`, `SearchProvider`, `IsEnabled`, `IsRateLimited`, `CooldownUntilUTC`, `AddedByTelegramId`. |
| `TelegramSubscribers` | Ghost nodes & users | `TelegramId`, `NodeToken`, `LastNodeHeartbeatUtc`, `NodeUrl`, `IsAdmin`. |
| `ApplicationSettings` | Key-value store & lock mutex | `Key` (e.g. `scrape_lock:12`), `Value` (`nodeId|timestamp`). |

---

## Summary of Key Statuses
When keys are processed by the Verifier, they transition into one of the following canonical states:
- `Unverified`: Newly scraped key waiting in queue.
- `Valid`: Verified working key with active quota.
- `ValidNoCredits` / `QuotaExhausted`: Key credentials are correct, but API provider account balance/quota is depleted.
- `Unauthorized` / `Invalid`: Key has been revoked or is invalid.
- `Error`: Network error or unexpected endpoint exception during validation (subject to re-verification).

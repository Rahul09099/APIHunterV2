# Implementation Plan: Server Credential Detection

## Overview

Implement server credential detection, verification, and metadata extraction for the UnsecuredAPIKeys tool. The feature adds a `ServerCredentialProvider` with regex patterns for 14+ credential types, multi-stage verification pipeline (network → auth → OSINT → geolocation), supporting services, database schema, CLI display, and export support. All code is C# targeting the existing .NET 9 project structure.

## Tasks

- [ ] 1. Add `ServerCredential` enum value and data model
  - Add `ServerCredential = 500` to `ApiTypeEnum` in `UnsecuredAPIKeys.Data/Common/CommonEnums.cs`
  - Add `ServerCredentials = 5` to `ApiCategoryEnum` in the same file
  - Create `UnsecuredAPIKeys.Data/Models/ServerCredential.cs` with all 15+ columns matching the design schema: `Id`, `CredentialType`, `Host`, `Port`, `Username`, `PasswordHash`, `Domain`, `NetworkStatus`, `AuthenticationStatus`, `ServerMetadata`, `GeolocationData`, `OSINTData`, `RiskLevel`, `IsHoneypot`, `SourceRepository`, `SourceFilePath`, `SurroundingContext`, `EntropyScore`, `DiscoveredAt`, `LastVerifiedAt`
  - Add supporting value objects: `NetworkVerificationResult`, `AuthVerificationResult`, `SslCertificateInfo`, `CredentialContext`, `RiskLevel` enum
  - _Requirements: 13.1–13.12_

- [ ] 2. Update `DBContext` and database schema
  - [ ] 2.1 Add `ServerCredentials` DbSet to `UnsecuredAPIKeys.Data/DBContext.cs`
    - Add `public DbSet<ServerCredential> ServerCredentials { get; set; } = null!;`
    - Add EF Core model configuration in `OnModelCreating`: unique constraint on `(Host, Port, Username, CredentialType)`, indexes on `CredentialType`, `RiskLevel`, `AuthenticationStatus`, `IsHoneypot`
    - _Requirements: 13.1–13.12_
  - [ ] 2.2 Update `master_init.sql` with `ServerCredentials` table DDL
    - Add Section 10 with `CREATE TABLE IF NOT EXISTS "ServerCredentials"` matching the design schema (all columns, JSONB for metadata columns, default values)
    - Add `ALTER TABLE ... ADD COLUMN IF NOT EXISTS` idempotent statements for each column
    - Add all four indexes: `idx_sc_type`, `idx_sc_risk`, `idx_sc_auth_status`, `idx_sc_honeypot`
    - Add unique constraint `uq_server_cred` on `(Host, Port, Username, CredentialType)`
    - _Requirements: 13.13_
  - [ ] 2.3 Add server credential search queries to `master_init.sql` and `DBContext.SeedDefaultDataAsync`
    - Add all 24 search query strings from Requirement 17 to the Section 8 `INSERT INTO "SearchQueries"` block in `master_init.sql`
    - Implement `SeedDefaultDataAsync` in `DBContext.cs` that inserts the same 24 queries using EF Core (idempotent — skip if already exists)
    - _Requirements: 17.1–17.14_

- [ ] 3. Create supporting service interfaces and implementations
  - [ ] 3.1 Create `IContextExtractor` interface and `ContextExtractor` implementation
    - Create `UnsecuredAPIKeys.Providers/Server Providers/Services/ContextExtractor.cs`
    - Implement `ExtractContextAsync(fileContent, matchPosition, contextLines = 10)` returning `CredentialContext` with ±10 lines
    - Implement `FindRelatedPassword`, `FindRelatedHost`, `FindRelatedPort` helper methods using the regex patterns from the design
    - _Requirements: 1.5, 2.8, 4.9, 6.10, 7.9, 10.8_
  - [ ] 3.2 Write unit tests for `ContextExtractor`
    - Test that `ExtractContextAsync` returns exactly ±10 lines around the match position
    - Test that `FindRelatedPassword` finds passwords in common patterns (`password=`, `pass:`, `pwd=`)
    - Test that `FindRelatedHost` extracts IP addresses and hostnames
    - Test boundary conditions: match at start of file, match at end of file
    - _Requirements: 1.5, 10.8_
  - [ ] 3.3 Create `IEntropyAnalyzer` interface and `EntropyAnalyzer` implementation
    - Create `UnsecuredAPIKeys.Providers/Server Providers/Services/EntropyAnalyzer.cs`
    - Implement `CalculateEntropy(string input)` using Shannon entropy formula: `H = -Σ p(x) * log2(p(x))`
    - Implement `IsHighEntropyPassword(string input, double threshold = 4.0)` returning bool
    - Handle edge cases: empty string returns 0.0, single-character string returns 0.0
    - _Requirements: 10.9_
  - [ ] 3.4 Write property test for `EntropyAnalyzer` — Property P7
    - **Property P7: Entropy Score Accuracy**
    - **Validates: Requirements 10.9**
    - For any non-empty string, `EntropyAnalyzer.CalculateEntropy(s)` must equal the mathematical Shannon entropy within 1e-9 tolerance
    - Use FsCheck to generate arbitrary non-empty strings and verify the result against a reference implementation
  - [ ] 3.5 Create `INetworkVerifier` interface and `NetworkVerifier` implementation
    - Create `UnsecuredAPIKeys.Providers/Server Providers/Services/NetworkVerifier.cs`
    - Implement `VerifyConnectivityAsync(host, port, timeoutSeconds = 10)` using `TcpClient` with `Task.WhenAny` timeout pattern
    - Implement `ExtractBannerAsync(host, port, timeoutSeconds = 10)` reading up to 1024 bytes from the TCP stream
    - Implement `ExtractSslCertificateAsync(host, port)` using `SslStream` with certificate validation bypass
    - Return typed result objects (`NetworkVerificationResult`) for all outcomes: Success, Unreachable, Timeout, Error
    - _Requirements: 9.1–9.19_
  - [ ] 3.6 Write unit tests for `NetworkVerifier`
    - Test that timeout is respected (mock a slow TCP connection)
    - Test that banner is trimmed of whitespace
    - Test that SSL certificate fields are populated when available
    - Test that `NetworkVerificationResult` factory methods produce correct status strings
    - _Requirements: 9.17, 9.18, 9.19_
  - [ ] 3.7 Create `IAuthenticationVerifier` interface and `AuthenticationVerifier` implementation
    - Create `UnsecuredAPIKeys.Providers/Server Providers/Services/AuthenticationVerifier.cs`
    - Implement `IsOnCooldown(credentialHash)` and `SetCooldown(credentialHash)` using `IMemoryCache` with 24-hour expiry
    - Implement `ComputeHash(host, port, username)` using SHA-256
    - Implement all protocol verifiers: `VerifySSHAsync`, `VerifyFTPAsync`, `VerifyRDPAsync`, `VerifySMTPAsync`, `VerifyIMAPAsync`, `VerifyPOP3Async`, `VerifyCPanelAsync`, `VerifyWHMAsync`, `VerifyPleskAsync`, `VerifyDatabaseAsync`
    - Each verifier must check cooldown first, perform single attempt, set cooldown after attempt, return `AuthVerificationResult`
    - cPanel: use UAPI Basic auth to `/execute/Email/list_pops`; WHM: use JSON-API to `/json-api/version`; Plesk: use REST API to `/api/v2/server`
    - _Requirements: 8 (auth), 18.1–18.9_
  - [ ] 3.8 Write property test for `AuthenticationVerifier` — Property P3
    - **Property P3: Single Authentication Attempt Per Credential Per Day**
    - **Validates: Requirements 8.15, 8.16**
    - For any `(host, port, username)` tuple, after one authentication attempt the cooldown must be active and `IsOnCooldown` must return true for the next 24 hours
    - Use FsCheck to generate arbitrary host/port/username combinations

- [ ] 4. Create `OSINTService` and `GeolocationService`
  - [ ] 4.1 Create `IOSINTService` interface and `OSINTService` implementation
    - Create `UnsecuredAPIKeys.Providers/Server Providers/Services/OSINTService.cs`
    - Implement `QueryShodanAsync`, `QueryCensysAsync`, `QueryGreyNoiseAsync` with `IMemoryCache` 24-hour caching
    - Implement rate limiting using `SemaphoreSlim(1,1)` with 5-second delay between requests
    - Implement `IsHoneypotAsync` checking GreyNoise classification and bot flag
    - When OSINT services are unavailable, log warning and return empty result without blocking
    - _Requirements: 8 (OSINT).1–9_
  - [ ] 4.2 Write property test for `OSINTService` — Property P8
    - **Property P8: OSINT Cache Freshness**
    - **Validates: Requirements 8 (OSINT).7**
    - For any cached OSINT result older than 24 hours, the service must fetch fresh data rather than returning the stale cache entry
    - Use FsCheck with a mock `IMemoryCache` that returns entries with configurable timestamps
  - [ ] 4.3 Create `IGeolocationService` interface and `GeolocationService` implementation
    - Create `UnsecuredAPIKeys.Providers/Server Providers/Services/GeolocationService.cs`
    - Implement `GeolocateAsync(ipAddress)` using MaxMind GeoLite2 database (via `MaxMind.GeoIP2` NuGet package)
    - Implement `IsCloudProviderIP(ipAddress, out providerName)` checking CIDR ranges for AWS, Azure, GCP, DigitalOcean, Linode, Vultr, Hetzner, OracleCloud
    - Store cloud provider IP range files as embedded resources or load from configurable path
    - When geolocation fails, return result with `"Geolocation unavailable"` message
    - _Requirements: 9 (geo).1–9_
  - [ ] 4.4 Write unit tests for `GeolocationService`
    - Test that known AWS IP ranges are correctly identified as `"AWS"` cloud provider
    - Test that a private IP (192.168.x.x) is not flagged as cloud provider
    - Test that geolocation failure returns `"Geolocation unavailable"` without throwing
    - _Requirements: 9 (geo).5, 9 (geo).9_

- [ ] 5. Create `AdaptiveIOManager` and `RenderOptimizer`
  - [ ] 5.1 Create `AdaptiveIOManager` implementation
    - Create `UnsecuredAPIKeys.Providers/Server Providers/Services/AdaptiveIOManager.cs`
    - Implement `InitializeAsync()` that benchmarks Direct I/O vs streaming on a 1MB temp file
    - Implement `ReadFileAsync(path, maxSizeBytes)` that routes to `ReadDirectIOAsync` or `ReadStreamingAsync` based on benchmark result and file size
    - Use `FileOptions.SequentialScan` for Direct I/O path; use `StreamReader` with 32KB buffer for streaming path
    - Files larger than `maxSizeBytes` always use streaming regardless of benchmark result
    - _Requirements: 11.6, 11.7, 11.9, 11.11_
  - [ ] 5.2 Write unit tests for `AdaptiveIOManager`
    - Test that streaming is selected when Direct I/O benchmark is slower
    - Test that files exceeding `maxSizeBytes` always use streaming
    - Test that `ReadFileAsync` returns correct content for both strategies
    - _Requirements: 11.6, 11.7_
  - [ ] 5.3 Create `RenderOptimizer` implementation
    - Create `UnsecuredAPIKeys.Providers/Server Providers/Services/RenderOptimizer.cs`
    - Implement properties: `MaxConcurrentScans` (2 on free tier, 10 otherwise), `MaxConcurrentVerify` (1/5), `VerificationBatchSize` (10/50), `MaxFileSizeBytes` (10MB/100MB), `MaxFilesPerScan` (100/1000), `BufferSizeBytes` (32KB/64KB)
    - Implement `CheckMemoryPressureAsync()` that triggers `GC.Collect(2, GCCollectionMode.Aggressive)` when memory exceeds 400MB
    - Detect Render free tier via environment variable `RENDER_FREE_TIER=true` or memory limit heuristic
    - _Requirements: 11.1–11.10_
  - [ ] 5.4 Write property test for `RenderOptimizer` — Property P6
    - **Property P6: Render Free Tier Concurrency Invariant**
    - **Validates: Requirements 11.1, 11.2**
    - For any sequence of concurrent scan/verify operations on Render free tier, the active count must never exceed 2 scans or 1 verification simultaneously
    - Use FsCheck to generate random sequences of start/stop operations and verify the invariant holds throughout

- [ ] 6. Create `VerificationQueue` and `HostCircuitBreaker`
  - [ ] 6.1 Create `VerificationQueue` implementation
    - Create `UnsecuredAPIKeys.Providers/Server Providers/Services/VerificationQueue.cs`
    - Implement priority queue using `PriorityQueue<ServerCredential, int>` with `SemaphoreSlim` for thread safety
    - Priority mapping: Critical=0, High=1, Medium=2, Low=3
    - Implement `EnqueueAsync` that rejects new items when queue size ≥ 1000
    - Implement `DequeueAsync` returning null when queue is empty
    - Implement exponential backoff tracking: 10s, 30s, 60s, 300s delays for failed attempts
    - Implement minimum 10-second delay between attempts to the same host
    - _Requirements: 16.1–16.9_
  - [ ] 6.2 Write unit tests for `VerificationQueue`
    - Test that items are dequeued in priority order (Critical before High before Medium before Low)
    - Test that queue blocks new additions at 1000 items
    - Test that exponential backoff delays are correct: 10s → 30s → 60s → 300s
    - _Requirements: 16.1, 16.3, 16.5_
  - [ ] 6.3 Create `HostCircuitBreaker` implementation
    - Create `UnsecuredAPIKeys.Providers/Server Providers/Services/HostCircuitBreaker.cs`
    - Implement `IsOpen(host)` checking if circuit is open (failure threshold reached)
    - Implement `RecordFailure(host)` incrementing failure count; open circuit for 30 minutes after 3 failures
    - Implement `RecordSuccess(host)` resetting failure count
    - _Requirements: 18.6_

- [ ] 7. Create `ServerCredentialProvider`
  - [ ] 7.1 Create the provider class with all regex patterns
    - Create `UnsecuredAPIKeys.Providers/Server Providers/ServerCredentialProvider.cs`
    - Inherit from `BaseApiKeyProvider`, apply `[ApiProvider]` attribute
    - Set `ProviderName = "Server Credentials"`, `ApiType = ApiTypeEnum.ServerCredential`
    - Implement `RegexPatterns` property with all patterns from the design: SSH (3 patterns), FTP/SFTP (5 patterns), database connection strings (9 patterns), RDP/remote access (5 patterns), SMTP/email (7 patterns), cPanel/WHM/Plesk (6 patterns), cloud/container (3 patterns), web server auth (3 patterns)
    - _Requirements: 1–8 (detection), 12.1–12.9_
  - [ ] 7.2 Write property test for `ServerCredentialProvider` — Property P1
    - **Property P1: Pattern Completeness**
    - **Validates: Requirements 1–8 (detection)**
    - For every supported credential type (SSH, FTP, RDP, VNC, WinRM, SMTP, IMAP, POP3, cPanel, WHM, Plesk, MySQL, PostgreSQL, MongoDB, Redis, MSSQL, Kubernetes, Docker), at least one regex pattern must match the canonical example string defined in the test
    - Use FsCheck or parameterized tests with a dictionary of `CredentialType → canonicalExample`
  - [ ] 7.3 Implement credential parsing methods
    - Implement `ParseSSHCredential`, `ParseFTPCredential`, `ParseDatabaseCredential`, `ParseRDPCredential`, `ParseSMTPCredential`, `ParseControlPanelCredential`, `ParseCloudCredential`
    - Each parser extracts host, port, username from the regex match and calls `ContextExtractor` to find related password within ±10 lines
    - Implement `DetermineCredentialType(pattern)` mapping regex pattern to `CredentialType` enum value
    - Implement `GetDefaultPort(CredentialType)` returning correct default ports for all 20+ credential types
    - _Requirements: 1–8 (detection)_
  - [ ] 7.4 Implement `ValidateKeyWithHttpClientAsync` orchestration
    - Override `ValidateKeyWithHttpClientAsync` to orchestrate the full verification pipeline:
      1. Parse credential from `apiKey` string using appropriate parser
      2. Calculate entropy score via `EntropyAnalyzer`
      3. Enqueue to `VerificationQueue` with risk-level priority
      4. Perform network connectivity check via `NetworkVerifier`
      5. If accessible: extract banner, perform safe auth test via `AuthenticationVerifier`
      6. Query OSINT via `OSINTService` (non-blocking)
      7. Geolocate IP via `GeolocationService` (non-blocking)
      8. Persist `ServerCredential` record to database
    - Return `ValidationResult.Success` with metadata or `ValidationResult.HasProviderSpecificError` on failure
    - _Requirements: 9 (network), 8 (auth), 8 (OSINT), 9 (geo)_
  - [ ] 7.5 Write property test for `ServerCredentialProvider` — Property P2
    - **Property P2: No Plaintext Password Storage**
    - **Validates: Requirements 13.5 (PasswordHash column)**
    - For any credential with a non-empty password, the stored `PasswordHash` must equal `SHA256(rawPassword)` and the raw password must not appear anywhere in the serialized `ServerCredential` object
    - Use FsCheck to generate arbitrary password strings and verify hash correctness
  - [ ] 7.6 Write property test for `ServerCredentialProvider` — Property P5
    - **Property P5: Honeypot Propagation**
    - **Validates: Requirements 8 (OSINT).5**
    - For any IP address that `OSINTService.IsHoneypotAsync` returns true for, all `ServerCredential` records with that host must have `IsHoneypot = true`
    - Use FsCheck with a mock `IOSINTService` that returns configurable honeypot flags

- [ ] 8. Checkpoint — Core services complete
  - Ensure all tests pass, ask the user if questions arise.

- [ ] 9. Update `DBContext` with `SeedDefaultDataAsync` and register services
  - [ ] 9.1 Implement `SeedDefaultDataAsync` in `DBContext.cs`
    - Add method `public async Task SeedDefaultDataAsync()` that inserts all 24 server credential search queries from Requirement 17 using EF Core
    - Use `AddRangeAsync` with `WHERE NOT EXISTS` equivalent: check `AnyAsync` before inserting each query
    - Call `SeedDefaultDataAsync` from `InitializeDatabaseAsync` (currently commented out — add server credential seeding only)
    - _Requirements: 17.1–17.14_
  - [ ] 9.2 Register new services in dependency injection
    - Update `UnsecuredAPIKeys.CLI/Program.cs` service registration to add:
      - `services.AddMemoryCache()`
      - `services.AddSingleton<IContextExtractor, ContextExtractor>()`
      - `services.AddSingleton<IEntropyAnalyzer, EntropyAnalyzer>()`
      - `services.AddSingleton<INetworkVerifier, NetworkVerifier>()`
      - `services.AddSingleton<IAuthenticationVerifier, AuthenticationVerifier>()`
      - `services.AddSingleton<IOSINTService, OSINTService>()`
      - `services.AddSingleton<IGeolocationService, GeolocationService>()`
      - `services.AddSingleton<AdaptiveIOManager>()`
      - `services.AddSingleton<RenderOptimizer>()`
      - `services.AddSingleton<VerificationQueue>()`
      - `services.AddSingleton<HostCircuitBreaker>()`
    - _Requirements: 11.1–11.11_

- [ ] 10. Add CLI display for server credentials
  - [ ] 10.1 Add `DisplayServerCredentials` method to `Program.cs`
    - Create a Spectre.Console `Table` with columns: Type, Host:Port, Username, Network Status, Auth Status, Risk Level, Honeypot, Country/ISP, Discovered
    - Color-code `NetworkStatus`: green for Accessible, red for Unreachable, yellow for Timeout
    - Color-code `AuthenticationStatus`: green for Valid, red for Invalid, yellow for RateLimited, grey for Untested
    - Color-code `RiskLevel`: red for Critical, darkorange for High, yellow for Medium, green for Low
    - Display `[yellow]⚠ HONEYPOT[/]` warning when `IsHoneypot = true`
    - Show `"N/A"` for missing metadata fields
    - _Requirements: 15.1–15.9_
  - [ ] 10.2 Add server credentials section to `ShowStatusAsync`
    - Query `db.ServerCredentials` for recent entries (top 20 by `DiscoveredAt` descending)
    - Display count summary: total found, valid, invalid, untested, honeypots flagged
    - Call `DisplayServerCredentials` to render the table
    - Add server credential counts to the categorized breakdown section
    - _Requirements: 15.1–15.9_
  - [ ] 10.3 Add server credential filtering and sorting options
    - Add a sub-menu under "View Status" for server credentials: filter by credential type, risk level, auth status
    - Implement `GetFilteredServerCredentialsAsync(db, type, riskLevel, authStatus)` query method
    - _Requirements: 15.8_

- [ ] 11. Update export service for server credentials
  - [ ] 11.1 Add CSV export for `ServerCredentials`
    - Locate the existing `ExportKeysAsync` method in `DatabaseService` (or equivalent export logic)
    - Add a new export path for `ServerCredential` records: query `db.ServerCredentials`, serialize each row to CSV
    - Flatten JSON columns (`ServerMetadata`, `GeolocationData`, `OSINTData`) to readable strings for CSV
    - Include all columns: CredentialType, Host, Port, Username, NetworkStatus, AuthenticationStatus, RiskLevel, IsHoneypot, EntropyScore, SourceRepository, SourceFilePath, DiscoveredAt, LastVerifiedAt
    - _Requirements: 14.1, 14.3, 14.4, 14.5, 14.8_
  - [ ] 11.2 Add JSON export for `ServerCredentials`
    - Add JSON export path that serializes `ServerCredential` records with nested objects for `ServerMetadata`, `GeolocationData`, `OSINTData`
    - Include export timestamp and source repository information
    - Handle null metadata columns by outputting `null` in JSON
    - _Requirements: 14.2, 14.7, 14.8_
  - [ ] 11.3 Add server credential export option to CLI export menu
    - Add `"3. Server Credentials (CSV)"` and `"4. Server Credentials (JSON)"` choices to the export selection prompt in `ExportKeysAsync`
    - Add filtering prompt: filter by credential type, risk level, or export all
    - _Requirements: 14.6, 14.9_

- [ ] 12. Checkpoint — Integration wiring complete
  - Ensure all tests pass, ask the user if questions arise.

- [ ] 13. Create test project and write integration tests
  - [ ] 13.1 Create test project `UnsecuredAPIKeys.Tests`
    - Create `UnsecuredAPIKeys.Tests/UnsecuredAPIKeys.Tests.csproj` targeting net9.0
    - Add NuGet references: `xunit`, `xunit.runner.visualstudio`, `FsCheck.Xunit`, `Microsoft.NET.Test.Sdk`, `Moq`, `Microsoft.EntityFrameworkCore.InMemory`
    - Add project references to `UnsecuredAPIKeys.Data`, `UnsecuredAPIKeys.Providers`
    - _Requirements: all_
  - [ ] 13.2 Write property test for `NetworkVerifier` — Property P4
    - **Property P4: Network Timeout Enforcement**
    - **Validates: Requirements 9.17**
    - For any TCP connectivity test, the call must complete within `timeoutSeconds + 1` second regardless of target responsiveness
    - Use FsCheck with a mock TCP listener that delays indefinitely; verify elapsed time is within bounds
  - [ ] 13.3 Write integration test: end-to-end credential detection pipeline
    - Set up an in-memory `DBContext` with SQLite
    - Feed a mock GitHub file containing all 14+ credential type patterns
    - Run `ServerCredentialProvider` pattern matching and verify all credential types are detected and stored
    - Verify `PasswordHash` is SHA-256 of the raw password (not plaintext)
    - _Requirements: 1–8 (detection), 13.1–13.12_
  - [ ] 13.4 Write integration test: database round-trip
    - Store a `ServerCredential` record with all fields populated
    - Query it back and verify all fields match
    - Export to CSV and JSON; verify all columns appear in output
    - _Requirements: 13.1–13.12, 14.1–14.9_

- [ ] 14. Final checkpoint — Ensure all tests pass
  - Ensure all tests pass, ask the user if questions arise.

## Notes

- Tasks marked with `*` are optional and can be skipped for a faster MVP
- Property tests use FsCheck (via `FsCheck.Xunit`) with minimum 100 iterations per property
- Each property test references its design document property number (P1–P8)
- The `ServerCredentialProvider` is auto-discovered by `ApiProviderRegistry` via the `[ApiProvider]` attribute — no manual registration needed in the registry
- New services (`IContextExtractor`, `INetworkVerifier`, etc.) require constructor injection; the provider needs a parameterless constructor for registry auto-discovery — use a default no-op implementation or service locator pattern for the parameterless case
- `master_init.sql` changes are additive and idempotent — safe to run on existing databases
- The `SeedDefaultDataAsync` call in `InitializeDatabaseAsync` is currently commented out; only uncomment the server credential seeding portion to avoid re-seeding existing API key queries

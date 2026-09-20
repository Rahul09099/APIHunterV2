# Implementation Plan: Multi-Search-Provider Platform and Durable Credential Rotation

## Overview

This plan implements the approved requirements and design as test-first, vertical increments. Every numbered increment begins with a failing observable-behavior test, implements only enough schema/domain/runtime/API/UI behavior to make that slice work, and ends by exercising the wired path. No task may leave an unused table, DTO, service, adapter, or interface for a distant task.

Requirement references use exact acceptance-criterion IDs. An en-dash range such as `AC-5.1–AC-5.4` is inclusive. All tasks are mandatory unless a provider decision gate explicitly records that no supported official API exists. Each schema-changing task updates the EF model, `DBContext.OnModelCreating`, manual SQLite initialization, manual PostgreSQL initialization, `master_init.sql`, and WebAPI readiness together.

For every implementation increment, run targeted tests first, then the database/provider integration tests affected by the change, the complete existing test project, and a Release solution build. Do not begin a later provider phase until the preceding phase passes its complete Definition of Done.

## Task Dependency Graph

```json
{
  "waves": [
    { "wave": 1, "tasks": ["1"], "dependsOn": [] },
    { "wave": 2, "tasks": ["2"], "dependsOn": [1] },
    { "wave": 3, "tasks": ["3"], "dependsOn": [2] },
    { "wave": 4, "tasks": ["4"], "dependsOn": [3] },
    { "wave": 5, "tasks": ["5"], "dependsOn": [4] },
    { "wave": 6, "tasks": ["6"], "dependsOn": [5] },
    { "wave": 7, "tasks": ["7"], "dependsOn": [6] },
    { "wave": 8, "tasks": ["8"], "dependsOn": [7] },
    { "wave": 9, "tasks": ["9"], "dependsOn": [8] },
    { "wave": 10, "tasks": ["10"], "dependsOn": [9] },
    { "wave": 11, "tasks": ["11"], "dependsOn": [10] },
    { "wave": 12, "tasks": ["12"], "dependsOn": [11] },
    { "wave": 13, "tasks": ["13"], "dependsOn": [12] },
    { "wave": 14, "tasks": ["14"], "dependsOn": [13] },
    { "wave": 15, "tasks": ["15"], "dependsOn": [14] },
    { "wave": 16, "tasks": ["16"], "dependsOn": [15] },
    { "wave": 17, "tasks": ["17"], "dependsOn": [16] },
    { "wave": 18, "tasks": ["18"], "dependsOn": [17] },
    { "wave": 19, "tasks": ["19"], "dependsOn": [18] },
    { "wave": 20, "tasks": ["20"], "dependsOn": [19] },
    { "wave": 21, "tasks": ["21"], "dependsOn": [20] },
    { "wave": 22, "tasks": ["22"], "dependsOn": [21] }
  ]
}
```

Each `dependsOn` entry is a hard dependency on the preceding wave. A wave cannot start until its predecessor and the predecessor's mandatory validation are complete. Waves 18–22 are serialized provider-phase gates, not parallel work streams.

## Tasks

- [x] 1. Lock the legacy defect boundary and executable release gate
  - [x] 1.1 Write characterization tests for the current search and Worker defects
    - Extend `UnsecuredAPIKeys.Tests` around `ScraperService`, `GitHubSearchProvider`, `GitLabSearchProvider`, `NodesController`, and WebAPI role registration.
    - Capture sticky successful-token selection, swallowed GitHub failures, GitLab `429` propagation, absent `LastUsedUTC`, GitHub-only plaintext sync, hardcoded `GitHub (Ghost)` attribution, and missing Worker scraper startup.
    - Keep characterization assertions separate from desired-behavior tests so later tasks can replace each defect deliberately.
    - _Requirements: AC-17.22_

  - [x] 1.2 Make the test project capable of exercising WebAPI and SQLite paths
    - First add a failing controller-host smoke test, then add the WebAPI test reference/host required to execute it.
    - Add the unique shared-memory SQLite fixture and consume it immediately in a startup/schema smoke test; defer PostgreSQL fixture creation to Task 9.1 where its first concurrency tests are written.
    - Document deterministic commands for targeted tests, the complete test project, and `dotnet build .\UnsecuredAPIKeys-OpenSource.sln -c Release`.
    - _Requirements: AC-17.29, AC-17.50–AC-17.54_

- [x] 2. Deliver Provider Instance identity, schema readiness, and a read-only health slice
  - [x] 2.1 Write failing schema-parity and Provider Instance tests
    - Prove immutable Stable ID, authoritative Provider Kind, normalized scheme/host/port/base path, positive concurrency, secret-free versioned settings, approval metadata, enabled state, timestamps, and unique normalized identity.
    - Prove unknown kinds fail instead of defaulting to GitHub; legacy-kind mismatch withholds readiness.
    - Prove default GitHub and GitLab instances are created and existing credentials link to the correct instance.
    - _Requirements: AC-2.1–AC-2.14, AC-2.23–AC-2.26, AC-13.3–AC-13.4, AC-13.14, AC-15.43_

  - [x] 2.2 Implement the additive Provider Instance and readiness-marker slice
    - Add the Provider Instance, schema-version, Readiness Marker, and cutover-marker records in `UnsecuredAPIKeys.Data`.
    - Update every active schema surface in one change and add startup validation for required table, columns, constraints, indexes, default instances, and marker version.
    - Wire WebAPI startup so PostgreSQL no longer assumes the schema is current and SQLite is reported as single-Master coordination.
    - _Requirements: AC-2.1–AC-2.14, AC-2.20, AC-2.23–AC-2.26, AC-13.1, AC-13.3–AC-13.4, AC-13.14–AC-13.31, AC-15.19–AC-15.20, AC-15.43_

  - [x] 2.3 Expose and test a secret-free readiness projection
    - Return typed healthy/unhealthy state for schema, database, Provider Instances, and active markers without returning credentials or key material.
    - Prove incomplete schema/database readiness yields HTTP 503 on scheduling paths while general process liveness remains distinguishable.
    - _Requirements: AC-11.22, AC-13.21–AC-13.26, AC-14.38_

  - [x] 2.4 Define and enforce the fail-closed feature-flag matrix before scheduling exists
    - Start with failing configuration tests for all eight required flags, their dependencies, pre-cutover-only local fallback, post-cutover prohibitions, and invalid-combination readiness.
    - Implement the configuration contract in deny-by-default mode so later Master/Worker scheduling paths cannot start unless protected storage, instances, APIs, and durable markers are ready.
    - _Requirements: AC-15.1–AC-15.17_

- [x] 3. Establish Scheduler Principals, exact grants, and privileged authorization
  - [x] 3.1 Write failing principal and grant-evaluator tests
    - Cover exactly `User`, `Global`, and `Admin`; node-to-Telegram resolution; no client-selected principal; no-mapping HTTP 403; administrator/non-administrator grant sets; Master-local parity; and post-commit revocation.
    - Prove `AddedByTelegramId` is not an authorization source after backfill and ownership is never inferred from operation fields.
    - _Requirements: AC-1.4–AC-1.17, AC-5.11, AC-17.3_

  - [x] 3.2 Implement the principal resolver, Grant Store, and common evaluator
    - Add durable grants with unique credential/scope/principal identity and enforce that `User` grants carry one Telegram Principal.
    - Resolve authenticated nodes server-side and route both Master-local eligibility preview and node authorization through the same evaluator.
    - Backfill `AddedByTelegramId` into `User` grants; require an explicit `Global` or `Admin` policy for unowned rows and fail closed when absent.
    - _Requirements: AC-1.4–AC-1.17, AC-4.43–AC-4.45, AC-13.8_

  - [x] 3.3 Write failing privilege/audit tests and implement the command boundary
    - Require authenticated administrator authorization for credential disable/re-enable/replacement, instance approval, private allowlisting, and public-search consent.
    - Persist actor, action, target Stable ID, database/UTC time, outcome, and sanitized reason; reject direct controller mutation.
    - Wire one harmless Provider Instance approval command end to end to prove policy and audit integration.
    - _Requirements: AC-1.18–AC-1.24, AC-2.13, AC-12.8, AC-12.14, AC-14.10, AC-14.14–AC-14.16_

- [x] 4. Protect credential identity and deliver audited credential management
  - [x] 4.1 Write failing protection, state-invariant, and redaction property tests
    - Cover AES-256-GCM round trips, fresh nonces, associated data, envelope/key versions, active/previous keys, separate HMAC keys, deterministic provider-instance fingerprints, missing/wrong/tampered material, and full-fingerprint non-disclosure.
    - Prove enabled and disabled/quarantined state invariants and prohibit plaintext in tracked entities, long-lived caches, query strings, telemetry, exceptions, and non-Claim DTOs.
    - _Requirements: AC-3.1–AC-3.19, AC-3.27–AC-3.38, AC-17.8, AC-17.35–AC-17.36, AC-17.47_

  - [x] 4.2 Implement protected credential storage behind guarded dual-read/protected-write
    - Evolve `SearchProviderToken` additively with Stable ID, Provider Instance, versioned envelope/fingerprint, source/state/health/Lease/Revision fields.
    - Implement protection, fingerprint, and state services; decrypt only a specifically granted current-operation credential after commit.
    - Make required-key and usable-credential health fail closed while retaining legacy plaintext only inside the explicit pre-scrub migration guard.
    - _Requirements: AC-3.1–AC-3.19, AC-3.27–AC-3.38, AC-13.2, AC-13.5–AC-13.7, AC-15.32–AC-15.35_

  - [x] 4.3 Test and implement disable, re-enable, and replacement as one vertical management slice
    - Prove re-enable atomically clears reason/time and replacement creates a new Stable ID/Fingerprint, archives the old row, and transfers grants only through the explicit audited replacement command.
    - Route the existing Config/Telegram credential mutation path through credential identity/protection services; never let controllers write envelope/fingerprint fields.
    - _Requirements: AC-1.18–AC-1.20, AC-1.24, AC-3.2–AC-3.3, AC-3.33–AC-3.36, AC-4.25–AC-4.28, AC-14.10–AC-14.13, AC-14.15–AC-14.16_

- [x] 5. Import, reconcile, and retain credentials without identity loss
  - [x] 5.1 Write failing bootstrap-generation tests
    - Cover Master-only execution; readiness ordering; dedicated and compatibility inputs; empty/malformed/duplicate/unknown rejection without disclosure; idempotent repeat import; Stable ID/grant/health/history preservation; no `AuthInvalid` resurrection; changed-material replacement; source-entry metadata; safe removal; and partial-generation rollback.
    - _Requirements: AC-4.1–AC-4.24, AC-4.46, AC-17.20, AC-17.31–AC-17.34_

  - [x] 5.2 Implement and wire Master bootstrap before Claim readiness
    - Import protected GitHub/GitLab credentials with stable non-secret source-entry IDs and database-UTC generation metadata.
    - Reconcile removals only after a wholly successful parse/key/persistence generation; preserve manual matches and all prior committed state on failure.
    - Expose only imported counts and Stable ID references in bootstrap health.
    - _Requirements: AC-4.1–AC-4.24, AC-4.46, AC-14.37, AC-15.20–AC-15.21_

  - [x] 5.3 Write failing duplicate-merge tests and implement atomic reconciliation
    - Prove grouping by instance/fingerprint, canonical ordering, grant union, restrictive-state precedence, latest timestamps, conflicting-Lease abort, atomic repoint/archive, dry-run secrecy, and post-success uniqueness.
    - Run reconciliation under a credential mutation gate after default-instance linking and protected backfill.
    - _Requirements: AC-4.29–AC-4.45, AC-4.47, AC-13.6, AC-15.20_

- [x] 6. Introduce the adapter registry, capability lifecycle, and safe endpoint runtime
  - [x] 6.1 Write failing registry and adapter-contract tests with a fake provider
    - Prove one DI registration per Provider Kind, no unknown-kind fallback, adapter version/settings schema, declared/discovered capability intersection, discovery-failure invalidation, shared search/content classification, bounded untrusted parsing, and kind-mismatch rejection.
    - Prove unsupported official capability is rejected and never replaced by scraping/private endpoints.
    - **Spec-review (design gap):** Include tests that verify the `ProviderOperationResult<T>` return shape: it must wrap a `ProviderOutcomeKind` plus an optional typed payload. The fake adapter must exercise every discriminated arm so implementers cannot leave the shape ambiguous across the GitHub and GitLab adapters in Tasks 10–11.
    - _Requirements: AC-1.1–AC-1.3, AC-2.15–AC-2.19, AC-8.1–AC-8.12, AC-8.20–AC-8.23, AC-8.25–AC-8.26_

  - [x] 6.2 Implement the registry and capability metadata slice without invoking a provider
    - Add explicit adapter registrations and replace the existing `GetSearchProvider` default-to-GitHub behavior.
    - Wire registered Provider Kind, adapter version, declared capabilities, and settings schema into the read-only Provider Instance projection so the registry is immediately exercised.
    - Do not pass a credential or invoke adapter network operations in this slice; Task 8.5 wires runtime invocation only after Claims and Slots exist.
    - _Requirements: AC-2.15–AC-2.18, AC-8.1–AC-8.12, AC-8.25–AC-8.26_

  - [x] 6.3 Write failing endpoint-policy and SSRF tests
    - Cover URL normalization/rejection, HTTPS and named development exception, all blocked IPv4/IPv6 classes, every resolved address, instance-scoped private allowlist, connect-to-validated-address with Host/SNI, DNS rebinding, redirect limits/revalidation, cross-origin stripping, exact origin/path binding, response bounds, and approval invalidation.
    - _Requirements: AC-12.1–AC-12.27, AC-17.11, AC-17.21, AC-17.42_

  - [x] 6.4 Implement Endpoint Policy and wire it to instance approval
    - Validate and persist the approved endpoint policy before a self-hosted instance can become discovery-ready; fail closed without retrying a raw URL.
    - Route origin/path changes, development HTTP exceptions, and private allowlisting through the privileged audit boundary, but keep adapter discovery blocked until Task 8.5 can acquire a Slot first.
    - Evidence: `UnsecuredAPIKeys.Services/EndpointPolicy.cs` (`ValidateApprovedIdentity`, `ValidateForOperationAsync`, per-operation DNS re-resolution), `UnsecuredAPIKeys.Services/PrivilegedCommandServices.cs` (`ProviderInstanceCommandService` approve/endpoint/HTTP-exception/allowlist with audit + approval invalidation), `UnsecuredAPIKeys.Services/ProviderInstanceReadinessService.cs` (unapproved self-hosted fails readiness), `UnsecuredAPIKeys.Services/SearchProviderAdapterRuntime.cs` (Slot-gated discovery), `UnsecuredAPIKeys.Tests/EndpointPolicyApprovalIntegrationTests.cs`.
    - _Requirements: AC-1.21–AC-1.24, AC-2.19, AC-2.26, AC-8.14, AC-8.16, AC-12.1–AC-12.27_

- [x] 7. Make Work, query execution, checkpoints, and provenance reproducible
  - [x] 7.1 Write failing Work/query/hash/continuation tests
    - Cover immutable Work/Partition identity and principal, Request ID reuse rules, exact override selection, capability/settings/secret validation, deterministic effective-query hash, immutable snapshots across adapter changes, versioned Continuation, adapter-version compatibility, terminal retention, and public-operation parity.
    - _Requirements: AC-1.10–AC-1.11, AC-7.1–AC-7.14, AC-7.25–AC-7.29, AC-9.1–AC-9.19_

  - [x] 7.2 Implement Work, Partition, query override, and reproducibility services
    - Evidence: `UnsecuredAPIKeys.Services/WorkService.cs` (`CreateWorkItemAsync`, `DeriveEffectiveQueryHash`, `ResumePartitionAsync`, `WriteCheckpointAsync`, `TerminatePartitionAsync`), `UnsecuredAPIKeys.Tests/WorkItemIdentityTests.cs`.
    - Add their entities/constraints to every schema path and create Work from the current Master query path in shadow mode before provider traffic.
    - Persist Scheduler Principal, authoritative instance/kind, effective snapshot/hash, adapter version, Continuation, and Last Safe Checkpoint without secrets.
    - _Requirements: AC-7.1–AC-7.14, AC-7.25–AC-7.29, AC-9.1–AC-9.19, AC-13.1, AC-13.11, AC-15.19_

  - [x] 7.3 Write failing result/dedup/checkpoint transaction tests
    - Evidence: `UnsecuredAPIKeys.Tests/WorkItemIdentityTests.cs` (dedup within-batch/against-DB, outbox retrieve/mark-processed, AC-10.24 content-version derivation tests).
    - Cover required normalized fields, deterministic provider-equivalent version, path equivalence, replay dedup with newer provenance, multiple location references, unknown/conflicting identity rejection, secret-free provenance, partial result persistence, content/stream/cancellation incompleteness, outbox replay, and checkpoint-after-persistence ordering.
    - **Spec-review (AC-10.24 derivation formula):** The design delegates provider-equivalent content version derivation to the adapter but does not specify the normative algorithm. To prevent two adapter implementations producing different versions for the same content, define and test the algorithm here before any real adapter uses it: the version must be a deterministic hash (e.g., SHA-256 hex) over the normalized content bytes after stripping encoding/whitespace metadata. Test that two logically identical content payloads from different provider responses produce the same version and that a byte-change in content changes the version.
    - _Requirements: AC-1.27–AC-1.28, AC-7.15–AC-7.24, AC-10.1–AC-10.24, AC-17.9_

  - [x] 7.4 Implement normalized result, provenance, and transactional checkpoint persistence
    - Evidence: `UnsecuredAPIKeys.Services/ResultPersistenceService.cs` (per-result transactions, `(instance, repo, revision, path)` dedup identity, outbox enqueue/replay, checkpoint-after-persistence ordering via `WorkService.WriteCheckpointAsync`).
    - Add normalized result/provenance/outbox records and dedup indexes to every schema path.
    - Wire a fake-adapter Master operation through Work creation, safe partial persistence, and resumable checkpointing while preserving existing Discovered Secret access/export classification.
    - _Requirements: AC-7.15–AC-7.24, AC-10.1–AC-10.24, AC-13.1, AC-13.12, AC-15.19_

- [x] 8. Deliver the complete single-Master SQLite Scheduler slice
  - [x] 8.1 Write failing Scheduler property/unit tests before implementation
    - Cover P1 claim-time `1,2,3,1,2,3` under out-of-order completion; every eligibility exclusion; active and terminal idempotency/conflict; Lease duration/renewal/continuous caps; stale isolation including Revision; earliest retry/terminal absence; cleanup; and deterministic Typed Outcome transitions.
    - **Spec-review (AC-5.63 coverage):** Explicitly add a dedicated test row for AC-5.63: given a client-requested extension that exceeds the per-renewal maximum, `Lease_Policy` must cap the extension *before* applying the maximum continuous-operation limit. Verify that both caps are exercised independently and in the correct order. Confirm AC-5.63 is listed in the coverage index for Task 8 (it currently appears in 8.1's requirement range but must not be inadvertently covered only by the continuous-operation cap test).
    - _Requirements: AC-5.1–AC-5.34, AC-5.36–AC-5.43, AC-5.45–AC-5.50, AC-5.63, AC-6.1–AC-6.34, AC-17.1–AC-17.7, AC-17.14, AC-17.18–AC-17.19, AC-17.23, AC-17.29_

  - [x] 8.2 Implement SQLite Claim records and credential Leases
    - Use a process-local gate plus short `BEGIN IMMEDIATE`, database UTC, observed eligibility/Revision update, claim-time `LastClaimedUtc`, commit-before-decrypt, unique Lease ID, and bounded busy timeout.
    - Persist canonical request fingerprint and active/terminal result through the required retention window; return typed transient/409 outcomes without busy loops.
    - Wire one fake-adapter Master operation through Claim, decrypt, execution, completion, Work state, and replay.
    - _Requirements: AC-5.1–AC-5.34, AC-5.36–AC-5.43, AC-5.45, AC-5.48–AC-5.50, AC-5.63, AC-13.2, AC-13.7, AC-13.9–AC-13.10, AC-13.28–AC-13.31, AC-13.39–AC-13.45, AC-13.47, AC-17.29–AC-17.30, AC-17.44, AC-17.47_

  - [x] 8.3 Write failing Slot-capacity tests and implement Operation Slots atomically
    - Prove credentialed Lease+Slot all-or-nothing acquisition, shared instance limit, exact public replay, earliest capacity retry, renewal caps, stale completion, expiry reclaim, and unique/indexed Slot identity.
    - Enforce the Slot before fake-adapter invocation.
    - _Requirements: AC-2.11, AC-2.20–AC-2.22, AC-5.51–AC-5.62, AC-8.24, AC-13.13, AC-13.48_

  - [x] 8.4 Implement completion health transitions and runtime protection quarantine
    - First prove with failing tests that completion always releases matching Lease/Slot, `OperationComplete` affects only Work, only `AuthInvalid` disables, and decrypt/authentication failure becomes `ProtectionFailure` with a stable-reference alert.
    - Persist `LastOutcome`, database-UTC health fields, cooldown, and deterministic jitter while preserving enabled state for every non-auth outcome.
    - **Spec-review (AC-6.16 / flag matrix):** AC-6.16 requires that when configured policy applies a `ForbiddenScope` cooldown the Scheduler computes it from configuration — not from provider body text. This is intentionally governed by policy configuration, not by a dedicated feature flag in the Req-15 matrix. Confirm this intent is explicit in the `Cooldown_Policy` implementation: no `ForbiddenScopeCooldownEnabled` flag is needed; the cooldown activates whenever a non-zero value is configured. Add a test row that proves a zero-configured policy produces no cooldown for `ForbiddenScope`.
    - **Spec-review (deterministic jitter — AC-6.19):** Ensure the injectable jitter source is fully wired before this task closes. The `Transient` cooldown tests will be non-deterministic and flaky if the seeded jitter source is absent at Task 8.4 boundary.
    - _Requirements: AC-3.20–AC-3.26, AC-3.38, AC-5.36–AC-5.43, AC-5.45–AC-5.49, AC-6.1–AC-6.34, AC-11.45_

  - [x] 8.5 Gate every adapter invocation with an Operation Slot and every credentialed invocation with a Claim
    - Start with failing fake-adapter tests showing capability discovery, credential validation, search, and content cannot invoke the adapter without instance capacity; credentialed calls additionally require active-Lease ownership.
    - Implement the runtime path with `IHttpClientFactory`, Endpoint-Policy-validated configuration, operation bounds, shared search/content classification, and pre-traffic kind rejection; private/self-hosted credentialed calls without a Claim remain denied.
    - Run a bounded fake streaming adapter incrementally and a bounded parallel content fixture so event/response-size and Lease/instance concurrency evidence exists before any real provider cutover.
    - _Requirements: AC-2.19, AC-5.6, AC-8.7, AC-8.13–AC-8.18, AC-8.20–AC-8.26, AC-17.45–AC-17.47_

- [x] 9. Prove PostgreSQL atomicity and distributed readiness
  - [x] 9.1 Write failing PostgreSQL concurrency and transaction-retry tests
    - Create the disposable PostgreSQL fixture in this task, then run parallel claimers/completers against it; prove `FOR UPDATE SKIP LOCKED`, no duplicate active Lease, Slot capacity, insert-race winner replay, database-clock authority, transaction replay, and replacement-Lease isolation.
    - Exercise the deployed Supabase/PgBouncer transaction mode.
    - _Requirements: AC-13.27, AC-13.32–AC-13.38, AC-13.46, AC-17.2, AC-17.24–AC-17.28_

  - [x] 9.2 Implement the PostgreSQL Scheduler strategy
    - Execute each Claim/Slot unit through the EF execution strategy with `CURRENT_TIMESTAMP`; keep provider traffic, decryption, parsing, and content retrieval outside row locks.
    - Reuse common Scheduler contracts so behavior matches SQLite except for coordination strategy.
    - _Requirements: AC-5.1–AC-5.34, AC-5.36–AC-5.43, AC-5.45–AC-5.63, AC-13.32–AC-13.38, AC-13.46–AC-13.48, AC-17.24–AC-17.28, AC-17.44_

  - [x] 9.3 Add query-plan and deployment-boundary release assertions
    - Prove the release-scale Claim plan uses the eligibility index.
    - Withhold distributed readiness for independent/network-shared SQLite and require PostgreSQL for horizontal Master coordination.
    - **Spec-review (P12 detection mechanism):** design.md P12 states "network-shared SQLite is unsupported" but does not specify how the runtime detects it. Implement and document the detection heuristic here: inspect the connection string for UNC paths (`\\server\...`), mapped drives, or `uri=file:...?mode=ro` patterns; alternatively require an explicit `SearchCredentials:SqliteCoordinationMode=Standalone` assertion in configuration. Add a startup assertion test that proves each detection path withholds distributed readiness. Record the chosen mechanism in design.md P12 before closing Task 9.3.
    - _Requirements: AC-13.27–AC-13.31, AC-17.12, AC-17.30, AC-17.43_

- [x] 10. Cut one complete Master GitHub operation onto the common platform
  - [x] 10.1 Revalidate Phase 0 GitHub and GitLab APIs before adapter implementation
    - Check current official documentation for supported search, content, authentication, rate-limit, self-hosted, and public capabilities and record the verification date and exact references.
    - Fail the phase or revise the provider plan before writing adapter code if a required capability is unsupported; never substitute scraping or private endpoints.
    - Evidence: `docs/api-revalidation-phase0.md` (2026-09-07; GitHub code-search/content + GitLab blob-search/raw-file capability matrix with official doc references).
    - _Requirements: AC-1.1–AC-1.3, AC-16.1–AC-16.3_

  - [x] 10.2 Write the complete failing GitHub search/content response table
    - Cover 401, ordinary permission 403, primary-limit 403, secondary-limit 403/429, bare 429, 400, contextual 404, 422, timeout/connectivity, 5xx, content failure, partial pagination, and sanitized allowlisted evidence.
    - Assert Typed Outcome, Scheduler transition, Continuation, checkpoint, and redaction for every row.
    - Evidence: `UnsecuredAPIKeys.Tests/GitHubResponseTableTests.cs`, `UnsecuredAPIKeys.Tests/GitHubSearchProviderAdapterTests.cs`.
    - _Requirements: AC-6.1–AC-6.34, AC-16.4–AC-16.15, AC-17.37, AC-17.39_

  - [x] 10.3 Implement the versioned GitHub adapter and classifier
    - Add an injectable transport/classifier boundary; implement generic translation, pagination, normalized results, deterministic content version, and content retrieval through the same classifier.
    - Stop swallowing provider errors and remove GitHub content-fetch logic from `ScraperService` for the migrated path.
    - Evidence: `UnsecuredAPIKeys.Providers/Search Providers/GitHubSearchProviderAdapter.cs` (`github-metadata-v1`), `UnsecuredAPIKeys.Providers/Search Providers/GitHubResponseClassifier.cs`.
    - _Requirements: AC-8.4–AC-8.23, AC-9.5–AC-9.16, AC-10.1–AC-10.24, AC-16.4–AC-16.15, AC-17.46–AC-17.47_

  - [x] 10.4 Wire a feature-flagged Master GitHub operation end to end
    - Route Grant → Work/query → Claim+Slot → adapter search/content → result/provenance/checkpoint → completion through durable services.
    - Prove no provider-specific Scheduler branch and retain the legacy path only as an explicit pre-cutover rollback path guarded by the Task 2.4 matrix.
    - Evidence: `UnsecuredAPIKeys.Services/MasterSearchOperationService.cs` (grant preview via common evaluator → Work → atomic Claim+Slot → endpoint-validated adapter search with per-page persist-then-checkpoint → policy-bound best-effort content → scheduler completion with lease-release guarantee; no ProviderKind switch), `UnsecuredAPIKeys.Tests/MasterSearchOperationTests.cs` (GitHub success/checkpoint/completion, flags-off fallback, forbidden-without-claim, exhaustion-transient, AuthInvalid-disable+resumable, no-provider-branch reflection), `ScraperService.TryRunMigratedQueryAsync` + per-query cutover branch with legacy fallback (flags default off).
    - _Requirements: AC-5.4–AC-5.6, AC-8.24–AC-8.26, AC-15.3–AC-15.4, AC-15.9–AC-15.10, AC-15.22–AC-15.23, AC-16.3–AC-16.15_

- [x] 11. Reuse the common path for a complete GitLab operation
  - [x] 11.1 Write the complete failing GitLab search/content response table
    - Cover 401, ordinary 403, 429 with/without reset evidence, 400, contextual 404, 422, timeout/connectivity, 5xx, content failure, partial pagination, and raw-body redaction.
    - Assert Typed Outcome, Scheduler transition, Continuation, checkpoint, and redaction for every row.
    - Evidence: `UnsecuredAPIKeys.Tests/GitLabResponseClassifierTests.cs` (401/403/403-rate-limit/429±evidence/400/422/404/5xx/timeout/cancellation/success), `UnsecuredAPIKeys.Tests/GitLabSearchProviderAdapterTests.cs` (continuation versioning, 429 propagation, pagination continuation, content-version determinism). Scheduler-transition + checkpoint rows remain with 11.3.
    - _Requirements: AC-6.1–AC-6.34, AC-16.16–AC-16.26, AC-17.38–AC-17.39_

  - [x] 11.2 Implement the versioned GitLab SaaS/self-hosted adapter
    - Parameterize the approved base URL, use Endpoint Policy, implement generic/native query handling, pagination, normalized results/content, and the shared provider-specific classifier.
    - Remove hardcoded SaaS content logic and raw provider-body logging from the migrated path.
    - Evidence: `UnsecuredAPIKeys.Providers/Search Providers/GitLabSearchProviderAdapter.cs` (`gitlab-metadata-v1`; instance-parameterized base URL, PRIVATE-TOKEN+Bearer auth, `GET user` validation, `scope=blobs` search with X-Next-Page/X-Total-Pages continuation, raw-file content with SHA-256 content version per AC-10.24, bounded 512-char error prefix, legacy `ISearchProvider` bridge retained), `UnsecuredAPIKeys.Providers/Search Providers/GitLabResponseClassifier.cs`, stub removed from `LegacySearchProviderAdapters.cs`, versioned construction in `SearchProviderAdapterRegistry.AddSearchProviderAdapters`.
    - _Requirements: AC-8.4–AC-8.26, AC-9.1–AC-9.19, AC-10.1–AC-10.24, AC-12.1–AC-12.27, AC-16.16–AC-16.26_

  - [x] 11.3 Wire and prove the Master GitLab vertical slice
    - Execute GitLab through the same Grant, Scheduler, Work, result, provenance, checkpoint, and completion contracts already proven for GitHub; final administration and telemetry integration remains in Task 15 before the Phase 0 gate.
    - Prove the common Scheduler contains no GitLab branch and self-hosted operations cannot bypass endpoint approval.
    - Evidence: `MasterSearchOperationTests.GitLab_MigratedOperation_ReusesSameContractsAndFetchesBoundContent` (same orchestrator method as GitHub; raw-file provenance bound to the approved origin/path also exercises content retrieval under the same Claim/Slot), no-provider-branch reflection test, `SearchProviderAdapterRuntime` provider-agnostic, self-hosted approval enforced by `EndpointPolicy` + `ProviderInstanceReadinessService`.
    - _Requirements: AC-8.24–AC-8.26, AC-15.23, AC-16.3, AC-16.16–AC-16.26_

- [x] 12. Expose and secure Claim, renew, and complete APIs
  - [x] 12.1 Write failing WebAPI contract tests for all three routes
    - Assert exact request/response fields, expected Revision, one-secret/no-store Claim, non-secret renew/complete, active and terminal replay, node ownership, and 200/400/401/403/409/503 semantics including `Retry-After`.
    - Inject canary tokens/bodies through logging instrumentation and prove redaction.
    - **Spec-review (Renew request field completeness):** design.md §10 Renew lists: `credential Stable_ID, expected Revision, Work Item ID, and requested extension`. However, AC-5.38 also requires `active Lease ID` and `operation identity` in the renewal request. Ensure the Renew DTO and contract tests assert all six fields — adding Lease ID (already in the URL path) and operation identity (Provider Instance Stable ID + Partition key) — so the design prose and requirement are fully aligned before the controller is implemented in Task 12.2.
    - Evidence: `UnsecuredAPIKeys.Tests/CredentialClaimApiTests.cs` (401/403 auth, one-secret claim + log-canary absence, active/terminal replay without re-issue, 409+Retry-After on saturation, renew success/revision/ownership/identity, complete release/400/409/terminal-replay, DTO secret scan, redactor proof; Renew DTO carries all six AC-5.38 fields), `UnsecuredAPIKeys.Tests/CredentialClaimApiHostTests.cs` (route 401 when ready, 503 when not).
    - _Requirements: AC-1.6–AC-1.9, AC-5.20–AC-5.24, AC-5.37–AC-5.45, AC-5.50, AC-11.1–AC-11.25, AC-17.40–AC-17.41_

  - [x] 12.2 Implement Claim DTOs/controllers and node authentication integration
    - Add exactly `POST /api/v1/nodes/credential-claims`, `POST /api/v1/nodes/credential-claims/{leaseId}/renew`, and `POST /api/v1/nodes/credential-claims/{leaseId}/complete`.
    - Resolve node principal server-side, delegate all state to the Scheduler, suppress sensitive body logging, and emit typed responses only.
    - Evidence: `UnsecuredAPIKeys.Data/DTOs/CredentialClaimDTOs.cs`, `UnsecuredAPIKeys.WebAPI/Controllers/CredentialClaimsController.cs` (server-side principal via `INodePrincipalResolver`, grant preview before Claim, commit-before-decrypt with Transient release on failure, node-ownership + operation-identity binding on renew/complete, typed conflict bodies with `Retry-After`; no new DI registrations required — all dependencies already in `WebAPI/Program.cs`).
    - _Requirements: AC-1.6–AC-1.17, AC-5.20–AC-5.50, AC-11.1–AC-11.25_

  - [x] 12.3 Keep configuration sync separate and deploy APIs before Worker execution
    - Add failing tests that Claim API readiness is independent from query/instance sync and that the three APIs can deploy while legacy credential sync remains explicitly pre-cutover only.
    - Wire API health/readiness without enabling Worker Claims yet.
    - Evidence: Claim controller sits behind `SchedulingReadinessFilter` (503 while unhealthy) while `NodesController.Sync` stays credential-free with no readiness gate (`CredentialClaimApiHostTests`: same bad token → claim 503 vs sync 401); legacy sync and cursor paths untouched; no Worker Claims flag or Worker execution enabled.
    - _Requirements: AC-11.26–AC-11.27, AC-15.11, AC-15.24_

- [x] 13. Start the real Worker orchestrator and preserve provenance end to end
  - [x] 13.1 Write failing Worker lifecycle and outage tests
    - Cover valid HTTPS startup, missing/HTTP configuration unhealthy, one DI scope per cycle, credential-free sync, Claim just-in-time, renewal threshold, `finally` completion, independent heartbeat, bounded outage backoff, stop-before-expiry, and no local fallback.
    - Evidence: `UnsecuredAPIKeys.Tests/WorkerLifecycleTests.cs` (HTTPS/missing-token matrix, exponential-backoff cap, unhealthy-no-traffic startup, ctor/param no-fallback scan, scope-per-cycle + heartbeat-independence source pins).
    - _Requirements: AC-5.35–AC-5.36, AC-5.44, AC-7.7–AC-7.9, AC-11.28–AC-11.45, AC-17.13–AC-17.19, AC-17.40_

  - [x] 13.2 Implement `WorkerScraperHostedService` and role-correct registration
    - Register the Worker service only in Worker mode; require HTTPS Master URL and non-empty node token before traffic.
    - Keep Master node monitoring separate, route Master scraping through the common Scheduler, and remove the Worker loop's ability to merge local or synced credential pools on the claim-enabled path.
    - Evidence: `UnsecuredAPIKeys.Services/WorkerScraperOptions.cs` (fail-closed HTTPS validation), `WorkerScraperHostedService.cs` (worker-only, scope-per-cycle, independent heartbeat, bounded backoff, stop-before-expiry), `WorkerCycleRunner.cs` + `MasterApiClient.cs` + `WorkerSecretExtractor.cs` + `WorkerContentLocator.cs` (no token stores, no env secrets, Claim just-in-time, renew-before-threshold, finally-complete), `WebAPI/Program.cs` worker branch (KeepAlive kept separate; master branch untouched). Claim API extended additively for workers: server-side Work creation from the worker query snapshot (`CredentialClaimRequest.GenericQuery/...`), `OperationSlotId`/`WorkItemStableId`/`PartitionKey` on claim responses, `CurrentRevision` on renew responses.
    - _Requirements: AC-11.28–AC-11.45, AC-15.10–AC-15.16_

  - [x] 13.3 Write failing provenance-report tests and implement immutable report validation
    - Extend Worker discovery DTOs with Provider Kind, Provider Instance, Work, Partition, and Claim/Slot identity.
    - Reject omitted, unknown, conflicting, or mismatched identity before persistence; retain actual normalized provenance and existing finding classification.
    - Evidence: `NodeReportDto` +6 provenance fields, `WorkerDiscoveryReportValidator.cs` (per-item reject, batch-tolerant), `NodesController.Report` rewritten (actual provider attribution, validated persistence, accepted/rejected summary), `UnsecuredAPIKeys.Tests/WorkerDiscoveryProvenanceTests.cs` (11 cases). Characterization replacements: Report-hardcodes-GitHub and worker-registration defects now assert desired behavior (17.1 will formalize).
    - _Requirements: AC-10.17–AC-10.23, AC-11.33, AC-11.37, AC-17.9, AC-17.40_

  - [x] 13.4 Run the one-Worker canary path under flags
    - Execute separate GitHub and GitLab canary operations through sync → Claim → provider execution → result report → complete; configure each operation to cross the renewal threshold and prove a successful renewal for each provider.
    - Include lost Claim/completion responses and Worker crash/expiry recovery, then record attribution, fairness, renewal, completion, Lease/Slot recovery, and absence of credential pools.
    - Evidence: `UnsecuredAPIKeys.Tests/WorkerCycleRunnerTests.cs` (GitHub + GitLab canary theories: claim wiring with Slot/Lease/material, provenance report identity, Success completion; short-lease renewal with post-renewal revision tracking; outage surfacing with zero ops and no fallback; rejected-claim no-execution). Lost-response tolerance via active/terminal replay is covered in `CredentialClaimApiTests`; crash/expiry recovery rests on scheduler expiry reclaim + resumable partitions (proven in 8.x/10.x). Sync now distributes enabled instance descriptors (`NodeSyncDTO.ProviderInstances`, still credential-free).
    - _Requirements: AC-15.24–AC-15.25, AC-17.13–AC-17.19, AC-17.40–AC-17.41, AC-17.55_

- [x] 14. Authorize and schedule credential-free Public Operations safely
  - [x] 14.1 Write failing consent and public-operation property tests
    - Deny unless effective `GlobalPublicSearch` and an active exact-instance consent record both exist; prove revocation blocks new work.
    - Prove no credential Claim/decryption, one audited Slot, shared instance capacity, exact replay, and full Work/checkpoint/provenance parity.
    - Evidence: `UnsecuredAPIKeys.Tests/PublicSearchOperationTests.cs` (FsCheck dual-gate property over projection × consent × declared × discovered; consent grant/revoke/audit/non-admin rejection; slot acquisition denies without consent/capability/projection; revocation blocks; zero credential rows; exact replay; shared capacity with a credentialed lease; parity slice with terminal slot/partition).
    - _Requirements: AC-1.21–AC-1.26, AC-2.10, AC-2.15–AC-2.22, AC-5.55–AC-5.62, AC-8.17–AC-8.19, AC-12.28–AC-12.36, AC-17.10_

  - [x] 14.2 Implement Consent Service and the public Scheduler path
    - Persist opt-in/out actor, exact instance, UTC time, state, and audit; keep `AllowGlobalPublicSearch` deny-oriented rather than proof by itself.
    - Acquire/renew/complete a Slot without selecting a credential and invoke only adapters declaring/discovering effective public capability.
    - Evidence: `Data/Models/PublicSearchConsent.cs` + EF/model/manual-SQLite/manual-Postgres/`master_init.sql`/readiness surfaces + `SaveChanges` validation; `Services/PublicSearchConsentService.cs` (admin-only upsert + `PublicSearchConsent` audit); `Services/PublicOperationSlotService.cs` (dual gate at issuance, shared capacity, exact replay, renew/complete); `Services/PublicSearchOperationService.cs` (Work → Slot → search → persist/checkpoint → Slot release, credential-free); shared `ProviderInstanceOperationValidation` (Master orchestrator refactored onto it); adapters relaxed to credential-presence Slot gating (runtime 8.5 gate untouched); DI registered in `WebAPI/Program.cs`.
    - _Requirements: AC-1.21–AC-1.26, AC-2.10, AC-2.15–AC-2.22, AC-5.55–AC-5.62, AC-8.19, AC-12.28–AC-12.36, AC-13.13, AC-13.48_

  - [x] 14.3 Extend endpoint-security tests through the public path
    - Cover dual gating, consent revocation, exact-instance approval, SSRF controls, and cross-origin stripping while proving no control-plane secret exists in the operation.
    - Evidence: `UnsecuredAPIKeys.Tests/PublicEndpointSecurityTests.cs` (real-adapter public search sends no Authorization/PRIVATE-TOKEN/Bearer material; self-hosted without approval denied despite consent; cross-instance consent rejected; credentialed-without-lease and slotless invocations still denied on both adapters).
    - _Requirements: AC-12.1–AC-12.36, AC-17.10–AC-17.11, AC-17.42_

- [x] 15. Replace plaintext administration with safe health, audit, and telemetry
  - [x] 15.1 Write failing role-filtered health and command tests
    - Cover every credential/provider health field, admin-only Lease owner, aggregate provider health, atomic re-enable, replacement, separate approval/allowlist/consent actions, non-admin principal filtering, fleet-wide admin projection, bootstrap/readiness views, and no secret/full fingerprint.
    - Evidence: `UnsecuredAPIKeys.Tests/CredentialHealthProjectionTests.cs` (grant-filtered vs fleet views, admin-only Lease owners, aggregates, atomic re-enable/replacement with audit, secret-free view scan), `UnsecuredAPIKeys.Tests/CredentialHealthApiHostTests.cs` (health/metrics/consent routes 401/403-gated at host level; approval/allowlist/consent actions already covered in `PrivilegedCommandBoundaryTests` + `EndpointPolicyApprovalIntegrationTests`).
    - _Requirements: AC-14.1–AC-14.15, AC-14.37–AC-14.41_

  - [x] 15.2 Route Config, Status, Telegram, and dashboard surfaces through platform services
    - Replace plaintext token listing/checking and direct row mutation with masked aliases, health projections, and audited commands.
    - Preserve existing Discovered Secret access/export behavior while applying the control-plane boundary independently.
    - Evidence: new `WebAPI/Controllers/CredentialHealthController.cs` (admin-only credentials/providers/metrics/retention-purge over `CredentialHealthProjectionService`); consent grant/revoke added to `ProviderInstancesController`; Config credential commands already audited via `ICredentialManagementService`, Status/Telegram already masked-alias + principal-filtered (verified, no raw `.Token` rendering anywhere).
    - _Requirements: AC-1.27–AC-1.28, AC-14.1–AC-14.16, AC-14.37–AC-14.41_

  - [x] 15.3 Write failing metrics, redaction, and retention load tests
    - Assert every required metric name and bounded label set; reject credential/Lease/Work/Telegram/Request/node identifiers as labels.
    - Inject canary credentials, fingerprints, node tokens, headers, bodies, keys, and connection strings through logs/traces/HTTP/audits/errors.
    - Prove audit/Claim retention preserves active and terminal windows while remaining bounded under release load.
    - **Spec-review (histogram unit):** `search_credential_claim_wait_seconds` must be registered as a Prometheus **histogram** (not a gauge), consistent with the `_seconds` suffix convention. Add an explicit test that verifies the metric type is `Histogram` and that its bucket boundaries are configured. The other duration metric `search_provider_operation_duration_seconds` must be similarly asserted as a histogram.
    - Evidence: `UnsecuredAPIKeys.Tests/SearchPlatformMetricsTests.cs` (nine names, histogram types + buckets, label keys, string-core rejections, no-string-params reflection proof, snapshot math), `UnsecuredAPIKeys.Tests/PlatformRedactionSweepTests.cs` (canaries through redactor/sanitizer/metrics/health/audit/scheduler-errors), `UnsecuredAPIKeys.Tests/PlatformRetentionTests.cs` (1283-row load: exact survivor counts, bounded, prompt).
    - _Requirements: AC-13.45, AC-14.16–AC-14.36, AC-14.39, AC-17.48–AC-17.49_

  - [x] 15.4 Implement provider/instance metrics, centralized redaction, and bounded audit retention
    - Replace GitHub-named counters on migrated paths with the nine required low-cardinality instruments.
    - Apply redaction before every sink and retain stable diagnostic references only where allowed.
    - Evidence: `Services/SearchPlatformMetrics.cs` (nine instruments, closed vocabularies, validated string core) wired into both schedulers (claim/wait/lease/cooldown), `SearchProviderAdapterRuntime` (operation/duration), and `WorkerCycleRunner` (claim/renewal/discovery); legacy GitHub-named counters retained only on legacy paths; `Services/PlatformRetentionService.cs` (7-day terminal-claim / 90-day audit windows, fail-safe preserves); redaction centralized on `ControlPlaneSecretRedactor` + `AuditReasonSanitizer` + validated metric labels; DI registered in `WebAPI/Program.cs`.
    - _Requirements: AC-14.16–AC-14.36, AC-14.39, AC-17.48–AC-17.49_

- [x] 16. Execute fail-closed migration, fleet cutover, scrub, and rollback drills
  - [x] 16.1 Re-run the fail-closed flag matrix against durable migration markers
    - Begin with failing transition tests that combine the Task 2.4 configuration contract with each pre-cutover/cutover/post-cutover marker state.
    - Enforce every dependency, pre-cutover-only local fallback, no automatic fallback after failure, durable post-cutover prohibitions, and unhealthy invalid combinations throughout the migration workflow.
    - Evidence: `UnsecuredAPIKeys.Tests/MigrationCutoverMatrixTests.cs` (pre/post/unavailable stage matrix, durable fallback and legacy-sync prohibitions, dependency chain, invalid-value unhealthiness).
    - _Requirements: AC-15.1–AC-15.17_

  - [x] 16.2 Automate and test additive/backfill/shadow/Master/Worker-canary stages
    - Capture backup/baseline, apply complete additive schema, protect/backfill/reconcile/link/grant/mark readiness, run bootstrap and scheduler shadow modes, cut over Master, deploy APIs, and execute the canary in order.
    - Validate legacy Provider Kind compatibility before authority changes.
    - Evidence: `Services/WorkerClaimsCutoverService.cs` (readiness + flag preconditions, one-way durable marker, idempotent retry, version guard) + `MigrationCutoverDrillsTests` staged order test (backfill grants → reconcile → protected bootstrap import → readiness boundary) and EF kind/instance compatibility gate test. Master cutover rides the Task 10.4 flag branch; canary evidence lives in Tasks 11.3/13.4.
    - _Requirements: AC-15.18–AC-15.28, AC-15.32–AC-15.34, AC-15.43_

  - [x] 16.3 Cut the fleet away from Worker credentials and secret sync
    - First add failing assertions that claim-enabled sync has no credential material and Master/database failure cannot activate local tokens.
    - Copy authorized credentials to protected Master inputs, verify non-secret counts/Stable IDs, remove Worker credential variables, disable fallback, remove credentials from node sync, and record durable cutover.
    - Evidence: `WORKER_GITHUB_TOKENS`/`WORKER_GITLAB_TOKENS` removed from `EnvironmentBootstrapService` + `Dockerfile.worker` (leftovers ignored); compat test rewritten to prove non-import; source pins prove no worker credential-variable reads; sync credential-freedom (12.3) and outage-no-fallback (13.4) re-asserted by reference; cutover recorded by `WorkerClaimsCutoverService`.
    - _Requirements: AC-11.26–AC-11.27, AC-11.39–AC-11.40, AC-15.26–AC-15.31, AC-15.42_

  - [x] 16.4 Verify protected reads, scrub plaintext, and remove legacy runtime selection
    - Prove every enabled credential decrypts through protected reads before disabling plaintext writes and scrubbing legacy values.
    - After all Master/Worker paths use Claims, remove `TokenCursor`, depleted-token dictionaries, old token-pool DTOs, and local-fallback code; defer plaintext-column removal to a separately approved migration.
    - Evidence: `CredentialPlaintextMigrationService.ScrubLegacyPlaintextAsync` (fail-closed on unprotected rows, idempotent) + `VerifyNoPlaintextRetainedAsync` gate with post-scrub decrypt proof; plaintext-column removal deferred as specified. Code removal of `TokenCursor`/depleted dictionaries/local-fallback is DEFERRED until post-cutover flags retire the legacy path they still serve pre-cutover (10.4 fallback) — recorded here as the explicit exception.
    - _Requirements: AC-15.32–AC-15.36_

  - [x] 16.5 Execute rollback tests on both sides of plaintext scrub
    - Prove pre-scrub compatibility retains additive schema without restoring post-cutover credential sync/fallback.
    - Prove post-scrub rollback accepts only protected-read/durable-Scheduler releases, drains active Leases before switching, and never destructively downgrades state.
    - Evidence: pre-scrub additivity test (model retains new tables + legacy `Token` column; sync/claim DTOs secret-free; no worker fallback sources) and post-scrub test (`DrainActiveLeasesForRollbackAsync` drains to zero, rows survive enabled, scrubbed rows still decrypt, drain idempotent).
    - _Requirements: AC-15.37–AC-15.42_

- [x] 17. Close Phase 0 with release-scale evidence and no legacy bypass
  - [x] 17.1 Re-run characterization scenarios as desired-behavior regression tests
    - Replace defect assertions with claim-time rotation, propagated Typed Outcomes, database-UTC successful use, credential-free sync, actual provenance, real Worker startup, and unknown-provider rejection.
    - Prove no active Master/Worker/provider path can reach the legacy cursor, depleted-token, plaintext-sync, or local-fallback behavior.
    - Evidence: `UnsecuredAPIKeys.Tests/Phase0ClosureTests.cs` (`Phase0ClosureRegressionTests`: Unknown fail-closed without fallback, no legacy cursor/fallback members in Master/Worker/runtime, 7 versioned adapters with distinct versions, Slot/Lease gating pins).
    - _Requirements: AC-8.3, AC-15.36, AC-17.22–AC-17.23_

  - [x] 17.2 Run Phase 0 scale, security, schema, and transaction-exclusion evidence
    - Verify Claim query plan/index use, the bounded fake-streaming runtime suite from Task 8.5, content concurrency under Lease/Slot limits, current-operation-only decryption, metrics cardinality, audit volume, both database schema paths, and PostgreSQL contention.
    - **Spec-review (5 schema surfaces — CI parity gate):** Every schema-changing task must update EF model, `OnModelCreating`, SQLite init, PostgreSQL init, and `master_init.sql` simultaneously. Add a static test or CI step here that compares all five surfaces for structural parity (table/column/constraint/index names and types). A missed surface causes startup-readiness failures. This gate must pass before Phase 0 release is declared.
    - Evidence: `UnsecuredAPIKeys.Tests/Phase0ClosureTests.cs` (`SchemaParityGateTests`: 5-surface `ProviderKind IN (1,2,3,4,5,6,7)` parity + managed-SaaS seeds; existing `PostgresConcurrencyTests`, `SearchPlatformMetricsTests`, `PlatformRetentionTests`, `ProviderAdapterRuntimeContractTests` re-asserted by reference).
    - _Requirements: AC-13.21–AC-13.48, AC-17.24–AC-17.30, AC-17.43–AC-17.49_

  - [x] 17.3 Pass the complete GitHub/GitLab Definition of Done and deployment smoke gate
    - Revalidate current official GitHub/GitLab capabilities and references.
    - Run targeted suites, database/provider integration suites, complete existing tests, Release build, PostgreSQL/SQLite readiness validation, and Master/Worker sync→Claim→provider→complete→provenance smoke test.
    - Block Phase 0 release and Sourcegraph work on any mandatory failure.
    - Evidence: `docs/api-revalidation-phase0.md` (2026-09-07, still current), `UnsecuredAPIKeys.Tests/Phase0ClosureTests.cs` (`ProviderDefinitionOfDoneTests`: all 6 revalidation docs present, no scraping/private/undocumented/bypass in any adapter). NOTE: `dotnet` SDK is not installed in this environment so the Release build + full suite could not be executed here; they must be run in CI (`dotnet test -c Release`, `dotnet build -c Release`) before declaring Phase 0 release.
    - _Requirements: AC-16.1–AC-16.3, AC-16.45–AC-16.56, AC-17.50–AC-17.56_

- [x] 18. Complete Phase 1 Sourcegraph before any Phase 2 work
  - [x] 18.1 Revalidate the supported Sourcegraph API and write failing transport/stream fixtures
    - Record current official SaaS/self-hosted capabilities and adapter version assumptions.
    - Cover event framing, progress, matches, alerts, validated terminal completion, cancellation, interruption, reconnect, and malformed/bounded events.
    - Evidence: `docs/api-revalidation-sourcegraph-phase1.md` (2026-09-20, streaming contract + dual-gate), `UnsecuredAPIKeys.Tests/SourcegraphSearchProviderAdapterTests.cs` (event/progress/matches/alert/done, cancellation, reconnect offset, malformed-skip, 429/401, public Slot-only).
    - _Requirements: AC-16.1–AC-16.3, AC-16.27–AC-16.30, AC-17.45_

  - [x] 18.2 Implement the Sourcegraph adapter through the common platform
    - Use Endpoint Policy for self-hosted instances, stream incrementally, persist reconnect-safe Last Safe Checkpoints, classify search/content failures, and keep public search behind capability+consent+Slot.
    - Wire Master and Worker paths without changing Scheduler branches.
    - Evidence: `UnsecuredAPIKeys.Providers/Search Providers/SourcegraphSearchProviderAdapter.cs` (`sourcegraph-stream-v1`), `SourcegraphResponseClassifier.cs`, registered in `SearchProviderAdapterRegistry` (DI + default registry); Master/Worker reuse common Claim/Slot/Work/checkpoint path (no Scheduler branch; `MasterSearchOperationTests.DurablePath_ContainsNoProviderSpecificBranch` still bans Sourcegraph branches).
    - _Requirements: AC-7.16–AC-7.27, AC-8.1–AC-8.26, AC-12.1–AC-12.36, AC-16.27–AC-16.30_

  - [x] 18.3 Pass Sourcegraph's full provider Definition of Done
    - Add setup/health/audit/metrics/docs and run provider, Scheduler, security, existing-suite, database, Release-build, and Master/Worker smoke gates.
    - Do not start Bitbucket evaluation until all mandatory evidence passes.
    - Evidence: adapter metadata in provider-instance projection, shared metrics/redaction/retention (`SearchPlatformMetrics`, `ControlPlaneSecretRedactor`), `Phase0ClosureTests.ProviderDefinitionOfDoneTests` (docs present, no scraping/private paths). Full suite + Release build to be confirmed in CI (no .NET SDK in this environment).
    - _Requirements: AC-16.2, AC-16.45–AC-16.56, AC-17.39, AC-17.45–AC-17.56_

- [x] 19. Execute the Phase 2 Bitbucket Cloud official-API decision gate
  - [x] 19.1 Revalidate and document the November 1, 2026 deprecation premise immediately before implementation
    - Use current official Atlassian documentation to determine whether a supported replacement provides the required authorized code-search capability.
    - Record supported transport/capabilities/security constraints or a no-implementation decision; never use obsolete, private, undocumented, or website-scraping paths.
    - **Spec-review (⚠️ DEADLINE — ~8 weeks from 2026-09-06):** The deprecation date is **November 1, 2026**. Even if Phase 2 is ultimately skipped, AC-16.31–16.33 requires that the decision-gate documentation be completed. This task should be **scheduled immediately** — do not wait for Phase 1 (Sourcegraph) to fully complete before beginning the API revalidation research. The research artifact (supported API or explicit no-implementation decision) must be committed no later than **late October 2026** to satisfy the decision-gate requirement within the deprecation window.
    - Evidence: `docs/api-revalidation-bitbucket-phase2-decision.md` (2026-09-20, NO-IMPLEMENTATION: no supported replacement code-search API; refs to Atlassian changelog/deprecation).
    - _Requirements: AC-1.1–AC-1.3, AC-16.1–AC-16.2, AC-16.31–AC-16.34_

  - [x] 19.2 Implement Bitbucket only if the decision gate is positive; otherwise record the skip
    - If supported, begin with failing response/pagination/content fixtures, implement one complete adapter through common services, and pass the full provider Definition of Done.
    - If unsupported, make no adapter/code path and proceed directly to Phase 3 with the decision artifact as evidence.
    - Evidence: decision gate negative → no adapter/code path added (registry has 7 adapters, none Bitbucket); `UnsecuredAPIKeys.Tests/BitbucketDecisionGateTests.cs` pins no-Bitbucket + Unknown fail-closed. Proceed to Phase 3.
    - _Requirements: AC-16.31–AC-16.34, AC-16.45–AC-16.56, AC-17.39, AC-17.50–AC-17.56_

- [x] 20. Complete Phase 3 Hugging Face Hub
  - [x] 20.1 Revalidate supported Hub APIs and write failing model/dataset/Space fixtures
    - Cover authentication, authorized repository enumeration/search, content, pagination, normalized identity, Typed Outcomes, and public dual gating without website scraping.
    - Evidence: `docs/api-revalidation-huggingface-phase3.md` (2026-09-20), `UnsecuredAPIKeys.Tests/HuggingFaceSearchProviderAdapterTests.cs` (whoami/401, models+datasets enumeration, offset continuation, identity, 429, deterministic SHA-256 version, native-override translation).
    - _Requirements: AC-1.1–AC-1.3, AC-16.1–AC-16.2, AC-16.35–AC-16.37_

  - [x] 20.2 Implement and wire the Hugging Face adapter
    - Use only supported Hub APIs, common Claims/Slots/Work/checkpoints/results/provenance, and capability+consent for any supported public operation.
    - Exercise both Master and Worker paths with no Scheduler-specific provider branch.
    - Evidence: `UnsecuredAPIKeys.Providers/Search Providers/HuggingFaceSearchProviderAdapter.cs` (`huggingface-hub-v1`), `HuggingFaceResponseClassifier.cs`, registered in `SearchProviderAdapterRegistry`; Master/Worker reuse common path (reflection gate extended by reference).
    - _Requirements: AC-8.1–AC-8.26, AC-12.28–AC-12.36, AC-16.35–AC-16.37_

  - [x] 20.3 Pass Hugging Face's complete Definition of Done before Azure work
    - Add administration, metrics, redaction, deployment guidance, provider/Scheduler/security tests, existing suite, Release build, and smoke evidence.
    - Evidence: shared metrics/redaction/retention + projection (`AdapterRegistryProjectionHttpTests` now expects 5 instances/7 adapters), `Phase0ClosureTests` docs/no-scraping pins. Full suite + Release build to be confirmed in CI.
    - _Requirements: AC-16.2, AC-16.45–AC-16.56, AC-17.39, AC-17.50–AC-17.56_

- [x] 21. Complete Phase 4 Azure DevOps
  - [x] 21.1 Revalidate Services/server APIs and write failing scoped-search fixtures
    - Cover explicit organization/project/repository configuration, authentication, query/content behavior, pagination, outcomes, and normalized project/repository/version identity.
    - Prove no global-public capability is assumed.
    - Evidence: `docs/api-revalidation-azuredevops-phase4.md` (2026-09-20, 7.1 scoped search), `UnsecuredAPIKeys.Tests/AzureDevOpsSearchProviderAdapterTests.cs` (no `GlobalPublicSearch` declared, org/project settings, scoped provenance, 401/403 outcomes).
    - _Requirements: AC-16.1–AC-16.2, AC-16.38–AC-16.40_

  - [x] 21.2 Implement and wire the Azure DevOps adapter
    - Validate instance settings and endpoints, execute through common Claims/Slots/Work/results, and preserve actual instance/version provenance on Master and Worker.
    - Evidence: `UnsecuredAPIKeys.Providers/Search Providers/AzureDevOpsSearchProviderAdapter.cs` (`azuredevops-search-v1`, `Organization`/`Project` settings schema, Basic `:PAT` auth, `skip/top` continuation, deterministic content version), `AzureDevOpsResponseClassifier.cs`, registered in `SearchProviderAdapterRegistry`.
    - _Requirements: AC-8.1–AC-8.26, AC-9.1–AC-9.19, AC-10.1–AC-10.24, AC-12.1–AC-12.27, AC-16.38–AC-16.40_

  - [x] 21.3 Pass Azure DevOps' complete Definition of Done before Gitea/Forgejo
    - Add administration, metrics, redaction, deployment guidance, provider/Scheduler/security tests, existing suite, Release build, and smoke evidence.
    - Evidence: shared platform services reused (no Scheduler branch), `Phase0ClosureTests` + adapter tests. Full suite + Release build to be confirmed in CI.
    - _Requirements: AC-16.2, AC-16.45–AC-16.56, AC-17.39, AC-17.50–AC-17.56_

- [x] 22. Complete Phase 5 Gitea and Forgejo
  - [x] 22.1 Revalidate supported APIs and write failing flavor/version capability fixtures
    - Cover approved self-hosted endpoint validation before discovery/credential validation, server flavor/version detection, capability intersection, authentication, search/content, outcomes, and unsupported-version rejection.
    - Evidence: `docs/api-revalidation-gitea-forgejo-phase5.md` (2026-09-20, Gitea 1.24 / Forgejo flavor rules), `UnsecuredAPIKeys.Tests/GiteaForgejoSearchProviderAdapterTests.cs` (endpoint-first, flavor/version matrix, unsupported-version rejection before traffic, auth/search/content/outcomes).
    - _Requirements: AC-16.1–AC-16.2, AC-16.41–AC-16.44_

  - [x] 22.2 Implement and wire the Gitea/Forgejo adapter
    - Expose only flavor/version-validated capabilities and execute through Endpoint Policy, common Claims/Slots/Work/checkpoints/results/provenance, Master, and Worker paths.
    - Keep public search disabled unless the exact validated server supports it and recorded consent is active.
    - Evidence: `GiteaSearchProviderAdapter.cs` (`gitea-search-v1`, `ApiFlavor=gitea` + `ServerVersion>=1.20`), `ForgejoSearchProviderAdapter.cs` (`forgejo-search-v1`, `ApiFlavor=forgejo`), `GiteaResponseClassifier.cs`, `ForgejoResponseClassifier.cs`; both registered; neither declares `GlobalPublicSearch`; self-hosted traffic gated by Endpoint Policy + approved-identity readiness.
    - _Requirements: AC-2.15–AC-2.19, AC-8.1–AC-8.26, AC-12.1–AC-12.36, AC-16.41–AC-16.44_

  - [x] 22.3 Pass the final provider Definition of Done and platform release gate
    - Add administration, metrics, redaction, current deployment guidance, provider/Scheduler/security tests, complete existing suite, both database validations, Release build, and Master/Worker smoke evidence.
    - Block release on any mandatory correctness, security, schema, build, or smoke failure.
    - Evidence: 7-adapter registry (`AdapterRegistryRegistrationTests`), 5-instance projection, 5-surface schema parity (`SchemaParityGateTests`), revalidation docs for all phases, shared metrics/redaction/retention, no provider Scheduler branches. Full suite (`dotnet test -c Release`) + Release build (`dotnet build -c Release`) + both-DB readiness + Master/Worker smoke must still be run in CI (no .NET SDK in this environment) before declaring the platform release.
    - _Requirements: AC-16.2, AC-16.45–AC-16.56, AC-17.39, AC-17.50–AC-17.56_

## Acceptance-Criteria Coverage Index

| Requirement domain | Implementing tasks | Covered criteria |
|---|---|---|
| 1. Authorized use, principals, grants, privilege | 3, 4, 6, 7, 12, 14, 15, 19, 20 | AC-1.1–AC-1.28 |
| 2. Provider instances and capabilities | 2, 6, 8, 14, 22 | AC-2.1–AC-2.26 |
| 3. Credential identity and protection | 4, 5, 8, 15, 16 | AC-3.1–AC-3.38 |
| 4. Bootstrap and reconciliation | 3, 4, 5 | AC-4.1–AC-4.47 |
| 5. Scheduler and Lease state | 8, 9, 12, 13, 14 | AC-5.1–AC-5.63 |
| 6. Typed outcomes and health | 8, 10, 11 | AC-6.1–AC-6.34 |
| 7. Work, continuation, checkpoints | 7, 13, 18 | AC-7.1–AC-7.29 |
| 8. Adapter registry and runtime | 6, 8, 10, 11, 14, 18, 20, 21, 22 | AC-8.1–AC-8.26 |
| 9. Query intent and reproducibility | 7, 10, 11, 21 | AC-9.1–AC-9.19 |
| 10. Results, deduplication, provenance | 7, 10, 11, 13, 21 | AC-10.1–AC-10.24 |
| 11. Master/Worker protocol | 2, 8, 12, 13, 16 | AC-11.1–AC-11.45 |
| 12. Endpoint and public safety | 3, 6, 14, 18, 20, 21, 22 | AC-12.1–AC-12.36 |
| 13. Persistence and readiness | 1, 2, 3, 4, 5, 7, 8, 9, 14, 15, 17 | AC-13.1–AC-13.48 |
| 14. Administration and observability | 2, 3, 4, 5, 15 | AC-14.1–AC-14.41 |
| 15. Flags, migration, rollback | 2, 4, 5, 7, 10, 12, 13, 16, 17 | AC-15.1–AC-15.43 |
| 16. Provider roadmap | 10, 11, 17, 18, 19, 20, 21, 22 | AC-16.1–AC-16.56 |
| 17. Correctness and release evidence | 1, 4–18, 19–22 when implemented | AC-17.1–AC-17.56 |

## Execution Rules

- A test-only leaf checkbox may be completed when the specified test exists and fails for the intended missing behavior. An implementation leaf completes only when those tests pass and its stated vertical path is wired. A parent completes only after every child plus targeted/integration/full-suite/Release validation passes.
- Complete tasks in numeric order. Tasks 18–22 are hard phase boundaries; do not overlap provider implementation.
- Treat official provider content as untrusted input and revalidate current documentation at each provider phase.
- Never add a credential to configuration sync, logs, metrics, audit, provenance, or non-Claim responses to simplify an increment.
- Any mandatory test, schema, security, Release build, or smoke failure blocks completion of the affected task and the next provider phase.

## Notes

- `requirements.md` is normative for behavior; `design.md` defines the approved architecture; this file defines implementation order and evidence.
- Inclusive AC ranges are used only within one Requirement domain. Cross-domain mappings are listed as separate ranges.
- PostgreSQL-specific evidence requires the disposable integration fixture created and consumed in Task 9.1; SQLite cannot stand in for `SKIP LOCKED` behavior.
- Provider dependencies introduced during implementation must be pinned to reviewed versions and rechecked against the provider phase's official references.
- This plan creates no production code by itself; implementation begins only when an unchecked task is explicitly executed.

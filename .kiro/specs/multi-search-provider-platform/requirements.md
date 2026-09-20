# Requirements Document: Multi-Search-Provider Platform and Durable Credential Rotation

## Introduction

This feature establishes an authorized multi-search-provider platform for APIHunterV2 and defines Phase 0 as the protected, database-coordinated credential scheduling foundation for GitHub and GitLab. The platform replaces process-local credential selection with durable fair claims, typed provider outcomes, provider-instance adapters, reproducible work and checkpoint state, normalized provenance, secure master/worker coordination, and fail-closed administration. Later phases add Sourcegraph, Bitbucket Cloud subject to a current official-API decision gate, Hugging Face Hub, Azure DevOps, and Gitea/Forgejo one complete provider at a time.

## Glossary

- **Authorized_Principal**: An authenticated user, node, administrator, or explicit system identity permitted to initiate platform work
- **Telegram_Principal**: The registered Telegram identity to which a worker node or interactive request is resolved
- **Scheduler_Principal**: The immutable principal identity and privilege context evaluated for a credential claim or public operation
- **Grant**: A durable authorization record connecting a search-provider access credential to an allowed principal scope
- **User_Grant**: A grant usable by the matching registered Telegram principal
- **Global_Grant**: A grant usable by authorized non-administrators and administrators according to the common grant evaluator
- **Admin_Grant**: A grant usable only by an explicitly authorized administrator or system principal
- **Provider_Kind**: The adapter family, such as GitHub, GitLab, or Sourcegraph
- **Provider_Instance**: One configured SaaS or self-hosted API origin for a Provider_Kind
- **Effective_Capabilities**: The intersection of adapter-declared capabilities and capabilities discovered from a validated Provider_Instance
- **Provider_Access_Credential**: Protected authorization material used by an adapter to access a Provider_Instance
- **Discovered_Secret**: A finding located in repository content and governed by the existing finding-access and export policies
- **Stable_ID**: An immutable external identifier that survives row migration and reconciliation
- **Fingerprint**: A versioned keyed, non-reversible identity used to deduplicate Provider_Access_Credentials
- **Protection_Envelope**: A versioned authenticated-encryption representation containing ciphertext, nonce, authentication tag, and key version
- **Claim**: An atomic scheduler decision that grants one Provider_Access_Credential for one logical operation
- **Lease**: The time-bounded exclusive right to use a claimed credential
- **Operation_Slot**: A durable Provider_Instance concurrency lease used for credentialed and approved credential-free public operations
- **Request_ID**: The client-generated identifier used with the authenticated principal to make a Claim idempotent
- **Work_Item**: A durable unit of provider-instance, query, and partition work
- **Partition**: A stable subdivision of a Work_Item that can be resumed independently
- **Effective_Query_Hash**: A deterministic digest of the exact effective query inputs used by an adapter
- **Continuation**: Versioned provider-specific state needed to resume paginated or streaming work
- **Last_Safe_Checkpoint**: The latest provider position for which required result and provenance persistence completed safely
- **Typed_Outcome**: A normalized provider result that determines credential health, cooldown, retry, and checkpoint behavior
- **Public_Operation**: An approved anonymous or global-public provider operation that does not receive a credential Claim
- **Master**: The control-plane role that owns scheduling, protection, persistence, node APIs, bootstrap, and readiness
- **Worker**: A registered execution role that obtains work and credential Claims from the Master
- **Endpoint_Policy**: The origin, DNS, redirect, address-range, path, and response controls applied before provider communication
- **Readiness_Marker**: Durable schema and cutover metadata proving that the active runtime has all required storage and protection prerequisites
- **Terminal_Retry_Window**: The configured interval during which clients may replay an ambiguously completed request and receive its original terminal result

## Requirements

### Requirement 1: Authorized use, principals, grants, privilege policy

**User Story:** As a platform administrator, I want every search operation and credential use tied to an authorized principal and explicit privilege policy, so that the platform cannot exceed approved access.

#### Acceptance Criteria

1. **AC-1.1** THE Platform SHALL permit search-provider operations only for authorized security auditing
2. **AC-1.2** THE Platform SHALL NOT bypass provider permissions, access controls, rate limits, or supported API restrictions
3. **AC-1.3** WHEN no supported provider API supplies a required search capability, THE Platform SHALL NOT substitute website scraping or an undocumented private endpoint
4. **AC-1.4** THE Grant_Service SHALL support exactly the grant scopes `User`, `Global`, and `Admin`
5. **AC-1.5** THE Grant_Service SHALL bind every `User` grant to one Telegram_Principal
6. **AC-1.6** WHEN a node authenticates, THE Principal_Resolver SHALL resolve the node to its registered Telegram_Principal without accepting a principal from the request body
7. **AC-1.7** WHEN a node has no registered Telegram_Principal mapping, THE Claim_API SHALL reject its credential operation with HTTP 403
8. **AC-1.8** WHEN a non-administrator requests a Claim, THE Grant_Evaluator SHALL consider only that principal's `User` grants and authorized `Global` grants
9. **AC-1.9** WHEN an administrator requests a Claim, THE Grant_Evaluator SHALL consider only `Admin` and `Global` grants authorized for that administrator's role
10. **AC-1.10** WHEN a Master job originates from a user action, THE Work_Item SHALL persist the initiating Telegram_Principal
11. **AC-1.11** WHEN a Master job has no initiating user, THE Work_Item SHALL persist an explicit `System` or `Admin` Scheduler_Principal
12. **AC-1.12** THE Grant_Evaluator SHALL apply the same authorization rules to Master-local work and Worker claims
13. **AC-1.13** THE Grant_Evaluator SHALL NOT use `AddedByTelegramId` as an authorization source after grant backfill is complete
14. **AC-1.14** WHILE compatibility migration is active, THE Platform SHALL treat `AddedByTelegramId` only as migration metadata
15. **AC-1.15** WHEN one physical credential is authorized for multiple principals, THE Grant_Service SHALL represent those authorizations as separate grants on one deduplicated credential row
16. **AC-1.16** WHEN a grant is revoked, THE Scheduler SHALL exclude that grant from every Claim started after the revocation commits
17. **AC-1.17** THE Scheduler SHALL NOT infer ownership or grant scope from a client-supplied provider, query, work-item, or partition field
18. **AC-1.18** THE Privilege_Policy SHALL restrict manual credential disablement to authenticated, administrator-authorized credential-management commands
19. **AC-1.19** THE Privilege_Policy SHALL restrict manual credential re-enablement to authenticated, administrator-authorized credential-management commands
20. **AC-1.20** THE Privilege_Policy SHALL restrict credential replacement to authenticated, administrator-authorized credential-management commands
21. **AC-1.21** THE Privilege_Policy SHALL require administrator authorization for Provider_Instance approval
22. **AC-1.22** THE Privilege_Policy SHALL require administrator authorization for private-network allowlisting
23. **AC-1.23** THE Privilege_Policy SHALL require administrator authorization for global-public-search consent
24. **AC-1.24** WHEN a privileged action is accepted, THE Audit_Service SHALL record the authenticated actor, action, target Stable_ID, UTC time, and non-secret reason
25. **AC-1.25** WHEN an anonymous or global-public operation is authorized, THE Scheduler SHALL NOT issue a Provider_Access_Credential Claim for that operation
26. **AC-1.26** WHEN an anonymous or global-public operation is authorized, THE Scheduler SHALL acquire a durable Operation_Slot for its Provider_Instance
27. **AC-1.27** THE Platform SHALL apply the Provider_Access_Credential and node-token non-disclosure rules in this specification independently from the existing Discovered_Secret access and export policies
28. **AC-1.28** THE Platform SHALL continue to govern Discovered_Secrets through the repository's existing finding-access and export policies

### Requirement 2: Provider-instance lifecycle and capabilities

**User Story:** As a platform administrator, I want provider installations represented as managed instances with verified capabilities, so that SaaS and self-hosted providers execute under explicit configuration and concurrency limits.

#### Acceptance Criteria

1. **AC-2.1** THE Provider_Instance_Store SHALL assign each Provider_Instance a database identifier and an immutable Stable_ID
2. **AC-2.2** THE Provider_Instance_Store SHALL persist one authoritative `ProviderKind` for each Provider_Instance
3. **AC-2.3** AFTER provider-instance migration completes, THE Runtime SHALL use `ProviderInstance.ProviderKind` as the authoritative provider discriminator
4. **AC-2.4** WHILE the legacy `SearchProvider` field remains, THE Readiness_Service SHALL require it to match the linked Provider_Instance's `ProviderKind`
5. **AC-2.5** WHEN a legacy `SearchProvider` value conflicts with the linked Provider_Instance, THE Readiness_Service SHALL fail closed for the affected scheduling path
6. **AC-2.6** WHEN an unknown Provider_Kind is supplied, THE Provider_Instance_Service SHALL reject it rather than defaulting to GitHub
7. **AC-2.7** THE Provider_Instance_Store SHALL persist a display name for each Provider_Instance
8. **AC-2.8** THE Provider_Instance_Store SHALL persist the normalized API scheme, host, port, and base path for each Provider_Instance
9. **AC-2.9** THE Provider_Instance_Store SHALL persist enabled state for each Provider_Instance
10. **AC-2.10** THE Provider_Instance_Store SHALL persist `AllowGlobalPublicSearch` independently for each Provider_Instance
11. **AC-2.11** THE Provider_Instance_Store SHALL persist a positive maximum concurrent-operation count for each Provider_Instance
12. **AC-2.12** THE Provider_Instance_Store SHALL persist versioned instance settings without permitting settings to contain Provider_Access_Credentials or node tokens
13. **AC-2.13** THE Provider_Instance_Store SHALL persist the approving administrator's Telegram identifier when approval is required
14. **AC-2.14** THE Provider_Instance_Store SHALL persist created and updated UTC timestamps
15. **AC-2.15** THE Capability_Model SHALL represent `CodeSearch`, `PaginatedSearch`, `StreamingSearch`, `ContentRetrieval`, `PrivateRepositories`, `GlobalPublicSearch`, `SelfHosted`, `RepositoryMetadata`, and `NativeQueryOverrides` independently
16. **AC-2.16** WHEN capabilities are evaluated, THE Capability_Service SHALL calculate Effective_Capabilities as the intersection of adapter-declared and validated server-discovered capabilities
17. **AC-2.17** WHEN configuration requests a capability absent from either side of the capability intersection, THE Capability_Service SHALL leave that capability disabled
18. **AC-2.18** WHEN capability discovery fails, THE Capability_Service SHALL NOT retain an unverified capability as effective for a new operation
19. **AC-2.19** BEFORE capability discovery runs against a self-hosted instance, THE Endpoint_Policy SHALL validate that instance
20. **AC-2.20** WHEN a Provider_Instance is disabled, THE Scheduler SHALL reject new Claims and Operation_Slots for that instance
21. **AC-2.21** THE Scheduler SHALL enforce the Provider_Instance maximum concurrent-operation count through durable Operation_Slots
22. **AC-2.22** THE Scheduler SHALL count credentialed and Public_Operations against the same Provider_Instance concurrency limit
23. **AC-2.23** WHEN Phase 0 migration initializes provider instances, THE Migration_Service SHALL create a default GitHub instance for `https://api.github.com`
24. **AC-2.24** WHEN Phase 0 migration initializes provider instances, THE Migration_Service SHALL create a default GitLab instance for `https://gitlab.com/api/v4`
25. **AC-2.25** WHEN existing GitHub or GitLab credentials are migrated, THE Migration_Service SHALL link each credential to its corresponding default Provider_Instance
26. **AC-2.26** THE Provider_Instance_Service SHALL require endpoint revalidation after a normalized origin or permitted API path prefix changes

### Requirement 3: Credential identity and protected-secret boundary

**User Story:** As a security operator, I want provider access credentials protected by stable identity, authenticated encryption, and a narrow plaintext boundary, so that database or telemetry access does not disclose usable secrets.

#### Acceptance Criteria

1. **AC-3.1** THE Credential_Store SHALL preserve `SearchProviderToken` as the initial physical table identity during the first additive migration
2. **AC-3.2** THE Credential_Store SHALL assign every Provider_Access_Credential an immutable Stable_ID
3. **AC-3.3** THE Credential_Store SHALL link every Provider_Access_Credential to exactly one Provider_Instance
4. **AC-3.4** THE Credential_Store SHALL persist a versioned authenticated Protection_Envelope instead of plaintext for protected runtime reads
5. **AC-3.5** THE Credential_Protection_Service SHALL use AES-256-GCM authenticated encryption for each new Protection_Envelope
6. **AC-3.6** THE Credential_Protection_Service SHALL encode the envelope format version, protection-key version, nonce, ciphertext, and authentication tag in each Protection_Envelope
7. **AC-3.7** WHEN encrypting credential material, THE Credential_Protection_Service SHALL generate a fresh cryptographically random nonce
8. **AC-3.8** WHEN encrypting credential material, THE Credential_Protection_Service SHALL authenticate the credential Stable_ID as associated data
9. **AC-3.9** WHEN encrypting credential material, THE Credential_Protection_Service SHALL authenticate the authoritative Provider_Kind as associated data
10. **AC-3.10** WHEN encrypting credential material, THE Credential_Protection_Service SHALL authenticate the Provider_Instance Stable_ID as associated data
11. **AC-3.11** THE Credential_Protection_Service SHALL obtain versioned protection keys from deployment configuration rather than from credential database rows
12. **AC-3.12** THE Credential_Protection_Service SHALL support decryption with active and configured previous protection-key versions
13. **AC-3.13** THE Credential_Protection_Service SHALL use the active protection-key version for every new or rewrapped envelope
14. **AC-3.14** THE Credential_Fingerprint_Service SHALL use a versioned HMAC-SHA-256 fingerprint key that is distinct from every protection key
15. **AC-3.15** THE Credential_Fingerprint_Service SHALL bind the authoritative Provider_Kind, Provider_Instance Stable_ID, and canonical credential material into the HMAC input
16. **AC-3.16** THE Credential_Store SHALL persist the fingerprint-key version with each Fingerprint
17. **AC-3.17** THE Platform SHALL NOT emit a complete Fingerprint through logs, metrics, audit records, APIs, Telegram, or administration views
18. **AC-3.18** THE Platform SHALL NOT expose deployment configuration key names as part of the credential-storage contract
19. **AC-3.19** WHEN an enabled credential's required protection key is unavailable at startup, THE Readiness_Service SHALL report the credential path unhealthy
20. **AC-3.20** WHEN an enabled credential's Protection_Envelope cannot be authenticated or decrypted at runtime, THE Credential_Scheduler SHALL release its active Lease
21. **AC-3.21** WHEN runtime credential decryption fails, THE Credential_Scheduler SHALL quarantine the credential by setting `IsEnabled=false`
22. **AC-3.22** WHEN runtime credential decryption fails, THE Credential_Scheduler SHALL set `DisabledReason` to `ProtectionFailure`
23. **AC-3.23** WHEN runtime credential decryption fails, THE Credential_Scheduler SHALL set `DisabledAtUtc` to database UTC
24. **AC-3.24** WHEN runtime credential decryption fails, THE Credential_Scheduler SHALL NOT classify the credential as `AuthInvalid`
25. **AC-3.25** WHEN runtime credential decryption fails, THE Alert_Service SHALL emit a non-secret protection-failure alert containing only stable references
26. **AC-3.26** WHEN no usable credential remains for a required credentialed execution path after protection failures, THE Readiness_Service SHALL report that path unhealthy
27. **AC-3.27** WHEN a Claim transaction commits, THE Credential_Protection_Service SHALL decrypt only the credential selected by that Claim
28. **AC-3.28** THE Credential_Protection_Service SHALL decrypt claimed material as close as possible to claim-response or provider-request construction
29. **AC-3.29** THE Runtime SHALL NOT retain plaintext Provider_Access_Credentials in tracked database entities
30. **AC-3.30** THE Runtime SHALL NOT retain plaintext Provider_Access_Credentials in shared or long-lived caches
31. **AC-3.31** THE Runtime SHALL NOT place Provider_Access_Credentials or node tokens in query strings
32. **AC-3.32** THE Platform SHALL treat database backups containing encrypted Provider_Access_Credentials as sensitive data
33. **AC-3.33** WHEN a credential is enabled, THE Credential_State_Service SHALL require `IsEnabled=true`, `DisabledAtUtc=null`, and `DisabledReason=null`
34. **AC-3.34** WHEN a credential enters any disabled or quarantined state, THE Credential_State_Service SHALL set `IsEnabled=false`
35. **AC-3.35** WHEN a credential enters any disabled or quarantined state, THE Credential_State_Service SHALL persist a non-empty disabled reason
36. **AC-3.36** WHEN a credential enters any disabled or quarantined state, THE Credential_State_Service SHALL persist the database UTC disabled time
37. **AC-3.37** THE Platform SHALL classify Provider_Access_Credentials and node tokens as control-plane secrets distinct from Discovered_Secrets
38. **AC-3.38** THE Credential_Scheduler SHALL treat `ProtectionFailure` as an internal protection quarantine rather than as a provider Typed_Outcome or Scheduler completion outcome

### Requirement 4: Credential ingestion, environment bootstrap, duplicate reconciliation

**User Story:** As a deployment operator, I want credentials imported and reconciled idempotently without losing history or grants, so that migration and environment changes cannot corrupt credential identity or health.

#### Acceptance Criteria

1. **AC-4.1** THE Environment_Bootstrap_Service SHALL run only in the Master role
2. **AC-4.2** THE Environment_Bootstrap_Service SHALL finish a successful import generation before the Scheduler becomes claim-ready
3. **AC-4.3** THE Environment_Bootstrap_Service SHALL parse dedicated Master GitHub and GitLab credential settings
4. **AC-4.4** WHILE the migration compatibility stage is active, THE Environment_Bootstrap_Service SHALL recognize the documented legacy Worker credential formats
5. **AC-4.5** WHEN an input credential is empty, THE Environment_Bootstrap_Service SHALL reject that entry without logging its value
6. **AC-4.6** WHEN an input credential is malformed, THE Environment_Bootstrap_Service SHALL reject that entry without logging its value
7. **AC-4.7** WHEN a bootstrap generation contains a duplicate entry, THE Environment_Bootstrap_Service SHALL reject the duplicate without logging its value
8. **AC-4.8** WHEN an input entry names an unsupported Provider_Kind, THE Environment_Bootstrap_Service SHALL reject that entry without defaulting to GitHub
9. **AC-4.9** WHEN a complete bootstrap input parses successfully, THE Environment_Bootstrap_Service SHALL compute the versioned provider-instance Fingerprint before lookup
10. **AC-4.10** WHEN the same environment credential is imported repeatedly, THE Environment_Bootstrap_Service SHALL retain its existing Stable_ID
11. **AC-4.11** WHEN the same environment credential is imported repeatedly, THE Environment_Bootstrap_Service SHALL preserve its grants
12. **AC-4.12** WHEN the same environment credential is imported repeatedly, THE Environment_Bootstrap_Service SHALL preserve its cooldown and disabled state
13. **AC-4.13** WHEN the same environment credential is imported repeatedly, THE Environment_Bootstrap_Service SHALL preserve its lease audit and usage history
14. **AC-4.14** WHEN an `AuthInvalid` environment credential remains configured, THE Environment_Bootstrap_Service SHALL NOT re-enable it
15. **AC-4.15** WHEN environment credential material changes for a stable non-secret bootstrap entry, THE Environment_Bootstrap_Service SHALL create a new credential row with a new Stable_ID and Fingerprint
16. **AC-4.16** WHEN environment credential material changes for a stable non-secret bootstrap entry, THE Environment_Bootstrap_Service SHALL leave the prior credential disabled and archived with a replacement reference
17. **AC-4.17** THE Environment_Bootstrap_Service SHALL persist bootstrap source, source-entry identifier, generation, and last-seen UTC metadata for an imported environment credential
18. **AC-4.18** WHEN an environment-managed credential is absent from a wholly successful generation, THE Environment_Bootstrap_Service SHALL disable it with reason `RemovedFromEnvironment`
19. **AC-4.19** WHEN an environment-managed credential is disabled after removal, THE Environment_Bootstrap_Service SHALL retain its row and history
20. **AC-4.20** WHEN a credential has manual-management provenance matching a removed environment entry, THE Environment_Bootstrap_Service SHALL preserve its manual-management state
21. **AC-4.21** WHEN a credential has manual-management provenance matching a removed environment entry, THE Environment_Bootstrap_Service SHALL NOT disable it solely because the environment entry was removed
22. **AC-4.22** WHEN any bootstrap entry fails parsing, THE Environment_Bootstrap_Service SHALL NOT reconcile removals for that generation
23. **AC-4.23** WHEN any required protection or fingerprint key is unavailable, THE Environment_Bootstrap_Service SHALL NOT reconcile removals for that generation
24. **AC-4.24** WHEN bootstrap persistence fails, THE Environment_Bootstrap_Service SHALL NOT reconcile removals for that generation
25. **AC-4.25** WHEN an administrator replaces credential material, THE Credential_Management_Service SHALL create a new credential row with a new Stable_ID and Fingerprint
26. **AC-4.26** WHEN an administrator replaces credential material, THE Credential_Management_Service SHALL leave the prior row disabled and archived
27. **AC-4.27** THE Credential_Management_Service SHALL transfer grants from an old credential to a replacement only through an explicit replacement command
28. **AC-4.28** WHEN grants transfer to a replacement credential, THE Audit_Service SHALL record the old and new Stable_IDs without credential material
29. **AC-4.29** BEFORE duplicate reconciliation mutates credential rows, THE Migration_Service SHALL prevent concurrent credential mutations
30. **AC-4.30** BEFORE duplicate grouping, THE Migration_Service SHALL create default instances and link legacy credentials to them
31. **AC-4.31** WHEN migration protects legacy credential material, THE Migration_Service SHALL produce Protection_Envelopes and Fingerprints without logging plaintext
32. **AC-4.32** THE Duplicate_Reconciler SHALL group duplicate credentials only by Provider_Instance and Fingerprint
33. **AC-4.33** WHEN a duplicate group has multiple rows, THE Duplicate_Reconciler SHALL choose the canonical row by `CreatedUtc ASC`, database identifier ASC, and Stable_ID ASC
34. **AC-4.34** WHEN duplicate rows have distinct grants, THE Duplicate_Reconciler SHALL union those grants onto the canonical row
35. **AC-4.35** WHEN any duplicate row is administrator-disabled, authentication-disabled, or protection-quarantined, THE Duplicate_Reconciler SHALL keep the canonical row disabled
36. **AC-4.36** WHEN duplicate rows have different cooldown timestamps, THE Duplicate_Reconciler SHALL preserve the latest cooldown timestamp
37. **AC-4.37** WHEN duplicate rows have different claim timestamps, THE Duplicate_Reconciler SHALL preserve the latest claim timestamp
38. **AC-4.38** WHEN duplicate rows have different successful-use timestamps, THE Duplicate_Reconciler SHALL preserve the latest successful-use timestamp
39. **AC-4.39** WHEN duplicate rows have conflicting active Leases, THE Duplicate_Reconciler SHALL abort until those Leases complete or expire
40. **AC-4.40** WHEN a canonical row is selected, THE Duplicate_Reconciler SHALL commit grant union, state merge, reference repointing, audit repointing, and duplicate archival atomically under the mutation gate
41. **AC-4.41** THE Migration_Service SHALL create the unique Provider_Instance-and-Fingerprint constraint only after duplicate reconciliation succeeds
42. **AC-4.42** WHEN duplicate reconciliation runs in dry-run mode, THE Migration_Service SHALL output only counts and Stable_ID references
43. **AC-4.43** WHEN legacy credentials have `AddedByTelegramId`, THE Grant_Backfill SHALL create corresponding `User` grants
44. **AC-4.44** WHEN a legacy credential has no owner, THE Grant_Backfill SHALL apply the explicitly configured `Global` or `Admin` compatibility policy
45. **AC-4.45** WHEN the unowned-credential compatibility policy is absent, THE Grant_Backfill SHALL fail without assigning an implicit grant
46. **AC-4.46** THE Bootstrap configuration SHALL identify each credential slot with a stable non-secret source-entry identifier whose configuration key name is not part of the storage contract
47. **AC-4.47** WHEN duplicate disabled reasons differ, THE Duplicate_Reconciler SHALL select `AdministratorDisabled`, then `AuthInvalid`, then `ProtectionFailure`, then `RemovedFromEnvironment`, then the ordinally first remaining reason

### Requirement 5: Durable scheduler and lease state machine

**User Story:** As a workload operator, I want credential selection coordinated through durable fair leases, so that concurrent nodes rotate healthy credentials without duplicate use or stale-state corruption.

#### Acceptance Criteria

1. **AC-5.1** THE Credential_Scheduler SHALL be the only runtime component permitted to select or claim a Provider_Access_Credential
2. **AC-5.2** THE Credential_Scheduler SHALL be the only runtime component permitted to renew or complete a credential Lease
3. **AC-5.3** THE Credential_Scheduler SHALL be the only runtime component permitted to apply automatic credential cooldown or automatic disablement
4. **AC-5.4** THE Credential_Scheduler SHALL define one Claim as one Provider_Instance, search-query, Work_Item, and Partition operation including its pagination and required content retrieval
5. **AC-5.5** WHILE one credential Lease is active, THE Credential_Scheduler SHALL prevent that credential from being granted to another operation
6. **AC-5.6** WHEN bounded parallel content requests belong to the same operation, THE Adapter_Runtime SHALL permit them to share that operation's Lease
7. **AC-5.7** WHEN eligibility is evaluated, THE Credential_Scheduler SHALL use database UTC
8. **AC-5.8** THE Credential_Scheduler SHALL require `IsEnabled=true` for eligibility
9. **AC-5.9** THE Credential_Scheduler SHALL require `DisabledAtUtc=null` for eligibility
10. **AC-5.10** THE Credential_Scheduler SHALL require the linked Provider_Instance to be enabled for eligibility
11. **AC-5.11** THE Credential_Scheduler SHALL require an applicable grant for the Scheduler_Principal for eligibility
12. **AC-5.12** THE Credential_Scheduler SHALL exclude a credential whose `CooldownUntilUtc` is later than database UTC
13. **AC-5.13** THE Credential_Scheduler SHALL exclude a credential whose Lease is unexpired at database UTC
14. **AC-5.14** THE Credential_Scheduler SHALL require the credential's authoritative Provider_Instance and Provider_Kind to match the Work_Item
15. **AC-5.15** WHEN a Lease has expired, THE Credential_Scheduler SHALL treat the credential as reclaimable without waiting for cleanup to clear lease fields
16. **AC-5.16** THE Credential_Scheduler SHALL order eligible credentials by `LastClaimedUtc ASC NULLS FIRST` and then `StableId ASC`
17. **AC-5.17** WHEN a credential is claimed, THE Credential_Scheduler SHALL update `LastClaimedUtc` atomically at claim time
18. **AC-5.18** THE Credential_Scheduler SHALL NOT defer the fairness update until Lease completion
19. **AC-5.19** WHEN three healthy credentials remain eligible at each sequential claim boundary, THE Credential_Scheduler SHALL grant them in the repeating order `1,2,3,1,2,3` regardless of preceding operation completion order
20. **AC-5.20** BEFORE selecting a credential, THE Credential_Scheduler SHALL validate Provider_Instance, operation identity, Scheduler_Principal, and Request_ID
21. **AC-5.21** THE Credential_Scheduler SHALL define the Claim idempotency key as authenticated Scheduler_Principal plus Request_ID
22. **AC-5.22** THE Credential_Scheduler SHALL retain each Claim idempotency record throughout every active or renewed Lease and until the later of initial-claim time plus `MaximumLeaseDuration` or terminal time plus `TerminalRetryWindow`
23. **AC-5.23** WHEN an active Claim is replayed with the same idempotency key and identical immutable Claim fields, THE Credential_Scheduler SHALL return the original active Lease without selecting another credential
24. **AC-5.24** WHEN a terminal Claim is replayed with the same idempotency key and identical immutable Claim fields, THE Credential_Scheduler SHALL return the original terminal result without selecting another credential
25. **AC-5.25** WHEN a new Claim succeeds, THE Credential_Scheduler SHALL atomically assign a Lease ID, owner node ID, expiry, Request_ID, and claim time
26. **AC-5.26** WHEN a new Claim succeeds, THE Credential_Scheduler SHALL increment the credential Revision in the same transaction
27. **AC-5.27** WHEN a new Claim succeeds, THE Credential_Scheduler SHALL persist a non-secret lease audit and idempotency record in the same transaction
28. **AC-5.28** THE Credential_Scheduler SHALL commit the database Claim before decrypting credential material
29. **AC-5.29** WHEN a Claim is granted, THE Credential_Scheduler SHALL return exactly one Provider_Access_Credential
30. **AC-5.30** WHEN matching credentials are temporarily unavailable, THE Credential_Scheduler SHALL return the earliest applicable cooldown or active-Lease expiry
31. **AC-5.31** WHEN every matching credential is absent or disabled, THE Credential_Scheduler SHALL return a terminal no-credential reason without a retry time
32. **AC-5.32** THE Lease_Policy SHALL use a configurable five-minute default Lease duration
33. **AC-5.33** THE Lease_Policy SHALL require a configured maximum Lease duration greater than or equal to the default duration
34. **AC-5.34** THE Lease_Policy SHALL cap a client-requested Lease duration at the configured maximum
35. **AC-5.35** THE Worker SHALL request renewal before its current Lease expires
36. **AC-5.36** THE Lease_Policy SHALL expose a renewal threshold earlier than Lease expiry
37. **AC-5.37** WHEN renewal is requested, THE Credential_Scheduler SHALL require the authenticated Lease owner to match
38. **AC-5.38** WHEN renewal is requested, THE Credential_Scheduler SHALL require the active Lease ID, Work_Item, operation identity, and expected Revision to match
39. **AC-5.39** THE Credential_Scheduler SHALL prevent renewals from extending an operation beyond the configured maximum continuous-operation duration
40. **AC-5.40** WHEN completion is requested, THE Credential_Scheduler SHALL require credential Stable_ID, Lease ID, Lease owner, and Work_Item to match the active Lease
41. **AC-5.41** WHEN the same terminal completion is replayed, THE Credential_Scheduler SHALL return the original terminal result without a second health transition
42. **AC-5.42** WHEN completion targets an expired or reclaimed Lease, THE Credential_Scheduler SHALL return a stale-Lease result
43. **AC-5.43** WHEN completion is stale, THE Credential_Scheduler SHALL NOT alter current lease, cooldown, health, disablement, usage, or Revision state
44. **AC-5.44** WHEN renewal fails or Master communication is lost, THE Worker SHALL stop issuing new provider requests before Lease expiry
45. **AC-5.45** WHEN the Complete endpoint accepts a matching Lease, THE Credential_Scheduler SHALL terminalize and release that Lease regardless of the work operation-complete flag
46. **AC-5.46** WHEN a valid completion reports definitive `AuthInvalid`, THE Credential_Scheduler SHALL automatically disable the credential with an authentication reason and database UTC
47. **AC-5.47** WHEN a Scheduler completion reports any Typed_Outcome other than `AuthInvalid`, THE Credential_Scheduler SHALL NOT automatically disable the credential
48. **AC-5.48** THE Credential_Scheduler SHALL persist cooldown state so that restart or node change does not reset eligibility
49. **AC-5.49** WHEN the Cleanup_Service runs, THE Cleanup_Service SHALL clear only Lease fields whose expiry is not later than database UTC
50. **AC-5.50** WHEN a replay uses an existing principal-plus-Request_ID key with different Provider_Instance, SearchQuery, Work_Item, Partition, or requested operation fields, THE Credential_Scheduler SHALL return a typed HTTP 409 conflict without a secret
51. **AC-5.51** THE Operation_Slot_Store SHALL assign each acquired slot a unique slot ID, Provider_Instance, owner principal, Work_Item, Partition, expiry, and Revision
52. **AC-5.52** THE Credential_Scheduler SHALL use database UTC when acquiring, renewing, expiring, or completing an Operation_Slot
53. **AC-5.53** WHEN a credentialed operation is claimed, THE Credential_Scheduler SHALL acquire its credential Lease and Provider_Instance Operation_Slot in one transaction
54. **AC-5.54** WHEN either a credential Lease or required Operation_Slot cannot be acquired, THE Credential_Scheduler SHALL grant neither resource
55. **AC-5.55** WHEN a Public_Operation is claimed, THE Credential_Scheduler SHALL acquire an Operation_Slot without selecting or decrypting a credential
56. **AC-5.56** THE Credential_Scheduler SHALL count only unexpired Operation_Slots against `MaxConcurrentOperations`
57. **AC-5.57** WHEN an exact active public-operation request is replayed, THE Credential_Scheduler SHALL return the original Operation_Slot without consuming another slot
58. **AC-5.58** WHEN Provider_Instance capacity is full, THE Credential_Scheduler SHALL return the earliest active Operation_Slot expiry as the retry time
59. **AC-5.59** WHEN an Operation_Slot renewal is requested, THE Credential_Scheduler SHALL require matching slot ID, owner principal, Work_Item, Partition, and Revision
60. **AC-5.60** THE Credential_Scheduler SHALL prevent Operation_Slot renewal beyond the configured maximum continuous-operation duration
61. **AC-5.61** WHEN Operation_Slot completion identity is stale, THE Credential_Scheduler SHALL leave the current slot and operation state unchanged
62. **AC-5.62** WHEN an Operation_Slot expires, THE Credential_Scheduler SHALL make its instance capacity reclaimable without waiting for cleanup
63. **AC-5.63** WHEN a requested Lease renewal extension exceeds its configured per-renewal maximum, THE Lease_Policy SHALL cap the extension before applying the maximum continuous-operation limit

### Requirement 6: Typed outcomes and credential-health transitions

**User Story:** As a provider integrator, I want provider responses mapped to deterministic typed outcomes, so that credential health changes only for the correct reason and retries preserve work safely.

#### Acceptance Criteria

1. **AC-6.1** THE Typed_Outcome_Model SHALL define `Success`, `RateLimited`, `AuthInvalid`, `ForbiddenScope`, `Transient`, `RequestInvalid`, `ResourceMissing`, and `Cancellation`
2. **AC-6.2** THE Adapter_Runtime SHALL return a Typed_Outcome for search operations
3. **AC-6.3** THE Adapter_Runtime SHALL return a Typed_Outcome for required content-retrieval operations
4. **AC-6.4** WHEN a provider operation fails, THE Adapter_Runtime SHALL NOT convert that failure into an empty `Success`
5. **AC-6.5** WHEN a complete operation returns `Success`, THE Credential_Scheduler SHALL update `LastUsedUTC` using database UTC
6. **AC-6.6** WHEN a complete operation returns `Success`, THE Credential_Scheduler SHALL reset consecutive transient failures
7. **AC-6.7** WHEN an operation returns `RateLimited`, THE Credential_Scheduler SHALL persist a cooldown derived from trusted typed evidence
8. **AC-6.8** WHEN an operation returns `RateLimited`, THE Credential_Scheduler SHALL NOT increment the transient-failure count
9. **AC-6.9** WHEN an operation returns `RateLimited`, THE Work_Service SHALL preserve its Continuation
10. **AC-6.10** WHEN an operation returns `RateLimited`, THE Work_Service SHALL leave the operation incomplete
11. **AC-6.11** WHEN an operation returns definitive `AuthInvalid`, THE Credential_Scheduler SHALL set `IsEnabled=false`
12. **AC-6.12** WHEN an operation returns definitive `AuthInvalid`, THE Credential_Scheduler SHALL persist an authentication-specific disabled reason
13. **AC-6.13** WHEN an operation returns definitive `AuthInvalid`, THE Credential_Scheduler SHALL persist `DisabledAtUtc` using database UTC
14. **AC-6.14** WHEN an operation returns `ForbiddenScope`, THE Credential_Scheduler SHALL NOT disable the credential
15. **AC-6.15** WHEN an operation returns `ForbiddenScope`, THE Credential_Scheduler SHALL record the scope outcome
16. **AC-6.16** WHEN configured policy applies a `ForbiddenScope` cooldown, THE Credential_Scheduler SHALL compute it from configuration rather than provider body text
17. **AC-6.17** WHEN an operation returns `Transient`, THE Credential_Scheduler SHALL increment the consecutive transient-failure count once
18. **AC-6.18** WHEN an operation returns `Transient`, THE Credential_Scheduler SHALL apply configurable capped exponential backoff with jitter
19. **AC-6.19** WHEN deterministic test mode supplies a jitter source, THE Credential_Scheduler SHALL produce repeatable transient cooldown values
20. **AC-6.20** WHEN an operation returns `RequestInvalid`, THE Credential_Scheduler SHALL release the Lease without penalizing the credential
21. **AC-6.21** WHEN an operation returns `RequestInvalid`, THE Work_Service SHALL record a query or request error without cycling blindly through the credential pool
22. **AC-6.22** WHEN an operation returns `ResourceMissing`, THE Credential_Scheduler SHALL release the Lease without penalizing the credential
23. **AC-6.23** WHEN an operation returns `ResourceMissing`, THE Work_Service SHALL preserve diagnostic provenance for the skipped resource
24. **AC-6.24** WHEN an operation returns `Cancellation`, THE Credential_Scheduler SHALL release the Lease without credential penalty when Master communication is available
25. **AC-6.25** WHEN an operation returns `Cancellation`, THE Work_Service SHALL NOT advance an incomplete checkpoint
26. **AC-6.26** THE Completion_API SHALL accept only HTTP status, allowlisted sanitized provider code, optional provider retry time, Typed_Outcome, and operation-complete state as provider evidence
27. **AC-6.27** THE Completion_API SHALL NOT accept raw provider response bodies, authorization headers, credential material, or exception text
28. **AC-6.28** WHEN completion evidence contains a provider reset time, THE Master SHALL clamp that time to configured past-skew and future-horizon bounds
29. **AC-6.29** THE Master SHALL compute the final cooldown from sanitized typed evidence and configured policy values
30. **AC-6.30** THE Cooldown_Policy SHALL expose configurable rate-limit buffer, missing-reset fallback, transient base, exponent, cap, and jitter values
31. **AC-6.31** WHEN policy inputs and sanitized evidence are identical under deterministic test configuration, THE Master SHALL compute the same cooldown
32. **AC-6.32** WHEN trusted rate-limit evidence omits a reset time, THE Master SHALL apply the configured bounded fallback cooldown
33. **AC-6.33** WHEN a Typed_Outcome is accepted, THE Credential_Scheduler SHALL persist it as `LastOutcome`
34. **AC-6.34** WHEN a non-authentication outcome is accepted, THE Credential_Scheduler SHALL preserve enabled state unless an explicit credential-management command changes it

### Requirement 7: Work identity, continuation, checkpoint semantics

**User Story:** As a workload operator, I want every provider operation reproducible and safely resumable, so that retries cannot skip results or repeat side effects incorrectly.

#### Acceptance Criteria

1. **AC-7.1** THE Work_Service SHALL assign every Work_Item an immutable Work_Item identifier
2. **AC-7.2** THE Work_Service SHALL persist the Scheduler_Principal that initiated each Work_Item
3. **AC-7.3** THE Work_Service SHALL bind every Work_Item to one Provider_Instance Stable_ID and authoritative Provider_Kind
4. **AC-7.4** THE Work_Service SHALL bind every Work_Item to one SearchQuery identifier
5. **AC-7.5** THE Work_Service SHALL persist a stable Partition key for each independently resumable subdivision
6. **AC-7.6** THE Work_Service SHALL define the operation identity from Provider_Instance, SearchQuery, Work_Item, and Partition
7. **AC-7.7** THE Worker SHALL generate one Request_ID for each logical Claim attempt
8. **AC-7.8** WHEN a logical Claim is retried after an ambiguous response, THE Worker SHALL reuse its original Request_ID
9. **AC-7.9** WHEN a new logical operation begins, THE Worker SHALL use a new Request_ID
10. **AC-7.10** THE Work_Service SHALL persist the Effective_Query_Hash before provider execution
11. **AC-7.11** THE Work_Service SHALL persist the adapter version before provider execution
12. **AC-7.12** THE Work_Service SHALL persist versioned Continuation state separately for each Partition
13. **AC-7.13** THE Work_Service SHALL NOT persist Provider_Access_Credentials or node tokens in Continuation state
14. **AC-7.14** THE Work_Service SHALL persist a Last_Safe_Checkpoint separately for each Partition
15. **AC-7.15** WHEN pagination has an unconsumed Continuation, THE Work_Service SHALL leave the operation incomplete
16. **AC-7.16** WHEN streaming has not received a validated terminal-completion event, THE Work_Service SHALL leave the operation incomplete
17. **AC-7.17** WHEN required content retrieval returns `AuthInvalid`, `RateLimited`, or `Transient`, THE Work_Service SHALL leave the operation incomplete
18. **AC-7.18** WHEN processing is cancelled, THE Work_Service SHALL leave the operation incomplete
19. **AC-7.19** WHEN parsed results precede an incomplete provider outcome, THE Result_Service SHALL persist those safely parsed results idempotently
20. **AC-7.20** WHEN partial results are persisted, THE Work_Service SHALL retain a Continuation that resumes after the Last_Safe_Checkpoint
21. **AC-7.21** WHEN result, provenance, and checkpoint records use the platform database, THE Persistence_Service SHALL commit their writes in one transaction
22. **AC-7.22** WHEN a result is delivered to an external sink, THE Persistence_Service SHALL use a transactional outbox keyed by Work_Item, Partition, provider position, and result deduplication identity
23. **AC-7.23** THE Work_Service SHALL advance Last_Safe_Checkpoint only after all required persistence for that provider position succeeds
24. **AC-7.24** WHEN a normal terminal response contains no matches, THE Work_Service SHALL mark the operation successful
25. **AC-7.25** WHEN an adapter resumes work, THE Adapter_Runtime SHALL receive the persisted Continuation and adapter version
26. **AC-7.26** WHEN a persisted Continuation is incompatible with the active adapter version, THE Adapter_Runtime SHALL return a typed request error rather than silently advancing the checkpoint
27. **AC-7.27** WHEN a streaming connection is interrupted, THE Adapter_Runtime SHALL resume from the Last_Safe_Checkpoint without treating progress-only events as completed matches
28. **AC-7.28** THE Work_Service SHALL retain terminal operation identity for at least the configured Terminal_Retry_Window
29. **AC-7.29** WHEN a Public_Operation runs, THE Work_Service SHALL apply the same Work_Item, Partition, Continuation, checkpoint, and idempotency semantics used by credentialed work

### Requirement 8: Adapter registry and provider-runtime contract

**User Story:** As a provider developer, I want one explicit adapter contract for all provider kinds, so that new providers share security, scheduling, outcome, and normalization behavior.

#### Acceptance Criteria

1. **AC-8.1** THE Adapter_Registry SHALL obtain adapters through dependency injection and explicit Provider_Kind registration
2. **AC-8.2** THE Adapter_Registry SHALL contain at most one active adapter registration for each Provider_Kind
3. **AC-8.3** WHEN no adapter is registered for a Provider_Kind, THE Adapter_Registry SHALL reject that kind without falling through to GitHub
4. **AC-8.4** THE Provider_Adapter SHALL expose its Provider_Kind
5. **AC-8.5** THE Provider_Adapter SHALL expose its declared capability flags
6. **AC-8.6** THE Provider_Adapter SHALL expose a version identifier used for reproducibility
7. **AC-8.7** THE Provider_Adapter SHALL support capability discovery for a validated Provider_Instance
8. **AC-8.8** THE Provider_Adapter SHALL support credential validation through a Typed_Outcome
9. **AC-8.9** THE Provider_Adapter SHALL support generic-query translation
10. **AC-8.10** THE Provider_Adapter SHALL support asynchronous paginated or streaming search according to Effective_Capabilities
11. **AC-8.11** THE Provider_Adapter SHALL support content retrieval when `ContentRetrieval` is effective
12. **AC-8.12** THE Provider_Adapter SHALL return normalized values, a Typed_Outcome, and optional Continuation rather than throwing an unclassified provider failure across the runtime boundary
13. **AC-8.13** THE Adapter_Runtime SHALL provide HTTP clients through `IHttpClientFactory`
14. **AC-8.14** THE Adapter_Runtime SHALL provide only Endpoint_Policy-validated Provider_Instance configuration to an adapter
15. **AC-8.15** THE Adapter_Runtime SHALL provide claimed credential material only for the active operation that owns its Lease
16. **AC-8.16** THE Adapter_Runtime SHALL provide cancellation, timeout, response-size, content-type, and content-concurrency bounds in the operation context
17. **AC-8.17** WHEN a private-repository operation begins, THE Adapter_Runtime SHALL require an authorized credential Claim
18. **AC-8.18** WHEN a self-hosted credentialed operation begins, THE Adapter_Runtime SHALL require an authorized credential Claim
19. **AC-8.19** WHEN a Public_Operation begins, THE Adapter_Runtime SHALL require the public-search gates in Requirement 12 instead of a credential Claim
20. **AC-8.20** THE Adapter_Runtime SHALL classify provider errors for both search and content retrieval through the same provider-specific classifier
21. **AC-8.21** THE Provider_Adapter SHALL parse provider JSON or stream events within configured size and event-count bounds
22. **AC-8.22** THE Provider_Adapter SHALL treat provider API content and repository content as untrusted data
23. **AC-8.23** THE Provider_Adapter SHALL NOT execute repository content or interpret repository text as runtime instructions
24. **AC-8.24** THE Adapter_Runtime SHALL enforce Provider_Instance concurrency before calling an adapter
25. **AC-8.25** THE Adapter_Runtime SHALL NOT require a provider-specific branch in the Credential_Scheduler
26. **AC-8.26** WHEN the linked Provider_Instance Provider_Kind differs from the adapter kind, THE Adapter_Runtime SHALL reject the operation before provider traffic

### Requirement 9: Generic query intent, native overrides, reproducibility

**User Story:** As a search administrator, I want generic search intent translated predictably with controlled native overrides, so that provider-specific syntax remains reproducible without weakening policy.

#### Acceptance Criteria

1. **AC-9.1** THE Query_Service SHALL retain existing `SearchQuery` records as the default provider-neutral intent
2. **AC-9.2** THE Query_Override_Store SHALL key each override by SearchQuery identifier and Provider_Instance identifier
3. **AC-9.3** THE Query_Override_Store SHALL persist independent enabled state for each override
4. **AC-9.4** THE Query_Override_Store SHALL persist native query text separately from validated structured settings
5. **AC-9.5** WHEN an enabled override exists for the exact SearchQuery and Provider_Instance pair, THE Query_Service SHALL use that override
6. **AC-9.6** WHEN no enabled exact-instance override exists, THE Query_Service SHALL pass the generic intent to the adapter translator
7. **AC-9.7** BEFORE execution, THE Query_Service SHALL validate every requested filter against Effective_Capabilities
8. **AC-9.8** WHEN a requested query semantic is unsupported, THE Query_Service SHALL reject the operation explicitly
9. **AC-9.9** WHEN a requested query semantic is unsupported, THE Query_Service SHALL NOT silently broaden or weaken the search
10. **AC-9.10** THE Query_Service SHALL validate override settings against the selected adapter version's declared settings schema
11. **AC-9.11** THE Query_Service SHALL NOT permit an override to bypass grant evaluation
12. **AC-9.12** THE Query_Service SHALL NOT permit an override to bypass public-search consent
13. **AC-9.13** THE Query_Service SHALL NOT permit an override to bypass Endpoint_Policy
14. **AC-9.14** THE Query_Service SHALL NOT permit native query text or settings to contain Provider_Access_Credentials or node tokens
15. **AC-9.15** THE Query_Service SHALL calculate Effective_Query_Hash from the canonical generic intent, selected native override, validated settings, Provider_Instance Stable_ID, and adapter version
16. **AC-9.16** WHEN identical canonical query inputs use the same adapter version, THE Query_Service SHALL produce the same Effective_Query_Hash
17. **AC-9.17** WHEN an override or adapter version changes, THE Work_Service SHALL preserve the original effective-query snapshot and hash for already-created Work_Items
18. **AC-9.18** WHEN changed query inputs require execution, THE Work_Service SHALL create new work identity rather than rewriting completed-work reproducibility data
19. **AC-9.19** THE Query_Override_Store SHALL enforce one override row per SearchQuery and Provider_Instance pair

### Requirement 10: Normalized results, deduplication, provenance

**User Story:** As a security researcher, I want results normalized with accurate provider provenance and stable deduplication, so that findings remain attributable and replay-safe across providers and workers.

#### Acceptance Criteria

1. **AC-10.1** THE Normalized_Result SHALL persist the actual Provider_Kind reported by the executing adapter
2. **AC-10.2** THE Normalized_Result SHALL persist the actual Provider_Instance Stable_ID used for execution
3. **AC-10.3** THE Normalized_Result SHALL persist the provider's stable repository identifier
4. **AC-10.4** THE Normalized_Result SHALL persist repository owner and repository name when supplied by the provider
5. **AC-10.5** THE Normalized_Result SHALL persist repository web URL as provenance rather than identity
6. **AC-10.6** THE Normalized_Result SHALL persist branch and immutable revision or provider-equivalent version when supplied
7. **AC-10.7** THE Normalized_Result SHALL persist normalized file path and optional file name
8. **AC-10.8** THE Normalized_Result SHALL persist optional line number and bounded snippet
9. **AC-10.9** THE Normalized_Result SHALL persist optional content API URL and file UI URL as provenance rather than identity
10. **AC-10.10** THE Normalized_Result SHALL persist SearchQuery identifier, Work_Item identifier, and discovery UTC
11. **AC-10.11** THE Normalized_Result SHALL persist versioned, sanitized provenance data
12. **AC-10.12** THE Deduplication_Service SHALL build result identity from Provider_Instance Stable_ID, repository stable ID, immutable revision or provider-equivalent version, and normalized file path
13. **AC-10.13** THE Deduplication_Service SHALL NOT use a repository, content, or UI URL as the sole result identity
14. **AC-10.14** THE Path_Normalizer SHALL produce the same normalized path for provider-equivalent separator and dot-segment representations
15. **AC-10.15** WHEN an identical result is replayed, THE Result_Service SHALL preserve one deduplicated result identity without losing newer provenance references
16. **AC-10.16** WHEN the same Discovered_Secret appears in multiple repository locations, THE Result_Service SHALL retain each distinct repository and provenance reference
17. **AC-10.17** THE Worker discovery DTO SHALL carry actual Provider_Kind and Provider_Instance Stable_ID
18. **AC-10.18** WHEN a Worker report omits or supplies an unknown provider identity, THE Master SHALL reject the report rather than attributing it to GitHub or `GitHub (Ghost)`
19. **AC-10.19** WHEN Worker-reported Provider_Kind conflicts with the registered Provider_Instance, THE Master SHALL reject the report
20. **AC-10.20** THE Provenance_Store SHALL NOT contain Provider_Access_Credentials, node tokens, complete Fingerprints, or authorization headers
21. **AC-10.21** THE Result_Service SHALL apply the transactional or replay-safe checkpoint rules in Requirement 7 to normalized result persistence
22. **AC-10.22** THE Result_Service SHALL preserve the existing access and export policy classification of each Discovered_Secret
23. **AC-10.23** WHEN a Worker reports a discovery, THE Master SHALL require its Provider_Kind and Provider_Instance Stable_ID to match the immutable Work_Item, Partition, and Claim or Operation_Slot identity before persistence
24. **AC-10.24** WHEN a provider supplies no immutable revision identifier, THE Provider_Adapter SHALL derive a deterministic provider-equivalent content version before persistence without using branch names or URLs as that version

### Requirement 11: Master/worker protocol and runtime roles

**User Story:** As a distributed deployment operator, I want an authenticated, fail-closed Master/Worker protocol with explicit runtime roles, so that Workers execute assigned provider work without receiving credential pools.

#### Acceptance Criteria

1. **AC-11.1** THE Master SHALL expose `POST /api/v1/nodes/credential-claims` as the only Worker endpoint that may return a Provider_Access_Credential secret
2. **AC-11.2** THE Claim request SHALL contain Request_ID, Provider_Instance Stable_ID, SearchQuery identifier, Work_Item identifier, Partition key, and requested Lease seconds
3. **AC-11.3** WHEN a Claim is granted, THE Claim response SHALL contain credential Stable_ID, Provider_Instance Stable_ID, Lease ID, Lease expiry, expected Revision, and exactly one secret
4. **AC-11.4** WHEN an active Claim replay succeeds idempotently, THE Claim_API SHALL return HTTP 200 with the original active Lease result
5. **AC-11.5** WHEN a terminal Claim replay succeeds idempotently, THE Claim_API SHALL return HTTP 200 with the original terminal result and no secret
6. **AC-11.6** WHEN a Claim response contains a secret, THE Claim_API SHALL set `Cache-Control: no-store`
7. **AC-11.7** THE Claim_API SHALL disable or redact request and response body logging for the Claim route
8. **AC-11.8** THE Master SHALL expose `POST /api/v1/nodes/credential-claims/{leaseId}/renew` for Lease renewal
9. **AC-11.9** THE Renewal request SHALL contain credential Stable_ID, expected Revision, Work_Item identifier, and requested extension
10. **AC-11.10** WHEN renewal succeeds, THE Renewal_API SHALL return only the new Lease expiry and non-secret protocol metadata
11. **AC-11.11** THE Renewal_API SHALL NOT return credential material
12. **AC-11.12** THE Master SHALL expose `POST /api/v1/nodes/credential-claims/{leaseId}/complete` for typed Lease completion
13. **AC-11.13** THE Completion request SHALL contain credential Stable_ID, Work_Item identifier, Typed_Outcome, optional HTTP status, optional allowlisted provider code, optional provider retry time, and work operation-complete flag
14. **AC-11.14** THE Completion_API SHALL NOT accept or return credential material
15. **AC-11.15** THE Completion_API SHALL NOT accept raw provider bodies, authorization headers, or exception text
16. **AC-11.16** WHEN a node token is missing or invalid, THE Node_API SHALL return HTTP 401
17. **AC-11.17** WHEN a node is authenticated but lacks the required grant or operation authorization, THE Node_API SHALL return HTTP 403
18. **AC-11.18** WHEN a request has malformed fields, unsupported Provider_Kind, non-positive duration, or an invalid operation identity, THE Node_API SHALL return HTTP 400
19. **AC-11.19** WHEN no credential or Operation_Slot is currently eligible, THE Node_API SHALL return HTTP 409 with a typed response body
20. **AC-11.20** WHEN a 409 condition has a trusted retry time, THE Node_API SHALL include that time in the typed body and a `Retry-After` header
21. **AC-11.21** WHEN a Lease is stale or conflicts with current Lease identity, THE Node_API SHALL return HTTP 409 with a typed stale-or-conflict body
22. **AC-11.22** WHEN Scheduler, schema, database, or required-key readiness fails, THE Node_API SHALL return HTTP 503 without a secret
23. **AC-11.23** WHEN a grant, renewal, completion, or idempotent replay succeeds, THE Node_API SHALL return HTTP 200
24. **AC-11.24** THE Node_API SHALL authenticate all three credential endpoints with `X-Node-Token` over HTTPS
25. **AC-11.25** THE Node_API SHALL redact `X-Node-Token` from logs, traces, metrics, and errors
26. **AC-11.26** THE Configuration_Sync protocol SHALL remain separate from credential Claim, renewal, and completion
27. **AC-11.27** AFTER Worker-claims cutover, THE Configuration_Sync protocol SHALL contain no Provider_Access_Credential material
28. **AC-11.28** WHEN `IS_WORKER_MODE=true`, THE Runtime SHALL register `WorkerScraperHostedService`
29. **AC-11.29** WHEN `IS_WORKER_MODE=true`, THE Worker startup SHALL require a valid `MASTER_API_URL`
30. **AC-11.30** WHEN `IS_WORKER_MODE=true`, THE Worker startup SHALL require a non-empty `NODE_TOKEN`
31. **AC-11.31** WHEN required Worker settings are missing, THE Readiness_Service SHALL report unhealthy rather than a healthy idle Worker
32. **AC-11.32** THE Worker hosted service SHALL create a dependency-injection scope for each orchestration cycle
33. **AC-11.33** THE Worker hosted service SHALL synchronize assigned Work_Items, queries, and Provider_Instances without credential pools
34. **AC-11.34** THE Worker hosted service SHALL request a Claim immediately before each credentialed provider operation
35. **AC-11.35** THE Worker hosted service SHALL renew a Lease before the configured renewal threshold
36. **AC-11.36** THE Worker hosted service SHALL attempt completion in a `finally` path whenever Master communication is available
37. **AC-11.37** THE Worker hosted service SHALL report discoveries with Provider_Kind and Provider_Instance provenance
38. **AC-11.38** THE Worker hosted service SHALL send node heartbeats independently from Lease completion
39. **AC-11.39** WHEN the Master or database is unavailable, THE Worker SHALL retry sync or Claim with bounded backoff
40. **AC-11.40** WHEN the Master or database is unavailable, THE Worker SHALL NOT use a local credential fallback
41. **AC-11.41** WHEN `IS_WORKER_MODE=false`, THE Runtime SHALL NOT register `WorkerScraperHostedService`
42. **AC-11.42** WHEN running the Master role, THE Runtime SHALL register Master-side node monitoring separately from Worker execution
43. **AC-11.43** THE Master scraper SHALL obtain credentials through the same Credential_Scheduler used by Worker Claims
44. **AC-11.44** WHEN `IS_WORKER_MODE=true`, THE Worker startup SHALL reject a `MASTER_API_URL` whose scheme is not HTTPS before sending a node token or Claim request
45. **AC-11.45** WHEN the Completion_API accepts a request, THE work operation-complete flag SHALL control only Work_Item and checkpoint terminal state rather than Lease release

### Requirement 12: Endpoint/origin/SSRF and public-search safety

**User Story:** As a security administrator, I want provider endpoints and public-search operations constrained by validated origins and explicit consent, so that credentials and network access cannot be redirected to unapproved destinations.

#### Acceptance Criteria

1. **AC-12.1** BEFORE capability discovery, credential validation, search, or content retrieval for a self-hosted instance, THE Endpoint_Policy SHALL validate that instance
2. **AC-12.2** THE Endpoint_Policy SHALL normalize scheme, host, port, and base path before storing or comparing an origin
3. **AC-12.3** WHEN a configured endpoint contains user-info, THE Endpoint_Policy SHALL reject it
4. **AC-12.4** WHEN a configured endpoint contains a fragment, THE Endpoint_Policy SHALL reject it
5. **AC-12.5** WHEN a configured endpoint contains an unexpected query string, THE Endpoint_Policy SHALL reject it
6. **AC-12.6** THE Endpoint_Policy SHALL require HTTPS for provider instances by default
7. **AC-12.7** WHEN HTTP is approved for development, THE Endpoint_Policy SHALL limit the exception to one explicitly named development Provider_Instance
8. **AC-12.8** WHEN an HTTP development exception is created or changed, THE Audit_Service SHALL record the administrator and exact Provider_Instance Stable_ID
9. **AC-12.9** THE Endpoint_Policy SHALL resolve DNS for each connection or within a configured short cache lifetime
10. **AC-12.10** WHEN DNS returns multiple addresses, THE Endpoint_Policy SHALL validate every returned address
11. **AC-12.11** THE Endpoint_Policy SHALL block metadata-service address ranges by default
12. **AC-12.12** THE Endpoint_Policy SHALL block multicast, unspecified, loopback, and link-local IPv4 and IPv6 ranges by default
13. **AC-12.13** THE Endpoint_Policy SHALL block private IPv4 and IPv6 ranges by default
14. **AC-12.14** WHEN a private network endpoint is required, THE Endpoint_Policy SHALL require an explicit allowlist entry scoped to the Provider_Instance
15. **AC-12.15** WHEN a resolved address is not covered by the approved instance policy, THE Endpoint_Policy SHALL block the connection
16. **AC-12.16** THE Endpoint_Policy SHALL connect only to an address from the validated DNS result while preserving the configured host for HTTP Host and TLS SNI
17. **AC-12.17** WHEN DNS changes after prior validation, THE Endpoint_Policy SHALL reapply address classification before connection
18. **AC-12.18** THE Endpoint_Policy SHALL enforce a configured maximum redirect count
19. **AC-12.19** BEFORE following each redirect, THE Endpoint_Policy SHALL normalize, resolve, and validate the redirect target
20. **AC-12.20** WHEN a redirect changes origin, THE Adapter_Runtime SHALL strip Provider_Access_Credentials before sending the redirected request
21. **AC-12.21** WHEN a redirect changes origin, THE Adapter_Runtime SHALL strip node tokens, cookies, and authorization headers before sending the redirected request
22. **AC-12.22** THE Adapter_Runtime SHALL send Provider_Access_Credentials only to the exact approved scheme, host, and port of the Provider_Instance
23. **AC-12.23** THE Adapter_Runtime SHALL send Provider_Access_Credentials only under the approved API path prefix
24. **AC-12.24** THE Endpoint_Policy SHALL enforce configured request timeout, response-size, and content-type limits
25. **AC-12.25** WHEN endpoint validation fails, THE Adapter_Runtime SHALL disable new operations for that Provider_Instance
26. **AC-12.26** WHEN endpoint validation fails, THE Adapter_Runtime SHALL NOT retry against the raw unvalidated URL
27. **AC-12.27** WHEN an instance origin or path policy changes, THE Provider_Instance_Service SHALL require new endpoint approval before operations resume
28. **AC-12.28** WHEN a Public_Operation is requested, THE Capability_Service SHALL require effective `GlobalPublicSearch` capability
29. **AC-12.29** WHEN a Public_Operation is requested, THE Consent_Service SHALL require active administrator consent recorded for the exact Provider_Instance
30. **AC-12.30** WHEN either public-search capability or active consent is absent, THE Adapter_Runtime SHALL reject the Public_Operation
31. **AC-12.31** WHEN administrator consent is revoked, THE Adapter_Runtime SHALL reject every new Public_Operation for that Provider_Instance
32. **AC-12.32** WHEN a Public_Operation executes, THE Scheduler SHALL acquire and audit a durable Operation_Slot without issuing a credential Claim
33. **AC-12.33** WHEN a Public_Operation executes, THE Work_Service SHALL retain Work_Item, principal, query, checkpoint, and provenance records
34. **AC-12.34** THE Adapter_Runtime SHALL require credential-scoped execution for private-repository searches
35. **AC-12.35** THE Adapter_Runtime SHALL require credential-scoped execution for self-hosted searches other than a separately approved capability-supported Public_Operation
36. **AC-12.36** THE Consent_Service SHALL audit public-search opt-in, opt-out, administrator identity, Provider_Instance Stable_ID, and UTC time without secrets

### Requirement 13: Persistence, PostgreSQL/SQLite concurrency, readiness

**User Story:** As a deployment operator, I want complete schema validation and database-appropriate concurrency, so that scheduling remains atomic and fails closed on unsupported or incomplete deployments.

#### Acceptance Criteria

1. **AC-13.1** THE Persistence_Model SHALL include Provider_Instance, evolved `SearchProviderToken`, credential grant, lease audit/idempotency, query override, Work_Item, Partition checkpoint, Operation_Slot, public-search consent, normalized result, and provenance entities
2. **AC-13.2** THE Credential_Store SHALL persist protected-secret, fingerprint, key-version, source, generation, last-seen, enabled, cooldown, health, disabled, Lease, Request_ID, owner, expiry, and Revision fields
3. **AC-13.3** THE Provider_Instance_Store SHALL enforce a unique Stable_ID
4. **AC-13.4** THE Provider_Instance_Store SHALL enforce unique normalized Provider_Kind and base-URL identity
5. **AC-13.5** THE Credential_Store SHALL enforce a unique credential Stable_ID
6. **AC-13.6** AFTER duplicate reconciliation, THE Credential_Store SHALL enforce unique Provider_Instance and Fingerprint identity
7. **AC-13.7** THE Credential_Store SHALL provide an eligibility index beginning with Provider_Instance, enabled/disabled state, cooldown, and Lease availability before fairness fields
8. **AC-13.8** THE Grant_Store SHALL enforce a unique credential, scope, and principal combination
9. **AC-13.9** THE Idempotency_Store SHALL enforce a unique Scheduler_Principal and Request_ID combination
10. **AC-13.10** THE Credential_Store SHALL enforce uniqueness for every non-null Lease ID
11. **AC-13.11** THE Query_Override_Store SHALL enforce a unique SearchQuery and Provider_Instance combination
12. **AC-13.12** THE Result_Store SHALL index Provider_Instance, repository stable ID, immutable revision or equivalent, and normalized path for deduplication
13. **AC-13.13** THE Operation_Slot_Store SHALL persist slot ID, Request_ID, Provider_Instance, principal, Work_Item, Partition, expiry, Revision, and terminal status
14. **AC-13.14** THE Schema SHALL persist an explicit schema version and Readiness_Marker
15. **AC-13.15** THE Schema_Updater SHALL update model classes for every active schema change
16. **AC-13.16** THE Schema_Updater SHALL update `DBContext.OnModelCreating` for every active schema change
17. **AC-13.17** THE Schema_Updater SHALL update manual SQLite initialization for every active schema change
18. **AC-13.18** THE Schema_Updater SHALL update manual PostgreSQL initialization for every active schema change
19. **AC-13.19** THE Schema_Updater SHALL update `master_init.sql` for every active schema change
20. **AC-13.20** THE Schema_Updater SHALL update WebAPI startup and readiness validation for every active schema change
21. **AC-13.21** BEFORE Claim readiness becomes healthy, THE Readiness_Service SHALL validate required tables, columns, constraints, and indexes
22. **AC-13.22** BEFORE Claim readiness becomes healthy, THE Readiness_Service SHALL validate required protection and fingerprint key versions
23. **AC-13.23** BEFORE Claim readiness becomes healthy, THE Readiness_Service SHALL validate default Phase 0 Provider_Instances
24. **AC-13.24** BEFORE Claim readiness becomes healthy, THE Readiness_Service SHALL validate the active schema version and cutover marker
25. **AC-13.25** WHEN schema validation fails, THE Claim_API SHALL remain unavailable with HTTP 503
26. **AC-13.26** WHEN database connectivity fails, THE Claim_API SHALL fail closed with HTTP 503
27. **AC-13.27** THE Platform SHALL require PostgreSQL for horizontally scaled Masters or production multi-node coordination
28. **AC-13.28** THE Platform SHALL restrict SQLite scheduling to one Master process or deterministic tests
29. **AC-13.29** THE Readiness_Service SHALL NOT advertise distributed scheduling readiness for independent SQLite files
30. **AC-13.30** THE Platform SHALL NOT support network-shared SQLite as a coordination mechanism
31. **AC-13.31** WHEN local Workers call one SQLite-backed Master, THE Readiness_Service SHALL identify the deployment as single-Master rather than distributed coordination
32. **AC-13.32** THE PostgreSQL Scheduler SHALL execute each Claim in a short explicit transaction through the configured EF Core execution strategy
33. **AC-13.33** THE PostgreSQL Scheduler SHALL use database `CURRENT_TIMESTAMP` for Claim, expiry, cooldown, and completion decisions
34. **AC-13.34** THE PostgreSQL Scheduler SHALL use row locking with `FOR UPDATE SKIP LOCKED` for eligible-credential selection
35. **AC-13.35** WHEN an idempotency insert race occurs, THE PostgreSQL Scheduler SHALL roll back the losing transaction and return the winning record
36. **AC-13.36** THE PostgreSQL Scheduler SHALL replay the entire transaction unit on a configured transient transaction retry
37. **AC-13.37** THE PostgreSQL Scheduler SHALL NOT decrypt a credential while holding a credential row lock
38. **AC-13.38** THE PostgreSQL Scheduler SHALL NOT call a provider while holding a credential row lock
39. **AC-13.39** THE SQLite Scheduler SHALL serialize Claims through a process-local gate
40. **AC-13.40** THE SQLite Scheduler SHALL use a short `BEGIN IMMEDIATE` transaction for selection and conditional update
41. **AC-13.41** THE SQLite Scheduler SHALL condition its Lease update on the observed eligibility and Revision
42. **AC-13.42** THE SQLite Scheduler SHALL commit before credential decryption
43. **AC-13.43** THE SQLite Scheduler SHALL use a configured bounded busy timeout
44. **AC-13.44** WHEN SQLite remains busy after its timeout, THE SQLite Scheduler SHALL return a typed transient scheduler result
45. **AC-13.45** THE Lease_Audit_Service SHALL apply configured bounded retention or aggregation without deleting an active Claim record or a terminal Claim record still inside its Terminal_Retry_Window
46. **AC-13.46** THE PostgreSQL integration path SHALL remain compatible with the transaction mode used by the deployed Supabase or PgBouncer connection
47. **AC-13.47** THE Claim_Record SHALL persist Scheduler_Principal, Request_ID, canonical request fingerprint, Provider_Instance, SearchQuery, Work_Item, Partition, requested Lease duration, credential Stable_ID, Lease ID, status, Revision, created time, expiry, and terminal time
48. **AC-13.48** THE Operation_Slot_Store SHALL enforce a unique non-null slot ID and index unexpired slots by Provider_Instance and expiry

### Requirement 14: Administration, audit, observability, redaction

**User Story:** As an administrator, I want credential and provider health visible through safe Telegram and administration surfaces, so that I can remediate failures without exposing secrets.

#### Acceptance Criteria

1. **AC-14.1** THE Admin and Telegram health views SHALL display Provider_Kind and Provider_Instance display name
2. **AC-14.2** THE Admin and Telegram health views SHALL display a credential Stable_ID reference or masked alias without displaying credential material
3. **AC-14.3** THE Admin and Telegram health views SHALL display enabled or disabled state and manual or environment source
4. **AC-14.4** THE Admin and Telegram health views SHALL display last Claim time and last successful-use time
5. **AC-14.5** THE Admin and Telegram health views SHALL display current cooldown and next eligibility time
6. **AC-14.6** THE Admin and Telegram health views SHALL display consecutive transient-failure count and last Typed_Outcome
7. **AC-14.7** THE Admin and Telegram health views SHALL display disabled reason and disabled time
8. **AC-14.8** THE administrator-only health view SHALL display active Lease owner and expiry
9. **AC-14.9** THE Admin and Telegram provider views SHALL display aggregate claims, successes, rate limits, authentication failures, and latency by Provider_Instance
10. **AC-14.10** THE Credential_Management UX SHALL expose explicit manual disable, re-enable, and replacement commands only to authorized administrators
11. **AC-14.11** WHEN an administrator re-enables a credential, THE Credential_Management_Service SHALL clear disabled reason and time in the same audited command that sets `IsEnabled=true`
12. **AC-14.12** WHEN an administrator replaces a credential, THE Controller SHALL route the change through the credential identity and protection services
13. **AC-14.13** THE Controller SHALL NOT write ciphertext or Fingerprint fields directly
14. **AC-14.14** THE Administration UX SHALL expose Provider_Instance approval, private-range allowlisting, and public-search consent as separately audited actions
15. **AC-14.15** THE Audit_Service SHALL record actor, action, target Stable_ID, UTC time, outcome, and sanitized reason for each privileged action
16. **AC-14.16** THE Audit_Service SHALL NOT record Provider_Access_Credentials, node tokens, full Fingerprints, or raw provider bodies
17. **AC-14.17** THE Structured_Log SHALL permit Provider_Kind, Provider_Instance Stable_ID, credential Stable_ID, Lease ID, Work_Item ID, Typed_Outcome, HTTP status, and sanitized retry time as diagnostic fields
18. **AC-14.18** THE Metrics_Service SHALL expose `search_credential_claim_total{provider,instance,outcome}`
19. **AC-14.19** THE Metrics_Service SHALL expose `search_credential_claim_wait_seconds{provider,instance}`
20. **AC-14.20** THE Metrics_Service SHALL expose `search_credential_active_leases{provider,instance}`
21. **AC-14.21** THE Metrics_Service SHALL expose `search_credential_cooldown_total{provider,instance,reason}`
22. **AC-14.22** THE Metrics_Service SHALL expose `search_provider_operation_total{provider,instance,outcome}`
23. **AC-14.23** THE Metrics_Service SHALL expose `search_provider_operation_duration_seconds{provider,instance}`
24. **AC-14.24** THE Metrics_Service SHALL expose `search_worker_claim_api_total{status}`
25. **AC-14.25** THE Metrics_Service SHALL expose `search_worker_lease_renewal_total{status}`
26. **AC-14.26** THE Metrics_Service SHALL expose `search_worker_discovery_total{provider,instance}`
27. **AC-14.27** THE Metrics_Service SHALL restrict metric labels to bounded provider, instance, outcome, reason, and status values
28. **AC-14.28** THE Metrics_Service SHALL NOT use credential Stable_ID, Lease ID, Work_Item ID, Telegram ID, Request_ID, or node ID as metric labels
29. **AC-14.29** THE Platform SHALL NOT log or export raw Provider_Access_Credentials
30. **AC-14.30** THE Platform SHALL NOT log or export complete Fingerprints
31. **AC-14.31** THE Platform SHALL NOT log or export decrypted Claim bodies
32. **AC-14.32** THE Platform SHALL redact `Authorization`, `PRIVATE-TOKEN`, and `X-Node-Token` headers
33. **AC-14.33** THE Platform SHALL NOT log raw provider bodies that may echo secrets
34. **AC-14.34** THE Platform SHALL NOT log protection keys or fingerprint keys
35. **AC-14.35** THE Platform SHALL NOT log connection strings containing passwords
36. **AC-14.36** THE Redaction_Test_Suite SHALL inject canary secrets through log sinks and HTTP instrumentation and prove that no canary value is emitted
37. **AC-14.37** THE Bootstrap health view SHALL report imported counts and Stable_ID references without credential values or complete Fingerprints
38. **AC-14.38** THE Readiness health view SHALL report schema, key-version, Scheduler, and Provider_Instance readiness without secret material
39. **AC-14.39** THE Audit_Service SHALL apply configured bounded retention while preserving records required by active Lease and idempotency windows
40. **AC-14.40** WHEN a non-administrator opens a Telegram or administration health view, THE Administration_Service SHALL return only Provider_Instances and credential aliases authorized for that resolved Telegram_Principal
41. **AC-14.41** WHEN an authorized administrator opens a fleet health view, THE Administration_Service SHALL permit the fleet-wide projection without exposing credential material or complete Fingerprints

### Requirement 15: Feature flags, migration, cutover, rollback

**User Story:** As a release operator, I want a staged and fail-closed cutover controlled by explicit flags and durable markers, so that protected scheduling can be deployed or rolled back without restoring unsafe secret distribution.

#### Acceptance Criteria

1. **AC-15.1** THE Configuration SHALL define `SearchCredentials:ProtectedStorageEnabled`
2. **AC-15.2** THE Configuration SHALL define `SearchCredentials:EnvironmentBootstrapEnabled`
3. **AC-15.3** THE Configuration SHALL define `CredentialScheduler:Enabled`
4. **AC-15.4** THE Configuration SHALL define `CredentialScheduler:MasterScraperClaimsEnabled`
5. **AC-15.5** THE Configuration SHALL define `CredentialScheduler:WorkerClaimsEnabled`
6. **AC-15.6** THE Configuration SHALL define `CredentialScheduler:LegacyTokenSyncEnabled`
7. **AC-15.7** THE Configuration SHALL define `CredentialScheduler:AllowWorkerLocalFallback`
8. **AC-15.8** THE Configuration SHALL define `SearchProviders:InstancesEnabled`
9. **AC-15.9** WHEN `CredentialScheduler:Enabled=true`, THE Readiness_Service SHALL require protected storage and provider instances to be enabled and ready
10. **AC-15.10** WHEN `CredentialScheduler:MasterScraperClaimsEnabled=true`, THE Readiness_Service SHALL require the common Credential_Scheduler to be enabled
11. **AC-15.11** WHEN `CredentialScheduler:WorkerClaimsEnabled=true`, THE Readiness_Service SHALL require the Claim, renewal, and completion APIs to be ready
12. **AC-15.12** WHEN Worker-claims cutover is recorded, THE Readiness_Service SHALL require `CredentialScheduler:AllowWorkerLocalFallback=false`
13. **AC-15.13** WHEN Worker-claims cutover is recorded, THE Readiness_Service SHALL require credential-bearing legacy token sync to be disabled
14. **AC-15.14** THE Platform SHALL permit `CredentialScheduler:AllowWorkerLocalFallback=true` only during an explicitly marked pre-cutover migration stage
15. **AC-15.15** THE Platform SHALL NOT enable Worker local fallback automatically after Master, database, schema, bootstrap, or key failure
16. **AC-15.16** AFTER Worker-claims cutover, THE Platform SHALL reject any configuration that enables Worker local fallback
17. **AC-15.17** WHEN a feature-flag combination violates the cutover matrix, THE Readiness_Service SHALL report unhealthy before scheduling starts
18. **AC-15.18** BEFORE additive migration, THE Release_Process SHALL capture baseline behavior and a restorable database backup
19. **AC-15.19** THE additive schema stage SHALL add provider instances, protected credential fields, grants, Leases, audits, Operation_Slots, work state, and query overrides without changing active selection
20. **AC-15.20** THE backfill stage SHALL protect credentials, reconcile duplicates, create grants, link default instances, and establish the Readiness_Marker before cutover
21. **AC-15.21** THE bootstrap-shadow stage SHALL import Master environment credentials and report non-secret counts without scheduling them
22. **AC-15.22** THE scheduler-shadow stage SHALL calculate candidate ordering without granting a Claim
23. **AC-15.23** THE Master-cutover stage SHALL route Master GitHub and GitLab operations through durable Claims behind the Master claims flag
24. **AC-15.24** THE Worker-API stage SHALL deploy all three credential endpoints before enabling a Worker canary
25. **AC-15.25** THE Worker-canary stage SHALL run one real Worker orchestrator and verify attribution, fairness, renewal, completion, and Lease recovery
26. **AC-15.26** BEFORE fleet cutover, THE Release_Process SHALL copy authorized Worker GitHub and GitLab credentials into protected Master configuration
27. **AC-15.27** BEFORE fleet cutover, THE Release_Process SHALL verify imported counts and Stable_ID references in the administration health view
28. **AC-15.28** BEFORE removing Worker credential variables, THE Release_Process SHALL verify claim-enabled Worker health
29. **AC-15.29** AT fleet cutover, THE Release_Process SHALL remove Worker-local credential variables
30. **AC-15.30** AT fleet cutover, THE Release_Process SHALL disable local credential fallback
31. **AC-15.31** AFTER fleet cutover, THE Release_Process SHALL remove Provider_Access_Credentials from node synchronization
32. **AC-15.32** BEFORE plaintext scrub, THE Migration_Service SHALL prove that every enabled credential can be decrypted through protected reads
33. **AC-15.33** DURING the protected-storage migration window, THE Runtime SHALL allow legacy plaintext only through guarded dual-read and protected-write behavior
34. **AC-15.34** AFTER protected-read verification, THE Migration_Service SHALL disable plaintext writes before scrubbing legacy plaintext values
35. **AC-15.35** THE Migration_Service SHALL remove or repurpose the legacy plaintext column only in a later separately approved migration
36. **AC-15.36** AFTER all active paths use the Scheduler, THE Release_Process SHALL remove legacy `TokenCursor`, depleted-token dictionaries, and old secret-sync shapes
37. **AC-15.37** BEFORE plaintext scrub, THE Rollback_Process SHALL restore a compatibility path through explicit flags while retaining additive schema
38. **AC-15.38** AFTER plaintext scrub, THE Rollback_Process SHALL use only a release capable of protected credential reads and durable scheduling
39. **AC-15.39** THE Rollback_Process SHALL NOT destructively downgrade credential, grant, Lease, work, result, or Provider_Instance tables
40. **AC-15.40** BEFORE disabling Master Claim APIs, THE Rollback_Process SHALL disable new Worker Claims
41. **AC-15.41** BEFORE switching scheduling modes, THE Rollback_Process SHALL allow active Leases to complete or expire
42. **AC-15.42** THE Rollback_Process SHALL NOT restore credential-bearing node sync after Worker-claims cutover
43. **AC-15.43** DURING migration, THE authoritative Provider_Instance `ProviderKind` SHALL replace legacy provider selection only after compatibility values have been validated as matching

### Requirement 16: Provider-specific behavior and phased roadmap

**User Story:** As a product owner, I want each provider delivered through a verified phased roadmap and common definition of done, so that provider-specific behavior never bypasses the shared platform contract.

#### Acceptance Criteria

1. **AC-16.1** AT the start of each provider phase, THE Provider_Team SHALL revalidate required capabilities against current official provider documentation
2. **AC-16.2** THE Roadmap SHALL complete one provider phase before starting implementation of the next provider phase
3. **AC-16.3** PHASE 0 SHALL deliver the common platform foundation with GitHub and GitLab before Sourcegraph implementation begins
4. **AC-16.4** THE Phase_0 GitHub adapter SHALL use an instance-ready paginated SaaS API path
5. **AC-16.5** WHEN a GitHub rate-limit response includes `Retry-After`, THE GitHub classifier SHALL return `RateLimited` using the clamped delay
6. **AC-16.6** WHEN GitHub reports `X-RateLimit-Remaining=0` with a reset time, THE GitHub classifier SHALL return `RateLimited` using the clamped reset plus configured safety buffer
7. **AC-16.7** WHEN an allowlisted GitHub secondary-limit code or message is present, THE GitHub classifier SHALL return `RateLimited` using at least the configured minimum delay and recurrence policy
8. **AC-16.8** WHEN GitHub returns HTTP 429 without more specific trusted evidence, THE GitHub classifier SHALL return `RateLimited` using the configured bounded fallback
9. **AC-16.9** WHEN GitHub returns definitive HTTP 401 or an allowlisted invalid-token code, THE GitHub classifier SHALL return `AuthInvalid`
10. **AC-16.10** WHEN GitHub returns an ordinary permission HTTP 403 without rate-limit evidence, THE GitHub classifier SHALL return `ForbiddenScope`
11. **AC-16.11** WHEN GitHub returns a contextual private-resource HTTP 404, THE GitHub classifier SHALL return `ResourceMissing` or `ForbiddenScope` rather than `AuthInvalid`
12. **AC-16.12** WHEN GitHub returns HTTP 400 or 422 for invalid search input, THE GitHub classifier SHALL return `RequestInvalid`
13. **AC-16.13** WHEN GitHub returns HTTP 408, a timeout, a connectivity failure, or HTTP 5xx, THE GitHub classifier SHALL return `Transient`
14. **AC-16.14** THE GitHub adapter SHALL propagate rate-limit and API failures through Typed_Outcomes rather than swallowing them as partial or empty success
15. **AC-16.15** THE GitHub adapter SHALL expose an injectable transport and classifier boundary for deterministic response-table tests
16. **AC-16.16** THE Phase_0 GitLab adapter SHALL support the SaaS instance and administrator-approved self-hosted instances through parameterized base URLs
17. **AC-16.17** WHEN GitLab returns definitive HTTP 401, THE GitLab classifier SHALL return `AuthInvalid`
18. **AC-16.18** WHEN GitLab returns HTTP 403 without rate-limit evidence, THE GitLab classifier SHALL return `ForbiddenScope`
19. **AC-16.19** WHEN GitLab returns HTTP 429, THE GitLab classifier SHALL return `RateLimited`
20. **AC-16.20** WHEN GitLab rate-limit evidence lacks a supported reset time, THE GitLab classifier SHALL use the configured bounded fallback cooldown
21. **AC-16.21** WHEN GitLab returns HTTP 400 or 422 for invalid search input, THE GitLab classifier SHALL return `RequestInvalid`
22. **AC-16.22** WHEN GitLab returns contextual HTTP 404, THE GitLab classifier SHALL return `ResourceMissing` or `ForbiddenScope`
23. **AC-16.23** WHEN GitLab returns HTTP 408, a timeout, a connectivity failure, or HTTP 5xx, THE GitLab classifier SHALL return `Transient`
24. **AC-16.24** THE GitLab adapter SHALL propagate provider failures through Typed_Outcomes rather than swallowing them as partial or empty success
25. **AC-16.25** THE GitHub and GitLab classifiers SHALL extract provider error codes only through bounded allowlisted parsers
26. **AC-16.26** THE GitHub and GitLab classifiers SHALL NOT log raw provider error bodies
27. **AC-16.27** PHASE 1 SHALL add Sourcegraph SaaS and approved self-hosted instances through the supported streaming search API
28. **AC-16.28** THE Sourcegraph adapter SHALL parse event framing, progress, matches, alerts, and terminal-completion events distinctly
29. **AC-16.29** THE Sourcegraph adapter SHALL support cancellation and reconnect-safe Last_Safe_Checkpoints
30. **AC-16.30** THE Sourcegraph adapter SHALL keep global-public search disabled until the exact instance passes the dual public-search gate
31. **AC-16.31** IMMEDIATELY before Phase 2 implementation, THE Provider_Team SHALL revalidate the premise that identified Bitbucket Cloud code-search endpoints are deprecated on November 1, 2026 against current official documentation
32. **AC-16.32** BEFORE Bitbucket implementation, THE Provider_Team SHALL document whether a supported official replacement provides the required authorized code-search capability
33. **AC-16.33** WHEN no supported Bitbucket replacement provides the required capability, THE Roadmap SHALL skip Bitbucket implementation and proceed to Phase 3
34. **AC-16.34** THE Bitbucket adapter SHALL NOT use an obsolete, private, or undocumented endpoint
35. **AC-16.35** PHASE 3 SHALL use supported Hugging Face Hub APIs for authorized model, dataset, and Space repositories
36. **AC-16.36** THE Hugging_Face adapter SHALL NOT scrape website pages or depend on undocumented routes
37. **AC-16.37** THE Hugging_Face adapter SHALL require the dual public-search gate for any supported global-public operation
38. **AC-16.38** PHASE 4 SHALL support approved Azure DevOps Services and server variants through explicit organization, project, and repository configuration
39. **AC-16.39** THE Azure_DevOps adapter SHALL normalize project, repository, and version identity into the common result model
40. **AC-16.40** THE Azure_DevOps adapter SHALL NOT assume a global-public-search capability
41. **AC-16.41** PHASE 5 SHALL support administrator-approved Gitea and Forgejo self-hosted instances
42. **AC-16.42** THE Gitea_Forgejo adapter SHALL detect server flavor and version before exposing server-discovered capabilities
43. **AC-16.43** THE Gitea_Forgejo adapter SHALL expose only capabilities validated for that server flavor and version
44. **AC-16.44** BEFORE Gitea or Forgejo capability discovery or credential validation, THE Endpoint_Policy SHALL pass the complete self-hosted safety policy
45. **AC-16.45** FOR every provider phase, THE Definition_Of_Done SHALL require authentication and credential validation
46. **AC-16.46** FOR every provider phase, THE Definition_Of_Done SHALL require complete pagination or streaming terminal behavior
47. **AC-16.47** FOR every provider phase, THE Definition_Of_Done SHALL require generic-query translation and supported native overrides
48. **AC-16.48** FOR every provider phase, THE Definition_Of_Done SHALL require normalized results and required content retrieval
49. **AC-16.49** FOR every provider phase, THE Definition_Of_Done SHALL require typed outcomes and rate-limit handling
50. **AC-16.50** FOR every provider phase, THE Definition_Of_Done SHALL require durable Claims, cooldowns, and Operation_Slots
51. **AC-16.51** FOR every provider phase, THE Definition_Of_Done SHALL require deduplication, checkpoints, and provenance
52. **AC-16.52** FOR every provider phase, THE Definition_Of_Done SHALL require Master and Worker execution paths
53. **AC-16.53** FOR every provider phase, THE Definition_Of_Done SHALL require Telegram and administration setup and health views
54. **AC-16.54** FOR every provider phase, THE Definition_Of_Done SHALL require low-cardinality metrics and secret-redacted logs
55. **AC-16.55** FOR every provider phase, THE Definition_Of_Done SHALL require official deployment guidance and current API references
56. **AC-16.56** FOR every provider phase, THE Definition_Of_Done SHALL require automated provider, scheduler, security, and existing-suite compatibility tests

### Requirement 17: Correctness, resilience, performance, release evidence

**User Story:** As a release owner, I want executable evidence for correctness, recovery, security, and performance boundaries, so that each increment is safe to deploy across supported databases and runtime roles.

#### Acceptance Criteria

1. **AC-17.1** THE Scheduler property suite SHALL prove P1 by producing `1,2,3,1,2,3` for six sequential Claims over three continuously eligible credentials using claim-time order
2. **AC-17.2** THE concurrency suite SHALL prove P2 by preventing more than one active unexpired Lease for a credential under concurrent multi-node Claims
3. **AC-17.3** THE eligibility suite SHALL prove P3 by never granting disabled, auth-disabled, cooling, unauthorized, instance-disabled, or actively leased credentials
4. **AC-17.4** THE idempotency suite SHALL prove P4 by returning the same active or terminal result for the same Scheduler_Principal and Request_ID without consuming another credential
5. **AC-17.5** THE completion suite SHALL prove P5 by showing that a stale completion changes no cooldown, health, disablement, usage, Revision, or current-Lease state
6. **AC-17.6** THE restart suite SHALL prove P6 by preserving cooldown across process restart and honoring it on every node until database UTC reaches expiry
7. **AC-17.7** THE classifier suite SHALL prove P7 by automatically disabling only definitive provider-specific `AuthInvalid` outcomes
8. **AC-17.8** THE redaction suite SHALL prove P8 by finding no plaintext Provider_Access_Credential in logs, metrics, audits, exceptions, non-Claim DTOs, or discovery data
9. **AC-17.9** THE provenance suite SHALL prove P9 by preserving actual Provider_Kind and Provider_Instance through Worker reporting and Master persistence
10. **AC-17.10** THE public-search suite SHALL prove P10 by denying Public_Operations unless effective capability and recorded instance consent are both active
11. **AC-17.11** THE endpoint-security suite SHALL prove P11 by sending provider credentials only to the validated origin and stripping them before a cross-origin redirect
12. **AC-17.12** THE deployment suite SHALL prove P12 by withholding distributed readiness from independent SQLite files and requiring PostgreSQL for multi-node coordination
13. **AC-17.13** WHEN a Worker crashes with an active Lease, THE Recovery_Behavior SHALL make the credential reclaimable only after Lease expiry
14. **AC-17.14** WHEN the Master restarts, THE Recovery_Behavior SHALL resume with persisted fairness, cooldown, Lease, and idempotency state
15. **AC-17.15** WHEN a Claim response is lost, THE Worker SHALL replay the same Request_ID
16. **AC-17.16** WHEN a completion response is lost, THE Worker SHALL replay the same terminal completion
17. **AC-17.17** WHEN renewal is lost, THE Worker SHALL stop new provider traffic before Lease expiry
18. **AC-17.18** WHEN all matching credentials are cooling down or leased, THE Claim_API SHALL return the earliest trusted retry time
19. **AC-17.19** WHEN all matching credentials are disabled or absent, THE Claim_API SHALL return a terminal reason without busy looping
20. **AC-17.20** WHEN bootstrap input is partially invalid, THE Recovery_Behavior SHALL preserve the previously committed generation and skip removal reconciliation
21. **AC-17.21** WHEN provider DNS changes to a blocked address, THE Recovery_Behavior SHALL block the next connection pending administrator remediation
22. **AC-17.22** BEFORE Phase 0 refactoring, THE Characterization_Suite SHALL capture sticky cursor selection, swallowed GitHub failures, GitLab 429 behavior, missing `LastUsedUTC`, GitHub-only secret sync, hardcoded Worker attribution, and absent Worker scraper startup as legacy defects
23. **AC-17.23** THE Scheduler unit suite SHALL cover every eligibility exclusion, successful health reset, rate-limit cooldown, deterministic transient backoff, auth disablement, scope non-disablement, expiry reclaim, stale isolation, idempotency, earliest retry, and terminal no-credential behavior
24. **AC-17.24** THE PostgreSQL concurrency suite SHALL run parallel claimers and prove that `SKIP LOCKED` distributes Claims without duplicate active Leases
25. **AC-17.25** THE PostgreSQL concurrency suite SHALL prove transaction-retry idempotency under simulated transient database failures
26. **AC-17.26** THE PostgreSQL concurrency suite SHALL prove database-clock authority despite client clock skew
27. **AC-17.27** THE PostgreSQL concurrency suite SHALL prove that concurrent completion, expiry, and reclaim cannot mutate a replacement Lease
28. **AC-17.28** THE PostgreSQL integration suite SHALL exercise the transaction mode used by the deployed Supabase or PgBouncer configuration
29. **AC-17.29** THE SQLite suite SHALL prove serialized fairness, bounded busy-timeout behavior, Lease expiry, idempotency, and completion in one process
30. **AC-17.30** THE SQLite suite SHALL prove that separate SQLite files do not coordinate distributed Claims
31. **AC-17.31** THE bootstrap suite SHALL prove repeated import idempotency and preservation of Stable_ID, grants, health, and history
32. **AC-17.32** THE bootstrap suite SHALL prove that removals occur only after a wholly successful generation
33. **AC-17.33** THE bootstrap suite SHALL prove that configured `AuthInvalid` credentials are not resurrected
34. **AC-17.34** THE bootstrap suite SHALL prove that changed secrets create new identity while manual matching credentials survive environment removal
35. **AC-17.35** THE protection suite SHALL prove active and previous protection-key round trips
36. **AC-17.36** THE protection suite SHALL prove fail-closed behavior for missing, wrong, or tampered key material and envelopes
37. **AC-17.37** THE GitHub response-table suite SHALL cover 401, ordinary 403, primary-limit 403, secondary-limit 403 or 429, 429, 400, contextual 404, 422, timeout, 5xx, content failure, and partial pagination
38. **AC-17.38** THE GitLab response-table suite SHALL cover 401, ordinary 403, 429 with and without reset evidence, 400, contextual 404, 422, timeout, 5xx, content failure, and partial pagination
39. **AC-17.39** FOR each provider response-table case, THE Provider_Test SHALL assert Typed_Outcome, scheduler transition, Continuation behavior, checkpoint behavior, and redaction
40. **AC-17.40** THE Worker suite SHALL prove valid startup, missing-configuration readiness failure, credential-free sync, one-secret Claim, ownership checks, renewal, idempotent completion, stale isolation, provenance, and no fallback during Master outage
41. **AC-17.41** THE API suite SHALL assert HTTP 200, 400, 401, 403, 409, and 503 semantics defined in Requirement 11
42. **AC-17.42** THE SSRF suite SHALL cover blocked IPv4 and IPv6 classes, explicit private allowlisting, redirect revalidation, cross-origin header stripping, DNS rebinding, malformed URLs, user-info, unsupported schemes, and public-search dual gating
43. **AC-17.43** THE claim-query plan SHALL use the configured eligibility index in the release-scale database fixture
44. **AC-17.44** THE Claim transaction SHALL exclude provider network calls, secret decryption, result parsing, and content retrieval
45. **AC-17.45** THE Adapter_Runtime SHALL process streaming events incrementally within configured event and response-size bounds
46. **AC-17.46** THE Adapter_Runtime SHALL enforce configured content-request concurrency within the active Lease and Provider_Instance limit
47. **AC-17.47** THE Runtime SHALL decrypt only the credential granted to the current operation
48. **AC-17.48** THE Metrics_Service SHALL avoid unbounded-cardinality labels under release-load testing
49. **AC-17.49** THE Audit_Service SHALL keep retained audit volume within its configured retention or aggregation policy under release-load testing
50. **AC-17.50** FOR each implementation increment, THE Release_Gate SHALL run targeted tests for changed behavior
51. **AC-17.51** FOR each implementation increment, THE Release_Gate SHALL run database-appropriate scheduler and provider integration tests
52. **AC-17.52** FOR each implementation increment, THE Release_Gate SHALL run the complete existing test project
53. **AC-17.53** FOR each implementation increment, THE Release_Gate SHALL build the full solution in Release configuration
54. **AC-17.54** FOR each schema-changing increment, THE Release_Gate SHALL validate PostgreSQL and SQLite schema/readiness paths
55. **AC-17.55** BEFORE deployment, THE Release_Gate SHALL complete a minimal Master/Worker smoke test covering sync, Claim, provider execution, completion, and provenance
56. **AC-17.56** WHEN any mandatory correctness, security, schema, build, or smoke gate fails, THE Release_Gate SHALL block release of the affected increment

## Scope Boundaries

- Phase 0 includes the shared platform, protected durable scheduling, and complete GitHub/GitLab integration; it does not implement later provider adapters.
- Phases 1 through 5 are roadmap requirements that begin only after the preceding provider satisfies the common definition of done.
- This specification does not replace the existing node-authentication identity mechanism, unrelated detection and verification providers, or Discovered_Secret access and export policy.
- This specification does not authorize public search by default, permission bypass, rate-limit bypass, website scraping, undocumented provider endpoints, or distributed coordination through independent SQLite files.
- Provider_Access_Credentials and node tokens follow this specification's non-disclosure boundary; repository findings remain governed by existing finding policies.

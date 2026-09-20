using System.ComponentModel.DataAnnotations;
using UnsecuredAPIKeys.Data.Common;

namespace UnsecuredAPIKeys.Data.Models;

/// <summary>
/// Normalized search result persisted from a provider operation. Dedup identity is:
/// ProviderInstanceStableId + RepositoryStableId + ImmutableRevisionOrEquivalentVersion + NormalizedFilePath.
/// Branch names and URLs are never used as the content version.
/// </summary>
public sealed class NormalizedResult
{
    [Key]
    public long Id { get; set; }

    /// <summary>Provider Kind that produced this result.</summary>
    public SearchProviderEnum ProviderKind { get; set; }

    /// <summary>Stable ID of the Provider Instance that produced this result.</summary>
    public Guid ProviderInstanceStableId { get; set; }

    /// <summary>Provider-assigned stable repository identity (e.g., repo ID or org/repo slug).</summary>
    [MaxLength(512)]
    public string RepositoryStableId { get; set; } = string.Empty;

    /// <summary>Optional owner login/organization.</summary>
    [MaxLength(256)]
    public string? RepositoryOwner { get; set; }

    /// <summary>Optional repository name.</summary>
    [MaxLength(256)]
    public string? RepositoryName { get; set; }

    /// <summary>
    /// Immutable provider revision or deterministic provider-equivalent content version
    /// (SHA-256 hex over normalized content bytes). Never a branch name or URL.
    /// </summary>
    [MaxLength(128)]
    public string ImmutableRevisionOrEquivalentVersion { get; set; } = string.Empty;

    /// <summary>Normalized file path within the repository.</summary>
    [MaxLength(2048)]
    public string NormalizedFilePath { get; set; } = string.Empty;

    /// <summary>Optional file name component.</summary>
    [MaxLength(512)]
    public string? FileName { get; set; }

    /// <summary>Optional line number within the file.</summary>
    public int? LineNumber { get; set; }

    /// <summary>Bounded, sanitized content snippet. Never raw credential material.</summary>
    [MaxLength(4096)]
    public string? Snippet { get; set; }

    /// <summary>Provenance URL pointing to the result location.</summary>
    [MaxLength(2048)]
    public string? ProvenanceUrl { get; set; }

    /// <summary>Repository branch at time of discovery.</summary>
    [MaxLength(256)]
    public string? Branch { get; set; }

    /// <summary>Search Query that produced this result.</summary>
    public int? SearchQueryId { get; set; }

    /// <summary>Work Item that this result was produced by.</summary>
    public long? WorkItemId { get; set; }
    public WorkItem? WorkItem { get; set; }

    /// <summary>Work Partition that this result was produced by.</summary>
    public long? WorkPartitionId { get; set; }

    public DateTime DiscoveredUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Versioned sanitized provenance JSON. Never contains credentials.</summary>
    public string ProvenanceJson { get; set; } = "{}";

    /// <summary>Linked API key record if this result was classified as a discovered secret.</summary>
    public int? ApiKeyId { get; set; }
}

/// <summary>
/// Deduplication tracking record. Each unique (ProviderInstanceStableId, RepositoryStableId,
/// ImmutableRevisionOrEquivalentVersion, NormalizedFilePath) tuple has at most one active record.
/// On replay, provenance is updated when the incoming record has newer discovery UTC.
/// </summary>
public sealed class ResultDeduplicationRecord
{
    [Key]
    public long Id { get; set; }

    [MaxLength(512)]
    public string ProviderInstanceStableId { get; set; } = string.Empty;

    [MaxLength(512)]
    public string RepositoryStableId { get; set; } = string.Empty;

    [MaxLength(128)]
    public string ImmutableRevisionOrEquivalentVersion { get; set; } = string.Empty;

    [MaxLength(2048)]
    public string NormalizedFilePath { get; set; } = string.Empty;

    public long NormalizedResultId { get; set; }

    public DateTime FirstDiscoveredUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Transactional outbox record for result events that need to be reliably delivered
/// downstream (e.g., verification queue, notification pipeline).
/// Replay-safe: each result event is idempotent.
/// </summary>
public sealed class ResultOutboxRecord
{
    [Key]
    public long Id { get; set; }

    public long NormalizedResultId { get; set; }

    [MaxLength(64)]
    public string EventKind { get; set; } = string.Empty;

    public string PayloadJson { get; set; } = "{}";

    public bool IsProcessed { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ProcessedUtc { get; set; }
}

/// <summary>
/// Durable Claim idempotency and audit record. Persists the non-secret fingerprint of
/// each Claim decision for the duration of the active/renewed Lease plus the
/// TerminalRetryWindow. Credential material is never stored here.
/// </summary>
public sealed class CredentialClaimRecord
{
    [Key]
    public long Id { get; set; }

    /// <summary>Authenticated Scheduler Principal scope.</summary>
    public CredentialGrantScope PrincipalScope { get; set; }

    /// <summary>Telegram principal for User grants; null for Global/Admin.</summary>
    public long? PrincipalTelegramId { get; set; }

    /// <summary>
    /// Idempotency key = authenticated Scheduler Principal + Request ID.
    /// Unique among active records within the retention window.
    /// </summary>
    public Guid RequestId { get; set; }

    /// <summary>Stable ID of the claimed credential. Never the plaintext secret.</summary>
    public Guid CredentialStableId { get; set; }

    /// <summary>Stable ID of the Provider Instance for this Claim.</summary>
    public Guid ProviderInstanceStableId { get; set; }

    public long WorkItemId { get; set; }

    public Guid LeaseId { get; set; }

    [MaxLength(256)]
    public string? LeaseOwnerNodeId { get; set; }

    public DateTime LeaseAcquiredUtc { get; set; }
    public DateTime LeaseExpiresUtc { get; set; }

    /// <summary>Credential Revision at time of Claim — used to detect stale completions.</summary>
    public long CredentialRevision { get; set; }

    /// <summary>Terminal result kind when the Claim has completed. Null while active.</summary>
    [MaxLength(64)]
    public string? TerminalOutcome { get; set; }

    public bool IsTerminal { get; set; }
    public DateTime? TerminalizedUtc { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Durable Provider Instance concurrency lease. Every credentialed and approved
/// public operation consumes exactly one Slot. Only unexpired Slots count against
/// MaxConcurrentOperations.
/// </summary>
public sealed class OperationSlot
{
    [Key]
    public long Id { get; set; }

    public Guid SlotId { get; set; } = Guid.NewGuid();

    /// <summary>Provider Instance this slot reserves capacity on.</summary>
    public long ProviderInstanceId { get; set; }
    public SearchProviderInstance? ProviderInstance { get; set; }

    public Guid RequestId { get; set; }

    /// <summary>Owner principal scope.</summary>
    public CredentialGrantScope PrincipalScope { get; set; }

    /// <summary>Owner Telegram principal for User grants; null otherwise.</summary>
    public long? PrincipalTelegramId { get; set; }

    public long WorkItemId { get; set; }

    [MaxLength(256)]
    public string PartitionKey { get; set; } = string.Empty;

    public DateTime AcquiredUtc { get; set; }
    public DateTime ExpiresUtc { get; set; }

    /// <summary>Revision counter used to detect stale renewals and completions.</summary>
    public long Revision { get; set; }

    public bool IsTerminal { get; set; }
    public DateTime? TerminalizedUtc { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

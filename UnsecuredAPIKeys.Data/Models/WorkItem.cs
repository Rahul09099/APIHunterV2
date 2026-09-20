using System.ComponentModel.DataAnnotations;
using UnsecuredAPIKeys.Data.Common;

namespace UnsecuredAPIKeys.Data.Models;

/// <summary>
/// Durable unit of provider-instance, query, and partition work. The Scheduler Principal,
/// authoritative instance, effective query hash, adapter version, and Continuation are
/// persisted without secrets. This record is created before Claims are issued and survives
/// restarts, node changes, and credential rotation.
/// </summary>
public sealed class WorkItem
{
    [Key]
    public long Id { get; set; }

    /// <summary>Immutable external identifier; survives migration and reconciliation.</summary>
    public Guid StableId { get; set; } = Guid.NewGuid();

    /// <summary>Scheduler Principal scope that initiated the work.</summary>
    public CredentialGrantScope PrincipalScope { get; set; }

    /// <summary>Telegram principal for User grants; null for Global/Admin/System.</summary>
    public long? PrincipalTelegramId { get; set; }

    /// <summary>Authoritative Provider Instance for this work item.</summary>
    public long ProviderInstanceId { get; set; }
    public SearchProviderInstance? ProviderInstance { get; set; }

    /// <summary>Authoritative Provider Kind — must match the linked instance.</summary>
    public SearchProviderEnum ProviderKind { get; set; }

    /// <summary>Search Query that originated this work item.</summary>
    public int? SearchQueryId { get; set; }
    public SearchQuery? SearchQuery { get; set; }

    /// <summary>
    /// SHA-256 hex digest over the exact effective query inputs (generic query +
    /// native override + settings JSON). Deterministic; never contains secrets.
    /// </summary>
    [MaxLength(64)]
    public string EffectiveQueryHash { get; set; } = string.Empty;

    /// <summary>Adapter version that produced this work item's query snapshot.</summary>
    [MaxLength(128)]
    public string AdapterVersion { get; set; } = string.Empty;

    /// <summary>Serialized effective query snapshot — never contains secrets.</summary>
    public string QuerySnapshotJson { get; set; } = "{}";

    /// <summary>True when the work item has reached a terminal state.</summary>
    public bool IsTerminal { get; set; }

    /// <summary>True when the work item completed successfully.</summary>
    public bool IsComplete { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    public ICollection<WorkPartition> Partitions { get; set; } = [];
}

/// <summary>
/// Stable subdivision of a WorkItem that can be resumed independently.
/// Continuation and Last Safe Checkpoint are versioned per Partition and never contain secrets.
/// </summary>
public sealed class WorkPartition
{
    [Key]
    public long Id { get; set; }

    public Guid StableId { get; set; } = Guid.NewGuid();

    public long WorkItemId { get; set; }
    public WorkItem? WorkItem { get; set; }

    /// <summary>Opaque partition discriminator (e.g., page range, date bucket).</summary>
    [MaxLength(256)]
    public string PartitionKey { get; set; } = string.Empty;

    /// <summary>
    /// Versioned provider-specific state needed to resume paginated or streaming work.
    /// Null until the first page is processed. Never contains secrets.
    /// </summary>
    public string? Continuation { get; set; }

    /// <summary>Adapter version that produced the current Continuation value.</summary>
    [MaxLength(128)]
    public string? ContinuationAdapterVersion { get; set; }

    /// <summary>
    /// Latest provider position for which required result and provenance persistence completed safely.
    /// Null until the first safe checkpoint is written.
    /// </summary>
    public string? LastSafeCheckpoint { get; set; }

    public bool IsTerminal { get; set; }
    public bool IsComplete { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Optional per-query native override and settings snapshot for a Work Item.
/// Captures the exact effective inputs to the adapter at the time of query creation.
/// </summary>
public sealed class SearchQueryOverride
{
    [Key]
    public long Id { get; set; }

    public long WorkItemId { get; set; }
    public WorkItem? WorkItem { get; set; }

    /// <summary>Generic query text used when no native override is present.</summary>
    public string GenericQuery { get; set; } = string.Empty;

    /// <summary>Provider-specific native query override; null when generic is used.</summary>
    public string? NativeOverride { get; set; }

    /// <summary>Serialized non-secret settings snapshot used for query derivation.</summary>
    public string SettingsJson { get; set; } = "{}";
}

namespace UnsecuredAPIKeys.Data.DTOs;

/// <summary>
/// Credential-free worker configuration. Provider access is obtained through a
/// separate operation-scoped claim path; sync never transports credential material.
/// </summary>
public class NodeSyncDTO
{
    public List<SearchQueryDTO> Queries { get; set; } = [];

    /// <summary>
    /// Enabled provider instances the worker may claim against (Task 13.2).
    /// Stable references only — never credential material.
    /// </summary>
    public List<ProviderInstanceDescriptor> ProviderInstances { get; set; } = [];

    /// <summary>Zero-based index of this node in the active node pool (for logging).</summary>
    public int NodeIndex { get; set; }

    /// <summary>Total number of active nodes at the time of sync.</summary>
    public int TotalNodes { get; set; }
}

/// <summary>Non-secret descriptor of one enabled search provider instance.</summary>
public class ProviderInstanceDescriptor
{
    public Guid StableId { get; set; }
    public UnsecuredAPIKeys.Data.Common.SearchProviderEnum ProviderKind { get; set; }
    public string DisplayName { get; set; } = string.Empty;
}

public class SearchQueryDTO
{
    public long Id { get; set; }
    public string Query { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public DateTime LastSearchUTC { get; set; }

    /// <summary>Propagated to workers so the incremental window uses the durable checkpoint.</summary>
    public DateTime? LastSuccessfulSearchUTC { get; set; }

    /// <summary>Propagated so workers preserve the repository push checkpoint.</summary>
    public DateTime? LastRepoPushedSeenUTC { get; set; }
}

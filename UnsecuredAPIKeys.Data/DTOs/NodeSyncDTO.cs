using UnsecuredAPIKeys.Data.Models;
using UnsecuredAPIKeys.Data.Common;

namespace UnsecuredAPIKeys.Data.DTOs;

public class NodeSyncDTO
{
    public List<SearchProviderTokenDTO> Tokens { get; set; } = new();
    public List<SearchQueryDTO> Queries { get; set; } = new();

    /// <summary>Zero-based index of this node in the active node pool (for logging).</summary>
    public int NodeIndex { get; set; }

    /// <summary>Total number of active nodes at the time of sync.</summary>
    public int TotalNodes { get; set; }
}

public class SearchProviderTokenDTO
{
    public string Token { get; set; } = string.Empty;
    public SearchProviderEnum SearchProvider { get; set; }
}

public class SearchQueryDTO
{
    public long Id { get; set; }
    public string Query { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public DateTime LastSearchUTC { get; set; }
    /// <summary>Propagated to workers so pushed:> filter uses the real incremental window.</summary>
    public DateTime? LastSuccessfulSearchUTC { get; set; }
    /// <summary>Propagated to workers so LastRepoPushedSeenUTC checkpoint is not lost on sync.</summary>
    public DateTime? LastRepoPushedSeenUTC { get; set; }
}

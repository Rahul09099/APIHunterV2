using UnsecuredAPIKeys.Data.Models;
using UnsecuredAPIKeys.Data.Common;

namespace UnsecuredAPIKeys.Data.DTOs;

public class NodeSyncDTO
{
    public List<SearchProviderTokenDTO> Tokens { get; set; } = new();
    public List<SearchQueryDTO> Queries { get; set; } = new();
}

public class SearchProviderTokenDTO
{
    public string Token { get; set; } = string.Empty;
    public SearchProviderEnum SearchProvider { get; set; }
}

public class SearchQueryDTO
{
    public int Id { get; set; }
    public string Query { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
}

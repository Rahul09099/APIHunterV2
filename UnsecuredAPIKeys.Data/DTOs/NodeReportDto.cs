using UnsecuredAPIKeys.Data.Common;

namespace UnsecuredAPIKeys.Data.DTOs
{
    public class NodeReportDto
    {
        public string ApiKey { get; set; } = string.Empty;
        public ApiTypeEnum ApiType { get; set; }
        public string? Metadata { get; set; }
        
        // Repo info
        public string RepoName { get; set; } = string.Empty;
        public string RepoOwner { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public string FileUrl { get; set; } = string.Empty;
    }

    public class NodeBulkReportDto
    {
        public List<NodeReportDto> Discoveries { get; set; } = new();
    }
}

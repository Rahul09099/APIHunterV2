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

        // Immutable normalized provenance (Task 13.3). The Master validates every
        // field before persistence and rejects omitted, unknown, conflicting, or
        // mismatched identity. Defaults (Unknown/Empty/null) mean "not provided".
        public SearchProviderEnum ProviderKind { get; set; } = SearchProviderEnum.Unknown;
        public Guid ProviderInstanceStableId { get; set; } = Guid.Empty;
        public Guid? WorkItemStableId { get; set; }
        public Guid? WorkPartitionStableId { get; set; }
        public string? PartitionKey { get; set; }
        public Guid? LeaseId { get; set; }
        public Guid? OperationSlotId { get; set; }
    }

    public class NodeBulkReportDto
    {
        public List<NodeReportDto> Discoveries { get; set; } = new();
    }
}

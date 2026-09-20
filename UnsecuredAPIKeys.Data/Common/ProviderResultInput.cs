using UnsecuredAPIKeys.Data.Common;

namespace UnsecuredAPIKeys.Data.Common;

/// <summary>
/// Input record for a single normalized provider result. No credential material is ever included.
/// Dedup identity: (ProviderInstanceStableId, RepositoryStableId, ImmutableRevisionOrEquivalentVersion, NormalizedFilePath).
/// </summary>
public sealed record ProviderResultInput(
    SearchProviderEnum ProviderKind,
    Guid ProviderInstanceStableId,
    string RepositoryStableId,
    string ImmutableRevisionOrEquivalentVersion,
    string NormalizedFilePath,
    string? RepositoryOwner = null,
    string? RepositoryName = null,
    string? FileName = null,
    int? LineNumber = null,
    string? Snippet = null,
    string? ProvenanceUrl = null,
    string? Branch = null,
    int? SearchQueryId = null,
    long? WorkItemId = null,
    long? WorkPartitionId = null,
    string ProvenanceJson = "{}");

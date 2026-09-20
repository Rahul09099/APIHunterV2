using System.ComponentModel.DataAnnotations;
using UnsecuredAPIKeys.Data.Common;

namespace UnsecuredAPIKeys.Data.Models;

/// <summary>
/// One normalized SaaS or self-hosted API origin for a search-provider adapter.
/// Provider access credentials are stored separately and linked by foreign key.
/// </summary>
public sealed class SearchProviderInstance
{
    [Key]
    public long Id { get; set; }

    public Guid StableId { get; set; } = Guid.NewGuid();

    public SearchProviderEnum ProviderKind { get; set; } = SearchProviderEnum.Unknown;

    public string DisplayName { get; set; } = string.Empty;

    public string NormalizedScheme { get; set; } = "https";

    public string NormalizedHost { get; set; } = string.Empty;

    public int NormalizedPort { get; set; } = 443;

    public string NormalizedBasePath { get; set; } = "/";

    public bool IsEnabled { get; set; } = true;

    public bool AllowGlobalPublicSearch { get; set; }

    public int MaxConcurrentOperations { get; set; } = 1;

    public int SettingsVersion { get; set; } = 1;

    public string SettingsJson { get; set; } = "{}";

    public long? ApprovedByTelegramId { get; set; }

    /// <summary>
    /// Version of the endpoint policy used for the current approval. Zero means unapproved.
    /// </summary>
    public int EndpointPolicyVersion { get; set; }

    /// <summary>
    /// Canonical non-secret scheme/host/port/path identity approved by an administrator.
    /// </summary>
    [MaxLength(2048)]
    public string? ApprovedEndpointIdentity { get; set; }

    public DateTime? EndpointApprovedAtUtc { get; set; }

    /// <summary>
    /// Explicit development-only HTTP exception scoped to this exact Provider Instance.
    /// </summary>
    public bool DevelopmentHttpAllowed { get; set; }

    /// <summary>
    /// Canonical JSON string array of instance-scoped private CIDRs. Contains no secrets.
    /// </summary>
    public string PrivateNetworkAllowlistJson { get; set; } = "[]";

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    public ICollection<SearchProviderToken> SearchProviderTokens { get; set; } = [];
}

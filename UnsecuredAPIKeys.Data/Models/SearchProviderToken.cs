using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using UnsecuredAPIKeys.Data.Common;

namespace UnsecuredAPIKeys.Data.Models;

/// <summary>
/// Durable provider-access credential identity. Protected runtime paths use the
/// authenticated envelope; <see cref="Token"/> exists only for the explicitly
/// guarded pre-scrub compatibility migration.
/// </summary>
public class SearchProviderToken
{
    [Key]
    public int Id { get; set; }

    public Guid StableId { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Legacy plaintext compatibility column. Production reads and writes must go
    /// through the guarded credential storage services.
    /// </summary>
    [Obsolete("Use protected credential storage services. This column is migration-only.")]
    [JsonIgnore]
    public string Token { get; set; } = string.Empty;

    public SearchProviderEnum SearchProvider { get; set; } = SearchProviderEnum.Unknown;

    public long ProviderInstanceId { get; set; }
    public SearchProviderInstance? ProviderInstance { get; set; }

    public int EnvelopeFormatVersion { get; set; }
    public int ProtectionKeyVersion { get; set; }
    [JsonIgnore]
    public byte[] ProtectionNonce { get; set; } = [];
    [JsonIgnore]
    public byte[] ProtectedCiphertext { get; set; } = [];
    [JsonIgnore]
    public byte[] AuthenticationTag { get; set; } = [];

    public int FingerprintKeyVersion { get; set; }
    [JsonIgnore]
    public byte[] Fingerprint { get; set; } = [];

    public CredentialSource Source { get; set; } = CredentialSource.Legacy;
    public string? SourceEntryId { get; set; }
    public long? SourceGeneration { get; set; }
    public DateTime? LastSeenUtc { get; set; }

    public bool IsEnabled { get; set; } = true;
    public string? DisabledReason { get; set; }
    public DateTime? DisabledAtUtc { get; set; }
    public DateTime? CooldownUntilUtc { get; set; }
    public int ConsecutiveTransientFailures { get; set; }
    public string? LastOutcome { get; set; }

    public DateTime? LastClaimedUtc { get; set; }
    public DateTime? LastUsedUTC { get; set; }

    public Guid? LeaseId { get; set; }
    public string? LeaseOwnerNodeId { get; set; }
    public Guid? LeaseRequestId { get; set; }
    public DateTime? LeaseAcquiredUtc { get; set; }
    public DateTime? LeaseExpiresUtc { get; set; }

    public long Revision { get; set; }
    public bool IsArchived { get; set; }
    public Guid? ReplacedByStableId { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    // Compatibility migration metadata only after CredentialGrant backfill.
    public long? AddedByTelegramId { get; set; }

    public ICollection<CredentialGrant> CredentialGrants { get; set; } = [];
}

using System.ComponentModel.DataAnnotations;

namespace UnsecuredAPIKeys.Data.Models;

/// <summary>
/// Durable global-public-search consent for one exact Provider Instance (Wave 14).
/// A Public Operation requires both effective <c>GlobalPublicSearch</c> capability and an
/// active consent record for the exact instance; revocation blocks every new operation.
/// One row per instance — opt-in/out mutates state, history lives in audit records.
/// </summary>
public sealed class PublicSearchConsent
{
    [Key]
    public long Id { get; set; }

    public long ProviderInstanceId { get; set; }

    public SearchProviderInstance? ProviderInstance { get; set; }

    public bool IsActive { get; set; }

    /// <summary>Administrator that performed the latest opt-in or opt-out.</summary>
    public long ActorTelegramId { get; set; }

    public DateTime OptedInUtc { get; set; }

    public DateTime? OptedOutUtc { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

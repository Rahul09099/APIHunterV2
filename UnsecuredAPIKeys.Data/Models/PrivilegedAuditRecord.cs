using System.ComponentModel.DataAnnotations;
using UnsecuredAPIKeys.Data.Common;

namespace UnsecuredAPIKeys.Data.Models;

/// <summary>
/// Immutable, secret-free evidence for an accepted privileged control-plane command.
/// </summary>
public sealed class PrivilegedAuditRecord
{
    [Key]
    public long Id { get; set; }

    public long ActorTelegramId { get; set; }

    public PrivilegedActionKind Action { get; set; }

    public Guid TargetStableId { get; set; }

    public DateTime OccurredUtc { get; set; }

    public PrivilegedActionOutcome Outcome { get; set; }

    [MaxLength(512)]
    public string SanitizedReason { get; set; } = string.Empty;
}

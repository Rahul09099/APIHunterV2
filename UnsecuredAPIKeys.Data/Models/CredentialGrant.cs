using System.ComponentModel.DataAnnotations;
using UnsecuredAPIKeys.Data.Common;

namespace UnsecuredAPIKeys.Data.Models;

/// <summary>
/// Durable authorization connecting one provider credential to one exact scope/principal.
/// </summary>
public sealed class CredentialGrant
{
    [Key]
    public long Id { get; set; }

    public int CredentialId { get; set; }

    public SearchProviderToken Credential { get; set; } = null!;

    public CredentialGrantScope Scope { get; set; }

    /// <summary>
    /// Required for <see cref="CredentialGrantScope.User"/> and null for Global/Admin grants.
    /// </summary>
    public long? TelegramPrincipalId { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

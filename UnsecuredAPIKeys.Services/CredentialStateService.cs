using UnsecuredAPIKeys.Data.Models;

namespace UnsecuredAPIKeys.Services;

/// <summary>
/// Central pure transition service for the enabled/disabled credential invariant.
/// Callers must supply database UTC for disablement transitions.
/// </summary>
public sealed class CredentialStateService
{
    public SearchProviderToken Enable(SearchProviderToken credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        credential.IsEnabled = true;
        credential.DisabledReason = null;
        credential.DisabledAtUtc = null;
        credential.UpdatedUtc = DateTime.UtcNow;
        credential.Revision++;
        return credential;
    }

    public SearchProviderToken Disable(
        SearchProviderToken credential,
        string reason,
        DateTime disabledAtUtc)
    {
        ArgumentNullException.ThrowIfNull(credential);
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 128 || reason.Any(char.IsControl))
        {
            throw new ArgumentException("A bounded non-secret disabled reason is required.", nameof(reason));
        }
        if (disabledAtUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Disabled time must be database UTC.", nameof(disabledAtUtc));
        }

        credential.IsEnabled = false;
        credential.DisabledReason = reason.Trim();
        credential.DisabledAtUtc = disabledAtUtc;
        credential.UpdatedUtc = disabledAtUtc;
        credential.Revision++;
        return credential;
    }
}

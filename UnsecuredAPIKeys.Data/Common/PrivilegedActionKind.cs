namespace UnsecuredAPIKeys.Data.Common;

/// <summary>
/// Closed set of control-plane actions that always require an authenticated administrator.
/// Later feature slices must use this policy boundary rather than introducing controller-local checks.
/// </summary>
public enum PrivilegedActionKind
{
    CredentialDisable = 1,
    CredentialReenable = 2,
    CredentialReplace = 3,
    ProviderInstanceApprove = 4,
    PrivateNetworkAllowlist = 5,
    PublicSearchConsent = 6
}

/// <summary>
/// Non-secret result persisted for an accepted privileged command.
/// </summary>
public enum PrivilegedActionOutcome
{
    Succeeded = 1,
    TargetNotFound = 2
}

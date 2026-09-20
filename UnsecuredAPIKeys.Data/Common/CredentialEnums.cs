namespace UnsecuredAPIKeys.Data.Common;

/// <summary>
/// Durable provenance for a provider access credential. This value is non-secret.
/// </summary>
public enum CredentialSource
{
    Legacy = 0,
    Manual = 1,
    Environment = 2
}

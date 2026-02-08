namespace UnsecuredAPIKeys.Services;

/// <summary>
/// Constants for the application.
/// </summary>
public static class LiteLimits
{
    /// <summary>
    /// Maximum valid keys.
    ///
    /// WARNING: If you modify this limit, do NOT publish your database
    /// or results to a public repository. This would expose working API
    /// keys to malicious actors who could abuse them.
    /// </summary>
    public const int MAX_VALID_KEYS = 1000000;

    /// <summary>
    /// Delay between verification batches (milliseconds).
    /// </summary>
    public const int VERIFICATION_DELAY_MS = 1000;

    /// <summary>
    /// Delay between GitHub search queries (milliseconds).
    /// </summary>
    public const int SEARCH_DELAY_MS = 5000;

    /// <summary>
    /// Number of keys to process per verification batch.
    /// </summary>
    public const int VERIFICATION_BATCH_SIZE = 100;
}

/// <summary>
/// Application-wide constants.
/// </summary>
public static class AppInfo
{
    public static readonly string Name = "UnsecuredAPIKeys Professional Edition";
    public static readonly string Version = "1.1.0";
    public static readonly string DatabaseName = "unsecuredapikeys.db";
}

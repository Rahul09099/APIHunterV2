namespace UnsecuredAPIKeys.Services;

/// <summary>
/// Worker process configuration (Wave 13, Task 13.2). Bound explicitly from deployment
/// environment variables — never from provider-token stores. The worker holds no
/// provider credentials at rest: every credential arrives as one operation-scoped Claim.
/// </summary>
public sealed class WorkerScraperOptions
{
    public string MasterApiUrl { get; set; } = string.Empty;

    public string NodeToken { get; set; } = string.Empty;

    public TimeSpan CycleInterval { get; set; } = TimeSpan.FromMinutes(2);

    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromMinutes(5);

    public TimeSpan OutageBackoffInitial { get; set; } = TimeSpan.FromSeconds(10);

    public TimeSpan OutageBackoffMax { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Maximum provider operations per cycle across all synced queries.</summary>
    public int MaxOperationsPerCycle { get; set; } = 20;

    /// <summary>
    /// Fail-closed configuration validation. Invalid configuration is unhealthy and the
    /// worker must not send any token or Claim traffic. HTTPS is required unconditionally.
    /// </summary>
    public (bool Healthy, string? Error) Validate()
    {
        if (string.IsNullOrWhiteSpace(MasterApiUrl))
        {
            return (false, "MASTER_API_URL is missing.");
        }

        if (!Uri.TryCreate(MasterApiUrl.Trim(), UriKind.Absolute, out var master) ||
            !string.Equals(master.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return (false, "MASTER_API_URL must be a valid absolute HTTPS URL.");
        }

        if (string.IsNullOrWhiteSpace(NodeToken))
        {
            return (false, "NODE_TOKEN is missing.");
        }

        if (CycleInterval <= TimeSpan.Zero ||
            HeartbeatInterval <= TimeSpan.Zero ||
            OutageBackoffInitial <= TimeSpan.Zero ||
            OutageBackoffMax <= TimeSpan.Zero ||
            MaxOperationsPerCycle <= 0)
        {
            return (false, "Worker intervals and operation bounds must be positive.");
        }

        return (true, null);
    }
}

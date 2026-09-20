namespace UnsecuredAPIKeys.Data;

/// <summary>
/// Versioned constants shared by schema initialization and readiness validation.
/// Stable identifiers are non-secret and deterministic across idempotent deployments.
/// </summary>
public static class ProviderInstanceSchema
{
    public const int CurrentVersion = 5;
    public const int MarkerRecordId = 1;
    public const int ProtectedReadsMarkerRecordId = 2;
    public const string ReadinessMarkerName = "provider-instance-schema";
    public const string ProtectedReadsReadinessMarkerName = "credential-protected-reads";
    public const string WorkerClaimsCutoverMarkerName = "worker-claims";
    public const int DefaultMaxConcurrentOperations = 4;

    public static readonly Guid DefaultGitHubStableId =
        Guid.Parse("8b3d0a4f-7a6d-4a4c-8cf0-0c6cb18f8b11");

    public static readonly Guid DefaultGitLabStableId =
        Guid.Parse("9386a72d-01df-4bc6-9ad4-cb47a85f0a22");

    public static readonly Guid DefaultSourcegraphStableId =
        Guid.Parse("a1b2c3d4-1111-4a4c-8cf0-0c6cb18f8b31");

    public static readonly Guid DefaultHuggingFaceStableId =
        Guid.Parse("b2c3d4e5-2222-4a4c-8cf0-0c6cb18f8b32");

    public static readonly Guid DefaultAzureDevOpsStableId =
        Guid.Parse("c3d4e5f6-3333-4a4c-8cf0-0c6cb18f8b33");
}

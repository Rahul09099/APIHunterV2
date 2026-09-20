using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using UnsecuredAPIKeys.Data;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Data.Models;

namespace UnsecuredAPIKeys.Services;

public enum DatabaseCoordinationMode
{
    Unsupported,
    SingleMaster,
    Distributed
}

/// <summary>
/// Secret-free startup/readiness result for the additive Provider Instance slice.
/// Task 2.3 may project this result over HTTP; Task 2.2 only establishes the contract.
/// </summary>
public sealed record ProviderInstanceReadinessReport(
    bool DatabaseReady,
    bool SchemaReady,
    bool ProviderInstancesReady,
    bool MarkersReady,
    DatabaseCoordinationMode CoordinationMode,
    bool DistributedCoordinationReady,
    IReadOnlyList<string> Failures)
{
    /// <summary>
    /// True only when the feature-flag dependency and cutover matrix is valid.
    /// Provider-only evaluations default this to true; the scheduling readiness service
    /// always overwrites it from the current configuration contract.
    /// </summary>
    public bool ConfigurationReady { get; init; } = true;

    public bool ProtectionKeysReady { get; init; } = true;
    public bool UsableCredentialsReady { get; init; } = true;
    public bool ProtectedCredentialReadsVerified { get; init; } = true;
    public bool ProtectedStorageReady { get; init; } = true;
    public bool BootstrapReady { get; init; } = true;
    public int BootstrapImportedCount { get; init; }
    public IReadOnlyList<Guid> BootstrapCredentialStableIds { get; init; } = [];

    public bool IsReady =>
        DatabaseReady &&
        SchemaReady &&
        ProviderInstancesReady &&
        MarkersReady &&
        ProtectionKeysReady &&
        UsableCredentialsReady &&
        ProtectedStorageReady &&
        BootstrapReady &&
        ConfigurationReady &&
        CoordinationMode != DatabaseCoordinationMode.Unsupported;

    public static ProviderInstanceReadinessReport NotEvaluated { get; } = new(
        DatabaseReady: false,
        SchemaReady: false,
        ProviderInstancesReady: false,
        MarkersReady: false,
        CoordinationMode: DatabaseCoordinationMode.Unsupported,
        DistributedCoordinationReady: false,
        Failures: ["Readiness has not been evaluated."]);
}

/// <summary>
/// Holds the latest startup evaluation without retaining a DbContext or any secret data.
/// </summary>
public sealed class ProviderInstanceReadinessState
{
    private ProviderInstanceReadinessReport _current = ProviderInstanceReadinessReport.NotEvaluated;

    public ProviderInstanceReadinessReport Current => Volatile.Read(ref _current);

    public void Update(ProviderInstanceReadinessReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        Volatile.Write(ref _current, report);
    }
}

/// <summary>
/// Validates the deployed schema and migration records before future scheduling paths
/// are allowed to become ready. SQLite is intentionally reported as single-Master only.
/// </summary>
public sealed class ProviderInstanceReadinessService
{
    private readonly DBContext dbContext;
    private readonly CredentialProtectionReadinessService? credentialProtectionReadinessService;
    private readonly IConfiguration? configuration;

    /// <summary>
    /// Compatibility constructor for schema-only callers. Runtime dependency injection uses
    /// the key-aware overload below.
    /// </summary>
    public ProviderInstanceReadinessService(DBContext dbContext)
    {
        this.dbContext = dbContext;
    }

    public ProviderInstanceReadinessService(
        DBContext dbContext,
        CredentialProtectionReadinessService credentialProtectionReadinessService)
    {
        this.dbContext = dbContext;
        this.credentialProtectionReadinessService = credentialProtectionReadinessService;
    }

    public ProviderInstanceReadinessService(
        DBContext dbContext,
        CredentialProtectionReadinessService credentialProtectionReadinessService,
        IConfiguration configuration)
    {
        this.dbContext = dbContext;
        this.credentialProtectionReadinessService = credentialProtectionReadinessService;
        this.configuration = configuration;
    }

    private static readonly IReadOnlyDictionary<string, string[]> RequiredColumns =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["SearchProviderInstances"] =
            [
                "Id", "StableId", "ProviderKind", "DisplayName", "NormalizedScheme",
                "NormalizedHost", "NormalizedPort", "NormalizedBasePath", "IsEnabled",
                "AllowGlobalPublicSearch", "MaxConcurrentOperations", "SettingsVersion",
                "SettingsJson", "ApprovedByTelegramId", "EndpointPolicyVersion",
                "ApprovedEndpointIdentity", "EndpointApprovedAtUtc", "DevelopmentHttpAllowed",
                "PrivateNetworkAllowlistJson", "CreatedUtc", "UpdatedUtc"
            ],
            ["SearchProviderTokens"] =
            [
                "Id", "StableId", "Token", "SearchProvider", "ProviderInstanceId",
                "EnvelopeFormatVersion", "ProtectionKeyVersion", "ProtectionNonce",
                "ProtectedCiphertext", "AuthenticationTag", "FingerprintKeyVersion",
                "Fingerprint", "Source", "SourceEntryId", "SourceGeneration", "LastSeenUtc",
                "IsEnabled", "DisabledReason", "DisabledAtUtc", "CooldownUntilUtc",
                "ConsecutiveTransientFailures", "LastOutcome", "LastClaimedUtc", "LastUsedUTC",
                "LeaseId", "LeaseOwnerNodeId", "LeaseRequestId", "LeaseAcquiredUtc",
                "LeaseExpiresUtc", "Revision", "IsArchived", "ReplacedByStableId",
                "CreatedUtc", "UpdatedUtc", "AddedByTelegramId"
            ],
            ["CredentialGrants"] =
            [
                "Id", "CredentialId", "Scope", "TelegramPrincipalId", "CreatedUtc"
            ],
            ["PublicSearchConsents"] =
            [
                "Id", "ProviderInstanceId", "IsActive", "ActorTelegramId",
                "OptedInUtc", "OptedOutUtc", "CreatedUtc", "UpdatedUtc"
            ],
            ["PrivilegedAuditRecords"] =
            [
                "Id", "ActorTelegramId", "Action", "TargetStableId", "OccurredUtc",
                "Outcome", "SanitizedReason"
            ],
            ["SchemaVersions"] = ["Id", "Version", "AppliedUtc"],
            ["ReadinessMarkers"] = ["Id", "Name", "SchemaVersion", "IsReady", "UpdatedUtc"],
            ["CutoverMarkers"] = ["Id", "Name", "Version", "IsComplete", "UpdatedUtc"]
        };

    private static readonly IReadOnlyDictionary<string, string[]> RequiredIndexes =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["UX_SearchProviderInstances_StableId"] = ["StableId"],
            ["UX_SearchProviderInstances_NormalizedIdentity"] =
            ["ProviderKind", "NormalizedScheme", "NormalizedHost", "NormalizedPort", "NormalizedBasePath"],
            ["IX_SearchProviderTokens_ProviderInstanceId"] = ["ProviderInstanceId"],
            ["UX_SearchProviderTokens_StableId"] = ["StableId"],
            ["UX_SearchProviderTokens_ProviderInstance_Fingerprint"] =
            ["ProviderInstanceId", "Fingerprint"],
            ["UX_SearchProviderTokens_LeaseId"] = ["LeaseId"],
            ["IX_SearchProviderTokens_Eligibility"] =
            [
                "ProviderInstanceId", "IsEnabled", "DisabledAtUtc", "CooldownUntilUtc",
                "LeaseExpiresUtc", "LastClaimedUtc", "StableId"
            ],
            ["UX_CredentialGrants_CredentialScopePrincipal"] =
            ["CredentialId", "Scope", "TelegramPrincipalId"],
            ["UX_CredentialGrants_CredentialScopeWithoutPrincipal"] =
            ["CredentialId", "Scope"],
            ["UX_PublicSearchConsents_ProviderInstanceId"] =
            ["ProviderInstanceId"],
            ["IX_PrivilegedAuditRecords_TargetStableId_OccurredUtc"] =
            ["TargetStableId", "OccurredUtc"],
            ["UX_ReadinessMarkers_Name"] = ["Name"],
            ["UX_CutoverMarkers_Name"] = ["Name"]
        };

    private static readonly string[] RequiredConstraints =
    [
        "CK_SearchProviderInstances_ProviderKind",
        "CK_SearchProviderInstances_MaxConcurrentOperations",
        "CK_SearchProviderInstances_SettingsVersion",
        "CK_SearchProviderInstances_EndpointApproval",
        "CK_SearchProviderTokens_State",
        "CK_SearchProviderTokens_ProtectedEnvelope",
        "CK_SearchProviderTokens_Fingerprint",
        "CK_SearchProviderTokens_Source",
        "CK_SearchProviderTokens_Revision",
        "FK_SearchProviderTokens_SearchProviderInstances_ProviderInstanceId",
        "CK_CredentialGrants_PrincipalScope",
        "FK_CredentialGrants_SearchProviderTokens_CredentialId",
        "CK_PublicSearchConsents_State",
        "FK_PublicSearchConsents_SearchProviderInstances_ProviderInstanceId",
        "CK_PrivilegedAuditRecords_Actor",
        "CK_PrivilegedAuditRecords_Action",
        "CK_PrivilegedAuditRecords_Outcome",
        "CK_PrivilegedAuditRecords_Reason",
        "CK_SchemaVersions_Version",
        "CK_ReadinessMarkers_SchemaVersion",
        "CK_CutoverMarkers_Version"
    ];

    public async Task<bool> CheckProviderInstanceReadinessAsync(
        CancellationToken cancellationToken = default) =>
        (await EvaluateAsync(cancellationToken)).IsReady;

    public async Task<ProviderInstanceReadinessReport> EvaluateAsync(
        CancellationToken cancellationToken = default)
    {
        var failures = new List<string>();
        var (coordinationMode, coordinationFailure) = GetCoordinationMode();
        if (coordinationMode == DatabaseCoordinationMode.Unsupported)
        {
            failures.Add(coordinationFailure ?? "The configured database provider is not supported for scheduling readiness.");
        }

        bool databaseReady;
        try
        {
            databaseReady = await dbContext.Database.CanConnectAsync(cancellationToken);
        }
        catch
        {
            databaseReady = false;
        }

        if (!databaseReady)
        {
            failures.Add("Database connectivity validation failed.");
            return new ProviderInstanceReadinessReport(
                DatabaseReady: false,
                SchemaReady: false,
                ProviderInstancesReady: false,
                MarkersReady: false,
                CoordinationMode: coordinationMode,
                DistributedCoordinationReady: false,
                Failures: failures);
        }

        var schemaReady = await ValidatePhysicalSchemaAsync(failures, cancellationToken);
        var providerInstancesReady = false;
        var markersReady = false;
        var protectionReadiness = credentialProtectionReadinessService is null
            ? CredentialProtectionReadinessResult.NotRequired
            : new CredentialProtectionReadinessResult(
                RequiredKeysReady: false,
                UsableCredentialsReady: false,
                ProtectedReadVerificationReady: false,
                ProtectedStorageRequired: true,
                Failures: ["Credential protection readiness was not evaluated."]);

        if (schemaReady)
        {
            providerInstancesReady = await ValidateProviderInstancesAsync(failures, cancellationToken);
            markersReady = await ValidateMarkersAsync(failures, cancellationToken);
            if (credentialProtectionReadinessService is not null)
            {
                protectionReadiness = await credentialProtectionReadinessService
                    .EvaluateAsync(cancellationToken);
                foreach (var failure in protectionReadiness.Failures)
                {
                    failures.Add(failure);
                }
            }
        }

        return new ProviderInstanceReadinessReport(
            DatabaseReady: true,
            SchemaReady: schemaReady,
            ProviderInstancesReady: providerInstancesReady,
            MarkersReady: markersReady,
            CoordinationMode: coordinationMode,
            DistributedCoordinationReady: coordinationMode == DatabaseCoordinationMode.Distributed,
            Failures: failures)
        {
            ProtectionKeysReady = protectionReadiness.RequiredKeysReady,
            UsableCredentialsReady = protectionReadiness.UsableCredentialsReady,
            ProtectedCredentialReadsVerified = protectionReadiness.ProtectedReadVerificationReady,
            ProtectedStorageReady = protectionReadiness.IsReady
        };
    }

    private (DatabaseCoordinationMode Mode, string? Failure) GetCoordinationMode()
    {
        if (dbContext.Database.IsSqlite())
        {
            var connStr = dbContext.Database.GetConnectionString();
            var configuredMode = configuration?["SearchCredentials:SqliteCoordinationMode"];
            return EvaluateSqliteCoordinationMode(connStr, configuredMode);
        }

        if (dbContext.Database.IsNpgsql())
        {
            return (DatabaseCoordinationMode.Distributed, null);
        }

        return (DatabaseCoordinationMode.Unsupported, "The configured database provider is not supported for scheduling readiness.");
    }

    /// <summary>
    /// Evaluates SQLite coordination mode per Task 9.3 and design.md P12.
    /// Detects network-shared SQLite via UNC paths, mapped drives, or mode=ro URI patterns,
    /// or requires explicit SearchCredentials:SqliteCoordinationMode=Standalone.
    /// </summary>
    public static (DatabaseCoordinationMode Mode, string? FailureReason) EvaluateSqliteCoordinationMode(
        string? connectionString,
        string? configuredCoordinationMode = null)
    {
        // 1. Explicit configuration assertion
        if (!string.IsNullOrWhiteSpace(configuredCoordinationMode))
        {
            if (string.Equals(configuredCoordinationMode, "Standalone", StringComparison.OrdinalIgnoreCase))
            {
                return (DatabaseCoordinationMode.SingleMaster, null);
            }

            return (DatabaseCoordinationMode.Unsupported,
                $"SQLite coordination mode '{configuredCoordinationMode}' is unsupported; only Standalone is supported.");
        }

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return (DatabaseCoordinationMode.SingleMaster, null);
        }

        // 2. Extract Data Source / Filename
        var path = ExtractSqlitePath(connectionString);

        // 3. UNC path detection: \\server\share or //server/share or file://
        if (path.StartsWith(@"\\", StringComparison.Ordinal) ||
            path.StartsWith("//", StringComparison.Ordinal) ||
            path.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            return (DatabaseCoordinationMode.Unsupported,
                $"Network-shared SQLite UNC or URI path detected ('{path}'). Network-shared SQLite is unsupported for distributed coordination.");
        }

        // 4. Read-only URI detection: mode=ro
        if (connectionString.Contains("mode=ro", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("mode=ro", StringComparison.OrdinalIgnoreCase))
        {
            return (DatabaseCoordinationMode.Unsupported,
                "SQLite read-only mode ('mode=ro') detected. Read-only SQLite cannot coordinate scheduling operations.");
        }

        // 5. Network drive detection on Windows (e.g. Z:\...)
        if (OperatingSystem.IsWindows() && path.Length >= 2 && path[1] == ':' && char.IsLetter(path[0]))
        {
            try
            {
                var root = Path.GetPathRoot(path);
                if (!string.IsNullOrEmpty(root))
                {
                    var driveInfo = new DriveInfo(root);
                    if (driveInfo.DriveType == DriveType.Network)
                    {
                        return (DatabaseCoordinationMode.Unsupported,
                            $"SQLite database path ('{path}') is on a network-mapped drive ('{root}'). Network-shared SQLite is unsupported for distributed coordination.");
                    }
                }
            }
            catch
            {
                // Drive might not exist or cannot be queried in test environment
            }
        }

        // 6. Local/standalone SQLite
        return (DatabaseCoordinationMode.SingleMaster, null);
    }

    private static string ExtractSqlitePath(string connectionString)
    {
        var parts = connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            var eq = part.IndexOf('=');
            if (eq > 0)
            {
                var key = part[..eq].Trim();
                var val = part[(eq + 1)..].Trim();
                if (key.Equals("Data Source", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("DataSource", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("Filename", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("Uri", StringComparison.OrdinalIgnoreCase))
                {
                    return val.Trim('\"', '\'');
                }
            }
        }
        return connectionString.Trim();
    }

    private async Task<bool> ValidatePhysicalSchemaAsync(
        ICollection<string> failures,
        CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await ReadSchemaSnapshotAsync(cancellationToken);

            foreach (var (table, expectedColumns) in RequiredColumns)
            {
                if (!snapshot.Columns.TryGetValue(table, out var actualColumns))
                {
                    failures.Add($"Required table '{table}' is missing.");
                    continue;
                }

                foreach (var column in expectedColumns)
                {
                    if (!actualColumns.Contains(column))
                    {
                        failures.Add($"Required column '{table}.{column}' is missing.");
                    }
                }
            }

            foreach (var (index, expectedColumns) in RequiredIndexes)
            {
                if (!snapshot.IndexDefinitions.TryGetValue(index, out var definition))
                {
                    failures.Add($"Required index '{index}' is missing.");
                    continue;
                }

                if (!ColumnsAppearInOrder(definition, expectedColumns))
                {
                    failures.Add($"Required index '{index}' has an incompatible definition.");
                }
            }

            foreach (var constraint in RequiredConstraints)
            {
                if (!snapshot.ConstraintDefinitions.ContainsKey(constraint))
                {
                    failures.Add($"Required constraint '{constraint}' is missing.");
                }
            }

            return !failures.Any(failure =>
                failure.Contains("table", StringComparison.OrdinalIgnoreCase) ||
                failure.Contains("column", StringComparison.OrdinalIgnoreCase) ||
                failure.Contains("index", StringComparison.OrdinalIgnoreCase) ||
                failure.Contains("constraint", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            failures.Add("Physical schema inspection failed.");
            return false;
        }
    }

    private async Task<bool> ValidateProviderInstancesAsync(
        ICollection<string> failures,
        CancellationToken cancellationToken)
    {
        try
        {
            var instances = await dbContext.SearchProviderInstances
                .AsNoTracking()
                .ToListAsync(cancellationToken);

            var github = instances.SingleOrDefault(instance =>
                instance.StableId == ProviderInstanceSchema.DefaultGitHubStableId &&
                instance.ProviderKind == SearchProviderEnum.GitHub &&
                instance.NormalizedScheme == "https" &&
                instance.NormalizedHost == "api.github.com" &&
                instance.NormalizedPort == 443 &&
                instance.NormalizedBasePath == "/");
            if (!IsSafeDefault(github))
            {
                failures.Add("The default GitHub Provider Instance is missing or invalid.");
            }

            var gitlab = instances.SingleOrDefault(instance =>
                instance.StableId == ProviderInstanceSchema.DefaultGitLabStableId &&
                instance.ProviderKind == SearchProviderEnum.GitLab &&
                instance.NormalizedScheme == "https" &&
                instance.NormalizedHost == "gitlab.com" &&
                instance.NormalizedPort == 443 &&
                instance.NormalizedBasePath == "/api/v4");
            if (!IsSafeDefault(gitlab))
            {
                failures.Add("The default GitLab Provider Instance is missing or invalid.");
            }

            var sourcegraph = instances.SingleOrDefault(instance =>
                instance.StableId == ProviderInstanceSchema.DefaultSourcegraphStableId &&
                instance.ProviderKind == SearchProviderEnum.Sourcegraph &&
                instance.NormalizedScheme == "https" &&
                instance.NormalizedHost == "sourcegraph.com" &&
                instance.NormalizedPort == 443 &&
                instance.NormalizedBasePath == "/.api");
            if (sourcegraph is not null && !IsSafeDefault(sourcegraph))
            {
                failures.Add("The default Sourcegraph Provider Instance is invalid.");
            }

            var huggingface = instances.SingleOrDefault(instance =>
                instance.StableId == ProviderInstanceSchema.DefaultHuggingFaceStableId &&
                instance.ProviderKind == SearchProviderEnum.HuggingFace &&
                instance.NormalizedScheme == "https" &&
                instance.NormalizedHost == "huggingface.co" &&
                instance.NormalizedPort == 443 &&
                instance.NormalizedBasePath == "/api");
            if (huggingface is not null && !IsSafeDefault(huggingface))
            {
                failures.Add("The default HuggingFace Provider Instance is invalid.");
            }

            var azure = instances.SingleOrDefault(instance =>
                instance.StableId == ProviderInstanceSchema.DefaultAzureDevOpsStableId &&
                instance.ProviderKind == SearchProviderEnum.AzureDevOps &&
                instance.NormalizedScheme == "https" &&
                instance.NormalizedHost == "dev.azure.com" &&
                instance.NormalizedPort == 443 &&
                instance.NormalizedBasePath == "/");
            if (azure is not null && !IsSafeDefault(azure))
            {
                failures.Add("The default AzureDevOps Provider Instance is invalid.");
            }

            var unsupportedKindExists = instances.Any(instance =>
                instance.ProviderKind is SearchProviderEnum.Unknown);
            if (unsupportedKindExists)
            {
                failures.Add("An unsupported Provider Kind is persisted.");
            }

            var managedIds = new HashSet<Guid>
            {
                ProviderInstanceSchema.DefaultGitHubStableId,
                ProviderInstanceSchema.DefaultGitLabStableId,
                ProviderInstanceSchema.DefaultSourcegraphStableId,
                ProviderInstanceSchema.DefaultHuggingFaceStableId,
                ProviderInstanceSchema.DefaultAzureDevOpsStableId
            };
            var unapprovedSelfHostedExists = instances.Any(instance =>
                instance.IsEnabled &&
                !managedIds.Contains(instance.StableId) &&
                !EndpointPolicy.ValidateApprovedIdentity(instance).Allowed);
            if (unapprovedSelfHostedExists)
            {
                failures.Add("A self-hosted Provider Instance lacks current endpoint-policy approval.");
            }

            var legacyMismatchExists = await dbContext.SearchProviderTokens
                .AsNoTracking()
                .AnyAsync(token =>
                    token.ProviderInstance == null ||
                    token.SearchProvider != token.ProviderInstance.ProviderKind,
                    cancellationToken);
            if (legacyMismatchExists)
            {
                failures.Add("A legacy credential is unlinked or conflicts with its Provider Instance kind.");
            }

            return IsSafeDefault(github) &&
                   IsSafeDefault(gitlab) &&
                   !unsupportedKindExists &&
                   !unapprovedSelfHostedExists &&
                   !legacyMismatchExists;
        }
        catch
        {
            failures.Add("Provider Instance data validation failed.");
            return false;
        }
    }

    private async Task<bool> ValidateMarkersAsync(
        ICollection<string> failures,
        CancellationToken cancellationToken)
    {
        try
        {
            var schemaVersion = await dbContext.SchemaVersions
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    record => record.Id == ProviderInstanceSchema.MarkerRecordId,
                    cancellationToken);
            var readinessMarker = await dbContext.ReadinessMarkers
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    marker => marker.Id == ProviderInstanceSchema.MarkerRecordId &&
                              marker.Name == ProviderInstanceSchema.ReadinessMarkerName,
                    cancellationToken);
            var cutoverMarker = await dbContext.CutoverMarkers
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    marker => marker.Id == ProviderInstanceSchema.MarkerRecordId &&
                              marker.Name == ProviderInstanceSchema.WorkerClaimsCutoverMarkerName,
                    cancellationToken);

            var schemaVersionReady = schemaVersion?.Version == ProviderInstanceSchema.CurrentVersion;
            var readinessMarkerReady = readinessMarker is
            {
                IsReady: true,
                SchemaVersion: ProviderInstanceSchema.CurrentVersion
            };
            var cutoverMarkerReady = cutoverMarker?.Version == ProviderInstanceSchema.CurrentVersion;

            if (!schemaVersionReady)
            {
                failures.Add("The active schema version is missing or incompatible.");
            }
            if (!readinessMarkerReady)
            {
                failures.Add("The Provider Instance readiness marker is missing or inactive.");
            }
            if (!cutoverMarkerReady)
            {
                failures.Add("The Worker Claims cutover marker version is missing or incompatible.");
            }

            return schemaVersionReady && readinessMarkerReady && cutoverMarkerReady;
        }
        catch
        {
            failures.Add("Schema marker validation failed.");
            return false;
        }
    }

    private static bool IsSafeDefault(SearchProviderInstance? instance) =>
        instance is
        {
            IsEnabled: true,
            AllowGlobalPublicSearch: false,
            MaxConcurrentOperations: > 0,
            SettingsVersion: > 0,
            SettingsJson.Length: > 0,
            ApprovedByTelegramId: null,
            EndpointPolicyVersion: 0,
            ApprovedEndpointIdentity: null,
            EndpointApprovedAtUtc: null,
            DevelopmentHttpAllowed: false,
            PrivateNetworkAllowlistJson: "[]"
        };

    private async Task<SchemaSnapshot> ReadSchemaSnapshotAsync(CancellationToken cancellationToken)
    {
        var connection = dbContext.Database.GetDbConnection();
        var closeWhenDone = connection.State != ConnectionState.Open;
        if (closeWhenDone)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            return dbContext.Database.IsSqlite()
                ? await ReadSqliteSchemaSnapshotAsync(connection, cancellationToken)
                : await ReadPostgresSchemaSnapshotAsync(connection, cancellationToken);
        }
        finally
        {
            if (closeWhenDone)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static async Task<SchemaSnapshot> ReadSqliteSchemaSnapshotAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        var columns = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in RequiredColumns.Keys)
        {
            var tableColumns = await ReadFirstColumnAsync(
                connection,
                $"PRAGMA table_info(\"{table.Replace("\"", "\"\"")}\");",
                cancellationToken,
                ordinal: 1);
            if (tableColumns.Count > 0)
            {
                columns[table] = tableColumns;
            }
        }

        var indexes = await ReadNameDefinitionMapAsync(
            connection,
            "SELECT name, COALESCE(sql, '') FROM sqlite_master WHERE type = 'index';",
            cancellationToken);
        var tableDefinitions = await ReadNameDefinitionMapAsync(
            connection,
            "SELECT name, COALESCE(sql, '') FROM sqlite_master WHERE type = 'table';",
            cancellationToken);
        var triggerDefinitions = await ReadNameDefinitionMapAsync(
            connection,
            "SELECT name, COALESCE(sql, '') FROM sqlite_master WHERE type = 'trigger';",
            cancellationToken);
        var constraintDefinitions = tableDefinitions.Values
            .Concat(triggerDefinitions.Values)
            .ToArray();
        var constraints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var requiredConstraint in RequiredConstraints)
        {
            var definition = constraintDefinitions.FirstOrDefault(value =>
                value.Contains(requiredConstraint, StringComparison.OrdinalIgnoreCase));
            if (definition is not null)
            {
                constraints[requiredConstraint] = definition;
            }
        }

        return new SchemaSnapshot(columns, indexes, constraints);
    }

    private static async Task<SchemaSnapshot> ReadPostgresSchemaSnapshotAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        var columns = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT table_name, column_name
                  FROM information_schema.columns
                 WHERE table_schema = current_schema();
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var table = reader.GetString(0);
                if (!columns.TryGetValue(table, out var tableColumns))
                {
                    tableColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    columns[table] = tableColumns;
                }
                tableColumns.Add(reader.GetString(1));
            }
        }

        var indexes = await ReadNameDefinitionMapAsync(
            connection,
            "SELECT indexname, indexdef FROM pg_indexes WHERE schemaname = current_schema();",
            cancellationToken);
        var constraints = await ReadNameDefinitionMapAsync(
            connection,
            """
            SELECT constraint_record.conname, pg_get_constraintdef(constraint_record.oid)
              FROM pg_constraint AS constraint_record
              JOIN pg_namespace AS namespace_record
                ON namespace_record.oid = constraint_record.connamespace
             WHERE namespace_record.nspname = current_schema();
            """,
            cancellationToken);

        return new SchemaSnapshot(columns, indexes, constraints);
    }

    private static async Task<HashSet<string>> ReadFirstColumnAsync(
        DbConnection connection,
        string commandText,
        CancellationToken cancellationToken,
        int ordinal = 0)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(reader.GetString(ordinal));
        }
        return values;
    }

    private static async Task<Dictionary<string, string>> ReadNameDefinitionMapAsync(
        DbConnection connection,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values[reader.GetString(0)] = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
        }
        return values;
    }

    private static bool ColumnsAppearInOrder(string definition, IReadOnlyList<string> columns)
    {
        var lastPosition = -1;
        foreach (var column in columns)
        {
            var position = definition.IndexOf(column, lastPosition + 1, StringComparison.OrdinalIgnoreCase);
            if (position < 0)
            {
                return false;
            }
            lastPosition = position;
        }
        return true;
    }

    private sealed record SchemaSnapshot(
        IReadOnlyDictionary<string, HashSet<string>> Columns,
        IReadOnlyDictionary<string, string> IndexDefinitions,
        IReadOnlyDictionary<string, string> ConstraintDefinitions);
}

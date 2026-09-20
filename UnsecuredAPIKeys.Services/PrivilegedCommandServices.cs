using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using UnsecuredAPIKeys.Data;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Data.Models;

namespace UnsecuredAPIKeys.Services;

public enum PrivilegeAuthorizationDecision
{
    Authorized,
    Unauthenticated,
    Forbidden
}

public interface IPrivilegePolicy
{
    PrivilegeAuthorizationDecision Authorize(
        NodePrincipalResolution resolution,
        PrivilegedActionKind action);

    void RequireAdministrator(
        SchedulerPrincipal principal,
        PrivilegedActionKind action);
}

/// <summary>
/// One fail-closed policy for every protected credential, instance, allowlist, and consent command.
/// </summary>
public sealed class PrivilegePolicy : IPrivilegePolicy
{
    public PrivilegeAuthorizationDecision Authorize(
        NodePrincipalResolution resolution,
        PrivilegedActionKind action)
    {
        ArgumentNullException.ThrowIfNull(resolution);

        if (!Enum.IsDefined(action))
        {
            return PrivilegeAuthorizationDecision.Forbidden;
        }

        if (!resolution.IsAuthenticated)
        {
            return PrivilegeAuthorizationDecision.Unauthenticated;
        }

        var principal = resolution.Principal;
        return principal is
        {
            IsAdministrator: true,
            IsSystem: false,
            TelegramPrincipalId: > 0
        }
            ? PrivilegeAuthorizationDecision.Authorized
            : PrivilegeAuthorizationDecision.Forbidden;
    }

    public void RequireAdministrator(
        SchedulerPrincipal principal,
        PrivilegedActionKind action)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (Authorize(NodePrincipalResolution.Resolved(principal), action) !=
            PrivilegeAuthorizationDecision.Authorized)
        {
            throw new PrivilegeAuthorizationException(action);
        }
    }
}

public sealed class PrivilegeAuthorizationException(PrivilegedActionKind action)
    : UnauthorizedAccessException("The privileged command requires an authenticated administrator.")
{
    public PrivilegedActionKind Action { get; } = action;
}

public interface IAuditReasonSanitizer
{
    string Sanitize(string reason);
}

/// <summary>
/// Bounds audit reasons and removes common control-plane secret representations before persistence.
/// Audit records retain operator intent, never credential material or complete fingerprints.
/// </summary>
public sealed class AuditReasonSanitizer : IAuditReasonSanitizer
{
    public const int MaximumLength = 512;

    private static readonly Regex SensitiveAssignmentPattern = new(
        @"(?ix)[""']?(?:authorization|private-token|x-node-token|node-token|access-token|token|secret|password|fingerprint)[""']?\s*[:=]\s*(?:Bearer\s+)?(?:""[^""]*""|'[^']*'|[^\s,;]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex BearerPattern = new(
        @"(?i)\bBearer\s+[^\s,;]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ProviderCredentialPattern = new(
        @"(?i)\b(?:ghp_|github_pat_|glpat-)[A-Za-z0-9_.-]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex CompleteFingerprintPattern = new(
        @"(?i)\b[a-f0-9]{64}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex WhitespacePattern = new(
        @"\s+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public string Sanitize(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A non-empty reason is required.", nameof(reason));
        }

        var redacted = SensitiveAssignmentPattern.Replace(reason, "[REDACTED]");
        redacted = BearerPattern.Replace(redacted, "[REDACTED]");
        redacted = ProviderCredentialPattern.Replace(redacted, "[REDACTED]");
        redacted = CompleteFingerprintPattern.Replace(redacted, "[REDACTED]");

        var withoutControls = new string(redacted
            .Select(character => char.IsControl(character) ? ' ' : character)
            .ToArray());
        var normalized = WhitespacePattern.Replace(withoutControls, " ").Trim();
        if (normalized.Length == 0)
        {
            throw new ArgumentException("A non-empty reason is required.", nameof(reason));
        }

        return normalized.Length <= MaximumLength
            ? normalized
            : normalized[..MaximumLength].TrimEnd();
    }
}

public interface IDatabaseUtcClock
{
    Task<DateTime> GetUtcNowAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads the authoritative command timestamp from the active database provider.
/// </summary>
public sealed class DatabaseUtcClock(DBContext dbContext) : IDatabaseUtcClock
{
    public async Task<DateTime> GetUtcNowAsync(CancellationToken cancellationToken = default)
    {
        if (dbContext.Database.IsSqlite())
        {
            // Materializing SQLite's formatted clock directly as DateTime can discard the
            // fractional component. Parse the round-trip text explicitly so state changes
            // cannot precede same-second credential creation timestamps.
            var text = await dbContext.Database
                .SqlQueryRaw<string>(
                    "SELECT strftime('%Y-%m-%dT%H:%M:%fZ', 'now') AS \"Value\"")
                .SingleAsync(cancellationToken);
            return DateTime.ParseExact(
                text,
                "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        }

        var value = await dbContext.Database
            .SqlQueryRaw<DateTime>("SELECT CURRENT_TIMESTAMP AS \"Value\"")
            .SingleAsync(cancellationToken);
        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
    }
}

public enum ProviderInstanceApprovalStatus
{
    Succeeded,
    NotFound
}

public sealed record ProviderInstanceApprovalResult(
    ProviderInstanceApprovalStatus Status,
    Guid ProviderInstanceStableId,
    long ActorTelegramId,
    DateTime OccurredUtc);

public interface IProviderInstanceCommandService
{
    Task<ProviderInstanceApprovalResult> ApproveAsync(
        Guid providerInstanceStableId,
        SchedulerPrincipal actor,
        string reason,
        CancellationToken cancellationToken = default);

    Task<ProviderInstanceApprovalResult> UpdateEndpointAsync(
        Guid providerInstanceStableId,
        string endpoint,
        SchedulerPrincipal actor,
        string reason,
        CancellationToken cancellationToken = default);

    Task<ProviderInstanceApprovalResult> SetDevelopmentHttpExceptionAsync(
        Guid providerInstanceStableId,
        bool allowed,
        SchedulerPrincipal actor,
        string reason,
        CancellationToken cancellationToken = default);

    Task<ProviderInstanceApprovalResult> ReplacePrivateNetworkAllowlistAsync(
        Guid providerInstanceStableId,
        IEnumerable<string> networks,
        SchedulerPrincipal actor,
        string reason,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Owns endpoint policy and Provider Instance approval mutations with their audit records
/// in one transaction. Any origin, path, HTTP-exception, or allowlist change invalidates
/// the prior approval. Controllers never receive the DbContext for these operations.
/// </summary>
public sealed class ProviderInstanceCommandService(
    DBContext dbContext,
    IPrivilegePolicy privilegePolicy,
    IAuditReasonSanitizer reasonSanitizer,
    IDatabaseUtcClock databaseUtcClock) : IProviderInstanceCommandService
{
    public Task<ProviderInstanceApprovalResult> ApproveAsync(
        Guid providerInstanceStableId,
        SchedulerPrincipal actor,
        string reason,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            providerInstanceStableId,
            actor,
            reason,
            PrivilegedActionKind.ProviderInstanceApprove,
            instance =>
            {
                var endpoint = EndpointPolicy.NormalizeAndValidate(
                    BuildEndpoint(instance),
                    instance.StableId,
                    instance.DevelopmentHttpAllowed ? instance.StableId : null);
                if (!endpoint.Allowed)
                {
                    throw new EndpointPolicyException(endpoint.Reason);
                }

                _ = EndpointPolicy.ParsePrivateNetworkAllowlist(instance.PrivateNetworkAllowlistJson);
                instance.ApprovedByTelegramId = actor.TelegramPrincipalId!.Value;
                instance.EndpointPolicyVersion = EndpointPolicy.CurrentPolicyVersion;
                instance.ApprovedEndpointIdentity = EndpointPolicy.CreateCanonicalIdentity(
                    endpoint.Scheme,
                    endpoint.Host,
                    endpoint.Port,
                    endpoint.BasePath);
            },
            cancellationToken,
            setApprovalTime: true);

    public Task<ProviderInstanceApprovalResult> UpdateEndpointAsync(
        Guid providerInstanceStableId,
        string endpoint,
        SchedulerPrincipal actor,
        string reason,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            providerInstanceStableId,
            actor,
            reason,
            PrivilegedActionKind.ProviderInstanceApprove,
            instance =>
            {
                var normalized = EndpointPolicy.NormalizeAndValidate(
                    endpoint,
                    instance.StableId,
                    instance.DevelopmentHttpAllowed ? instance.StableId : null);
                if (!normalized.Allowed)
                {
                    throw new EndpointPolicyException(normalized.Reason);
                }

                EndpointPolicy.InvalidateApprovalIfEndpointChanged(
                    instance,
                    normalized.Scheme,
                    normalized.Host,
                    normalized.Port,
                    normalized.BasePath);
                instance.NormalizedScheme = normalized.Scheme;
                instance.NormalizedHost = normalized.Host;
                instance.NormalizedPort = normalized.Port;
                instance.NormalizedBasePath = normalized.BasePath;
            },
            cancellationToken);

    public Task<ProviderInstanceApprovalResult> SetDevelopmentHttpExceptionAsync(
        Guid providerInstanceStableId,
        bool allowed,
        SchedulerPrincipal actor,
        string reason,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            providerInstanceStableId,
            actor,
            reason,
            PrivilegedActionKind.ProviderInstanceApprove,
            instance =>
            {
                if (instance.DevelopmentHttpAllowed == allowed)
                {
                    return;
                }
                instance.DevelopmentHttpAllowed = allowed;
                EndpointPolicy.ClearApproval(instance);
            },
            cancellationToken);

    public Task<ProviderInstanceApprovalResult> ReplacePrivateNetworkAllowlistAsync(
        Guid providerInstanceStableId,
        IEnumerable<string> networks,
        SchedulerPrincipal actor,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(networks);
        var serialized = EndpointPolicy.SerializePrivateNetworkAllowlist(networks);
        return ExecuteAsync(
            providerInstanceStableId,
            actor,
            reason,
            PrivilegedActionKind.PrivateNetworkAllowlist,
            instance =>
            {
                if (string.Equals(instance.PrivateNetworkAllowlistJson, serialized, StringComparison.Ordinal))
                {
                    return;
                }
                instance.PrivateNetworkAllowlistJson = serialized;
                EndpointPolicy.ClearApproval(instance);
            },
            cancellationToken);
    }

    private async Task<ProviderInstanceApprovalResult> ExecuteAsync(
        Guid providerInstanceStableId,
        SchedulerPrincipal actor,
        string reason,
        PrivilegedActionKind action,
        Action<SearchProviderInstance> mutation,
        CancellationToken cancellationToken,
        bool setApprovalTime = false)
    {
        if (providerInstanceStableId == Guid.Empty)
        {
            throw new ArgumentException("Provider Instance Stable ID must be non-empty.", nameof(providerInstanceStableId));
        }

        ArgumentNullException.ThrowIfNull(actor);
        privilegePolicy.RequireAdministrator(actor, action);
        var sanitizedReason = reasonSanitizer.Sanitize(reason);
        var actorTelegramId = actor.TelegramPrincipalId!.Value;
        var executionStrategy = dbContext.Database.CreateExecutionStrategy();

        return await executionStrategy.ExecuteAsync(async () =>
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            var occurredUtc = await databaseUtcClock.GetUtcNowAsync(cancellationToken);
            var instance = await dbContext.SearchProviderInstances
                .SingleOrDefaultAsync(
                    candidate => candidate.StableId == providerInstanceStableId,
                    cancellationToken);

            var outcome = instance is null
                ? PrivilegedActionOutcome.TargetNotFound
                : PrivilegedActionOutcome.Succeeded;

            if (instance is not null)
            {
                mutation(instance);
                if (setApprovalTime)
                {
                    instance.EndpointApprovedAtUtc = occurredUtc;
                }
                instance.UpdatedUtc = occurredUtc;
            }

            dbContext.PrivilegedAuditRecords.Add(new PrivilegedAuditRecord
            {
                ActorTelegramId = actorTelegramId,
                Action = action,
                TargetStableId = providerInstanceStableId,
                OccurredUtc = occurredUtc,
                Outcome = outcome,
                SanitizedReason = sanitizedReason
            });

            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return new ProviderInstanceApprovalResult(
                instance is null
                    ? ProviderInstanceApprovalStatus.NotFound
                    : ProviderInstanceApprovalStatus.Succeeded,
                providerInstanceStableId,
                actorTelegramId,
                occurredUtc);
        });
    }

    private static string BuildEndpoint(SearchProviderInstance instance)
    {
        var host = instance.NormalizedHost.Contains(':')
            ? $"[{instance.NormalizedHost}]"
            : instance.NormalizedHost;
        return $"{instance.NormalizedScheme}://{host}:{instance.NormalizedPort}{instance.NormalizedBasePath}";
    }
}

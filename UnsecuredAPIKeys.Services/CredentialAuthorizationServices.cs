using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using UnsecuredAPIKeys.Data;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Data.Models;

namespace UnsecuredAPIKeys.Services;

/// <summary>
/// Immutable server-created identity used by both Master-local and node authorization.
/// </summary>
public sealed record SchedulerPrincipal
{
    private SchedulerPrincipal(long? telegramPrincipalId, bool isAdministrator, bool isSystem)
    {
        TelegramPrincipalId = telegramPrincipalId;
        IsAdministrator = isAdministrator;
        IsSystem = isSystem;
    }

    public long? TelegramPrincipalId { get; }

    public bool IsAdministrator { get; }

    public bool IsSystem { get; }

    public static SchedulerPrincipal ForTelegram(long telegramPrincipalId, bool isAdministrator)
    {
        if (telegramPrincipalId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(telegramPrincipalId),
                "A registered Telegram principal must have a positive identifier.");
        }

        return new SchedulerPrincipal(telegramPrincipalId, isAdministrator, isSystem: false);
    }

    public static SchedulerPrincipal System { get; } =
        new(telegramPrincipalId: null, isAdministrator: false, isSystem: true);
}

public sealed record NodePrincipalResolution(
    bool IsAuthenticated,
    SchedulerPrincipal? Principal)
{
    public bool IsResolved => Principal is not null;

    public static NodePrincipalResolution Unauthenticated { get; } = new(false, null);

    public static NodePrincipalResolution AuthenticatedWithoutMapping { get; } = new(true, null);

    public static NodePrincipalResolution Resolved(SchedulerPrincipal principal) =>
        new(true, principal);
}

public interface INodePrincipalResolver
{
    Task<NodePrincipalResolution> ResolveNodeAsync(
        string nodeToken,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Authenticates a node token and derives its Telegram principal exclusively from persisted data.
/// </summary>
public sealed class NodePrincipalResolver(DBContext dbContext) : INodePrincipalResolver
{
    public async Task<NodePrincipalResolution> ResolveNodeAsync(
        string nodeToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(nodeToken))
        {
            return NodePrincipalResolution.Unauthenticated;
        }

        var mapping = await dbContext.TelegramSubscribers
            .AsNoTracking()
            .Where(subscriber => subscriber.NodeToken == nodeToken)
            .Select(subscriber => new { subscriber.TelegramId, subscriber.IsAdmin })
            .SingleOrDefaultAsync(cancellationToken);

        if (mapping is null)
        {
            return NodePrincipalResolution.Unauthenticated;
        }

        return mapping.TelegramId <= 0
            ? NodePrincipalResolution.AuthenticatedWithoutMapping
            : NodePrincipalResolution.Resolved(
                SchedulerPrincipal.ForTelegram(mapping.TelegramId, mapping.IsAdmin));
    }
}

public interface ICredentialGrantEvaluator
{
    IQueryable<SearchProviderToken> ApplyAuthorization(
        IQueryable<SearchProviderToken> credentials,
        SchedulerPrincipal principal);

    Task<bool> IsCredentialAuthorizedAsync(
        int credentialId,
        SchedulerPrincipal principal,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Common, cache-free grant evaluator. Every call observes grants committed before its query begins.
/// </summary>
public sealed class CredentialGrantEvaluator(DBContext dbContext) : ICredentialGrantEvaluator
{
    public IQueryable<SearchProviderToken> ApplyAuthorization(
        IQueryable<SearchProviderToken> credentials,
        SchedulerPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(principal);

        if (principal.IsAdministrator || principal.IsSystem)
        {
            return credentials.Where(credential => credential.CredentialGrants.Any(grant =>
                grant.Scope == CredentialGrantScope.Global ||
                grant.Scope == CredentialGrantScope.Admin));
        }

        if (principal.TelegramPrincipalId is not > 0)
        {
            return credentials.Where(_ => false);
        }

        var telegramPrincipalId = principal.TelegramPrincipalId.Value;
        return credentials.Where(credential => credential.CredentialGrants.Any(grant =>
            grant.Scope == CredentialGrantScope.Global ||
            (grant.Scope == CredentialGrantScope.User &&
             grant.TelegramPrincipalId == telegramPrincipalId)));
    }

    public Task<bool> IsCredentialAuthorizedAsync(
        int credentialId,
        SchedulerPrincipal principal,
        CancellationToken cancellationToken = default) =>
        ApplyAuthorization(dbContext.SearchProviderTokens.AsNoTracking(), principal)
            .AnyAsync(credential => credential.Id == credentialId, cancellationToken);
}

public enum CredentialGrantBackfillStatus
{
    NotEvaluated,
    Ready,
    Failed
}

/// <summary>
/// Process readiness state for the startup backfill. It contains no credential data.
/// </summary>
public sealed class CredentialGrantBackfillState
{
    private int _status = (int)CredentialGrantBackfillStatus.NotEvaluated;

    public CredentialGrantBackfillStatus Status =>
        (CredentialGrantBackfillStatus)Volatile.Read(ref _status);

    public void MarkReady() =>
        Volatile.Write(ref _status, (int)CredentialGrantBackfillStatus.Ready);

    public void MarkFailed() =>
        Volatile.Write(ref _status, (int)CredentialGrantBackfillStatus.Failed);
}

public static class CredentialGrantBackfillConfiguration
{
    public const string UnownedCredentialScope =
        "SearchCredentials:LegacyUnownedGrantScope";
}

public sealed record CredentialGrantBackfillResult(
    int UserGrantsAdded,
    int CompatibilityGrantsAdded);

/// <summary>
/// Converts legacy ownership metadata into durable grants before scheduling readiness is evaluated.
/// </summary>
public sealed class CredentialGrantBackfillService(
    DBContext dbContext,
    IConfiguration configuration,
    CredentialGrantBackfillState readinessState,
    CredentialMutationGate? mutationGate = null)
{
    private readonly CredentialMutationGate effectiveMutationGate = mutationGate ?? new();

    public Task<CredentialGrantBackfillResult> BackfillAsync(
        CancellationToken cancellationToken = default) =>
        effectiveMutationGate.ExecuteAsync(
            () => BackfillUnderGateAsync(cancellationToken),
            cancellationToken);

    private async Task<CredentialGrantBackfillResult> BackfillUnderGateAsync(
        CancellationToken cancellationToken)
    {
        var legacyCredentials = await dbContext.SearchProviderTokens
            .AsNoTracking()
            .Select(credential => new
            {
                credential.Id,
                credential.AddedByTelegramId
            })
            .ToListAsync(cancellationToken);

        var hasUnownedCredentials = legacyCredentials.Any(credential =>
            credential.AddedByTelegramId is not > 0);
        var compatibilityScope = hasUnownedCredentials
            ? ResolveExplicitCompatibilityScope()
            : null;

        if (hasUnownedCredentials && compatibilityScope is null)
        {
            readinessState.MarkFailed();
            throw new InvalidOperationException(
                "Legacy unowned credentials require an explicit Global or Admin grant policy.");
        }

        var executionStrategy = dbContext.Database.CreateExecutionStrategy();
        return await executionStrategy.ExecuteAsync(async () =>
        {
            dbContext.ChangeTracker.Clear();
            var existingGrantKeys = (await dbContext.CredentialGrants
                    .AsNoTracking()
                    .Select(grant => new
                    {
                        grant.CredentialId,
                        grant.Scope,
                        grant.TelegramPrincipalId
                    })
                    .ToListAsync(cancellationToken))
                .Select(grant => (grant.CredentialId, grant.Scope, grant.TelegramPrincipalId))
                .ToHashSet();

            var userGrantsAdded = 0;
            var compatibilityGrantsAdded = 0;
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

            try
            {
                foreach (var credential in legacyCredentials)
                {
                    var scope = credential.AddedByTelegramId is > 0
                        ? CredentialGrantScope.User
                        : compatibilityScope!.Value;
                    var telegramPrincipalId = scope == CredentialGrantScope.User
                        ? credential.AddedByTelegramId
                        : null;
                    var key = (credential.Id, scope, telegramPrincipalId);
                    if (!existingGrantKeys.Add(key))
                    {
                        continue;
                    }

                    dbContext.CredentialGrants.Add(new CredentialGrant
                    {
                        CredentialId = credential.Id,
                        Scope = scope,
                        TelegramPrincipalId = telegramPrincipalId,
                        CreatedUtc = DateTime.UtcNow
                    });

                    if (scope == CredentialGrantScope.User)
                    {
                        userGrantsAdded++;
                    }
                    else
                    {
                        compatibilityGrantsAdded++;
                    }
                }

                await dbContext.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                readinessState.MarkReady();
                return new CredentialGrantBackfillResult(userGrantsAdded, compatibilityGrantsAdded);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                readinessState.MarkFailed();
                throw;
            }
        });
    }

    private CredentialGrantScope? ResolveExplicitCompatibilityScope()
    {
        var configured = configuration[CredentialGrantBackfillConfiguration.UnownedCredentialScope];
        if (!Enum.TryParse<CredentialGrantScope>(configured, ignoreCase: true, out var scope))
        {
            return null;
        }

        return scope is CredentialGrantScope.Global or CredentialGrantScope.Admin
            ? scope
            : null;
    }
}

public sealed record CredentialGrantReadinessResult(
    bool IsReady,
    IReadOnlyList<string> Failures)
{
    public static CredentialGrantReadinessResult Ready { get; } = new(true, []);
}

/// <summary>
/// Ensures startup backfill completed and every persisted credential has an explicit grant.
/// </summary>
public sealed class CredentialGrantReadinessService(
    DBContext dbContext,
    CredentialGrantBackfillState backfillState)
{
    public async Task<CredentialGrantReadinessResult> EvaluateAsync(
        CancellationToken cancellationToken = default)
    {
        var failures = new List<string>();
        if (backfillState.Status != CredentialGrantBackfillStatus.Ready)
        {
            failures.Add("Credential grant backfill is incomplete.");
        }

        try
        {
            if (await dbContext.SearchProviderTokens
                    .AsNoTracking()
                    .AnyAsync(
                        credential => !credential.CredentialGrants.Any(),
                        cancellationToken))
            {
                failures.Add("One or more credentials have no explicit grant.");
            }
        }
        catch
        {
            failures.Add("Credential grant readiness validation failed.");
        }

        return failures.Count == 0
            ? CredentialGrantReadinessResult.Ready
            : new CredentialGrantReadinessResult(false, failures);
    }
}

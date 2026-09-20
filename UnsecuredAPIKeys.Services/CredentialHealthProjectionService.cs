using Microsoft.EntityFrameworkCore;
using UnsecuredAPIKeys.Data;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Data.Models;

namespace UnsecuredAPIKeys.Services;

/// <summary>Secret-free health view of one provider credential. Lease fields are admin-only.</summary>
public sealed record CredentialHealthItem(
    Guid StableId,
    SearchProviderEnum ProviderKind,
    Guid ProviderInstanceStableId,
    string Alias,
    CredentialSource Source,
    bool IsEnabled,
    string? DisabledReason,
    DateTime? DisabledAtUtc,
    DateTime? LastClaimedUtc,
    DateTime? LastUsedUtc,
    DateTime? CooldownUntilUtc,
    int ConsecutiveTransientFailures,
    string? LastOutcome,
    string? LeaseOwnerNodeId,
    DateTime? LeaseExpiresUtc);

/// <summary>Secret-free aggregate health of one provider instance.</summary>
public sealed record ProviderHealthItem(
    Guid InstanceStableId,
    SearchProviderEnum ProviderKind,
    string DisplayName,
    bool IsEnabled,
    int TotalCredentials,
    int EnabledCredentials,
    int ActiveLeases,
    int CooldownCredentials,
    IReadOnlyDictionary<string, int> Outcomes,
    IReadOnlyList<string> ActiveLeaseOwners);

/// <summary>
/// Role-filtered credential and provider health projections (Wave 15, Task 15.1/15.2).
/// Non-administrators see only credentials authorized for their principal, without Lease
/// ownership; administrators see the fleet-wide projection including active Lease owners.
/// Projections carry masked aliases and stable references only — never credential
/// material, fingerprints, or node tokens.
/// </summary>
public sealed class CredentialHealthProjectionService(
    DBContext dbContext,
    ICredentialGrantEvaluator grantEvaluator)
{
    public static string ToAlias(Guid stableId) =>
        stableId == Guid.Empty ? "unassigned" : stableId.ToString("N")[..12];

    /// <param name="isAdmin">Fleet-wide view with Lease ownership when true.</param>
    /// <param name="telegramPrincipalId">Required for the non-admin filtered view.</param>
    public async Task<IReadOnlyList<CredentialHealthItem>> GetCredentialsAsync(
        bool isAdmin,
        long? telegramPrincipalId,
        CancellationToken cancellationToken = default)
    {
        IQueryable<SearchProviderToken> query = dbContext.SearchProviderTokens.AsNoTracking();
        if (!isAdmin)
        {
            if (telegramPrincipalId is not > 0)
            {
                return [];
            }

            query = grantEvaluator.ApplyAuthorization(
                query,
                SchedulerPrincipal.ForTelegram(telegramPrincipalId.Value, isAdministrator: false));
        }

        var rows = await query
            .OrderBy(credential => credential.Id)
            .Select(credential => new
            {
                credential.StableId,
                credential.SearchProvider,
                InstanceStableId = credential.ProviderInstance!.StableId,
                credential.Source,
                credential.IsEnabled,
                credential.DisabledReason,
                credential.DisabledAtUtc,
                credential.LastClaimedUtc,
                credential.LastUsedUTC,
                credential.CooldownUntilUtc,
                credential.ConsecutiveTransientFailures,
                credential.LastOutcome,
                credential.LeaseOwnerNodeId,
                credential.LeaseExpiresUtc
            })
            .ToListAsync(cancellationToken);

        return rows
            .Select(row => new CredentialHealthItem(
                row.StableId,
                row.SearchProvider,
                row.InstanceStableId,
                ToAlias(row.StableId),
                row.Source,
                row.IsEnabled,
                row.DisabledReason,
                row.DisabledAtUtc,
                row.LastClaimedUtc,
                row.LastUsedUTC,
                row.CooldownUntilUtc,
                row.ConsecutiveTransientFailures,
                row.LastOutcome,
                isAdmin ? row.LeaseOwnerNodeId : null,
                isAdmin ? row.LeaseExpiresUtc : null))
            .ToArray();
    }

    /// <param name="isAdmin">Fleet-wide view with Lease owners when true.</param>
    /// <param name="telegramPrincipalId">Required for the non-admin filtered view.</param>
    public async Task<IReadOnlyList<ProviderHealthItem>> GetProvidersAsync(
        bool isAdmin,
        long? telegramPrincipalId,
        CancellationToken cancellationToken = default)
    {
        HashSet<long>? visibleInstanceIds = null;
        if (!isAdmin)
        {
            if (telegramPrincipalId is not > 0)
            {
                return [];
            }

            var principal = SchedulerPrincipal.ForTelegram(telegramPrincipalId.Value, isAdministrator: false);
            var visibleIds = await grantEvaluator.ApplyAuthorization(
                    dbContext.SearchProviderTokens.AsNoTracking(), principal)
                .Select(credential => credential.ProviderInstanceId)
                .Distinct()
                .ToListAsync(cancellationToken);
            visibleInstanceIds = [.. visibleIds];
        }

        IQueryable<SearchProviderInstance> instanceQuery =
            dbContext.SearchProviderInstances.AsNoTracking();
        if (visibleInstanceIds is not null)
        {
            instanceQuery = instanceQuery.Where(instance => visibleInstanceIds.Contains(instance.Id));
        }

        var instances = await instanceQuery
            .OrderBy(instance => instance.ProviderKind)
            .ThenBy(instance => instance.StableId)
            .Select(instance => new
            {
                instance.Id,
                instance.StableId,
                instance.ProviderKind,
                instance.DisplayName,
                instance.IsEnabled
            })
            .ToListAsync(cancellationToken);

        var credentials = await dbContext.SearchProviderTokens
            .AsNoTracking()
            .Where(credential => instances.Select(instance => instance.Id).Contains(credential.ProviderInstanceId))
            .Select(credential => new
            {
                credential.ProviderInstanceId,
                credential.IsEnabled,
                credential.LeaseId,
                credential.LeaseExpiresUtc,
                credential.CooldownUntilUtc,
                credential.LastOutcome,
                credential.LeaseOwnerNodeId
            })
            .ToListAsync(cancellationToken);

        var now = DateTime.UtcNow;
        return instances
            .Select(instance =>
            {
                var owned = credentials
                    .Where(credential => credential.ProviderInstanceId == instance.Id)
                    .ToArray();
                var outcomes = owned
                    .Where(credential => !string.IsNullOrEmpty(credential.LastOutcome))
                    .GroupBy(credential => credential.LastOutcome!)
                    .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
                var activeOwners = isAdmin
                    ? owned
                        .Where(credential =>
                            credential.LeaseId.HasValue &&
                            credential.LeaseExpiresUtc > now &&
                            !string.IsNullOrEmpty(credential.LeaseOwnerNodeId))
                        .Select(credential => credential.LeaseOwnerNodeId!)
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(owner => owner, StringComparer.Ordinal)
                        .ToArray()
                    : Array.Empty<string>();

                return new ProviderHealthItem(
                    instance.StableId,
                    instance.ProviderKind,
                    instance.DisplayName,
                    instance.IsEnabled,
                    owned.Length,
                    owned.Count(credential => credential.IsEnabled),
                    owned.Count(credential =>
                        credential.LeaseId.HasValue && credential.LeaseExpiresUtc > now),
                    owned.Count(credential => credential.CooldownUntilUtc > now),
                    outcomes,
                    activeOwners);
            })
            .ToArray();
    }
}

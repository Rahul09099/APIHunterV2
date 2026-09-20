using Microsoft.EntityFrameworkCore;
using UnsecuredAPIKeys.Data;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Data.Models;

namespace UnsecuredAPIKeys.Services;

public enum PublicSearchConsentStatus
{
    Succeeded,
    NotFound
}

public sealed record PublicSearchConsentResult(
    PublicSearchConsentStatus Status,
    Guid ProviderInstanceStableId,
    bool IsActive,
    long ActorTelegramId,
    DateTime OccurredUtc);

public interface IPublicSearchConsentService
{
    Task<PublicSearchConsentResult> GrantAsync(
        Guid providerInstanceStableId,
        SchedulerPrincipal actor,
        string reason,
        CancellationToken cancellationToken = default);

    Task<PublicSearchConsentResult> RevokeAsync(
        Guid providerInstanceStableId,
        SchedulerPrincipal actor,
        string reason,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// True only when an active consent record exists for the exact instance.
    /// The instance <c>AllowGlobalPublicSearch</c> projection is deny-oriented and is
    /// never consulted here — it cannot prove consent by itself.
    /// </summary>
    Task<bool> IsConsentedAsync(
        Guid providerInstanceStableId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Owns global-public-search consent mutations with their audit records in one
/// transaction (Wave 14, Task 14.2). Opt-in and opt-out both require an authenticated
/// administrator (AC-1.23); every decision persists actor, exact instance, UTC time,
/// state, and sanitized reason. Controllers never receive the DbContext for these
/// operations.
/// </summary>
public sealed class PublicSearchConsentService(
    DBContext dbContext,
    IPrivilegePolicy privilegePolicy,
    IAuditReasonSanitizer reasonSanitizer,
    IDatabaseUtcClock databaseUtcClock) : IPublicSearchConsentService
{
    public Task<PublicSearchConsentResult> GrantAsync(
        Guid providerInstanceStableId,
        SchedulerPrincipal actor,
        string reason,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(providerInstanceStableId, actor, reason, grant: true, cancellationToken);

    public Task<PublicSearchConsentResult> RevokeAsync(
        Guid providerInstanceStableId,
        SchedulerPrincipal actor,
        string reason,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(providerInstanceStableId, actor, reason, grant: false, cancellationToken);

    public Task<bool> IsConsentedAsync(
        Guid providerInstanceStableId,
        CancellationToken cancellationToken = default) =>
        dbContext.PublicSearchConsents
            .AsNoTracking()
            .Where(consent =>
                consent.IsActive &&
                consent.ProviderInstance != null &&
                consent.ProviderInstance.StableId == providerInstanceStableId)
            .AnyAsync(cancellationToken);

    private async Task<PublicSearchConsentResult> ExecuteAsync(
        Guid providerInstanceStableId,
        SchedulerPrincipal actor,
        string reason,
        bool grant,
        CancellationToken cancellationToken)
    {
        if (providerInstanceStableId == Guid.Empty)
        {
            throw new ArgumentException(
                "Provider Instance Stable ID must be non-empty.", nameof(providerInstanceStableId));
        }

        ArgumentNullException.ThrowIfNull(actor);
        privilegePolicy.RequireAdministrator(actor, PrivilegedActionKind.PublicSearchConsent);
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
                var consent = await dbContext.PublicSearchConsents
                    .SingleOrDefaultAsync(
                        candidate => candidate.ProviderInstanceId == instance.Id,
                        cancellationToken);
                if (consent is null)
                {
                    consent = new PublicSearchConsent
                    {
                        ProviderInstanceId = instance.Id,
                        CreatedUtc = occurredUtc
                    };
                    dbContext.PublicSearchConsents.Add(consent);
                }

                consent.IsActive = grant;
                consent.ActorTelegramId = actorTelegramId;
                if (grant)
                {
                    consent.OptedInUtc = occurredUtc;
                    consent.OptedOutUtc = null;
                }
                else
                {
                    consent.OptedOutUtc = occurredUtc;
                }
                consent.UpdatedUtc = occurredUtc;
            }

            dbContext.PrivilegedAuditRecords.Add(new PrivilegedAuditRecord
            {
                ActorTelegramId = actorTelegramId,
                Action = PrivilegedActionKind.PublicSearchConsent,
                TargetStableId = providerInstanceStableId,
                OccurredUtc = occurredUtc,
                Outcome = outcome,
                SanitizedReason = sanitizedReason
            });

            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return new PublicSearchConsentResult(
                instance is null
                    ? PublicSearchConsentStatus.NotFound
                    : PublicSearchConsentStatus.Succeeded,
                providerInstanceStableId,
                grant && instance is not null,
                actorTelegramId,
                occurredUtc);
        });
    }
}

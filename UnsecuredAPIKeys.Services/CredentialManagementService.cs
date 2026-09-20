using Microsoft.EntityFrameworkCore;
using UnsecuredAPIKeys.Data;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Data.Models;

namespace UnsecuredAPIKeys.Services;

public enum CredentialManagementStatus
{
    Succeeded,
    NotFound,
    Archived,
    DuplicateMaterial
}

public sealed record CredentialManagementResult(
    CredentialManagementStatus Status,
    Guid CredentialStableId,
    Guid? ReplacementStableId,
    DateTime? OccurredUtc);

public interface ICredentialManagementService
{
    Task<CredentialManagementResult> DisableAsync(
        Guid credentialStableId,
        SchedulerPrincipal actor,
        string reason,
        CancellationToken cancellationToken = default);

    Task<CredentialManagementResult> ReenableAsync(
        Guid credentialStableId,
        SchedulerPrincipal actor,
        string reason,
        CancellationToken cancellationToken = default);

    Task<CredentialManagementResult> ReplaceAsync(
        Guid credentialStableId,
        string replacementMaterial,
        SchedulerPrincipal actor,
        string reason,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The sole mutation boundary for administrator disable, re-enable, and replacement.
/// State, identity, grant transfer, and secret-free audit evidence commit together.
/// </summary>
public sealed class CredentialManagementService(
    DBContext dbContext,
    IPrivilegePolicy privilegePolicy,
    IAuditReasonSanitizer reasonSanitizer,
    IDatabaseUtcClock databaseUtcClock,
    CredentialStateService stateService,
    CredentialStorageService storageService,
    CredentialMutationGate mutationGate) : ICredentialManagementService
{
    private const string AdministratorDisabledReason = "AdministratorDisabled";
    private const string ReplacedReason = "Replaced";

    public Task<CredentialManagementResult> DisableAsync(
        Guid credentialStableId,
        SchedulerPrincipal actor,
        string reason,
        CancellationToken cancellationToken = default) =>
        mutationGate.ExecuteAsync(
            () => ChangeStateAsync(
                credentialStableId,
                actor,
                reason,
                PrivilegedActionKind.CredentialDisable,
                enable: false,
                cancellationToken),
            cancellationToken);

    public Task<CredentialManagementResult> ReenableAsync(
        Guid credentialStableId,
        SchedulerPrincipal actor,
        string reason,
        CancellationToken cancellationToken = default) =>
        mutationGate.ExecuteAsync(
            () => ChangeStateAsync(
                credentialStableId,
                actor,
                reason,
                PrivilegedActionKind.CredentialReenable,
                enable: true,
                cancellationToken),
            cancellationToken);

    public Task<CredentialManagementResult> ReplaceAsync(
        Guid credentialStableId,
        string replacementMaterial,
        SchedulerPrincipal actor,
        string reason,
        CancellationToken cancellationToken = default) =>
        mutationGate.ExecuteAsync(
            () => ReplaceUnderGateAsync(
                credentialStableId,
                replacementMaterial,
                actor,
                reason,
                cancellationToken),
            cancellationToken);

    private async Task<CredentialManagementResult> ReplaceUnderGateAsync(
        Guid credentialStableId,
        string replacementMaterial,
        SchedulerPrincipal actor,
        string reason,
        CancellationToken cancellationToken)
    {
        ValidateStableId(credentialStableId);
        ArgumentNullException.ThrowIfNull(actor);
        privilegePolicy.RequireAdministrator(actor, PrivilegedActionKind.CredentialReplace);
        var sanitizedReason = reasonSanitizer.Sanitize(reason);
        var executionStrategy = dbContext.Database.CreateExecutionStrategy();

        return await executionStrategy.ExecuteAsync(async () =>
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            var oldCredential = await dbContext.SearchProviderTokens
                .Include(credential => credential.ProviderInstance)
                .Include(credential => credential.CredentialGrants)
                .SingleOrDefaultAsync(
                    credential => credential.StableId == credentialStableId,
                    cancellationToken);

            if (oldCredential is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new CredentialManagementResult(
                    CredentialManagementStatus.NotFound,
                    credentialStableId,
                    null,
                    null);
            }

            if (oldCredential.IsArchived)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new CredentialManagementResult(
                    CredentialManagementStatus.Archived,
                    credentialStableId,
                    oldCredential.ReplacedByStableId,
                    null);
            }

            var occurredUtc = await GetCommandUtcAsync(
                oldCredential.CreatedUtc,
                cancellationToken);

            var providerInstance = oldCredential.ProviderInstance
                ?? throw new InvalidOperationException("Credential Provider Instance is unavailable.");
            var fingerprint = storageService.ComputeFingerprint(
                replacementMaterial,
                providerInstance.ProviderKind,
                providerInstance.StableId);
            var duplicateExists = await dbContext.SearchProviderTokens
                .AsNoTracking()
                .AnyAsync(
                    credential =>
                        !credential.IsArchived &&
                        credential.ProviderInstanceId == providerInstance.Id &&
                        credential.FingerprintKeyVersion == fingerprint.FingerprintKeyVersion &&
                        credential.Fingerprint == fingerprint.Fingerprint,
                    cancellationToken);
            if (duplicateExists)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new CredentialManagementResult(
                    CredentialManagementStatus.DuplicateMaterial,
                    credentialStableId,
                    null,
                    null);
            }

            var replacement = storageService.CreateProtectedCredential(
                replacementMaterial,
                providerInstance,
                CredentialSource.Manual,
                addedByTelegramId: actor.TelegramPrincipalId,
                nowUtc: occurredUtc);
            foreach (var grant in oldCredential.CredentialGrants)
            {
                replacement.CredentialGrants.Add(new CredentialGrant
                {
                    Scope = grant.Scope,
                    TelegramPrincipalId = grant.TelegramPrincipalId,
                    CreatedUtc = occurredUtc
                });
            }

            stateService.Disable(oldCredential, ReplacedReason, occurredUtc);
            oldCredential.IsArchived = true;
            oldCredential.ReplacedByStableId = replacement.StableId;
            oldCredential.UpdatedUtc = occurredUtc;

            dbContext.SearchProviderTokens.Add(replacement);
            AddAudit(
                actor.TelegramPrincipalId!.Value,
                PrivilegedActionKind.CredentialReplace,
                oldCredential.StableId,
                occurredUtc,
                sanitizedReason);
            AddAudit(
                actor.TelegramPrincipalId.Value,
                PrivilegedActionKind.CredentialReplace,
                replacement.StableId,
                occurredUtc,
                sanitizedReason);

            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return new CredentialManagementResult(
                CredentialManagementStatus.Succeeded,
                oldCredential.StableId,
                replacement.StableId,
                occurredUtc);
        });
    }

    private async Task<CredentialManagementResult> ChangeStateAsync(
        Guid credentialStableId,
        SchedulerPrincipal actor,
        string reason,
        PrivilegedActionKind action,
        bool enable,
        CancellationToken cancellationToken)
    {
        ValidateStableId(credentialStableId);
        ArgumentNullException.ThrowIfNull(actor);
        privilegePolicy.RequireAdministrator(actor, action);
        var sanitizedReason = reasonSanitizer.Sanitize(reason);
        var executionStrategy = dbContext.Database.CreateExecutionStrategy();

        return await executionStrategy.ExecuteAsync(async () =>
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            var credential = await dbContext.SearchProviderTokens.SingleOrDefaultAsync(
                candidate => candidate.StableId == credentialStableId,
                cancellationToken);

            if (credential is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new CredentialManagementResult(
                    CredentialManagementStatus.NotFound,
                    credentialStableId,
                    null,
                    null);
            }

            if (credential.IsArchived)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new CredentialManagementResult(
                    CredentialManagementStatus.Archived,
                    credentialStableId,
                    credential.ReplacedByStableId,
                    null);
            }

            var occurredUtc = await GetCommandUtcAsync(
                credential.CreatedUtc,
                cancellationToken);

            if (enable)
            {
                stateService.Enable(credential);
                credential.UpdatedUtc = occurredUtc;
            }
            else
            {
                stateService.Disable(credential, AdministratorDisabledReason, occurredUtc);
            }

            AddAudit(
                actor.TelegramPrincipalId!.Value,
                action,
                credential.StableId,
                occurredUtc,
                sanitizedReason);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return new CredentialManagementResult(
                CredentialManagementStatus.Succeeded,
                credential.StableId,
                null,
                occurredUtc);
        });
    }

    private async Task<DateTime> GetCommandUtcAsync(
        DateTime minimumUtc,
        CancellationToken cancellationToken)
    {
        var occurredUtc = await databaseUtcClock.GetUtcNowAsync(cancellationToken);
        if (occurredUtc >= minimumUtc)
        {
            return occurredUtc;
        }

        var clockGap = minimumUtc - occurredUtc;
        if (clockGap > TimeSpan.FromSeconds(1))
        {
            throw new InvalidOperationException(
                "Database UTC precedes persisted credential identity time beyond the precision tolerance.");
        }

        await Task.Delay(clockGap + TimeSpan.FromMilliseconds(1), cancellationToken);
        occurredUtc = await databaseUtcClock.GetUtcNowAsync(cancellationToken);
        if (occurredUtc < minimumUtc)
        {
            throw new InvalidOperationException(
                "Database UTC did not reach the persisted credential identity time.");
        }

        return occurredUtc;
    }

    private void AddAudit(
        long actorTelegramId,
        PrivilegedActionKind action,
        Guid targetStableId,
        DateTime occurredUtc,
        string sanitizedReason)
    {
        dbContext.PrivilegedAuditRecords.Add(new PrivilegedAuditRecord
        {
            ActorTelegramId = actorTelegramId,
            Action = action,
            TargetStableId = targetStableId,
            OccurredUtc = occurredUtc,
            Outcome = PrivilegedActionOutcome.Succeeded,
            SanitizedReason = sanitizedReason
        });
    }

    private static void ValidateStableId(Guid credentialStableId)
    {
        if (credentialStableId == Guid.Empty)
        {
            throw new ArgumentException("Credential Stable ID must be non-empty.", nameof(credentialStableId));
        }
    }
}

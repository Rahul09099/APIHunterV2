using Microsoft.EntityFrameworkCore;
using UnsecuredAPIKeys.Data;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Data.DTOs;

namespace UnsecuredAPIKeys.Services;

/// <summary>Per-discovery validation verdict. Rejection reasons are non-secret.</summary>
public sealed record DiscoveryReportItemResult(bool Accepted, string? RejectionReason = null);

/// <summary>Batch validation verdict for one Worker discovery report.</summary>
public sealed record DiscoveryReportValidationResult(
    IReadOnlyList<DiscoveryReportItemResult> Items,
    int AcceptedCount,
    int RejectedCount);

/// <summary>
/// Validates Worker discovery provenance before persistence (Wave 13, Task 13.3).
/// Every discovery must carry its immutable normalized identity: Provider Kind,
/// Provider Instance, and Claim. Work/Partition/Slot are validated when present.
/// Omitted, unknown, conflicting, or mismatched identity is rejected per item without
/// failing the whole batch. The validator performs no provider-specific branching:
/// identity is resolved through the same instance/work/claim/slot contracts for every kind.
/// </summary>
public sealed class WorkerDiscoveryReportValidator(DBContext dbContext)
{
    public async Task<DiscoveryReportValidationResult> ValidateAsync(
        SchedulerPrincipal principal,
        IReadOnlyList<NodeReportDto> discoveries,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(discoveries);

        var results = new List<DiscoveryReportItemResult>(discoveries.Count);
        foreach (var discovery in discoveries)
        {
            results.Add(await ValidateOneAsync(principal, discovery, cancellationToken));
        }

        return new DiscoveryReportValidationResult(
            results,
            results.Count(static result => result.Accepted),
            results.Count(static result => !result.Accepted));
    }

    private async Task<DiscoveryReportItemResult> ValidateOneAsync(
        SchedulerPrincipal principal,
        NodeReportDto discovery,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(discovery.ApiKey))
        {
            return new DiscoveryReportItemResult(false, "The discovery carries no finding.");
        }

        if (discovery.ProviderKind == SearchProviderEnum.Unknown)
        {
            return new DiscoveryReportItemResult(false, "The discovery omits its provider kind.");
        }

        var instance = await dbContext.SearchProviderInstances
            .AsNoTracking()
            .Where(candidate => candidate.StableId == discovery.ProviderInstanceStableId)
            .Select(candidate => new { candidate.Id, candidate.ProviderKind })
            .SingleOrDefaultAsync(cancellationToken);
        if (instance is null)
        {
            return new DiscoveryReportItemResult(false, "The provider instance is unknown.");
        }
        if (instance.ProviderKind != discovery.ProviderKind)
        {
            return new DiscoveryReportItemResult(
                false, "The provider kind conflicts with the authoritative instance kind.");
        }

        if (discovery.LeaseId is null || discovery.LeaseId == Guid.Empty)
        {
            return new DiscoveryReportItemResult(false, "The discovery omits its claim identity.");
        }

        var claim = await dbContext.CredentialClaimRecords
            .AsNoTracking()
            .Where(candidate => candidate.LeaseId == discovery.LeaseId)
            .SingleOrDefaultAsync(cancellationToken);
        if (claim is null)
        {
            return new DiscoveryReportItemResult(false, "The claim identity is unknown.");
        }
        if (claim.PrincipalScope != MapScope(principal) ||
            claim.PrincipalTelegramId != principal.TelegramPrincipalId ||
            claim.ProviderInstanceStableId != discovery.ProviderInstanceStableId)
        {
            return new DiscoveryReportItemResult(
                false, "The claim identity does not belong to the reporting principal or instance.");
        }

        if (discovery.WorkItemStableId.HasValue)
        {
            var workItemId = await dbContext.WorkItems
                .AsNoTracking()
                .Where(work =>
                    work.StableId == discovery.WorkItemStableId &&
                    work.ProviderInstanceId == instance.Id &&
                    work.ProviderKind == discovery.ProviderKind)
                .Select(work => (long?)work.Id)
                .SingleOrDefaultAsync(cancellationToken);
            if (workItemId is null || workItemId != claim.WorkItemId)
            {
                return new DiscoveryReportItemResult(
                    false, "The work identity conflicts with the durable claim.");
            }
        }

        if (discovery.WorkPartitionStableId.HasValue)
        {
            var partition = await dbContext.WorkPartitions
                .AsNoTracking()
                .Where(candidate => candidate.StableId == discovery.WorkPartitionStableId)
                .Select(candidate => new { candidate.WorkItemId })
                .SingleOrDefaultAsync(cancellationToken);
            if (partition is null || partition.WorkItemId != claim.WorkItemId)
            {
                return new DiscoveryReportItemResult(
                    false, "The partition identity does not belong to the claim work item.");
            }
        }
        else if (!string.IsNullOrEmpty(discovery.PartitionKey))
        {
            var partitionMatches = await dbContext.WorkPartitions
                .AsNoTracking()
                .AnyAsync(
                    candidate => candidate.WorkItemId == claim.WorkItemId &&
                                 candidate.PartitionKey == discovery.PartitionKey,
                    cancellationToken);
            if (!partitionMatches)
            {
                return new DiscoveryReportItemResult(
                    false, "The partition key does not belong to the claim work item.");
            }
        }

        if (discovery.OperationSlotId.HasValue && discovery.OperationSlotId != Guid.Empty)
        {
            var slotMatches = await dbContext.OperationSlots
                .AsNoTracking()
                .AnyAsync(
                    candidate => candidate.SlotId == discovery.OperationSlotId &&
                                 candidate.RequestId == claim.RequestId &&
                                 candidate.ProviderInstanceId == instance.Id,
                    cancellationToken);
            if (!slotMatches)
            {
                return new DiscoveryReportItemResult(
                    false, "The slot identity does not match the claim operation.");
            }
        }

        return new DiscoveryReportItemResult(true);
    }

    private static CredentialGrantScope MapScope(SchedulerPrincipal principal) =>
        principal.IsSystem || principal.IsAdministrator ? CredentialGrantScope.Admin : CredentialGrantScope.User;
}

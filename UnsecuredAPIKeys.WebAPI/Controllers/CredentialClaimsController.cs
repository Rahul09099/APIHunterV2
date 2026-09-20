using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;
using UnsecuredAPIKeys.Data;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Data.DTOs;
using UnsecuredAPIKeys.Data.Models;
using UnsecuredAPIKeys.Providers;
using UnsecuredAPIKeys.Providers._Interfaces;
using UnsecuredAPIKeys.Services;
using UnsecuredAPIKeys.WebAPI.Services;

namespace UnsecuredAPIKeys.WebAPI.Controllers;

/// <summary>
/// Worker credential Claim, renew, and complete APIs (Wave 12, Task 12.2).
/// Nodes authenticate with X-Node-Token; the Scheduler Principal is always resolved
/// server-side and never accepted from request bodies (AC-1.6). All durable state
/// transitions delegate to <see cref="ICredentialScheduler"/>; this controller performs
/// no provider-specific branching and never logs credential material, node tokens, or
/// request bodies. Claim issuance is one-secret/no-store: the material is returned once
/// and never re-issued on active or terminal replay.
/// </summary>
[ApiController]
[Route("api/v1/nodes/credential-claims")]
[ServiceFilter(typeof(SchedulingReadinessFilter))]
public class CredentialClaimsController : ControllerBase
{
    private const string NodeTokenHeader = "X-Node-Token";
    private const int MaxPartitionKeyLength = 256;
    private const double MaxRenewalExtensionSeconds = 86400;

    private readonly DBContext _dbContext;
    private readonly ICredentialScheduler _scheduler;
    private readonly CredentialMaterialAccessService _materialAccess;
    private readonly ICredentialGrantEvaluator _grantEvaluator;
    private readonly INodePrincipalResolver _principalResolver;
    private readonly LeasePolicyOptions _leasePolicy;
    private readonly IDatabaseUtcClock _clock;
    private readonly IWorkService _workService;
    private readonly SearchProviderAdapterRegistry _adapterRegistry;
    private readonly ILogger<CredentialClaimsController> _logger;

    public CredentialClaimsController(
        DBContext dbContext,
        ICredentialScheduler scheduler,
        CredentialMaterialAccessService materialAccess,
        ICredentialGrantEvaluator grantEvaluator,
        INodePrincipalResolver principalResolver,
        LeasePolicyOptions leasePolicy,
        IDatabaseUtcClock clock,
        IWorkService workService,
        SearchProviderAdapterRegistry adapterRegistry,
        ILogger<CredentialClaimsController> logger)
    {
        _dbContext = dbContext;
        _scheduler = scheduler;
        _materialAccess = materialAccess;
        _grantEvaluator = grantEvaluator;
        _principalResolver = principalResolver;
        _leasePolicy = leasePolicy;
        _clock = clock;
        _workService = workService;
        _adapterRegistry = adapterRegistry;
        _logger = logger;
    }

    /// <summary>Acquire one operation-scoped credential Claim for the calling node.</summary>
    [HttpPost]
    public async Task<IActionResult> Claim(
        [FromHeader(Name = NodeTokenHeader)] string? nodeToken,
        [FromBody] CredentialClaimRequest? request)
    {
        var auth = await ResolveNodePrincipalAsync(nodeToken);
        if (auth.Failure is not null) return auth.Failure;
        var (principal, nodeId, scope) = auth.Success!;

        if (request is null ||
            request.ProviderInstanceStableId == Guid.Empty ||
            request.WorkItemStableId == Guid.Empty ||
            request.RequestId == Guid.Empty ||
            string.IsNullOrWhiteSpace(request.PartitionKey) ||
            request.PartitionKey.Length > MaxPartitionKeyLength)
        {
            return BadRequest(new { message = "Claim requires instance, work item, partition, and request identifiers." });
        }

        var instance = await _dbContext.SearchProviderInstances
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.StableId == request.ProviderInstanceStableId,
                HttpContext.RequestAborted);
        if (instance is null || !instance.IsEnabled)
        {
            return BadRequest(new { message = "The provider instance is unknown or disabled." });
        }

        var authorized = await _grantEvaluator.ApplyAuthorization(
                _dbContext.SearchProviderTokens
                    .AsNoTracking()
                    .Where(credential =>
                        credential.ProviderInstanceId == instance.Id &&
                        credential.IsEnabled &&
                        !credential.IsArchived &&
                        credential.DisabledAtUtc == null),
                principal)
            .AnyAsync(HttpContext.RequestAborted);
        if (!authorized)
        {
            _logger.LogInformation(
                "Claim denied for principal {Scope}/{TelegramId} on instance {InstanceStableId}: no grant",
                scope, principal.TelegramPrincipalId, request.ProviderInstanceStableId);
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "The principal holds no grant for this provider instance." });
        }

        // Worker flow (Task 13.2): an empty Work Item ID asks the Master to create
        // durable Work server-side from the worker-supplied query snapshot. Otherwise
        // the referenced Work Item must already exist.
        var workStableId = request.WorkItemStableId;
        if (workStableId == Guid.Empty)
        {
            if (string.IsNullOrWhiteSpace(request.GenericQuery))
            {
                return BadRequest(new { message = "A claim without a work item requires a query snapshot." });
            }

            ISearchProviderAdapter workAdapter;
            try
            {
                workAdapter = _adapterRegistry.GetRequiredAdapter(instance.ProviderKind);
                AdapterKindGuard.RequireMatchingKind(workAdapter.ProviderKind, instance.ProviderKind);
            }
            catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException)
            {
                return BadRequest(new { message = error.Message });
            }

            WorkItemResult createdWork;
            try
            {
                createdWork = await _workService.CreateWorkItemAsync(
                    instance.Id,
                    instance.ProviderKind,
                    scope,
                    principal.TelegramPrincipalId,
                    new SearchQuerySnapshot(
                        request.GenericQuery, request.NativeOverride, request.SettingsJson),
                    workAdapter.AdapterVersion,
                    HttpContext.RequestAborted);
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException)
            {
                return BadRequest(new { message = error.Message });
            }

            workStableId = createdWork.WorkItemStableId;
        }
        else
        {
            var workItemId = await _dbContext.WorkItems
                .AsNoTracking()
                .Where(work => work.StableId == workStableId)
                .Select(work => (long?)work.Id)
                .SingleOrDefaultAsync(HttpContext.RequestAborted);
            if (workItemId is null)
            {
                return BadRequest(new { message = "The work item is unknown." });
            }
        }

        var now = await _clock.GetUtcNowAsync(HttpContext.RequestAborted);

        var activeReplay = await _dbContext.CredentialClaimRecords
            .AsNoTracking()
            .Where(record =>
                record.PrincipalScope == scope &&
                record.PrincipalTelegramId == principal.TelegramPrincipalId &&
                record.RequestId == request.RequestId &&
                !record.IsTerminal &&
                record.LeaseExpiresUtc > now)
            .OrderByDescending(record => record.Id)
            .FirstOrDefaultAsync(HttpContext.RequestAborted);
        if (activeReplay is not null)
        {
            _logger.LogInformation(
                "Claim active replay for lease {LeaseId} credential {CredentialStableId}",
                activeReplay.LeaseId, activeReplay.CredentialStableId);
            return Ok(await ToReplayResponseAsync(activeReplay, HttpContext.RequestAborted));
        }

        var terminalReplay = await _dbContext.CredentialClaimRecords
            .AsNoTracking()
            .Where(record =>
                record.PrincipalScope == scope &&
                record.PrincipalTelegramId == principal.TelegramPrincipalId &&
                record.RequestId == request.RequestId &&
                record.IsTerminal)
            .OrderByDescending(record => record.Id)
            .FirstOrDefaultAsync(HttpContext.RequestAborted);
        if (terminalReplay is not null)
        {
            _logger.LogInformation(
                "Claim terminal replay for lease {LeaseId} outcome {Outcome}",
                terminalReplay.LeaseId, terminalReplay.TerminalOutcome);
            return Ok(await ToReplayResponseAsync(terminalReplay, HttpContext.RequestAborted));
        }

        ClaimOutcome claim;
        try
        {
            claim = await _scheduler.TryClaimAsync(
                request.ProviderInstanceStableId,
                workStableId,
                request.PartitionKey,
                nodeId,
                request.RequestId,
                scope,
                principal.TelegramPrincipalId,
                HttpContext.RequestAborted);
        }
        catch (InvalidOperationException error)
        {
            return BadRequest(new { message = error.Message });
        }

        if (!claim.IsSuccess || claim.Success is null)
        {
            return ConflictWithRetry(
                claim.Unavailable?.Reason ?? "No credential claim is available.",
                claim.Unavailable?.RetryAfter);
        }

        // The Operation Slot is part of the grant: workers present it on every adapter
        // invocation, so a claim without a recorded slot fails instead of issuing material.
        var slotId = await _dbContext.OperationSlots
            .AsNoTracking()
            .Where(slot => slot.RequestId == request.RequestId && !slot.IsTerminal)
            .OrderByDescending(slot => slot.Id)
            .Select(slot => (Guid?)slot.SlotId)
            .FirstOrDefaultAsync(HttpContext.RequestAborted);
        if (slotId is null || slotId == Guid.Empty)
        {
            try
            {
                await _scheduler.CompleteAsync(
                    claim.Success.LeaseId,
                    claim.Success.CredentialStableId,
                    claim.Success.CredentialRevision,
                    ProviderOutcomeKind.Transient,
                    HttpContext.RequestAborted);
            }
            catch (Exception completionError)
            {
                _logger.LogWarning(
                    completionError,
                    "Claim cleanup completion failed for lease {LeaseId}",
                    claim.Success.LeaseId);
            }

            return ConflictWithRetry("The claim recorded no operation slot.", null);
        }

        string material;
        try
        {
            var grant = await _materialAccess.GrantStoredCredentialAfterCommitAsync(
                claim.Success.CredentialStableId,
                Guid.NewGuid(),
                HttpContext.RequestAborted);
            using var decrypted = await _materialAccess.DecryptGrantedAsync(
                grant,
                HttpContext.RequestAborted);
            material = decrypted.Value;
        }
        catch (Exception error) when (
            error is CredentialProtectionException or InvalidOperationException)
        {
            try
            {
                await _scheduler.CompleteAsync(
                    claim.Success.LeaseId,
                    claim.Success.CredentialStableId,
                    claim.Success.CredentialRevision,
                    ProviderOutcomeKind.Transient,
                    HttpContext.RequestAborted);
            }
            catch (Exception completionError)
            {
                _logger.LogWarning(
                    completionError,
                    "Claim cleanup completion failed for lease {LeaseId}",
                    claim.Success.LeaseId);
            }

            return ConflictWithRetry("The claimed credential is unavailable; retry with a new request.", null);
        }

        _logger.LogInformation(
            "Claim succeeded for lease {LeaseId} credential {CredentialStableId} instance {InstanceStableId} principal {Scope}/{TelegramId}",
            claim.Success.LeaseId, claim.Success.CredentialStableId,
            claim.Success.ProviderInstanceStableId, scope, principal.TelegramPrincipalId);

        return Ok(new CredentialClaimResponse(
            claim.Success.LeaseId,
            claim.Success.CredentialStableId,
            claim.Success.ProviderInstanceStableId,
            claim.Success.LeaseExpiresUtc,
            claim.Success.CredentialRevision,
            claim.Success.RenewalThreshold.TotalSeconds,
            material,
            Replayed: false,
            OperationSlotId: slotId.Value,
            WorkItemStableId: workStableId,
            PartitionKey: request.PartitionKey));
    }

    /// <summary>Extend an active node-owned Lease within policy caps.</summary>
    [HttpPost("{leaseId:guid}/renew")]
    public async Task<IActionResult> Renew(
        [FromHeader(Name = NodeTokenHeader)] string? nodeToken,
        [FromRoute] Guid leaseId,
        [FromBody] CredentialLeaseRenewRequest? request)
    {
        var auth = await ResolveNodePrincipalAsync(nodeToken);
        if (auth.Failure is not null) return auth.Failure;
        var (principal, nodeId, scope) = auth.Success!;

        if (request is null ||
            request.CredentialStableId == Guid.Empty ||
            request.WorkItemStableId == Guid.Empty ||
            request.ProviderInstanceStableId == Guid.Empty ||
            string.IsNullOrWhiteSpace(request.PartitionKey) ||
            request.PartitionKey.Length > MaxPartitionKeyLength ||
            double.IsNaN(request.RequestedExtensionSeconds) ||
            request.RequestedExtensionSeconds <= 0 ||
            request.RequestedExtensionSeconds > MaxRenewalExtensionSeconds)
        {
            return BadRequest(new { message = "Renewal requires credential, revision, work, instance, partition, and a positive extension." });
        }

        var record = await _dbContext.CredentialClaimRecords
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.LeaseId == leaseId,
                HttpContext.RequestAborted);
        if (record is null)
        {
            return StatusCode(StatusCodes.Status409Conflict, new CredentialLeaseRenewResponse(
                false, null, "The lease is unknown or expired."));
        }

        var ownership = CheckOwnership(record, principal, nodeId, scope);
        if (ownership is not null) return ownership;
        if (record.IsTerminal)
        {
            return StatusCode(StatusCodes.Status409Conflict, new CredentialLeaseRenewResponse(
                false, null, $"The lease is terminal ({record.TerminalOutcome})."));
        }

        var identityFailure = await CheckOperationIdentityAsync(record, request, HttpContext.RequestAborted);
        if (identityFailure is not null)
        {
            return StatusCode(StatusCodes.Status409Conflict, new CredentialLeaseRenewResponse(
                false, null, identityFailure));
        }

        var renewal = await _scheduler.TryRenewAsync(
            leaseId,
            request.CredentialStableId,
            request.ExpectedRevision,
            TimeSpan.FromSeconds(request.RequestedExtensionSeconds),
            HttpContext.RequestAborted);

        _logger.LogInformation(
            "Renew {Outcome} for lease {LeaseId} credential {CredentialStableId}",
            renewal.Succeeded ? "succeeded" : "failed", leaseId, request.CredentialStableId);

        if (!renewal.Succeeded)
        {
            return StatusCode(StatusCodes.Status409Conflict, new CredentialLeaseRenewResponse(
                false, null, renewal.FailureReason ?? "The lease cannot be renewed."));
        }

        var currentRevision = await _dbContext.SearchProviderTokens
            .AsNoTracking()
            .Where(credential => credential.StableId == request.CredentialStableId)
            .Select(credential => (long?)credential.Revision)
            .SingleOrDefaultAsync(HttpContext.RequestAborted);

        return Ok(new CredentialLeaseRenewResponse(
            true, renewal.NewLeaseExpiresUtc, null, currentRevision));
    }

    /// <summary>Complete a node-owned Lease with a typed provider outcome.</summary>
    [HttpPost("{leaseId:guid}/complete")]
    public async Task<IActionResult> Complete(
        [FromHeader(Name = NodeTokenHeader)] string? nodeToken,
        [FromRoute] Guid leaseId,
        [FromBody] CredentialLeaseCompleteRequest? request)
    {
        var auth = await ResolveNodePrincipalAsync(nodeToken);
        if (auth.Failure is not null) return auth.Failure;
        var (principal, nodeId, scope) = auth.Success!;

        if (request is null ||
            request.CredentialStableId == Guid.Empty ||
            !Enum.TryParse<ProviderOutcomeKind>(request.Outcome, ignoreCase: true, out var outcome) ||
            !Enum.IsDefined(outcome))
        {
            return BadRequest(new { message = "Completion requires credential, revision, and a known typed outcome." });
        }

        var record = await _dbContext.CredentialClaimRecords
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.LeaseId == leaseId,
                HttpContext.RequestAborted);
        if (record is null)
        {
            return StatusCode(StatusCodes.Status409Conflict, new CredentialLeaseCompleteResponse(
                false, "The lease is unknown or expired."));
        }

        var ownership = CheckOwnership(record, principal, nodeId, scope);
        if (ownership is not null) return ownership;
        if (record.IsTerminal)
        {
            return Ok(new CredentialLeaseCompleteResponse(true, null));
        }

        if (request.WorkItemStableId.HasValue ||
            request.ProviderInstanceStableId.HasValue ||
            request.PartitionKey is not null)
        {
            var identityFailure = await CheckOptionalOperationIdentityAsync(
                record, request, HttpContext.RequestAborted);
            if (identityFailure is not null)
            {
                return StatusCode(StatusCodes.Status409Conflict, new CredentialLeaseCompleteResponse(
                    false, identityFailure));
            }
        }

        var completion = await _scheduler.CompleteAsync(
            leaseId,
            request.CredentialStableId,
            request.ExpectedRevision,
            outcome,
            HttpContext.RequestAborted);

        _logger.LogInformation(
            "Complete {Outcome} for lease {LeaseId} credential {CredentialStableId} with provider outcome {ProviderOutcome}",
            completion.Succeeded ? "succeeded" : "failed", leaseId, request.CredentialStableId, outcome);

        return completion.Succeeded
            ? Ok(new CredentialLeaseCompleteResponse(true, null))
            : StatusCode(StatusCodes.Status409Conflict, new CredentialLeaseCompleteResponse(
                false, completion.FailureReason ?? "The lease cannot be completed."));
    }

    private async Task<(NodeAuthSuccess? Success, IActionResult? Failure)> ResolveNodePrincipalAsync(
        string? nodeToken)
    {
        if (string.IsNullOrWhiteSpace(nodeToken))
        {
            return (null, Unauthorized(new { message = "Missing node token." }));
        }

        var resolution = await _principalResolver.ResolveNodeAsync(nodeToken, HttpContext.RequestAborted);
        if (!resolution.IsAuthenticated)
        {
            return (null, Unauthorized(new { message = "Invalid node token." }));
        }
        if (!resolution.IsResolved || resolution.Principal is null || resolution.Principal.TelegramPrincipalId is not > 0)
        {
            return (null, StatusCode(
                StatusCodes.Status403Forbidden,
                new { message = "The node has no registered Telegram principal mapping." }));
        }

        var principal = resolution.Principal;
        var scope = principal.IsAdministrator ? CredentialGrantScope.Admin : CredentialGrantScope.User;
        return (new NodeAuthSuccess(principal, $"node:{principal.TelegramPrincipalId!.Value}", scope), null);
    }

    private sealed record NodeAuthSuccess(SchedulerPrincipal Principal, string NodeId, CredentialGrantScope Scope);

    private IActionResult? CheckOwnership(
        CredentialClaimRecord record,
        SchedulerPrincipal principal,
        string nodeId,
        CredentialGrantScope scope)
    {
        if (!string.Equals(record.LeaseOwnerNodeId, nodeId, StringComparison.Ordinal) ||
            record.PrincipalScope != scope ||
            record.PrincipalTelegramId != principal.TelegramPrincipalId)
        {
            _logger.LogInformation(
                "Lease {LeaseId} ownership rejected for principal {Scope}/{TelegramId}",
                record.LeaseId, scope, principal.TelegramPrincipalId);
            return StatusCode(
                StatusCodes.Status403Forbidden,
                new { message = "The lease is owned by a different node." });
        }

        return null;
    }

    private async Task<string?> CheckOperationIdentityAsync(
        CredentialClaimRecord record,
        CredentialLeaseRenewRequest request,
        CancellationToken cancellationToken)
    {
        if (record.CredentialStableId != request.CredentialStableId ||
            record.ProviderInstanceStableId != request.ProviderInstanceStableId)
        {
            return "The renewal identity does not match the durable claim.";
        }

        var workItemId = await _dbContext.WorkItems
            .AsNoTracking()
            .Where(work => work.StableId == request.WorkItemStableId)
            .Select(work => (long?)work.Id)
            .SingleOrDefaultAsync(cancellationToken);
        if (workItemId is null || workItemId != record.WorkItemId)
        {
            return "The renewal identity does not match the durable claim.";
        }

        var slot = await _dbContext.OperationSlots
            .AsNoTracking()
            .Where(candidate => candidate.RequestId == record.RequestId && !candidate.IsTerminal)
            .SingleOrDefaultAsync(cancellationToken);
        if (slot is null || slot.PartitionKey != request.PartitionKey)
        {
            return "The operation slot is not active for the requested partition.";
        }

        return null;
    }

    private async Task<string?> CheckOptionalOperationIdentityAsync(
        CredentialClaimRecord record,
        CredentialLeaseCompleteRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ProviderInstanceStableId.HasValue &&
            request.ProviderInstanceStableId != Guid.Empty &&
            record.ProviderInstanceStableId != request.ProviderInstanceStableId)
        {
            return "The completion identity does not match the durable claim.";
        }

        if (request.WorkItemStableId.HasValue && request.WorkItemStableId != Guid.Empty)
        {
            var workItemId = await _dbContext.WorkItems
                .AsNoTracking()
                .Where(work => work.StableId == request.WorkItemStableId)
                .Select(work => (long?)work.Id)
                .SingleOrDefaultAsync(cancellationToken);
            if (workItemId is null || workItemId != record.WorkItemId)
            {
                return "The completion identity does not match the durable claim.";
            }
        }

        if (request.PartitionKey is not null)
        {
            var slot = await _dbContext.OperationSlots
                .AsNoTracking()
                .Where(candidate => candidate.RequestId == record.RequestId && !candidate.IsTerminal)
                .SingleOrDefaultAsync(cancellationToken);
            if (slot is null || slot.PartitionKey != request.PartitionKey)
            {
                return "The operation slot is not active for the requested partition.";
            }
        }

        if (record.CredentialStableId != request.CredentialStableId)
        {
            return "The completion identity does not match the durable claim.";
        }

        return null;
    }

    private async Task<CredentialClaimResponse> ToReplayResponseAsync(
        CredentialClaimRecord record,
        CancellationToken cancellationToken)
    {
        var slot = await _dbContext.OperationSlots
            .AsNoTracking()
            .Where(candidate => candidate.RequestId == record.RequestId)
            .OrderByDescending(candidate => candidate.Id)
            .Select(candidate => new { candidate.SlotId, candidate.PartitionKey })
            .FirstOrDefaultAsync(cancellationToken);
        var workStableId = await _dbContext.WorkItems
            .AsNoTracking()
            .Where(work => work.Id == record.WorkItemId)
            .Select(work => (Guid?)work.StableId)
            .SingleOrDefaultAsync(cancellationToken);

        return new CredentialClaimResponse(
            record.LeaseId,
            record.CredentialStableId,
            record.ProviderInstanceStableId,
            record.LeaseExpiresUtc,
            record.CredentialRevision,
            RenewalThreshold.TotalSeconds,
            CredentialMaterial: null,
            Replayed: true,
            OperationSlotId: slot?.SlotId ?? Guid.Empty,
            WorkItemStableId: workStableId,
            PartitionKey: slot?.PartitionKey);
    }

    private TimeSpan RenewalThreshold => TimeSpan.FromTicks(
        (long)(_leasePolicy.DefaultLeaseDuration.Ticks * _leasePolicy.RenewalThresholdFraction));

    private IActionResult ConflictWithRetry(string message, TimeSpan? retryAfter)
    {
        if (retryAfter.HasValue && retryAfter.Value > TimeSpan.Zero)
        {
            Response.Headers.Append(
                HeaderNames.RetryAfter,
                ((long)Math.Ceiling(retryAfter.Value.TotalSeconds)).ToString());
            return StatusCode(StatusCodes.Status409Conflict, new CredentialClaimConflict(
                message, retryAfter.Value.TotalSeconds));
        }

        return StatusCode(StatusCodes.Status409Conflict, new CredentialClaimConflict(message));
    }
}

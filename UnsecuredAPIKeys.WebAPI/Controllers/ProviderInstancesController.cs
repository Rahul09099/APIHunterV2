using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Services;

namespace UnsecuredAPIKeys.WebAPI.Controllers;

[ApiController]
[Route("api/provider-instances")]
public sealed class ProviderInstancesController(
    INodePrincipalResolver principalResolver,
    IPrivilegePolicy privilegePolicy,
    IProviderInstanceCommandService commandService,
    ProviderInstanceCatalogService catalogService,
    IPublicSearchConsentService consentService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ProviderInstanceAdapterProjection>>> GetAll(
        CancellationToken cancellationToken)
    {
        var projection = await catalogService.GetAllAsync(cancellationToken);
        return Ok(projection);
    }

    [HttpPost("{stableId:guid}/approval")]
    public async Task<IActionResult> Approve(
        Guid stableId,
        [FromBody] ProviderInstanceApprovalRequest request,
        [FromHeader(Name = "X-Node-Token")] string? nodeToken,
        CancellationToken cancellationToken)
    {
        var resolution = await principalResolver.ResolveNodeAsync(
            nodeToken ?? string.Empty,
            cancellationToken);
        var authorization = privilegePolicy.Authorize(
            resolution,
            PrivilegedActionKind.ProviderInstanceApprove);

        if (authorization == PrivilegeAuthorizationDecision.Unauthenticated)
        {
            return Unauthorized(new { message = "Authentication required" });
        }

        if (authorization != PrivilegeAuthorizationDecision.Authorized || resolution.Principal is null)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Administrator access required" });
        }

        ProviderInstanceApprovalResult result;
        try
        {
            result = await commandService.ApproveAsync(
                stableId,
                resolution.Principal,
                request.Reason,
                cancellationToken);
        }
        catch (Exception error) when (error is ArgumentException or EndpointPolicyException)
        {
            return BadRequest(new { message = "A valid, policy-compliant Provider Instance and non-empty reason are required" });
        }

        if (result.Status == ProviderInstanceApprovalStatus.NotFound)
        {
            return NotFound(new { message = "Provider Instance not found" });
        }

        return Ok(new
        {
            providerInstanceStableId = result.ProviderInstanceStableId,
            approvedByTelegramId = result.ActorTelegramId,
            approvedAtUtc = result.OccurredUtc
        });
    }

    [HttpPut("{stableId:guid}/endpoint")]
    public async Task<IActionResult> UpdateEndpoint(
        Guid stableId,
        [FromBody] ProviderInstanceEndpointRequest request,
        [FromHeader(Name = "X-Node-Token")] string? nodeToken,
        CancellationToken cancellationToken)
    {
        var authorization = await ResolveAdministratorAsync(
            nodeToken,
            PrivilegedActionKind.ProviderInstanceApprove,
            cancellationToken);
        if (authorization.Failure is not null)
        {
            return authorization.Failure;
        }

        try
        {
            var result = await commandService.UpdateEndpointAsync(
                stableId,
                request.Endpoint,
                authorization.Principal!,
                request.Reason,
                cancellationToken);
            return CommandResult(result);
        }
        catch (Exception error) when (error is ArgumentException or EndpointPolicyException)
        {
            return BadRequest(new { message = "Endpoint or reason is invalid" });
        }
    }

    [HttpPut("{stableId:guid}/development-http-exception")]
    public async Task<IActionResult> SetDevelopmentHttpException(
        Guid stableId,
        [FromBody] ProviderInstanceDevelopmentHttpRequest request,
        [FromHeader(Name = "X-Node-Token")] string? nodeToken,
        CancellationToken cancellationToken)
    {
        var authorization = await ResolveAdministratorAsync(
            nodeToken,
            PrivilegedActionKind.ProviderInstanceApprove,
            cancellationToken);
        if (authorization.Failure is not null)
        {
            return authorization.Failure;
        }

        try
        {
            return CommandResult(await commandService.SetDevelopmentHttpExceptionAsync(
                stableId,
                request.Allowed,
                authorization.Principal!,
                request.Reason,
                cancellationToken));
        }
        catch (ArgumentException)
        {
            return BadRequest(new { message = "A non-empty reason is required" });
        }
    }

    [HttpPut("{stableId:guid}/private-network-allowlist")]
    public async Task<IActionResult> ReplacePrivateNetworkAllowlist(
        Guid stableId,
        [FromBody] ProviderInstancePrivateAllowlistRequest request,
        [FromHeader(Name = "X-Node-Token")] string? nodeToken,
        CancellationToken cancellationToken)
    {
        var authorization = await ResolveAdministratorAsync(
            nodeToken,
            PrivilegedActionKind.PrivateNetworkAllowlist,
            cancellationToken);
        if (authorization.Failure is not null)
        {
            return authorization.Failure;
        }

        try
        {
            return CommandResult(await commandService.ReplacePrivateNetworkAllowlistAsync(
                stableId,
                request.Networks,
                authorization.Principal!,
                request.Reason,
                cancellationToken));
        }
        catch (ArgumentException)
        {
            return BadRequest(new { message = "Only valid private CIDRs and a non-empty reason are accepted" });
        }
    }

    [HttpPost("{stableId:guid}/public-search-consent")]
    public async Task<IActionResult> GrantPublicSearchConsent(
        Guid stableId,
        [FromBody] ProviderInstanceConsentRequest request,
        [FromHeader(Name = "X-Node-Token")] string? nodeToken,
        CancellationToken cancellationToken)
    {
        var authorization = await ResolveAdministratorAsync(
            nodeToken,
            PrivilegedActionKind.PublicSearchConsent,
            cancellationToken);
        if (authorization.Failure is not null)
        {
            return authorization.Failure;
        }

        try
        {
            return ConsentResult(await consentService.GrantAsync(
                stableId,
                authorization.Principal!,
                request.Reason,
                cancellationToken));
        }
        catch (ArgumentException)
        {
            return BadRequest(new { message = "A non-empty reason is required" });
        }
    }

    [HttpDelete("{stableId:guid}/public-search-consent")]
    public async Task<IActionResult> RevokePublicSearchConsent(
        Guid stableId,
        [FromBody] ProviderInstanceConsentRequest request,
        [FromHeader(Name = "X-Node-Token")] string? nodeToken,
        CancellationToken cancellationToken)
    {
        var authorization = await ResolveAdministratorAsync(
            nodeToken,
            PrivilegedActionKind.PublicSearchConsent,
            cancellationToken);
        if (authorization.Failure is not null)
        {
            return authorization.Failure;
        }

        try
        {
            return ConsentResult(await consentService.RevokeAsync(
                stableId,
                authorization.Principal!,
                request.Reason,
                cancellationToken));
        }
        catch (ArgumentException)
        {
            return BadRequest(new { message = "A non-empty reason is required" });
        }
    }

    private IActionResult ConsentResult(PublicSearchConsentResult result) =>
        result.Status == PublicSearchConsentStatus.NotFound
            ? NotFound(new { message = "Provider Instance not found" })
            : Ok(new
            {
                providerInstanceStableId = result.ProviderInstanceStableId,
                isActive = result.IsActive,
                actorTelegramId = result.ActorTelegramId,
                occurredUtc = result.OccurredUtc
            });

    private async Task<(SchedulerPrincipal? Principal, IActionResult? Failure)> ResolveAdministratorAsync(
        string? nodeToken,
        PrivilegedActionKind action,
        CancellationToken cancellationToken)
    {
        var resolution = await principalResolver.ResolveNodeAsync(nodeToken ?? string.Empty, cancellationToken);
        var decision = privilegePolicy.Authorize(resolution, action);
        if (decision == PrivilegeAuthorizationDecision.Unauthenticated)
        {
            return (null, Unauthorized(new { message = "Authentication required" }));
        }
        if (decision != PrivilegeAuthorizationDecision.Authorized || resolution.Principal is null)
        {
            return (null, StatusCode(StatusCodes.Status403Forbidden, new { message = "Administrator access required" }));
        }
        return (resolution.Principal, null);
    }

    private IActionResult CommandResult(ProviderInstanceApprovalResult result) =>
        result.Status == ProviderInstanceApprovalStatus.NotFound
            ? NotFound(new { message = "Provider Instance not found" })
            : Ok(new
            {
                providerInstanceStableId = result.ProviderInstanceStableId,
                actorTelegramId = result.ActorTelegramId,
                occurredUtc = result.OccurredUtc
            });
}

public sealed class ProviderInstanceApprovalRequest
{
    [Required]
    [StringLength(2048, MinimumLength = 1)]
    public string Reason { get; set; } = string.Empty;
}

public sealed class ProviderInstanceEndpointRequest
{
    [Required]
    public string Endpoint { get; set; } = string.Empty;

    [Required]
    [StringLength(2048, MinimumLength = 1)]
    public string Reason { get; set; } = string.Empty;
}

public sealed class ProviderInstanceDevelopmentHttpRequest
{
    public bool Allowed { get; set; }

    [Required]
    [StringLength(2048, MinimumLength = 1)]
    public string Reason { get; set; } = string.Empty;
}

public sealed class ProviderInstancePrivateAllowlistRequest
{
    public IReadOnlyCollection<string> Networks { get; set; } = [];

    [Required]
    [StringLength(2048, MinimumLength = 1)]
    public string Reason { get; set; } = string.Empty;
}

public sealed class ProviderInstanceConsentRequest
{
    [Required]
    [StringLength(2048, MinimumLength = 1)]
    public string Reason { get; set; } = string.Empty;
}

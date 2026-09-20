using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using UnsecuredAPIKeys.Data;
using UnsecuredAPIKeys.Services;
using UnsecuredAPIKeys.WebAPI.Services;

namespace UnsecuredAPIKeys.WebAPI.Controllers;

/// <summary>
/// Administrator credential-health, provider-health, platform-metrics, and retention
/// surfaces (Wave 15, Task 15.2). All views route through platform projection services
/// with masked aliases and stable references only — never credential material,
/// fingerprints, or node tokens. Non-administrators receive 401 on every route;
/// principal filtering itself is enforced and tested at the service layer.
/// </summary>
[ApiController]
[Route("api/credential-health")]
public sealed class CredentialHealthController(
    DBContext dbContext,
    DashboardAccessService accessService,
    CredentialHealthProjectionService healthService,
    SearchPlatformMetrics platformMetrics,
    PlatformRetentionService retentionService) : ControllerBase
{
    private async Task<bool> IsAdministratorAsync(
        string? nodeToken,
        string? accessToken,
        CancellationToken cancellationToken)
    {
        if (accessService.TryGetSession(accessToken, out var session) && session is not null)
        {
            return session.Role == DashboardAccessRole.Admin;
        }

        if (string.IsNullOrEmpty(nodeToken))
        {
            return false;
        }

        return await dbContext.TelegramSubscribers
            .AsNoTracking()
            .AnyAsync(
                subscriber => subscriber.NodeToken == nodeToken && subscriber.IsAdmin,
                cancellationToken);
    }

    /// <summary>Fleet-wide credential health with masked aliases and admin-only Lease owners.</summary>
    [HttpGet("credentials")]
    public async Task<IActionResult> GetCredentials(
        [FromHeader(Name = "X-Node-Token")] string? nodeToken,
        [FromHeader(Name = "X-Access-Token")] string? accessToken,
        CancellationToken cancellationToken)
    {
        if (!await IsAdministratorAsync(nodeToken, accessToken, cancellationToken))
        {
            return Unauthorized(new { message = "Admin access required" });
        }

        return Ok(await healthService.GetCredentialsAsync(
            isAdmin: true, telegramPrincipalId: null, cancellationToken));
    }

    /// <summary>Aggregate per-instance health with masked aliases and admin-only Lease owners.</summary>
    [HttpGet("providers")]
    public async Task<IActionResult> GetProviders(
        [FromHeader(Name = "X-Node-Token")] string? nodeToken,
        [FromHeader(Name = "X-Access-Token")] string? accessToken,
        CancellationToken cancellationToken)
    {
        if (!await IsAdministratorAsync(nodeToken, accessToken, cancellationToken))
        {
            return Unauthorized(new { message = "Admin access required" });
        }

        return Ok(await healthService.GetProvidersAsync(
            isAdmin: true, telegramPrincipalId: null, cancellationToken));
    }

    /// <summary>Low-cardinality platform instruments snapshot (counters, gauges, histograms).</summary>
    [HttpGet("metrics")]
    public async Task<IActionResult> GetMetrics(
        [FromHeader(Name = "X-Node-Token")] string? nodeToken,
        [FromHeader(Name = "X-Access-Token")] string? accessToken,
        CancellationToken cancellationToken)
    {
        if (!await IsAdministratorAsync(nodeToken, accessToken, cancellationToken))
        {
            return Unauthorized(new { message = "Admin access required" });
        }

        return Ok(platformMetrics.Snapshot());
    }

    /// <summary>Run bounded audit/Claim retention explicitly (also runs on schedule).</summary>
    [HttpPost("retention/purge")]
    public async Task<IActionResult> PurgeRetention(
        [FromHeader(Name = "X-Node-Token")] string? nodeToken,
        [FromHeader(Name = "X-Access-Token")] string? accessToken,
        CancellationToken cancellationToken)
    {
        if (!await IsAdministratorAsync(nodeToken, accessToken, cancellationToken))
        {
            return Unauthorized(new { message = "Admin access required" });
        }

        return Ok(await retentionService.PurgeAsync(cancellationToken));
    }
}

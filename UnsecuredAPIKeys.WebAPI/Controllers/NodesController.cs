using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using UnsecuredAPIKeys.Data;
using UnsecuredAPIKeys.Data.DTOs;
using UnsecuredAPIKeys.Data.Models;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Services;
using UnsecuredAPIKeys.WebAPI.Services;

namespace UnsecuredAPIKeys.WebAPI.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class NodesController : ControllerBase
{
    private readonly DBContext _dbContext;
    private readonly ILogger<NodesController> _logger;
    private readonly DashboardAccessService _accessService;
    private readonly INodePrincipalResolver _principalResolver;
    private readonly WorkerDiscoveryReportValidator _reportValidator;

    public NodesController(
        DBContext dbContext,
        ILogger<NodesController> logger,
        DashboardAccessService accessService,
        INodePrincipalResolver principalResolver,
        WorkerDiscoveryReportValidator reportValidator)
    {
        _dbContext = dbContext;
        _logger = logger;
        _accessService = accessService;
        _principalResolver = principalResolver;
        _reportValidator = reportValidator;
    }

    /// <summary>
    /// Worker heartbeat to report status and availability.
    /// </summary>
    [HttpPost("heartbeat")]
    public async Task<IActionResult> Heartbeat([FromHeader(Name = "X-Node-Token")] string nodeToken, [FromQuery] string? nodeUrl = null)
    {
        if (string.IsNullOrEmpty(nodeToken)) return Unauthorized("Missing Node Token");

        var node = await _dbContext.TelegramSubscribers
            .FirstOrDefaultAsync(s => s.NodeToken == nodeToken);

        if (node == null) return Unauthorized("Invalid Node Token");

        node.LastNodeHeartbeatUtc = DateTime.UtcNow;
        
        // Update NodeUrl if provided (helps Master know where to ping)
        if (!string.IsNullOrEmpty(nodeUrl))
        {
            node.NodeUrl = nodeUrl;
        }

        await _dbContext.SaveChangesAsync();

        return Ok(new { status = "success", timestamp = DateTime.UtcNow });
    }

    /// <summary>
    /// Sync tokens and queries for the specific node.
    /// Queries are partitioned across active nodes so each node scrapes a unique subset,
    /// preventing duplicate work and maximising GitHub API quota usage.
    /// </summary>
    [HttpGet("sync")]
    public async Task<IActionResult> Sync([FromHeader(Name = "X-Node-Token")] string nodeToken)
    {
        if (string.IsNullOrEmpty(nodeToken)) return Unauthorized("Missing Node Token");

        var resolution = await _principalResolver.ResolveNodeAsync(nodeToken, HttpContext.RequestAborted);
        if (!resolution.IsAuthenticated) return Unauthorized("Invalid Node Token");
        if (!resolution.IsResolved)
        {
            return StatusCode(StatusCodes.Status403Forbidden, "Node has no registered Telegram principal mapping");
        }

        var principal = resolution.Principal!;

        // Credential pools are deliberately absent from synchronization. Workers obtain
        // one operation-scoped claim immediately before a credentialed provider request.

        // ── Query Partitioning ────────────────────────────────────────────────
        // Determine how many nodes are currently active (heartbeat within last 10 min).
        // Assign each node a stable index based on its TelegramId sort order so the
        // partition is deterministic and doesn't change on every sync call.
        var activeThreshold = DateTime.UtcNow.AddMinutes(-10);
        var activeNodeIds = await _dbContext.TelegramSubscribers
            .Where(s => s.NodeToken != null && s.LastNodeHeartbeatUtc > activeThreshold)
            .OrderBy(s => s.TelegramId)   // stable ordering
            .Select(s => s.TelegramId)
            .ToListAsync();

        var allQueries = await _dbContext.SearchQueries
            .Where(q => q.IsEnabled)
            .OrderBy(q => q.Id)           // stable ordering for partitioning
            .ToListAsync();

        List<SearchQuery> assignedQueries;

        int totalNodes = activeNodeIds.Count;
        var telegramPrincipalId = principal.TelegramPrincipalId!.Value;
        int nodeIndex  = activeNodeIds.IndexOf(telegramPrincipalId);

        if (totalNodes <= 1 || nodeIndex < 0)
        {
            // Only one node active (or this node just came online) — give it everything
            assignedQueries = allQueries;
        }
        else
        {
            // Round-robin partition: node i gets queries where (query_index % totalNodes == nodeIndex)
            assignedQueries = allQueries
                .Select((q, i) => (q, i))
                .Where(x => x.i % totalNodes == nodeIndex)
                .Select(x => x.q)
                .ToList();
        }

        // Enabled provider instances as stable references (Task 13.2). Workers claim
        // against these descriptors; sync never transports credential material.
        var descriptors = await _dbContext.SearchProviderInstances
            .AsNoTracking()
            .Where(instance => instance.IsEnabled)
            .OrderBy(instance => instance.ProviderKind)
            .ThenBy(instance => instance.StableId)
            .Select(instance => new ProviderInstanceDescriptor
            {
                StableId = instance.StableId,
                ProviderKind = instance.ProviderKind,
                DisplayName = instance.DisplayName
            })
            .ToListAsync(HttpContext.RequestAborted);

        var result = new NodeSyncDTO
        {
            Queries = assignedQueries.Select(q => new SearchQueryDTO
            {
                Id = q.Id,
                Query = q.Query,
                IsEnabled = q.IsEnabled,
                LastSearchUTC = q.LastSearchUTC,          // Workers use this for priority ordering
                LastSuccessfulSearchUTC = q.LastSuccessfulSearchUTC, // Workers use this for pushed:> window
                LastRepoPushedSeenUTC = q.LastRepoPushedSeenUTC      // Workers preserve repo push checkpoint
            }).ToList(),
            ProviderInstances = descriptors,
            // Expose partition info so workers can log it
            NodeIndex  = nodeIndex < 0 ? 0 : nodeIndex,
            TotalNodes = totalNodes < 1 ? 1 : totalNodes
        };

        _logger.LogInformation(
            "Node {Id} synced credential-free configuration: partition {Index}/{Total}, {QCount} queries",
            telegramPrincipalId, result.NodeIndex + 1, result.TotalNodes,
            result.Queries.Count);

        return Ok(result);
    }

    /// <summary>
    /// Workers report discovered keys to the Master with immutable normalized provenance.
    /// Every discovery carries Provider Kind, Provider Instance, and Claim identity (plus
    /// Work/Partition/Slot when present); omitted, unknown, conflicting, or mismatched
    /// identity is rejected per item before persistence. Accepted findings retain their
    /// actual provider attribution and existing classification.
    /// </summary>
    [HttpPost("report")]
    public async Task<IActionResult> Report(
        [FromHeader(Name = "X-Node-Token")] string nodeToken,
        [FromBody] NodeBulkReportDto? report)
    {
        if (string.IsNullOrEmpty(nodeToken)) return Unauthorized("Missing Node Token");

        var resolution = await _principalResolver.ResolveNodeAsync(nodeToken, HttpContext.RequestAborted);
        if (!resolution.IsAuthenticated) return Unauthorized("Invalid Node Token");
        if (!resolution.IsResolved || resolution.Principal is null)
        {
            return StatusCode(StatusCodes.Status403Forbidden, "Node has no registered Telegram principal mapping");
        }

        var principal = resolution.Principal;

        if (report?.Discoveries is null)
        {
            return BadRequest(new { message = "The discovery report carries no findings." });
        }

        var node = await _dbContext.TelegramSubscribers
            .FirstOrDefaultAsync(s => s.NodeToken == nodeToken, HttpContext.RequestAborted);

        // Mark heartbeat during report too
        if (node is not null)
        {
            node.LastNodeHeartbeatUtc = DateTime.UtcNow;
        }

        var validation = await _reportValidator.ValidateAsync(
            principal, report.Discoveries, HttpContext.RequestAborted);

        int newKeys = 0;
        var reportedApiKeys = report.Discoveries.Select(d => d.ApiKey).Distinct().ToList();
        var existingApiKeys = await _dbContext.APIKeys
            .Where(k => reportedApiKeys.Contains(k.ApiKey))
            .Select(k => k.ApiKey)
            .ToListAsync(HttpContext.RequestAborted);

        var rejected = new List<object>();
        for (var index = 0; index < report.Discoveries.Count; index++)
        {
            var discovery = report.Discoveries[index];
            var verdict = validation.Items[index];
            if (!verdict.Accepted)
            {
                rejected.Add(new { index, reason = verdict.RejectionReason });
                continue;
            }

            if (existingApiKeys.Contains(discovery.ApiKey)) continue;

            var newKey = new APIKey
            {
                ApiKey = discovery.ApiKey,
                ApiType = discovery.ApiType,
                Status = ApiStatusEnum.Unverified,
                FirstFoundUTC = DateTime.UtcNow,
                LastFoundUTC = DateTime.UtcNow,
                DiscoveredByTelegramId = principal.TelegramPrincipalId,
                SearchProvider = discovery.ProviderKind,
                Metadata = node is null
                    ? $"[GhostNode: {principal.TelegramPrincipalId}]"
                    : $"[GhostNode: {(!string.IsNullOrEmpty(node.Username) ? $"@{node.Username} ({node.TelegramId})" : node.TelegramId.ToString())}]"
            };

            var repoRef = new RepoReference
            {
                RepoName = discovery.RepoName,
                RepoOwner = discovery.RepoOwner,
                FilePath = discovery.FilePath,
                FileURL = discovery.FileUrl,
                FoundUTC = DateTime.UtcNow,
                Provider = $"{discovery.ProviderKind} (Ghost)"
            };
            newKey.References.Add(repoRef);

            _dbContext.APIKeys.Add(newKey);

            // Add to existing list to avoid duplicates within the same batch
            existingApiKeys.Add(discovery.ApiKey);
            newKeys++;
        }

        await _dbContext.SaveChangesAsync(HttpContext.RequestAborted);
        _logger.LogInformation(
            "Node {Id} reported {Count} keys ({New} new, {Accepted} accepted, {Rejected} rejected)",
            principal.TelegramPrincipalId, report.Discoveries.Count, newKeys,
            validation.AcceptedCount, validation.RejectedCount);

        return Ok(new
        {
            status = "success",
            addedCount = newKeys,
            acceptedCount = validation.AcceptedCount,
            rejectedCount = validation.RejectedCount,
            rejected
        });
    }

    /// <summary>
    /// Returns aggregate statistics for the visual dashboard.
    /// </summary>
    [HttpGet("stats")]
    public async Task<IActionResult> GetStats()
    {
        var tenMinutesAgo = DateTime.UtcNow.AddMinutes(-10);
        
        var activeNodesCount = await _dbContext.TelegramSubscribers
            .CountAsync(s => s.LastNodeHeartbeatUtc > tenMinutesAgo);

        var totalKeysFound = await _dbContext.APIKeys.CountAsync();
        
        var activeQueriesCount = await _dbContext.SearchQueries
            .CountAsync(q => q.IsEnabled);

        var lastKey = await _dbContext.APIKeys
            .OrderByDescending(k => k.FirstFoundUTC)
            .Select(k => (DateTime?)k.FirstFoundUTC)
            .FirstOrDefaultAsync();

        return Ok(new
        {
            activeNodes = activeNodesCount,
            totalKeys = totalKeysFound,
            activeQueries = activeQueriesCount,
            lastDiscoveryAt = lastKey,
            serverUtc = DateTime.UtcNow
        });
    }

    /// <summary>
    /// Get all registered worker nodes (Admins only)
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetNodes(
        [FromHeader(Name = "X-Node-Token")] string? nodeToken,
        [FromHeader(Name = "X-Access-Token")] string? accessToken)
    {
        var isDashboardAdmin = _accessService.TryGetSession(accessToken, out var session) &&
                               session?.Role == DashboardAccessRole.Admin;
        var isNodeAdmin = !string.IsNullOrEmpty(nodeToken) &&
                          await _dbContext.TelegramSubscribers.AnyAsync(s => s.NodeToken == nodeToken && s.IsAdmin);
        if (!isDashboardAdmin && !isNodeAdmin) return Unauthorized("Admin access required");

        var tenMinutesAgo = DateTime.UtcNow.AddMinutes(-10);
        var nodes = await _dbContext.TelegramSubscribers
            .Where(s => s.NodeToken != null)
            .Select(s => new
            {
                s.TelegramId,
                s.Username,
                s.IsAdmin,
                s.NodeUrl,
                s.LastNodeHeartbeatUtc,
                isActive = s.LastNodeHeartbeatUtc != null && s.LastNodeHeartbeatUtc > tenMinutesAgo
            })
            .ToListAsync();

        return Ok(nodes);
    }
}

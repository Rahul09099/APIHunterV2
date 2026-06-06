using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using UnsecuredAPIKeys.Data;
using UnsecuredAPIKeys.Data.DTOs;
using UnsecuredAPIKeys.Data.Models;
using UnsecuredAPIKeys.Data.Common;

namespace UnsecuredAPIKeys.WebAPI.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class NodesController : ControllerBase
{
    private readonly DBContext _dbContext;
    private readonly ILogger<NodesController> _logger;

    public NodesController(DBContext dbContext, ILogger<NodesController> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
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

        var node = await _dbContext.TelegramSubscribers
            .FirstOrDefaultAsync(s => s.NodeToken == nodeToken);

        if (node == null) return Unauthorized("Invalid Node Token");

        // Fetch tokens belonging to this user or ALL tokens if admin
        var tokenQuery = _dbContext.SearchProviderTokens
            .Where(t => t.IsEnabled && t.SearchProvider == SearchProviderEnum.GitHub);

        if (!node.IsAdmin)
        {
            tokenQuery = tokenQuery.Where(t => t.AddedByTelegramId == node.TelegramId || t.AddedByTelegramId == null);
        }

        var tokens = await tokenQuery.ToListAsync();

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
            .OrderBy(q => q.Id)           // stable ordering
            .ToListAsync();

        List<SearchQuery> assignedQueries;

        int totalNodes = activeNodeIds.Count;
        int nodeIndex  = activeNodeIds.IndexOf(node.TelegramId);

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

        var result = new NodeSyncDTO
        {
            Tokens = tokens.Select(t => new SearchProviderTokenDTO 
            { 
                Token = t.Token, 
                SearchProvider = t.SearchProvider 
            }).ToList(),
            Queries = assignedQueries.Select(q => new SearchQueryDTO 
            { 
                Id = q.Id, 
                Query = q.Query, 
                IsEnabled = q.IsEnabled 
            }).ToList(),
            // Expose partition info so workers can log it
            NodeIndex  = nodeIndex < 0 ? 0 : nodeIndex,
            TotalNodes = totalNodes < 1 ? 1 : totalNodes
        };

        _logger.LogInformation(
            "Node {Id} synced: partition {Index}/{Total}, {QCount} queries, {TCount} tokens",
            node.TelegramId, result.NodeIndex + 1, result.TotalNodes,
            result.Queries.Count, result.Tokens.Count);

        return Ok(result);
    }

    /// <summary>
    /// Workers report discovered keys to the Master.
    /// </summary>
    [HttpPost("report")]
    public async Task<IActionResult> Report(
        [FromHeader(Name = "X-Node-Token")] string nodeToken, 
        [FromBody] NodeBulkReportDto report)
    {
        if (string.IsNullOrEmpty(nodeToken)) return Unauthorized("Missing Node Token");

        var node = await _dbContext.TelegramSubscribers
            .FirstOrDefaultAsync(s => s.NodeToken == nodeToken);

        if (node == null) return Unauthorized("Invalid Node Token");

        // Mark heartbeat during report too
        node.LastNodeHeartbeatUtc = DateTime.UtcNow;

        int newKeys = 0;
        var reportedApiKeys = report.Discoveries.Select(d => d.ApiKey).Distinct().ToList();
        var existingApiKeys = await _dbContext.APIKeys
            .Where(k => reportedApiKeys.Contains(k.ApiKey))
            .Select(k => k.ApiKey)
            .ToListAsync();

        foreach (var discovery in report.Discoveries)
        {
            if (existingApiKeys.Contains(discovery.ApiKey)) continue;

            var newKey = new APIKey
            {
                ApiKey = discovery.ApiKey,
                ApiType = discovery.ApiType,
                Status = ApiStatusEnum.Unverified,
                FirstFoundUTC = DateTime.UtcNow,
                LastFoundUTC = DateTime.UtcNow,
                DiscoveredByTelegramId = node.TelegramId,
                SearchProvider = SearchProviderEnum.GitHub,
                Metadata = $"[GhostNode: {(!string.IsNullOrEmpty(node.Username) ? $"@{node.Username} ({node.TelegramId})" : node.TelegramId.ToString())}]"
            };

            var repoRef = new RepoReference
            {
                RepoName = discovery.RepoName,
                RepoOwner = discovery.RepoOwner,
                FilePath = discovery.FilePath,
                FileURL = discovery.FileUrl,
                FoundUTC = DateTime.UtcNow,
                Provider = "GitHub (Ghost)"
            };
            newKey.References.Add(repoRef);

            _dbContext.APIKeys.Add(newKey);
            
            // Add to existing list to avoid duplicates within the same batch
            existingApiKeys.Add(discovery.ApiKey);
            newKeys++;
        }

        await _dbContext.SaveChangesAsync();
        _logger.LogInformation("Node {Id} reported {Count} keys ({New} new)", node.TelegramId, report.Discoveries.Count, newKeys);

        return Ok(new { status = "success", addedCount = newKeys });
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
    public async Task<IActionResult> GetNodes([FromHeader(Name = "X-Node-Token")] string nodeToken)
    {
        if (string.IsNullOrEmpty(nodeToken)) return Unauthorized("Missing Node Token");

        var adminNode = await _dbContext.TelegramSubscribers
            .FirstOrDefaultAsync(s => s.NodeToken == nodeToken);

        if (adminNode == null || !adminNode.IsAdmin) return Unauthorized("Admin access required");

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

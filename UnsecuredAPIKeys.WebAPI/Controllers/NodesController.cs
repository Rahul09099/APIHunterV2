using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using UnsecuredAPIKeys.Data;
using UnsecuredAPIKeys.Data.DTOs;
using UnsecuredAPIKeys.Data.Models;

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
    public async Task<IActionResult> Heartbeat([FromHeader(Name = "X-Node-Token")] string nodeToken)
    {
        if (string.IsNullOrEmpty(nodeToken)) return Unauthorized("Missing Node Token");

        var node = await _dbContext.TelegramSubscribers
            .FirstOrDefaultAsync(s => s.NodeToken == nodeToken);

        if (node == null) return Unauthorized("Invalid Node Token");

        node.LastNodeHeartbeatUtc = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync();

        return Ok(new { status = "success", timestamp = DateTime.UtcNow });
    }

    /// <summary>
    /// Sync tokens and queries for the specific node.
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
            tokenQuery = tokenQuery.Where(t => t.AddedByTelegramId == node.TelegramId);
        }

        var tokens = await tokenQuery.ToListAsync();

        var queries = await _dbContext.SearchQueries
            .Where(q => q.IsEnabled)
            .ToListAsync();

        var result = new NodeSyncDTO
        {
            Tokens = tokens.Select(t => new SearchProviderTokenDTO 
            { 
                Token = t.Token, 
                SearchProvider = t.SearchProvider 
            }).ToList(),
            Queries = queries.Select(q => new SearchQueryDTO 
            { 
                Id = q.Id, 
                Query = q.Query, 
                IsEnabled = q.IsEnabled 
            }).ToList()
        };

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
        foreach (var discovery in report.Discoveries)
        {
            var exists = await _dbContext.APIKeys.AnyAsync(k => k.ApiKey == discovery.ApiKey);
            if (exists) continue;

            var newKey = new APIKey
            {
                ApiKey = discovery.ApiKey,
                ApiType = discovery.ApiType,
                Status = ApiStatusEnum.Unverified,
                FirstFoundUTC = DateTime.UtcNow,
                LastFoundUTC = DateTime.UtcNow,
                DiscoveredByTelegramId = node.TelegramId,
                SearchProvider = SearchProviderEnum.GitHub,
                Metadata = $"[GhostNode: {node.Username ?? node.TelegramId.ToString()}]"
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
            newKeys++;
        }

        await _dbContext.SaveChangesAsync();
        _logger.LogInformation("Node {Id} reported {Count} keys ({New} new)", node.TelegramId, report.Discoveries.Count, newKeys);

        return Ok(new { status = "success", addedCount = newKeys });
    }
}

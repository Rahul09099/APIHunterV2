using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using UnsecuredAPIKeys.Data;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Data.Models;
using UnsecuredAPIKeys.Services;

namespace UnsecuredAPIKeys.WebAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
public class StatusController : ControllerBase
{
    private readonly DBContext _dbContext;
    private readonly DatabaseService _dbService;

    public StatusController(DBContext dbContext, DatabaseService dbService)
    {
        _dbContext = dbContext;
        _dbService = dbService;
    }

    private async Task<TelegramSubscriber?> GetAuthenticatedUser(string nodeToken)
    {
        if (string.IsNullOrEmpty(nodeToken)) return null;
        return await _dbContext.TelegramSubscribers.FirstOrDefaultAsync(s => s.NodeToken == nodeToken);
    }

    /// <summary>
    /// Get overall statistics
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetStatus([FromHeader(Name = "X-Node-Token")] string nodeToken)
    {
        var user = await GetAuthenticatedUser(nodeToken);
        if (user == null) return Unauthorized("Invalid Node Token");

        var stats = await _dbService.GetCategorizedStatisticsAsync(_dbContext, user.IsAdmin ? null : user.TelegramId);
        
        return Ok(new
        {
            totalKeys = stats.TotalKeys,
            validKeys = stats.ValidKeys,
            invalidKeys = stats.InvalidKeys,
            unverifiedKeys = stats.UnverifiedKeys,
            quotaExhaustedKeys = stats.ValidNoCreditsKeys,
            gitHubTokensCount = stats.GitHubTokensCount,
            timestamp = DateTime.UtcNow
        });
    }

    /// <summary>
    /// Get detailed statistics by category
    /// </summary>
    [HttpGet("detailed")]
    public async Task<IActionResult> GetDetailedStatus([FromHeader(Name = "X-Node-Token")] string nodeToken)
    {
        var user = await GetAuthenticatedUser(nodeToken);
        if (user == null) return Unauthorized("Invalid Node Token");

        var stats = await _dbService.GetCategorizedStatisticsAsync(_dbContext, user.IsAdmin ? null : user.TelegramId);
        return Ok(stats);
    }

    /// <summary>
    /// Get statistics for a specific API type
    /// </summary>
    [HttpGet("api-type/{apiType}")]
    public async Task<IActionResult> GetApiTypeStats(
        [FromHeader(Name = "X-Node-Token")] string nodeToken,
        string apiType)
    {
        var user = await GetAuthenticatedUser(nodeToken);
        if (user == null) return Unauthorized("Invalid Node Token");

        if (!Enum.TryParse<ApiTypeEnum>(apiType, true, out var apiTypeEnum))
        {
            return BadRequest(new { message = "Invalid API type" });
        }

        var query = _dbContext.APIKeys.Where(k => k.ApiType == apiTypeEnum);
        if (!user.IsAdmin)
        {
            query = query.Where(k => k.DiscoveredByTelegramId == user.TelegramId);
        }

        var keysCount = await query
            .GroupBy(k => k.Status)
            .Select(g => new { status = g.Key.ToString(), count = g.Count() })
            .ToListAsync();

        return Ok(new
        {
            apiType = apiTypeEnum.ToString(),
            statistics = keysCount,
            timestamp = DateTime.UtcNow
        });
    }

    /// <summary>
    /// Get recent API keys
    /// </summary>
    [HttpGet("recent-keys")]
    public async Task<IActionResult> GetRecentKeys(
        [FromHeader(Name = "X-Node-Token")] string nodeToken,
        [FromQuery] int limit = 100)
    {
        var user = await GetAuthenticatedUser(nodeToken);
        if (user == null) return Unauthorized("Invalid Node Token");

        var query = _dbContext.APIKeys.AsQueryable();

        // If not admin, only show keys discovered by this subscriber
        if (!user.IsAdmin)
        {
            query = query.Where(k => k.DiscoveredByTelegramId == user.TelegramId);
        }

        var keys = await query
            .OrderByDescending(k => k.FirstFoundUTC)
            .Take(Math.Min(limit, 500))
            .Select(k => new
            {
                k.Id,
                k.ApiKey,
                k.ApiType,
                k.Status,
                k.FirstFoundUTC,
                k.LastCheckedUTC,
                k.Balance,
                k.AccountTier,
                k.ValidationResponse,
                keyPreview = k.ApiKey
            })
            .ToListAsync();

        return Ok(keys);
    }

    /// <summary>
    /// Get valid keys count by API type
    /// </summary>
    [HttpGet("valid-keys")]
    public async Task<IActionResult> GetValidKeys([FromHeader(Name = "X-Node-Token")] string nodeToken)
    {
        var user = await GetAuthenticatedUser(nodeToken);
        if (user == null) return Unauthorized("Invalid Node Token");

        var query = _dbContext.APIKeys
            .Where(k => k.Status == ApiStatusEnum.Valid);

        if (!user.IsAdmin)
        {
            query = query.Where(k => k.DiscoveredByTelegramId == user.TelegramId);
        }

        var validKeys = await query
            .GroupBy(k => k.ApiType)
            .Select(g => new { apiType = g.Key.ToString(), count = g.Count() })
            .ToListAsync();

        return Ok(new
        {
            totalValid = validKeys.Sum(v => v.count),
            byApiType = validKeys,
            timestamp = DateTime.UtcNow
        });
    }

    /// <summary>
    /// Get GitHub tokens status
    /// </summary>
    [HttpGet("github-tokens")]
    public async Task<IActionResult> GetGitHubTokens([FromHeader(Name = "X-Node-Token")] string nodeToken)
    {
        var user = await GetAuthenticatedUser(nodeToken);
        if (user == null) return Unauthorized("Invalid Node Token");

        var query = _dbContext.SearchProviderTokens
            .Where(t => t.SearchProvider == SearchProviderEnum.GitHub);

        if (!user.IsAdmin)
        {
            query = query.Where(t => t.AddedByTelegramId == user.TelegramId);
        }

        var tokens = await query
            .Select(t => new
            {
                t.Id,
                t.IsEnabled,
                t.LastUsedUTC,
                tokenPreview = t.Token.Length > 10 ? t.Token.Substring(0, 10) + "..." : "***"
            })
            .ToListAsync();

        return Ok(tokens);
    }

    /// <summary>
    /// Get search queries
    /// </summary>
    [HttpGet("search-queries")]
    public async Task<IActionResult> GetSearchQueries([FromHeader(Name = "X-Node-Token")] string nodeToken)
    {
        var user = await GetAuthenticatedUser(nodeToken);
        if (user == null || !user.IsAdmin) return Unauthorized("Admin access required");

        var queries = await _dbContext.SearchQueries
            .Select(q => new
            {
                q.Id,
                q.Query,
                q.IsEnabled,
                q.LastSearchUTC
            })
            .ToListAsync();

        return Ok(queries);
    }

    /// <summary>
    /// Authenticate a user by their Telegram ID (called by Telegram WebApp for auto-login)
    /// </summary>
    [HttpGet("token-by-telegram/{telegramId}")]
    public async Task<IActionResult> GetTokenByTelegramId(long telegramId)
    {
        var subscriber = await _dbContext.TelegramSubscribers
            .FirstOrDefaultAsync(s => s.TelegramId == telegramId);

        if (subscriber == null || string.IsNullOrEmpty(subscriber.NodeToken))
        {
            return NotFound(new { message = "No registered node token found for this Telegram ID" });
        }

        return Ok(new { token = subscriber.NodeToken });
    }
}

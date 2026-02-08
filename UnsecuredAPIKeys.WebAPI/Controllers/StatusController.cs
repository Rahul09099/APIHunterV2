using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using UnsecuredAPIKeys.Data;
using UnsecuredAPIKeys.Data.Common;
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

    /// <summary>
    /// Get overall statistics
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetStatus()
    {
        var stats = await _dbService.GetCategorizedStatisticsAsync(_dbContext);
        
        return Ok(new
        {
            totalKeys = stats.TotalKeys,
            validKeys = stats.ValidKeys,
            invalidKeys = stats.InvalidKeys,
            unverifiedKeys = stats.UnverifiedKeys,
            quotaExhaustedKeys = stats.QuotaExhaustedKeys,
            gitHubTokensCount = stats.GitHubTokensCount,
            timestamp = DateTime.UtcNow
        });
    }

    /// <summary>
    /// Get detailed statistics by category
    /// </summary>
    [HttpGet("detailed")]
    public async Task<IActionResult> GetDetailedStatus()
    {
        var stats = await _dbService.GetCategorizedStatisticsAsync(_dbContext);
        return Ok(stats);
    }

    /// <summary>
    /// Get statistics for a specific API type
    /// </summary>
    [HttpGet("api-type/{apiType}")]
    public async Task<IActionResult> GetApiTypeStats(string apiType)
    {
        if (!Enum.TryParse<ApiTypeEnum>(apiType, true, out var apiTypeEnum))
        {
            return BadRequest(new { message = "Invalid API type" });
        }

        var keysCount = await _dbContext.APIKeys
            .Where(k => k.ApiType == apiTypeEnum)
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
    /// Get recent API keys (latest 100)
    /// </summary>
    [HttpGet("recent-keys")]
    public async Task<IActionResult> GetRecentKeys([FromQuery] int limit = 100)
    {
        var keys = await _dbContext.APIKeys
            .OrderByDescending(k => k.FirstFoundUTC)
            .Take(Math.Min(limit, 500))
            .Select(k => new
            {
                k.Id,
                k.ApiType,
                k.Status,
                k.FirstFoundUTC,
                k.LastCheckedUTC,
                keyPreview = k.ApiKey.Length > 20 ? k.ApiKey.Substring(0, 20) + "..." : k.ApiKey
            })
            .ToListAsync();

        return Ok(keys);
    }

    /// <summary>
    /// Get valid keys count by API type
    /// </summary>
    [HttpGet("valid-keys")]
    public async Task<IActionResult> GetValidKeys()
    {
        var validKeys = await _dbContext.APIKeys
            .Where(k => k.Status == ApiStatusEnum.Valid)
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
    public async Task<IActionResult> GetGitHubTokens()
    {
        var tokens = await _dbContext.SearchProviderTokens
            .Where(t => t.SearchProvider == SearchProviderEnum.GitHub)
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
    public async Task<IActionResult> GetSearchQueries()
    {
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
}

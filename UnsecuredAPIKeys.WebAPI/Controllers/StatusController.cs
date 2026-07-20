using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using UnsecuredAPIKeys.Data;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Data.Models;
using UnsecuredAPIKeys.Services;
using UnsecuredAPIKeys.WebAPI.Services;

namespace UnsecuredAPIKeys.WebAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
public class StatusController : ControllerBase
{
    private readonly DBContext _dbContext;
    private readonly DatabaseService _dbService;
    private readonly DashboardAccessService _accessService;

    public StatusController(DBContext dbContext, DatabaseService dbService, DashboardAccessService accessService)
    {
        _dbContext = dbContext;
        _dbService = dbService;
        _accessService = accessService;
    }

    private async Task<AccessContext?> GetAccess(string? nodeToken, string? accessToken)
    {
        if (_accessService.TryGetSession(accessToken, out var session) && session is not null)
        {
            return new AccessContext(session.Role == DashboardAccessRole.Admin, null, true);
        }

        if (string.IsNullOrEmpty(nodeToken)) return null;
        var subscriber = await _dbContext.TelegramSubscribers.FirstOrDefaultAsync(s => s.NodeToken == nodeToken);
        return subscriber is null ? null : new AccessContext(subscriber.IsAdmin, subscriber.TelegramId, false);
    }

    /// <summary>
    /// Get overall statistics
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetStatus(
        [FromHeader(Name = "X-Node-Token")] string? nodeToken,
        [FromHeader(Name = "X-Access-Token")] string? accessToken)
    {
        var access = await GetAccess(nodeToken, accessToken);
        if (access is null) return Unauthorized("Valid access code session required");

        var stats = await _dbService.GetCategorizedStatisticsAsync(_dbContext,
            !access.IsDashboardSession && !access.IsAdmin ? access.TelegramId : null);
        
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
    public async Task<IActionResult> GetDetailedStatus(
        [FromHeader(Name = "X-Node-Token")] string? nodeToken,
        [FromHeader(Name = "X-Access-Token")] string? accessToken)
    {
        var access = await GetAccess(nodeToken, accessToken);
        if (access is null) return Unauthorized("Valid access code session required");

        var stats = await _dbService.GetCategorizedStatisticsAsync(_dbContext,
            !access.IsDashboardSession && !access.IsAdmin ? access.TelegramId : null);
        return Ok(stats);
    }

    /// <summary>
    /// Get statistics for a specific API type
    /// </summary>
    [HttpGet("api-type/{apiType}")]
    public async Task<IActionResult> GetApiTypeStats(
        [FromHeader(Name = "X-Node-Token")] string? nodeToken,
        [FromHeader(Name = "X-Access-Token")] string? accessToken,
        string apiType)
    {
        var access = await GetAccess(nodeToken, accessToken);
        if (access is null) return Unauthorized("Valid access code session required");

        if (!Enum.TryParse<ApiTypeEnum>(apiType, true, out var apiTypeEnum))
        {
            return BadRequest(new { message = "Invalid API type" });
        }

        var query = _dbContext.APIKeys.Where(k => k.ApiType == apiTypeEnum);
        if (!access.IsDashboardSession && !access.IsAdmin)
        {
            query = query.Where(k => k.DiscoveredByTelegramId == access.TelegramId);
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
        [FromHeader(Name = "X-Node-Token")] string? nodeToken,
        [FromHeader(Name = "X-Access-Token")] string? accessToken,
        [FromQuery] int limit = 100)
    {
        var access = await GetAccess(nodeToken, accessToken);
        if (access is null) return Unauthorized("Valid access code session required");

        var query = _dbContext.APIKeys.AsQueryable();

        // Telegram subscribers retain their own-result filter. Dashboard users see
        // aggregate records but normal sessions receive a masked key value below.
        if (!access.IsDashboardSession && !access.IsAdmin)
        {
            query = query.Where(k => k.DiscoveredByTelegramId == access.TelegramId);
        }

        var keys = await query
            .OrderByDescending(k => k.FirstFoundUTC)
            .Take(Math.Min(limit, 500))
            .Select(k => new
            {
                k.Id,
                k.ApiKey,
                ApiType = (int)k.ApiType,
                ApiTypeName = k.ApiType.ToString(),
                Status = k.Status.ToString(),
                SearchProvider = k.SearchProvider.ToString(),
                k.Balance,
                k.AccountTier,
                k.ValidationResponse,
                k.Metadata,
                k.FirstFoundUTC,
                k.LastFoundUTC,
                k.LastCheckedUTC,
                k.TimesDisplayed,
                k.ErrorCount,
                k.DiscoveredByTelegramId,
                k.AwsAccountId,
                k.AwsUserArn,
                k.AwsUserId,
                k.AwsCredentialType,
                k.AwsAttachedPolicies,
                k.AwsRiskLevel,
                k.AwsIsRootAccount,
                keyPreview = k.ApiKey
            })
            .ToListAsync();

        var mayViewRawKeys = access.IsAdmin || !access.IsDashboardSession;
        return Ok(keys.Select(k => new
        {
            k.Id,
            apiKey = mayViewRawKeys ? k.ApiKey : null,
            k.ApiType,
            k.ApiTypeName,
            k.Status,
            k.SearchProvider,
            k.Balance,
            k.AccountTier,
            k.ValidationResponse,
            k.Metadata,
            k.FirstFoundUTC,
            k.LastFoundUTC,
            k.LastCheckedUTC,
            k.TimesDisplayed,
            k.ErrorCount,
            k.DiscoveredByTelegramId,
            k.AwsAccountId,
            k.AwsUserArn,
            k.AwsUserId,
            k.AwsCredentialType,
            k.AwsAttachedPolicies,
            k.AwsRiskLevel,
            k.AwsIsRootAccount,
            keyPreview = mayViewRawKeys ? k.keyPreview : MaskApiKey(k.ApiKey)
        }));
    }

    /// <summary>
    /// Get valid keys count by API type
    /// </summary>
    [HttpGet("valid-keys")]
    public async Task<IActionResult> GetValidKeys(
        [FromHeader(Name = "X-Node-Token")] string? nodeToken,
        [FromHeader(Name = "X-Access-Token")] string? accessToken)
    {
        var access = await GetAccess(nodeToken, accessToken);
        if (access is null) return Unauthorized("Valid access code session required");

        var query = _dbContext.APIKeys
            .Where(k => k.Status == ApiStatusEnum.Valid);

        if (!access.IsDashboardSession && !access.IsAdmin)
        {
            query = query.Where(k => k.DiscoveredByTelegramId == access.TelegramId);
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
    public async Task<IActionResult> GetGitHubTokens(
        [FromHeader(Name = "X-Node-Token")] string? nodeToken,
        [FromHeader(Name = "X-Access-Token")] string? accessToken)
    {
        var access = await GetAccess(nodeToken, accessToken);
        if (access is null || !access.IsAdmin) return Unauthorized("Admin access required");

        var query = _dbContext.SearchProviderTokens
            .Where(t => t.SearchProvider == SearchProviderEnum.GitHub);

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
    public async Task<IActionResult> GetSearchQueries(
        [FromHeader(Name = "X-Node-Token")] string? nodeToken,
        [FromHeader(Name = "X-Access-Token")] string? accessToken)
    {
        var access = await GetAccess(nodeToken, accessToken);
        if (access is null || !access.IsAdmin) return Unauthorized("Admin access required");

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

    private static string MaskApiKey(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return "***";
        if (apiKey.Length <= 8) return new string('*', apiKey.Length);

        return $"{apiKey[..4]}{new string('*', Math.Min(12, apiKey.Length - 8))}{apiKey[^4..]}";
    }

    private sealed record AccessContext(bool IsAdmin, long? TelegramId, bool IsDashboardSession);
}

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
public class ConfigController : ControllerBase
{
    private readonly DBContext _dbContext;
    private readonly DatabaseService _dbService;
    private readonly DashboardAccessService _accessService;

    public ConfigController(DBContext dbContext, DatabaseService dbService, DashboardAccessService accessService)
    {
        _dbContext = dbContext;
        _dbService = dbService;
        _accessService = accessService;
    }

    private async Task<bool> IsAdministratorAsync(string? nodeToken, string? accessToken)
    {
        if (_accessService.TryGetSession(accessToken, out var session) && session is not null)
        {
            return session.Role == DashboardAccessRole.Admin;
        }

        if (string.IsNullOrEmpty(nodeToken)) return false;
        return await _dbContext.TelegramSubscribers.AnyAsync(s => s.NodeToken == nodeToken && s.IsAdmin);
    }

    /// <summary>
    /// Add a GitHub token
    /// </summary>
    [HttpPost("github-token")]
    public async Task<IActionResult> AddGitHubToken(
        [FromBody] AddTokenRequest request,
        [FromHeader(Name = "X-Node-Token")] string? nodeToken = null,
        [FromHeader(Name = "X-Access-Token")] string? accessToken = null)
    {
        if (!await IsAdministratorAsync(nodeToken, accessToken)) return Unauthorized("Admin access required");

        if (string.IsNullOrWhiteSpace(request.Token))
        {
            return BadRequest(new { message = "Token is required" });
        }

        // Check if token already exists
        var exists = await _dbContext.SearchProviderTokens
            .AnyAsync(t => t.Token == request.Token && t.SearchProvider == SearchProviderEnum.GitHub);

        if (exists)
        {
            return Conflict(new { message = "Token already exists" });
        }

        var token = new SearchProviderToken
        {
            Token = request.Token,
            SearchProvider = SearchProviderEnum.GitHub,
            IsEnabled = true
        };

        _dbContext.SearchProviderTokens.Add(token);
        await _dbContext.SaveChangesAsync();

        return Ok(new { message = "GitHub token added successfully", tokenId = token.Id });
    }

    /// <summary>
    /// Delete a GitHub token
    /// </summary>
    [HttpDelete("github-token/{id}")]
    public async Task<IActionResult> DeleteGitHubToken(
        int id,
        [FromHeader(Name = "X-Node-Token")] string? nodeToken,
        [FromHeader(Name = "X-Access-Token")] string? accessToken)
    {
        if (!await IsAdministratorAsync(nodeToken, accessToken)) return Unauthorized("Admin access required");

        var token = await _dbContext.SearchProviderTokens.FindAsync(id);
        
        if (token == null)
        {
            return NotFound(new { message = "Token not found" });
        }

        _dbContext.SearchProviderTokens.Remove(token);
        await _dbContext.SaveChangesAsync();

        return Ok(new { message = "GitHub token deleted successfully" });
    }

    /// <summary>
    /// Add a search query
    /// </summary>
    [HttpPost("search-query")]
    public async Task<IActionResult> AddSearchQuery(
        [FromBody] AddQueryRequest request,
        [FromHeader(Name = "X-Node-Token")] string? nodeToken,
        [FromHeader(Name = "X-Access-Token")] string? accessToken)
    {
        if (!await IsAdministratorAsync(nodeToken, accessToken)) return Unauthorized("Admin access required");

        if (string.IsNullOrWhiteSpace(request.Query))
        {
            return BadRequest(new { message = "Query is required" });
        }

        var query = new SearchQuery
        {
            Query = request.Query,
            IsEnabled = true,
            LastSearchUTC = DateTime.UtcNow
        };

        _dbContext.SearchQueries.Add(query);
        await _dbContext.SaveChangesAsync();

        return Ok(new { message = "Search query added successfully", queryId = query.Id });
    }

    /// <summary>
    /// Delete a search query
    /// </summary>
    [HttpDelete("search-query/{id}")]
    public async Task<IActionResult> DeleteSearchQuery(
        int id,
        [FromHeader(Name = "X-Node-Token")] string? nodeToken,
        [FromHeader(Name = "X-Access-Token")] string? accessToken)
    {
        if (!await IsAdministratorAsync(nodeToken, accessToken)) return Unauthorized("Admin access required");

        var query = await _dbContext.SearchQueries.FindAsync(id);
        
        if (query == null)
        {
            return NotFound(new { message = "Query not found" });
        }

        _dbContext.SearchQueries.Remove(query);
        await _dbContext.SaveChangesAsync();

        return Ok(new { message = "Search query deleted successfully" });
    }

    /// <summary>
    /// Toggle search query enabled status
    /// </summary>
    [HttpPatch("search-query/{id}/toggle")]
    public async Task<IActionResult> ToggleSearchQuery(
        int id,
        [FromHeader(Name = "X-Node-Token")] string? nodeToken,
        [FromHeader(Name = "X-Access-Token")] string? accessToken)
    {
        if (!await IsAdministratorAsync(nodeToken, accessToken)) return Unauthorized("Admin access required");

        var query = await _dbContext.SearchQueries.FindAsync(id);
        
        if (query == null)
        {
            return NotFound(new { message = "Query not found" });
        }

        query.IsEnabled = !query.IsEnabled;
        await _dbContext.SaveChangesAsync();

        return Ok(new { 
            message = "Search query updated successfully", 
            isEnabled = query.IsEnabled 
        });
    }

    /// <summary>
    /// Export valid keys
    /// </summary>
    [HttpGet("export-keys")]
    public async Task<IActionResult> ExportKeys(
        [FromQuery] string format = "json",
        [FromHeader(Name = "X-Node-Token")] string? nodeTokenHeader = null,
        [FromQuery] string? nodeToken = null,
        [FromHeader(Name = "X-Access-Token")] string? accessToken = null)
    {
        var token = nodeTokenHeader ?? nodeToken ?? "";
        if (!await IsAdministratorAsync(token, accessToken)) return Unauthorized("Admin access required for export");

        var query = _dbContext.APIKeys
            .Where(k => k.Status == ApiStatusEnum.Valid);

        var validKeys = await query
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
                k.AwsIsRootAccount
            })
            .ToListAsync();

        if (format.Equals("csv", StringComparison.OrdinalIgnoreCase) ||
            format.Equals("excel", StringComparison.OrdinalIgnoreCase) ||
            format.Equals("xlsx", StringComparison.OrdinalIgnoreCase))
        {
            var csv = "Id,ApiKey,ApiType,ApiTypeName,Status,SearchProvider,Balance,AccountTier,ValidationResponse,Metadata,FirstFoundUTC,LastFoundUTC,LastCheckedUTC,TimesDisplayed,ErrorCount,DiscoveredByTelegramId,AwsAccountId,AwsUserArn,AwsUserId,AwsCredentialType,AwsAttachedPolicies,AwsRiskLevel,AwsIsRootAccount\n";
            csv += string.Join("\n", validKeys.Select(k => string.Join(",", new[]
            {
                k.Id.ToString(),
                Csv(k.ApiKey),
                k.ApiType.ToString(),
                Csv(k.ApiTypeName),
                Csv(k.Status),
                Csv(k.SearchProvider),
                Csv(k.Balance),
                Csv(k.AccountTier),
                Csv(k.ValidationResponse),
                Csv(k.Metadata),
                Csv(k.FirstFoundUTC.ToString("O")),
                Csv(k.LastFoundUTC.ToString("O")),
                Csv(k.LastCheckedUTC?.ToString("O")),
                k.TimesDisplayed.ToString(),
                k.ErrorCount.ToString(),
                k.DiscoveredByTelegramId?.ToString() ?? "",
                Csv(k.AwsAccountId),
                Csv(k.AwsUserArn),
                Csv(k.AwsUserId),
                Csv(k.AwsCredentialType),
                Csv(k.AwsAttachedPolicies),
                Csv(k.AwsRiskLevel),
                k.AwsIsRootAccount.ToString()
            })));

            var fileName = format.Equals("excel", StringComparison.OrdinalIgnoreCase) || format.Equals("xlsx", StringComparison.OrdinalIgnoreCase)
                ? "api_key_results.csv"
                : "api_key_results.csv";
            return File(System.Text.Encoding.UTF8.GetBytes(csv), "text/csv", fileName);
        }

        return Ok(validKeys);
    }

    private static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "\"\"";
        var sanitized = value.Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " ");
        return $"\"{sanitized}\"";
    }

    /// <summary>
    /// Reset database (WARNING: Deletes all data)
    /// </summary>
    [HttpPost("reset-database")]
    public async Task<IActionResult> ResetDatabase(
        [FromBody] ResetRequest request,
        [FromHeader(Name = "X-Node-Token")] string? nodeToken,
        [FromHeader(Name = "X-Access-Token")] string? accessToken)
    {
        if (!await IsAdministratorAsync(nodeToken, accessToken)) return Unauthorized("Admin access required");

        if (request.Confirmation != "CONFIRM_RESET")
        {
            return BadRequest(new { 
                message = "Invalid confirmation. Send { \"confirmation\": \"CONFIRM_RESET\" } to proceed" 
            });
        }

        await _dbService.ResetDatabaseAsync();
        await _dbService.InitializeDatabaseAsync();

        return Ok(new { message = "Database reset successfully" });
    }
}

public class AddTokenRequest
{
    public string Token { get; set; } = string.Empty;
}

public class AddQueryRequest
{
    public string Query { get; set; } = string.Empty;
}

public class ResetRequest
{
    public string Confirmation { get; set; } = string.Empty;
}

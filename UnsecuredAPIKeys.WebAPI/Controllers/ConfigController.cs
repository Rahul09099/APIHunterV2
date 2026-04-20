using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using UnsecuredAPIKeys.Data;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Data.Models;
using UnsecuredAPIKeys.Services;

namespace UnsecuredAPIKeys.WebAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ConfigController : ControllerBase
{
    private readonly DBContext _dbContext;
    private readonly DatabaseService _dbService;

    public ConfigController(DBContext dbContext, DatabaseService dbService)
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
    /// Add a GitHub token
    /// </summary>
    [HttpPost("github-token")]
    public async Task<IActionResult> AddGitHubToken(
        [FromBody] AddTokenRequest request,
        [FromHeader(Name = "X-Node-Token")] string? nodeToken = null)
    {
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

        // Optionally associate with a user if token provided
        long? addedBy = null;
        if (!string.IsNullOrEmpty(nodeToken))
        {
            var user = await GetAuthenticatedUser(nodeToken);
            addedBy = user?.TelegramId;
        }

        var token = new SearchProviderToken
        {
            Token = request.Token,
            SearchProvider = SearchProviderEnum.GitHub,
            IsEnabled = true,
            AddedByTelegramId = addedBy
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
        [FromHeader(Name = "X-Node-Token")] string nodeToken)
    {
        var user = await GetAuthenticatedUser(nodeToken);
        if (user == null || !user.IsAdmin) return Unauthorized("Admin access required");

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
        [FromHeader(Name = "X-Node-Token")] string nodeToken)
    {
        var user = await GetAuthenticatedUser(nodeToken);
        if (user == null || !user.IsAdmin) return Unauthorized("Admin access required");

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
        [FromHeader(Name = "X-Node-Token")] string nodeToken)
    {
        var user = await GetAuthenticatedUser(nodeToken);
        if (user == null || !user.IsAdmin) return Unauthorized("Admin access required");

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
        [FromHeader(Name = "X-Node-Token")] string nodeToken)
    {
        var user = await GetAuthenticatedUser(nodeToken);
        if (user == null || !user.IsAdmin) return Unauthorized("Admin access required");

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
        [FromHeader(Name = "X-Node-Token")] string? nodeToken = null)
    {
        var user = await GetAuthenticatedUser(nodeToken ?? "");
        if (user == null) return Unauthorized("Node Token required for export");

        var query = _dbContext.APIKeys
            .Where(k => k.Status == ApiStatusEnum.Valid);

        // If not admin, filter by the user who discovered them
        if (!user.IsAdmin)
        {
            query = query.Where(k => k.DiscoveredByTelegramId == user.TelegramId);
        }

        var validKeys = await query
            .Select(k => new
            {
                k.ApiType,
                k.ApiKey,
                k.LastCheckedUTC,
                k.FirstFoundUTC
            })
            .ToListAsync();

        if (format.ToLower() == "csv")
        {
            var csv = "ApiType,ApiKey,LastVerifiedAt,CreatedAt\n";
            csv += string.Join("\n", validKeys.Select(k => 
                $"{k.ApiType},\"{k.ApiKey}\",{k.LastCheckedUTC},{k.FirstFoundUTC}"));
            
            return File(System.Text.Encoding.UTF8.GetBytes(csv), "text/csv", "valid_keys.csv");
        }

        return Ok(validKeys);
    }

    /// <summary>
    /// Reset database (WARNING: Deletes all data)
    /// </summary>
    [HttpPost("reset-database")]
    public async Task<IActionResult> ResetDatabase(
        [FromBody] ResetRequest request,
        [FromHeader(Name = "X-Node-Token")] string nodeToken)
    {
        var user = await GetAuthenticatedUser(nodeToken);
        if (user == null || !user.IsAdmin) return Unauthorized("Admin access required");

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

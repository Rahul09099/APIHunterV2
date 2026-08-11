using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using UnsecuredAPIKeys.Data;
using UnsecuredAPIKeys.Services;
using UnsecuredAPIKeys.Services.Telegram;
using UnsecuredAPIKeys.WebAPI.Services;


namespace UnsecuredAPIKeys.WebAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ScraperController : ControllerBase
{
    private readonly DBContext _dbContext;
    private readonly IDbContextFactory<DBContext> _dbContextFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly BackgroundJobManager _jobManager;
    private readonly DashboardAccessService _accessService;

    public ScraperController(
        DBContext dbContext,
        IDbContextFactory<DBContext> dbContextFactory,
        IHttpClientFactory httpClientFactory,
        BackgroundJobManager jobManager,
        DashboardAccessService accessService)
    {
        _dbContext = dbContext;
        _dbContextFactory = dbContextFactory;
        _httpClientFactory = httpClientFactory;
        _jobManager = jobManager;
        _accessService = accessService;
    }

    private async Task<bool> IsAdministratorAsync(string? nodeToken, string? accessToken)
    {
        if (_accessService.TryGetSession(accessToken, out var session) && session is not null)
        {
            return session.Role == DashboardAccessRole.Admin;
        }

        return !string.IsNullOrEmpty(nodeToken) &&
               await _dbContext.TelegramSubscribers.AnyAsync(s => s.NodeToken == nodeToken && s.IsAdmin);
    }

    /// <summary>
    /// Start the scraper service in the background
    /// </summary>
    [HttpPost("start")]
    public async Task<IActionResult> StartScraper(
        [FromHeader(Name = "X-Node-Token")] string? nodeToken,
        [FromHeader(Name = "X-Access-Token")] string? accessToken)
    {
        if (!await IsAdministratorAsync(nodeToken, accessToken))
            return Unauthorized(new { message = "Admin access required" });

        var isAlreadyRunning = _jobManager.GetAllJobs().Any(j => (j.JobType == "Scraper" || j.JobType == "AutoScraper-All" || j.JobType.StartsWith("Scraper-")) && j.Status == "Running");
        if (isAlreadyRunning) return Conflict(new { message = "A scraper job is already running." });

        var hasTokens = await _dbContext.SearchProviderTokens
            .AnyAsync(t => t.IsEnabled && t.SearchProvider == UnsecuredAPIKeys.Data.Common.SearchProviderEnum.GitHub);

        if (!hasTokens)
        {
            return BadRequest(new { 
                message = "Scraper cannot start: No enabled GitHub search tokens configured in database. Please add a GitHub token first." 
            });
        }

        var jobId = _jobManager.StartJob("Scraper", async (cancellationToken) =>
        {
            var scraper = new ScraperService(_dbContext, _dbContextFactory, _httpClientFactory);
            await scraper.RunScrapeAllGroupsAsync(null, cancellationToken);
        });

        return Ok(new { 
            message = "Scraper started successfully",
            jobId = jobId,
            status = "Running"
        });
    }

    /// <summary>
    /// Stop a running scraper job
    /// </summary>
    [HttpPost("stop/{jobId}")]
    public async Task<IActionResult> StopScraper(
        string jobId,
        [FromHeader(Name = "X-Node-Token")] string? nodeToken,
        [FromHeader(Name = "X-Access-Token")] string? accessToken)
    {
        if (!await IsAdministratorAsync(nodeToken, accessToken))
            return Unauthorized(new { message = "Admin access required" });

        var success = _jobManager.StopJob(jobId);
        
        if (success)
        {
            return Ok(new { message = "Scraper stop requested", jobId = jobId });
        }
        
        return NotFound(new { message = "Job not found or already completed", jobId = jobId });
    }

    /// <summary>
    /// Get status of a scraper job
    /// </summary>
    [HttpGet("status/{jobId}")]
    public async Task<IActionResult> GetJobStatus(
        string jobId,
        [FromHeader(Name = "X-Node-Token")] string? nodeToken,
        [FromHeader(Name = "X-Access-Token")] string? accessToken)
    {
        if (!await IsAdministratorAsync(nodeToken, accessToken))
            return Unauthorized(new { message = "Admin access required" });

        var jobInfo = _jobManager.GetJobInfo(jobId);
        
        if (jobInfo == null)
        {
            return NotFound(new { message = "Job not found", jobId = jobId });
        }
        
        return Ok(jobInfo);
    }

    /// <summary>
    /// Get all scraper jobs
    /// </summary>
    [HttpGet("jobs")]
    public async Task<IActionResult> GetAllJobs(
        [FromHeader(Name = "X-Node-Token")] string? nodeToken,
        [FromHeader(Name = "X-Access-Token")] string? accessToken)
    {
        if (!await IsAdministratorAsync(nodeToken, accessToken))
            return Unauthorized(new { message = "Admin access required" });

        var jobs = _jobManager.GetAllJobs()
            .Where(j => j.JobType == "Scraper");
        
        return Ok(jobs);
    }
}

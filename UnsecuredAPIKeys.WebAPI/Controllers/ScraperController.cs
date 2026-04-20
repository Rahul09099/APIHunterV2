using Microsoft.AspNetCore.Mvc;
using UnsecuredAPIKeys.Data;
using UnsecuredAPIKeys.Services;
using UnsecuredAPIKeys.Services.Telegram;

namespace UnsecuredAPIKeys.WebAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ScraperController : ControllerBase
{
    private readonly DBContext _dbContext;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly BackgroundJobManager _jobManager;

    public ScraperController(
        DBContext dbContext,
        IHttpClientFactory httpClientFactory,
        BackgroundJobManager jobManager)
    {
        _dbContext = dbContext;
        _httpClientFactory = httpClientFactory;
        _jobManager = jobManager;
    }

    /// <summary>
    /// Start the scraper service in the background
    /// </summary>
    [HttpPost("start")]
    public async Task<IActionResult> StartScraper([FromHeader(Name = "X-Node-Token")] string nodeToken)
    {
        var node = await _dbContext.TelegramSubscribers
            .FirstOrDefaultAsync(s => s.NodeToken == nodeToken);

        if (node == null) return Unauthorized(new { message = "Invalid node token" });

        var jobId = _jobManager.StartJob("Scraper", async (cancellationToken) =>
        {
            var scraper = new ScraperService(_dbContext, _httpClientFactory);
            await scraper.RunScrapeAllGroupsAsync(null, cancellationToken);
        }, node.TelegramId);

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
    public IActionResult StopScraper(string jobId)
    {
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
    public IActionResult GetJobStatus(string jobId)
    {
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
    public async Task<IActionResult> GetAllJobs([FromHeader(Name = "X-Node-Token")] string nodeToken)
    {
        var node = await _dbContext.TelegramSubscribers
            .FirstOrDefaultAsync(s => s.NodeToken == nodeToken);

        if (node == null) return Unauthorized(new { message = "Invalid node token" });

        var jobs = _jobManager.GetAllJobs()
            .Where(j => j.JobType == "Scraper");
        
        if (!node.IsAdmin)
        {
            jobs = jobs.Where(j => j.OwnerTelegramId == node.TelegramId);
        }
        
        return Ok(jobs);
    }
}

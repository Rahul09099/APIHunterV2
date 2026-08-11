using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using UnsecuredAPIKeys.Data;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Services;
using UnsecuredAPIKeys.Services.Telegram;
using UnsecuredAPIKeys.WebAPI.Services;

namespace UnsecuredAPIKeys.WebAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
public class VerifierController : ControllerBase
{
    private readonly DBContext _dbContext;
    private readonly IDbContextFactory<DBContext> _dbContextFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly BackgroundJobManager _jobManager;
    private readonly DatabaseService _dbService;
    private readonly DashboardAccessService _accessService;

    public VerifierController(
        DBContext dbContext,
        IDbContextFactory<DBContext> dbContextFactory,
        IHttpClientFactory httpClientFactory,
        BackgroundJobManager jobManager,
        DatabaseService dbService,
        DashboardAccessService accessService)
    {
        _dbContext = dbContext;
        _dbContextFactory = dbContextFactory;
        _httpClientFactory = httpClientFactory;
        _jobManager = jobManager;
        _dbService = dbService;
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
    /// Start the verifier service for all API types or specific types
    /// </summary>
    /// <param name="apiTypes">Optional comma-separated list of API types (e.g., "OpenAI,Anthropic")</param>
    /// <param name="reverify">Set to true to only re-verify existing valid keys</param>
    [HttpPost("start")]
    public async Task<IActionResult> StartVerifier(
        [FromHeader(Name = "X-Node-Token")] string? nodeToken,
        [FromHeader(Name = "X-Access-Token")] string? accessToken,
        [FromQuery] string? apiTypes = null,
        [FromQuery] bool reverify = false)
    {
        if (!await IsAdministratorAsync(nodeToken, accessToken))
            return Unauthorized(new { message = "Admin access required" });

        var isAlreadyRunning = _jobManager.GetAllJobs().Any(j => j.JobType == "Verifier" && j.Status == "Running");
        if (isAlreadyRunning) return Conflict(new { message = "A verifier job is already running." });

        HashSet<ApiTypeEnum>? selectedTypes = null;
        
        if (!string.IsNullOrEmpty(apiTypes))
        {
            selectedTypes = new HashSet<ApiTypeEnum>();
            var typeNames = apiTypes.Split(',', StringSplitOptions.RemoveEmptyEntries);
            
            foreach (var typeName in typeNames)
            {
                if (Enum.TryParse<ApiTypeEnum>(typeName.Trim(), true, out var apiType))
                {
                    selectedTypes.Add(apiType);
                }
            }
        }

        var jobId = _jobManager.StartJob("Verifier", async (cancellationToken) =>
        {
            var verifier = new VerifierService(_dbContext, _dbContextFactory, _httpClientFactory, selectedTypes, reverify);
            await verifier.RunAsync(cancellationToken);
        });

        var typesList = selectedTypes != null 
            ? string.Join(", ", selectedTypes) 
            : "All API Types";

        return Ok(new { 
            message = "Verifier started successfully",
            jobId = jobId,
            apiTypes = typesList,
            status = "Running"
        });
    }

    /// <summary>
    /// Stop a running verifier job
    /// </summary>
    [HttpPost("stop/{jobId}")]
    public async Task<IActionResult> StopVerifier(
        string jobId,
        [FromHeader(Name = "X-Node-Token")] string? nodeToken,
        [FromHeader(Name = "X-Access-Token")] string? accessToken)
    {
        if (!await IsAdministratorAsync(nodeToken, accessToken))
            return Unauthorized(new { message = "Admin access required" });

        var success = _jobManager.StopJob(jobId);
        
        if (success)
        {
            return Ok(new { message = "Verifier stop requested", jobId = jobId });
        }
        
        return NotFound(new { message = "Job not found or already completed", jobId = jobId });
    }

    /// <summary>
    /// Get status of a verifier job
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
    /// Get all verifier jobs
    /// </summary>
    [HttpGet("jobs")]
    public async Task<IActionResult> GetAllJobs(
        [FromHeader(Name = "X-Node-Token")] string? nodeToken,
        [FromHeader(Name = "X-Access-Token")] string? accessToken)
    {
        if (!await IsAdministratorAsync(nodeToken, accessToken))
            return Unauthorized(new { message = "Admin access required" });

        var jobs = _jobManager.GetAllJobs()
            .Where(j => j.JobType == "Verifier");
        
        return Ok(jobs);
    }

    /// <summary>
    /// Get list of available API types
    /// </summary>
    [HttpGet("api-types")]
    public IActionResult GetApiTypes()
    {
        var apiTypes = Enum.GetValues<ApiTypeEnum>()
            .Where(t => t != ApiTypeEnum.Unknown)
            .Select(t => new { 
                name = t.ToString(), 
                value = (int)t,
                category = DatabaseService.GetCategoryForApiType(t).ToString()
            });
        
        return Ok(apiTypes);
    }
}

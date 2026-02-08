using Microsoft.AspNetCore.Mvc;
using UnsecuredAPIKeys.Data;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Services;
using UnsecuredAPIKeys.Services.Telegram;

namespace UnsecuredAPIKeys.WebAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
public class VerifierController : ControllerBase
{
    private readonly DBContext _dbContext;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly BackgroundJobManager _jobManager;
    private readonly DatabaseService _dbService;

    public VerifierController(
        DBContext dbContext,
        IHttpClientFactory httpClientFactory,
        BackgroundJobManager jobManager,
        DatabaseService dbService)
    {
        _dbContext = dbContext;
        _httpClientFactory = httpClientFactory;
        _jobManager = jobManager;
        _dbService = dbService;
    }

    /// <summary>
    /// Start the verifier service for all API types or specific types
    /// </summary>
    /// <param name="apiTypes">Optional comma-separated list of API types (e.g., "OpenAI,Anthropic")</param>
    [HttpPost("start")]
    public async Task<IActionResult> StartVerifier([FromQuery] string? apiTypes = null)
    {
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
            var verifier = new VerifierService(_dbContext, _httpClientFactory, selectedTypes);
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
    public IActionResult StopVerifier(string jobId)
    {
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
    /// Get all verifier jobs
    /// </summary>
    [HttpGet("jobs")]
    public IActionResult GetAllJobs()
    {
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

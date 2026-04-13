using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using UnsecuredAPIKeys.Data;
using UnsecuredAPIKeys.Data.DTOs;
using UnsecuredAPIKeys.Data.Models;
using UnsecuredAPIKeys.Data.Common;

namespace UnsecuredAPIKeys.WebAPI.Controllers
{
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

        [HttpPost("heartbeat")]
        public async Task<IActionResult> Heartbeat([FromHeader(Name = "X-Node-Token")] string token)
        {
            if (string.IsNullOrEmpty(token)) return Unauthorized();

            var subscriber = await _dbContext.TelegramSubscribers
                .FirstOrDefaultAsync(s => s.NodeToken == token);

            if (subscriber == null || subscriber.SubscriptionExpiryUtc < DateTime.UtcNow)
                return Unauthorized("Invalid or expired node token.");

            subscriber.LastNodeHeartbeatUtc = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync();

            return Ok(new { status = "active", expiry = subscriber.SubscriptionExpiryUtc });
        }

        [HttpPost("report")]
        public async Task<IActionResult> ReportDiscovery(
            [FromHeader(Name = "X-Node-Token")] string token,
            [FromBody] NodeBulkReportDto report)
        {
            if (string.IsNullOrEmpty(token)) return Unauthorized();

            var subscriber = await _dbContext.TelegramSubscribers
                .FirstOrDefaultAsync(s => s.NodeToken == token);

            if (subscriber == null || subscriber.SubscriptionExpiryUtc < DateTime.UtcNow)
                return Unauthorized("Invalid or expired node token.");

            int newKeys = 0;
            foreach (var discovery in report.Discoveries)
            {
                // Check if already exists
                var exists = await _dbContext.APIKeys.AnyAsync(k => k.ApiKey == discovery.ApiKey);
                if (exists) continue;

                var apiKey = new APIKey
                {
                    ApiKey = discovery.ApiKey,
                    ApiType = discovery.ApiType,
                    Status = ApiStatusEnum.Unverified,
                    Metadata = discovery.Metadata,
                    FirstFoundUTC = DateTime.UtcNow,
                    LastFoundUTC = DateTime.UtcNow,
                    DiscoveredByTelegramId = subscriber.TelegramId
                };

                apiKey.References.Add(new RepoReference
                {
                    RepoName = discovery.RepoName,
                    RepoOwner = discovery.RepoOwner,
                    FilePath = discovery.FilePath,
                    FileURL = discovery.FileUrl,
                    FoundUTC = DateTime.UtcNow
                });

                _dbContext.APIKeys.Add(apiKey);
                newKeys++;
            }

            if (newKeys > 0)
            {
                await _dbContext.SaveChangesAsync();
                subscriber.LastNodeHeartbeatUtc = DateTime.UtcNow;
                await _dbContext.SaveChangesAsync();
            }

            return Ok(new { accepted = true, newKeys = newKeys });
        }
    }
}

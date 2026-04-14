using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UnsecuredAPIKeys.Data;
using Microsoft.EntityFrameworkCore;

namespace UnsecuredAPIKeys.Services.Telegram;

/// <summary>
/// Background service that pings active worker nodes to keep them alive on Render.
/// </summary>
public class NodeKeepAliveService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<NodeKeepAliveService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;

    public NodeKeepAliveService(
        IServiceProvider serviceProvider,
        ILogger<NodeKeepAliveService> logger,
        IHttpClientFactory httpClientFactory)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Node Keep-Alive Service started.");

        // Wait 2 minutes after startup before first ping to allow workers to report in
        await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<DBContext>();

                // Get all nodes with a URL that have reported a heartbeat in the last 20 minutes
                var activeThreshold = DateTime.UtcNow.AddMinutes(-20);
                var nodesToPing = await dbContext.TelegramSubscribers
                    .Where(s => !string.IsNullOrEmpty(s.NodeUrl) && s.LastNodeHeartbeatUtc > activeThreshold)
                    .ToListAsync(stoppingToken);

                if (nodesToPing.Any())
                {
                    _logger.LogInformation("Sending keep-alive pings to {Count} active nodes...", nodesToPing.Count);
                    using var client = _httpClientFactory.CreateClient();
                    
                    foreach (var node in nodesToPing)
                    {
                        try 
                        {
                            var url = node.NodeUrl!.TrimEnd('/') + "/health";
                            _logger.LogDebug("Pinging node: {Url}", url);
                            
                            // Fire and forget (optional: await with timeout)
                            _ = client.GetAsync(url, stoppingToken);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning("Failed to ping node {Id}: {Msg}", node.TelegramId, ex.Message);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in Keep-Alive loop");
            }

            // Ping every 14 minutes (Render sleeps after 15 mins of inactivity)
            await Task.Delay(TimeSpan.FromMinutes(14), stoppingToken);
        }
    }
}

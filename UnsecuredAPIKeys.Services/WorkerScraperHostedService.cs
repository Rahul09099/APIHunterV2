using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace UnsecuredAPIKeys.Services;

/// <summary>
/// Worker scraper hosted service (Wave 13, Task 13.2). Registered only when
/// `IS_WORKER_MODE=true`; Master mode never registers this service and keeps its own
/// monitoring separate. Invalid configuration (missing token, non-HTTPS Master URL) is
/// unhealthy: the service logs once and sends no traffic. Each cycle runs in its own DI
/// scope; heartbeats run on an independent loop; Master/database outage uses bounded
/// exponential backoff with no local credential fallback.
/// </summary>
public sealed class WorkerScraperHostedService(
    IServiceProvider services,
    WorkerScraperOptions options,
    ILogger<WorkerScraperHostedService> logger) : BackgroundService
{
    internal static TimeSpan ComputeBackoff(
        int consecutiveFailures,
        TimeSpan initial,
        TimeSpan max)
    {
        if (consecutiveFailures <= 0)
        {
            return TimeSpan.Zero;
        }

        double factor;
        try
        {
            factor = Math.Pow(2, consecutiveFailures - 1);
        }
        catch
        {
            return max;
        }

        double ticks;
        try
        {
            ticks = checked((double)initial.Ticks * factor);
        }
        catch (OverflowException)
        {
            return max;
        }

        if (double.IsNaN(ticks) || double.IsInfinity(ticks) || ticks >= max.Ticks)
        {
            return max;
        }

        return TimeSpan.FromTicks(Math.Max(initial.Ticks, (long)ticks));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var (healthy, error) = options.Validate();
        if (!healthy)
        {
            logger.LogError("Worker configuration unhealthy: {Error} No traffic will be sent.", error);
            return;
        }

        logger.LogInformation("Worker scraper started against {Master}.", options.MasterApiUrl);

        using var heartbeatLifetime = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var heartbeat = Task.Run(
            () => HeartbeatLoopAsync(heartbeatLifetime.Token),
            heartbeatLifetime.Token);

        try
        {
            var consecutiveOutages = 0;
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = services.CreateScope();
                    var runner = scope.ServiceProvider.GetRequiredService<WorkerCycleRunner>();
                    var result = await runner.RunCycleAsync(stoppingToken);

                    consecutiveOutages = result.Outage ? consecutiveOutages + 1 : 0;
                    var delay = result.Outage
                        ? ComputeBackoff(consecutiveOutages, options.OutageBackoffInitial, options.OutageBackoffMax)
                        : options.CycleInterval;

                    logger.LogInformation(
                        "Worker cycle complete: {Queries} queries, {Operations} operations, " +
                        "{Reported} discoveries, {Renewals} renewals, {Failures} failures.",
                        result.QueriesSynced, result.OperationsExecuted,
                        result.DiscoveriesReported, result.Renewals, result.Failures);

                    await Task.Delay(delay, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception cycleError)
                {
                    consecutiveOutages++;
                    logger.LogError(cycleError, "Worker cycle failed; backing off.");
                    await Task.Delay(
                        ComputeBackoff(consecutiveOutages, options.OutageBackoffInitial, options.OutageBackoffMax),
                        stoppingToken);
                }
            }
        }
        finally
        {
            // Stop-before-expiry: in-flight leases are completed by runners' finally blocks;
            // here we only stop the independent heartbeat and observe its shutdown.
            await heartbeatLifetime.CancelAsync();
            try
            {
                await heartbeat;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception heartbeatError)
            {
                logger.LogWarning(heartbeatError, "Worker heartbeat loop did not shut down cleanly.");
            }
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var scope = services.CreateScope();
                var master = scope.ServiceProvider.GetRequiredService<IMasterApiClient>();
                await master.SendHeartbeatAsync(cancellationToken);
            }
            catch (Exception error)
            {
                logger.LogDebug(error, "Worker heartbeat failed.");
            }

            try
            {
                await Task.Delay(options.HeartbeatInterval, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}

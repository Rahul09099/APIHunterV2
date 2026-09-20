using Microsoft.AspNetCore.Mvc;
using UnsecuredAPIKeys.Services;

namespace UnsecuredAPIKeys.WebAPI.Controllers;

[ApiController]
public sealed class ReadinessController(
    ISearchPlatformSchedulingReadinessService readinessService,
    ProviderInstanceReadinessState readinessState) : ControllerBase
{
    /// <summary>
    /// Re-evaluates and returns scheduling readiness independently from process liveness.
    /// </summary>
    [HttpGet("/health/readiness")]
    [HttpGet("/api/health/readiness")]
    [ProducesResponseType<SchedulingReadinessProjection>(StatusCodes.Status200OK)]
    [ProducesResponseType<SchedulingReadinessProjection>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<SchedulingReadinessProjection>> GetSchedulingReadiness(
        CancellationToken cancellationToken)
    {
        ProviderInstanceReadinessReport report;
        try
        {
            report = await readinessService.EvaluateAsync(cancellationToken);
        }
        catch when (!cancellationToken.IsCancellationRequested)
        {
            report = ProviderInstanceReadinessReport.NotEvaluated;
        }

        readinessState.Update(report);
        var projection = SchedulingReadinessProjection.From(report);

        return report.IsReady
            ? Ok(projection)
            : StatusCode(StatusCodes.Status503ServiceUnavailable, projection);
    }
}

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using UnsecuredAPIKeys.Services;

namespace UnsecuredAPIKeys.WebAPI.Services;

/// <summary>
/// Fails closed before a scheduling action can access the database or start work.
/// Non-scheduling endpoints, including process liveness and job control, remain available.
/// </summary>
public sealed class SchedulingReadinessFilter(
    ISearchPlatformSchedulingReadinessService readinessService,
    ProviderInstanceReadinessState readinessState)
    : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(
        ActionExecutingContext context,
        ActionExecutionDelegate next)
    {
        ProviderInstanceReadinessReport report;
        try
        {
            report = await readinessService.EvaluateAsync(context.HttpContext.RequestAborted);
        }
        catch when (!context.HttpContext.RequestAborted.IsCancellationRequested)
        {
            report = ProviderInstanceReadinessReport.NotEvaluated;
        }

        readinessState.Update(report);
        if (report.IsReady)
        {
            await next();
            return;
        }

        context.Result = new ObjectResult(SchedulingReadinessProjection.From(report))
        {
            StatusCode = StatusCodes.Status503ServiceUnavailable
        };
    }
}

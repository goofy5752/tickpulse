using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;

namespace TickPulse.Api.Problems;

/// <summary>
/// Returns a problem from a controller through <see cref="IProblemDetailsService"/>, the same writers middleware
/// errors use. An error is therefore never replaced by a 406: when the client accepts neither JSON nor XML the
/// problem is written as JSON.
/// </summary>
public sealed class ProblemResult(ProblemDetails problem) : IActionResult, IStatusCodeActionResult
{
    public ProblemDetails Problem { get; } = problem ?? throw new ArgumentNullException(nameof(problem));

    public int? StatusCode => Problem.Status;

    public async Task ExecuteResultAsync(ActionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var httpContext = context.HttpContext;
        httpContext.Response.StatusCode = Problem.Status ?? StatusCodes.Status500InternalServerError;

        var problems = httpContext.RequestServices.GetRequiredService<IProblemDetailsService>();
        await problems.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = Problem,
            AdditionalMetadata = httpContext.GetEndpoint()?.Metadata,
        });
    }
}

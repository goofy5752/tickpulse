using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

namespace TickPulse.Api.Problems;

/// <summary>
/// Writes problems as <c>application/problem+json</c>. It is the last writer and accepts every request, so a problem
/// is always written: a client whose <c>Accept</c> header lists neither JSON nor XML (and so gets a 406, or an error)
/// still receives a problem body, as JSON.
/// </summary>
public sealed class JsonProblemDetailsWriter(IOptions<ProblemDetailsOptions> options, IOptions<JsonOptions> json) : IProblemDetailsWriter
{
    public const string ContentType = "application/problem+json";

    public bool CanWrite(ProblemDetailsContext context) => true;

    public ValueTask WriteAsync(ProblemDetailsContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ProblemDefaults.Apply(context, options.Value);

        var problem = context.ProblemDetails;
        return new ValueTask(context.HttpContext.Response.WriteAsJsonAsync(
            problem,
            problem.GetType(),
            json.Value.SerializerOptions,
            ContentType,
            context.HttpContext.RequestAborted));
    }
}

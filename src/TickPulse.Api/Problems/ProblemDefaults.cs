using Microsoft.AspNetCore.WebUtilities;

namespace TickPulse.Api.Problems;

/// <summary>
/// Completes a problem before either writer serialises it, so middleware errors (status code pages, exception handler,
/// rate limiter) look like the problems controllers return.
/// </summary>
internal static class ProblemDefaults
{
    public const string BlankType = "about:blank";
    public const string InternalDetail = "An unexpected error occurred.";
    public const string NotAcceptableDetail = "None of the media types in the Accept header can be produced; use application/json or application/xml.";

    public static void Apply(ProblemDetailsContext context, ProblemDetailsOptions options)
    {
        var problem = context.ProblemDetails;
        var status = problem.Status ?? context.HttpContext.Response.StatusCode;
        problem.Status = status;

        // An unhandled exception never reveals anything about itself: no message, no type, no stack trace.
        if (context.Exception is not null && status == StatusCodes.Status500InternalServerError)
        {
            Set(problem, ProblemSlugs.Internal, InternalDetail, force: true);
        }
        else if (problem.Type is null)
        {
            switch (status)
            {
                case StatusCodes.Status406NotAcceptable:
                    Set(problem, ProblemSlugs.NotAcceptable, NotAcceptableDetail, force: false);
                    break;
                case StatusCodes.Status429TooManyRequests:
                    Set(problem, ProblemSlugs.RateLimited, detail: null, force: false);
                    break;
                case StatusCodes.Status500InternalServerError:
                    Set(problem, ProblemSlugs.Internal, InternalDetail, force: false);
                    break;
                default:
                    // RFC 9457 §4.2.1: a problem with no more semantics than its status code.
                    problem.Type = BlankType;
                    problem.Title ??= ReasonPhrases.GetReasonPhrase(status);
                    break;
            }
        }

        problem.Title ??= ReasonPhrases.GetReasonPhrase(status);
        problem.Extensions.TryAdd(ProblemFactory.TraceIdKey, ProblemFactory.TraceId(context.HttpContext));
        options.CustomizeProblemDetails?.Invoke(context);
    }

    private static void Set(Microsoft.AspNetCore.Mvc.ProblemDetails problem, string slug, string? detail, bool force)
    {
        problem.Type = ProblemFactory.TypeFor(slug);
        if (force)
        {
            problem.Title = ProblemFactory.TitleFor(slug);
            problem.Detail = detail;
            problem.Extensions.Clear();
        }
        else
        {
            problem.Title ??= ProblemFactory.TitleFor(slug);
            problem.Detail ??= detail;
        }
    }
}

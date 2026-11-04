using System.Globalization;
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
    public const string NotFoundDetail = "No resource matches the request path.";
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
                case StatusCodes.Status404NotFound:
                    Set(problem, ProblemSlugs.NotFound, NotFoundDetail, force: false);
                    break;
                case StatusCodes.Status405MethodNotAllowed:
                    Set(problem, ProblemSlugs.MethodNotAllowed, MethodNotAllowedDetail(context.HttpContext), force: false);
                    break;
                case StatusCodes.Status406NotAcceptable:
                    Set(problem, ProblemSlugs.NotAcceptable, NotAcceptableDetail, force: false);
                    break;
                case StatusCodes.Status415UnsupportedMediaType:
                    Set(problem, ProblemSlugs.UnsupportedMediaType, UnsupportedMediaTypeDetail(context.HttpContext), force: false);
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

    // Routing answers 405 itself and sets Allow; the problem only points at it.
    private static string MethodNotAllowedDetail(HttpContext context) => string.Create(
        CultureInfo.InvariantCulture,
        $"The {Truncate(context.Request.Method)} method is not supported by this resource; the Allow header lists the methods that are.");

    private static string UnsupportedMediaTypeDetail(HttpContext context) =>
        context.Request.ContentType is { Length: > 0 } contentType
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"The request body's media type '{Truncate(contentType)}' is not supported; send application/json or application/xml.")
            : "The request body has no media type; send application/json or application/xml.";

    // The method and the Content-Type come from the client; a problem never echoes more than a short prefix of them.
    private static string Truncate(string value) => value.Length <= 100 ? value : value[..100] + "…";

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

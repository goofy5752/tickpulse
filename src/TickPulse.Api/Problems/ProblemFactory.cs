using System.Collections.Frozen;
using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using TickPulse.Application.Results;

namespace TickPulse.Api.Problems;

/// <summary>Builds the RFC 9457 problems TickPulse returns: a fixed set of type URIs plus a <c>traceId</c> extension.</summary>
public static class ProblemFactory
{
    /// <summary>Base URI of every TickPulse problem type; the slug is appended.</summary>
    public const string TypeBase = "https://tickpulse.dev/problems/";

    /// <summary>Name of the extension member that carries the request's trace id.</summary>
    public const string TraceIdKey = "traceId";

    /// <summary>The detail of a <see cref="CreateValidation"/> problem; the <c>errors</c> member names the parameters.</summary>
    public const string ValidationDetail = "One or more request parameters are invalid.";

    private static readonly FrozenDictionary<string, string> Titles = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [ProblemSlugs.UnknownSymbol] = "Unknown symbol",
        [ProblemSlugs.NoData] = "No data",
        [ProblemSlugs.InvalidSymbol] = "Invalid symbol",
        [ProblemSlugs.SymbolLimit] = "Symbol limit reached",
        [ProblemSlugs.AdminDisabled] = "Admin API disabled",
        [ProblemSlugs.Unauthorized] = "Unauthorized",
        [ProblemSlugs.Validation] = "Validation failed",
        [ProblemSlugs.NotAcceptable] = "Not acceptable",
        [ProblemSlugs.RateLimited] = "Too many requests",
        [ProblemSlugs.UpstreamUnavailable] = "Upstream unavailable",
        [ProblemSlugs.Internal] = "Internal server error",
        [ProblemSlugs.NotFound] = "Not found",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>A problem for a use-case failure.</summary>
    public static ProblemDetails Create(HttpContext httpContext, AppError error, int status, string detail) =>
        Create(httpContext, SlugFor(error), status, detail);

    /// <summary>A problem of the type <c>https://tickpulse.dev/problems/{slug}</c>.</summary>
    /// <exception cref="ArgumentException"><paramref name="slug"/> is not one of <see cref="ProblemSlugs"/>.</exception>
    public static ProblemDetails Create(HttpContext httpContext, string slug, int status, string detail)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);

        return new ProblemDetails
        {
            Type = TypeFor(slug),
            Title = TitleFor(slug),
            Status = status,
            Detail = detail,
            Extensions = { [TraceIdKey] = TraceId(httpContext) },
        };
    }

    /// <summary>A problem for a use-case failure, with the HTTP status that the error maps to.</summary>
    public static ProblemDetails Create(HttpContext httpContext, AppError error, string detail) =>
        Create(httpContext, error, StatusFor(error), detail);

    /// <summary>
    /// A 400 <c>validation</c> problem listing the invalid request parameters (<c>errors</c>, keyed by parameter name),
    /// for parameters that could not be bound or parsed.
    /// </summary>
    public static ValidationProblemDetails CreateValidation(HttpContext httpContext, ModelStateDictionary modelState)
    {
        ArgumentNullException.ThrowIfNull(modelState);
        return Validation(httpContext, new ValidationProblemDetails(modelState), ValidationDetail);
    }

    /// <summary>
    /// A 400 <c>validation</c> problem about one request parameter: <paramref name="message"/> is both the <c>detail</c>
    /// and the only entry of <c>errors[parameter]</c>.
    /// </summary>
    public static ValidationProblemDetails CreateValidation(HttpContext httpContext, string parameter, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parameter);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal) { [parameter] = [message] };
        return Validation(httpContext, new ValidationProblemDetails(errors), message);
    }

    private static ValidationProblemDetails Validation(HttpContext httpContext, ValidationProblemDetails problem, string detail)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        problem.Type = TypeFor(ProblemSlugs.Validation);
        problem.Title = TitleFor(ProblemSlugs.Validation);
        problem.Status = StatusCodes.Status400BadRequest;
        problem.Detail = detail;
        problem.Extensions[TraceIdKey] = TraceId(httpContext);
        return problem;
    }

    /// <summary>The HTTP status of a use-case failure.</summary>
    public static int StatusFor(AppError error) => error switch
    {
        AppError.UnknownSymbol or AppError.NoData => StatusCodes.Status404NotFound,
        AppError.InvalidSymbol => StatusCodes.Status422UnprocessableEntity,
        AppError.SymbolLimit => StatusCodes.Status409Conflict,
        AppError.Validation => StatusCodes.Status400BadRequest,
        AppError.UpstreamUnavailable => StatusCodes.Status503ServiceUnavailable,
        _ => throw new ArgumentOutOfRangeException(nameof(error), error, "No status is defined for this error."),
    };

    /// <summary>The problem slug of a use-case failure.</summary>
    public static string SlugFor(AppError error) => error switch
    {
        AppError.UnknownSymbol => ProblemSlugs.UnknownSymbol,
        AppError.NoData => ProblemSlugs.NoData,
        AppError.InvalidSymbol => ProblemSlugs.InvalidSymbol,
        AppError.SymbolLimit => ProblemSlugs.SymbolLimit,
        AppError.Validation => ProblemSlugs.Validation,
        AppError.UpstreamUnavailable => ProblemSlugs.UpstreamUnavailable,
        _ => throw new ArgumentOutOfRangeException(nameof(error), error, "No problem type is defined for this error."),
    };

    /// <summary>The type URI of a slug.</summary>
    /// <exception cref="ArgumentException"><paramref name="slug"/> is not one of <see cref="ProblemSlugs"/>.</exception>
    public static string TypeFor(string slug) =>
        Titles.ContainsKey(slug) ? TypeBase + slug : throw new ArgumentException($"Unknown problem slug '{slug}'.", nameof(slug));

    /// <summary>The human-readable title of a slug.</summary>
    /// <exception cref="ArgumentException"><paramref name="slug"/> is not one of <see cref="ProblemSlugs"/>.</exception>
    public static string TitleFor(string slug) =>
        Titles.TryGetValue(slug, out var title) ? title : throw new ArgumentException($"Unknown problem slug '{slug}'.", nameof(slug));

    /// <summary>The W3C id of the current activity, or the request's own identifier when there is none.</summary>
    public static string TraceId(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        return Activity.Current?.Id ?? httpContext.TraceIdentifier;
    }
}

/// <summary>The slugs of TickPulse's problem types (<see cref="ProblemFactory.TypeBase"/> + slug).</summary>
public static class ProblemSlugs
{
    public const string UnknownSymbol = "unknown-symbol";
    public const string NoData = "no-data";
    public const string InvalidSymbol = "invalid-symbol";
    public const string SymbolLimit = "symbol-limit";
    public const string AdminDisabled = "admin-disabled";
    public const string Unauthorized = "unauthorized";
    public const string Validation = "validation";
    public const string NotAcceptable = "not-acceptable";
    public const string RateLimited = "rate-limited";
    public const string UpstreamUnavailable = "upstream-unavailable";
    public const string Internal = "internal";
    public const string NotFound = "not-found";
}

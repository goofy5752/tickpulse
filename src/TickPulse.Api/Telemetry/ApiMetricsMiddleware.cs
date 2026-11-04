using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Metadata;
using TickPulse.Infrastructure.Telemetry;

namespace TickPulse.Api.Telemetry;

/// <summary>
/// Counts every <c>/api</c> response into <c>tickpulse.api.responses</c> by route template, status code and
/// negotiated media type, so the dashboards can show the JSON/XML split next to ASP.NET Core's request metrics.
/// </summary>
/// <remarks>
/// It runs outside the exception handler and the status code pages, so it sees the final status of every response,
/// problems included. <c>route</c> is the same template as ASP.NET Core's <c>http.route</c>
/// (<c>api/{symbol}/24hAvgPrice</c>), also for a 500 after an unhandled exception, and is left out when no endpoint
/// matched. The dashboard, the hub, health and
/// documentation are not counted.
/// </remarks>
internal sealed class ApiMetricsMiddleware(RequestDelegate next, TickPulseMetrics metrics)
{
    /// <summary>The <c>content_type</c> of a response without a body type.</summary>
    public const string NoContentType = "none";

    private static readonly PathString ApiPrefix = "/api";

    // The negotiated types, so the usual responses never allocate a tag value.
    private static readonly string[] KnownMediaTypes =
    [
        "application/json",
        "application/xml",
        "text/xml",
        "application/problem+json",
        "application/problem+xml",
        "text/plain",
        "text/html",
    ];

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments(ApiPrefix))
        {
            await next(context);
            return;
        }

        try
        {
            await next(context);
        }
        finally
        {
            metrics.ApiResponse(Route(context), context.Response.StatusCode, MediaType(context.Response.ContentType));
        }
    }

    /// <summary>
    /// The route template (<c>http.route</c>) of the request. After an unhandled exception the exception handler has
    /// cleared the endpoint for its re-execution, so the one that threw is taken from <see cref="IExceptionHandlerFeature"/>.
    /// </summary>
    public static string? Route(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var endpoint = context.GetEndpoint() ?? context.Features.Get<IExceptionHandlerFeature>()?.Endpoint;
        return endpoint?.Metadata.GetMetadata<IRouteDiagnosticsMetadata>()?.Route;
    }

    /// <summary>The media type of a <c>Content-Type</c> value without its parameters, in lower case.</summary>
    public static string MediaType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return NoContentType;
        }

        var value = contentType.AsSpan();
        var parameters = value.IndexOf(';');
        if (parameters >= 0)
        {
            value = value[..parameters];
        }

        value = value.Trim();
        foreach (var known in KnownMediaTypes)
        {
            if (value.Equals(known, StringComparison.OrdinalIgnoreCase))
            {
                return known;
            }
        }

        return value.IsEmpty ? NoContentType : value.ToString().ToLowerInvariant();
    }
}

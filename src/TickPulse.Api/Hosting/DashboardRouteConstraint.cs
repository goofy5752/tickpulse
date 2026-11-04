namespace TickPulse.Api.Hosting;

/// <summary>
/// Route constraint of the dashboard fallback (<c>index.html</c>): rejects paths whose first segment belongs to the
/// API, the hub, health, the docs or the OpenAPI document, so those keep their own 404 problem instead of the page.
/// </summary>
internal sealed class DashboardRouteConstraint : IRouteConstraint
{
    public const string Name = "dashboard";

    /// <summary>Route pattern of the fallback: any non-file path outside the reserved prefixes.</summary>
    public const string FallbackPattern = "{*path:nonfile:" + Name + "}";

    private static readonly string[] ReservedSegments = ["api", "hubs", "health", "docs", "openapi"];

    public bool Match(HttpContext? httpContext, IRouter? route, string routeKey, RouteValueDictionary values, RouteDirection routeDirection)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (!values.TryGetValue(routeKey, out var value) || value is not string path || path.Length == 0)
        {
            return true;
        }

        var first = path.AsSpan().TrimStart('/');
        var slash = first.IndexOf('/');
        if (slash >= 0)
        {
            first = first[..slash];
        }

        foreach (var reserved in ReservedSegments)
        {
            if (first.Equals(reserved, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }
}

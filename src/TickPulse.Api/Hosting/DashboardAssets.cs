using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.StaticAssets;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Net.Http.Headers;

namespace TickPulse.Api.Hosting;

/// <summary>
/// Serves the dashboard's static files: precompressed at build time, with cache headers suited to Vite's output.
/// </summary>
/// <remarks>
/// <para><b>Compression.</b> The image build copies <c>web/dist</c> into <c>wwwroot</c> before <c>dotnet publish</c>,
/// so the SDK's static web assets pipeline writes a Brotli and a gzip copy of every file, a strong <c>ETag</c> per
/// encoding and the endpoint manifest that <see cref="MapDashboardAssets"/> serves (<c>MapStaticAssets</c>), picking
/// the encoding from <c>Accept-Encoding</c> with <c>Vary: Accept-Encoding</c>. Nothing is compressed per request, and
/// API responses are never compressed here: in production nginx (and Cloudflare) compress them, and nginx leaves the
/// already-encoded static responses alone.</para>
/// <para><b>Caching.</b> Everything under <c>/assets/</c> is Vite build output whose file names carry a content hash,
/// so it is cached for a year as <c>immutable</c> (<see cref="UseDashboardAssetCaching"/>); a new build has new
/// names. The SDK cannot recognise Vite's hash as a fingerprint and marks those files <c>no-cache</c>, which this rule
/// overrides. Every other file, above all <c>index.html</c> (which names the current assets), stays <c>no-cache</c>:
/// stored, but revalidated with its <c>ETag</c> on every use.</para>
/// <para>The dashboard fallback (<c>MapFallbackToFile</c>, <c>index.html</c> for client-side routes) and any file no
/// endpoint matches go through the static file middleware, uncompressed (nginx compresses them), with the same cache
/// rules. With a build (not publish) manifest, as in development and the tests, <c>MapStaticAssets</c> itself adds a
/// fallback endpoint that serves files missing from the manifest, uncompressed and with the same cache rules.</para>
/// </remarks>
internal static class DashboardAssets
{
    public const string ImmutableCacheControl = "public, max-age=31536000, immutable";
    public const string RevalidateCacheControl = "no-cache";

    /// <summary>The <c>http.route</c> of every dashboard file and of the dashboard fallback.</summary>
    public const string StaticRoute = "static";

    private static readonly PathString AssetsPrefix = new("/assets");

    /// <summary><c>no-cache</c> on every file <c>UseStaticFiles()</c> and the dashboard fallback serve.</summary>
    public static IServiceCollection AddTickPulseDashboardAssets(this IServiceCollection services) =>
        services.Configure<StaticFileOptions>(o => o.OnPrepareResponse = context =>
            context.Context.Response.Headers[HeaderNames.CacheControl] = RevalidateCacheControl);

    /// <summary>
    /// <c>public, max-age=31536000, immutable</c> on every successful <c>/assets/</c> response (200, 206 or 304),
    /// whichever of <c>MapStaticAssets</c> and <c>UseStaticFiles</c> served it. A miss (404) is not cached.
    /// </summary>
    public static IApplicationBuilder UseDashboardAssetCaching(this IApplicationBuilder app) =>
        app.Use((context, next) =>
        {
            if (context.Request.Path.StartsWithSegments(AssetsPrefix))
            {
                context.Response.OnStarting(
                    static state =>
                    {
                        var response = (HttpResponse)state;
                        if (response.StatusCode is StatusCodes.Status200OK or StatusCodes.Status206PartialContent or StatusCodes.Status304NotModified)
                        {
                            response.Headers[HeaderNames.CacheControl] = ImmutableCacheControl;
                        }

                        return Task.CompletedTask;
                    },
                    context.Response);
            }

            return next(context);
        });

    /// <summary>
    /// The build-time assets from the static web assets manifest (<paramref name="manifestPath"/>, by default the
    /// app's <c>{ApplicationName}.staticwebassets.endpoints.json</c>; empty when the build had no <c>wwwroot</c>).
    /// </summary>
    /// <remarks>Every file shares the <see cref="StaticRoute"/> <c>http.route</c> (see <see cref="UseDashboardRouteTag"/>).</remarks>
    public static StaticAssetsEndpointConventionBuilder MapDashboardAssets(this IEndpointRouteBuilder endpoints, string? manifestPath = null) =>
        endpoints.MapStaticAssets(manifestPath).WithMetadata(StaticRouteMetadata.Instance);

    /// <summary>The dashboard fallback: <c>index.html</c> for client-side routes, under the <see cref="StaticRoute"/> label.</summary>
    public static IEndpointConventionBuilder MapDashboardFallback(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapFallbackToFile(DashboardRouteConstraint.FallbackPattern, "index.html").WithMetadata(StaticRouteMetadata.Instance);

    /// <summary>
    /// Keeps the <c>http.route</c> of the request metrics bounded: every dashboard file and the fallback page count
    /// under one <see cref="StaticRoute"/> value, instead of one value per file name (which carry a content hash, so
    /// every deploy would add a new set of series).
    /// </summary>
    /// <remarks>
    /// <para>A <c>MapStaticAssets</c> or fallback endpoint carries the route itself (<see cref="StaticRouteMetadata"/>).
    /// The fallback and <c>UseStaticFiles</c>, though, serve the file with no endpoint (the fallback clears its own),
    /// and ASP.NET Core then records no route at all. This middleware adds the route for those: a successful
    /// <c>GET</c>/<c>HEAD</c> outside the API, hub, health and docs prefixes that ended without an endpoint. A miss
    /// (404) stays route-less, one bounded series.</para>
    /// </remarks>
    public static IApplicationBuilder UseDashboardRouteTag(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            await next(context);
            var response = context.Response;
            if (context.GetEndpoint() is null
                && (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
                && response.StatusCode is >= 200 and < 300 or StatusCodes.Status304NotModified
                && !DashboardRouteConstraint.IsReserved(context.Request.Path.Value ?? string.Empty)
                && context.Features.Get<IHttpMetricsTagsFeature>() is { } tags)
            {
                tags.Tags.Add(new KeyValuePair<string, object?>("http.route", StaticRoute));
            }
        });

    /// <summary>Gives an endpoint the <see cref="StaticRoute"/> <c>http.route</c> in the request metrics.</summary>
    internal sealed class StaticRouteMetadata : IRouteDiagnosticsMetadata
    {
        public static readonly StaticRouteMetadata Instance = new();

        public string Route => StaticRoute;
    }
}

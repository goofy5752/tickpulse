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
    public static StaticAssetsEndpointConventionBuilder MapDashboardAssets(this IEndpointRouteBuilder endpoints, string? manifestPath = null) =>
        endpoints.MapStaticAssets(manifestPath);
}

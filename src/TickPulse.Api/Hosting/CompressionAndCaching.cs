using System.IO.Compression;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Net.Http.Headers;

namespace TickPulse.Api.Hosting;

/// <summary>
/// Brotli and gzip for text responses (API JSON/XML, problems, the dashboard's HTML, JS and CSS), and cache headers for
/// the dashboard's static files.
/// </summary>
/// <remarks>
/// <para><b>Compression and BREACH.</b> Compressing a response that mixes a secret with text an attacker can inject lets
/// the attacker recover the secret from the compressed sizes. TickPulse responses carry no secret: the API sets no
/// cookies and has no CSRF or session tokens, and the only credential, the admin <c>X-Api-Key</c>, travels in a request
/// header and is never echoed. Compression is therefore on for HTTPS too (the proxy forwards
/// <c>X-Forwarded-Proto: https</c>). A future response that carries a secret must opt out
/// (<see cref="IHttpsCompressionFeature"/>, mode <see cref="HttpsCompressionMode.DoNotCompress"/>).</para>
/// <para><b>Proxies.</b> The middleware adds <c>Vary: Accept-Encoding</c> to every compressible response, next to
/// <c>Vary: Accept</c> on <c>/api</c>. nginx in production gzips only responses that have no
/// <c>Content-Encoding</c> yet, so nothing is compressed twice.</para>
/// <para><b>Caching.</b> Everything under <c>/assets/</c> is Vite build output whose file names carry a content hash,
/// so it is cached for a year as <c>immutable</c>; a new build has new names. Every other static file, above all
/// <c>index.html</c> (which names the current assets), is <c>no-cache</c>: stored, but revalidated with its ETag on
/// every use.</para>
/// </remarks>
internal static class CompressionAndCaching
{
    public const string ImmutableCacheControl = "public, max-age=31536000, immutable";
    public const string RevalidateCacheControl = "no-cache";

    private static readonly PathString AssetsPrefix = new("/assets");

    /// <summary>The compressed media types: the framework's text defaults plus problems, JS, SVG and the web manifest.</summary>
    public static readonly string[] MimeTypes =
    [
        .. ResponseCompressionDefaults.MimeTypes,
        "text/javascript",
        "application/problem+json",
        "application/problem+xml",
        "image/svg+xml",
        "application/manifest+json",
    ];

    public static IServiceCollection AddTickPulseCompressionAndCaching(this IServiceCollection services)
    {
        services.AddResponseCompression(o =>
        {
            o.EnableForHttps = true; // see BREACH above
            o.Providers.Add<BrotliCompressionProvider>(); // preferred when the client accepts both at equal quality
            o.Providers.Add<GzipCompressionProvider>();
            o.MimeTypes = MimeTypes.Distinct(StringComparer.OrdinalIgnoreCase);
        });

        // Responses are compressed on every request, so speed beats the last few percent of size.
        services.Configure<BrotliCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);
        services.Configure<GzipCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);

        // UseStaticFiles() and the dashboard fallback (MapFallbackToFile) both read these options.
        services.Configure<StaticFileOptions>(o => o.OnPrepareResponse = SetCacheControl);
        return services;
    }

    private static void SetCacheControl(StaticFileResponseContext context) =>
        context.Context.Response.Headers[HeaderNames.CacheControl] = context.Context.Request.Path.StartsWithSegments(AssetsPrefix)
            ? ImmutableCacheControl
            : RevalidateCacheControl;
}

namespace TickPulse.Api.Hosting;

/// <summary>
/// Adds <c>X-Content-Type-Options: nosniff</c>, <c>Referrer-Policy: no-referrer</c>, <c>X-Frame-Options: DENY</c> and a
/// <c>Content-Security-Policy</c> to every response. They are set when the response starts, so error responses (whose
/// headers the exception handler clears) carry them too.
/// </summary>
/// <remarks>
/// <para>Every response gets <see cref="ContentSecurityPolicy"/>: everything from this origin only, no inline script,
/// no plugins (<c>object-src 'none'</c>), and inline styles because React and the chart library set <c>style</c>
/// attributes. <c>connect-src 'self'</c> also admits the SignalR hub's WebSocket: CSP Level 3 matches a same-origin
/// <c>ws:</c>/<c>wss:</c> URL against <c>'self'</c>, so no blanket <c>ws:</c>/<c>wss:</c> source is needed (and a
/// browser that refused it would still reach the hub through SignalR's HTTP fallback). The dashboard's
/// <c>index.html</c> loads even its pre-paint theme script from a file (<c>/theme-init.js</c>).</para>
/// <para>The API reference under <c>/docs</c> gets <see cref="DocsContentSecurityPolicy"/> instead. Scalar boots from
/// an inline module script that carries its configuration, so that page allows inline scripts; its fonts, telemetry
/// and AI agent are switched off (see <c>Program</c>), so it still loads nothing from another origin.</para>
/// </remarks>
internal static class SecurityHeaders
{
    public const string ContentSecurityPolicy =
        "default-src 'self'; connect-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; " +
        "script-src 'self'; font-src 'self'; object-src 'none'; frame-ancestors 'none'; base-uri 'self'; " +
        "form-action 'self'";

    public const string DocsContentSecurityPolicy =
        "default-src 'self'; connect-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; " +
        "script-src 'self' 'unsafe-inline'; font-src 'self'; object-src 'none'; frame-ancestors 'none'; " +
        "base-uri 'self'; form-action 'self'";

    /// <summary>
    /// Head content for the Scalar page. Scalar bundles Zod, whose first schema probes for eval with <c>Function("")</c>;
    /// <see cref="DocsContentSecurityPolicy"/> has no <c>'unsafe-eval'</c>, so the probe would be blocked and reported
    /// as a violation. Zod's jitless mode (read from this global before <c>scalar.js</c> runs) skips the probe.
    /// </summary>
    public const string DocsHeadContent = "<script>globalThis.__zod_globalConfig = { jitless: true }</script>";

    /// <summary>The path of the Scalar API reference (and of its own scripts).</summary>
    public static readonly PathString DocsPath = new("/docs");

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.Use((context, next) =>
        {
            context.Response.OnStarting(
                static state =>
                {
                    var http = (HttpContext)state;
                    var headers = http.Response.Headers;
                    headers.XContentTypeOptions = "nosniff";
                    headers["Referrer-Policy"] = "no-referrer";
                    headers.XFrameOptions = "DENY";
                    headers.ContentSecurityPolicy = http.Request.Path.StartsWithSegments(DocsPath)
                        ? DocsContentSecurityPolicy
                        : ContentSecurityPolicy;
                    return Task.CompletedTask;
                },
                context);
            return next(context);
        });
}

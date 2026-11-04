namespace TickPulse.Api.Hosting;

/// <summary>
/// Adds <c>X-Content-Type-Options: nosniff</c>, <c>Referrer-Policy: no-referrer</c> and <c>X-Frame-Options: DENY</c>
/// to every response. They are set when the response starts, so error responses (whose headers the exception handler
/// clears) carry them too.
/// </summary>
internal static class SecurityHeaders
{
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.Use((context, next) =>
        {
            context.Response.OnStarting(
                static state =>
                {
                    var headers = ((HttpResponse)state).Headers;
                    headers.XContentTypeOptions = "nosniff";
                    headers["Referrer-Policy"] = "no-referrer";
                    headers.XFrameOptions = "DENY";
                    return Task.CompletedTask;
                },
                context.Response);
            return next(context);
        });
}

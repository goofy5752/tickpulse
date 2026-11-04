using Microsoft.Net.Http.Headers;

namespace TickPulse.Api.Hosting;

/// <summary>
/// Adds <c>Vary: Accept</c> to every <c>/api</c> response, errors included: the body is JSON or XML depending on the
/// request's <c>Accept</c> header, so caches must keep the representations apart. Set when the response starts, so it
/// survives the exception handler clearing the headers.
/// </summary>
internal static class VaryByAccept
{
    private static readonly PathString ApiPrefix = new("/api");

    public static IApplicationBuilder UseVaryByAcceptOnApi(this IApplicationBuilder app) =>
        app.Use((context, next) =>
        {
            if (context.Request.Path.StartsWithSegments(ApiPrefix))
            {
                context.Response.OnStarting(
                    static state =>
                    {
                        var headers = ((HttpResponse)state).Headers;
                        var present = headers.Vary.ToString()
                            .Split(',', StringSplitOptions.TrimEntries)
                            .Any(token => token.Equals(HeaderNames.Accept, StringComparison.OrdinalIgnoreCase) || token == "*");
                        if (!present)
                        {
                            headers.Append(HeaderNames.Vary, HeaderNames.Accept);
                        }

                        return Task.CompletedTask;
                    },
                    context.Response);
            }

            return next(context);
        });
}

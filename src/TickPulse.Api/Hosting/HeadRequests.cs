using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;

namespace TickPulse.Api.Hosting;

/// <summary>
/// <c>HEAD</c> wherever <c>GET</c> works (RFC 9110 §9.3.2): the same status and headers, never a body. Uptime checkers
/// and caches probe with it.
/// </summary>
/// <remarks>
/// MVC's <c>[HttpGet]</c> matches <c>GET</c> only. <see cref="WithHeadForGet"/> adds <c>HEAD</c> to the routing
/// metadata of every <c>GET</c> controller endpoint, so routing also lists it in the <c>Allow</c> header of a 405. The
/// action descriptors are left alone, so the OpenAPI document does not repeat every operation as <c>HEAD</c>.
/// Static files, the dashboard fallback and health already answer <c>HEAD</c>. <see cref="UseHeadWithoutBody"/> then
/// drops whatever body an endpoint writes; Kestrel would drop it too, but not every server does (the test server
/// does not).
/// </remarks>
internal static class HeadRequests
{
    /// <summary>Lets every <c>GET</c> endpoint of <paramref name="builder"/> also match <c>HEAD</c>.</summary>
    public static TBuilder WithHeadForGet<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.Add(endpoint =>
        {
            // Routing reads the last IHttpMethodMetadata, so the one appended here wins.
            var methods = endpoint.Metadata.OfType<IHttpMethodMetadata>().LastOrDefault();
            if (methods is null
                || !methods.HttpMethods.Contains(HttpMethods.Get, StringComparer.OrdinalIgnoreCase)
                || methods.HttpMethods.Contains(HttpMethods.Head, StringComparer.OrdinalIgnoreCase))
            {
                return;
            }

            endpoint.Metadata.Add(new HttpMethodMetadata([.. methods.HttpMethods, HttpMethods.Head], methods.AcceptCorsPreflight));
        });
        return builder;
    }

    /// <summary>Discards the response body of every <c>HEAD</c> request; status and headers are sent as usual.</summary>
    public static IApplicationBuilder UseHeadWithoutBody(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (!HttpMethods.IsHead(context.Request.Method))
            {
                await next(context);
                return;
            }

            var body = context.Features.GetRequiredFeature<IHttpResponseBodyFeature>();
            context.Features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(Stream.Null, body));
            try
            {
                await next(context);
            }
            finally
            {
                context.Features.Set(body);
            }
        });
}

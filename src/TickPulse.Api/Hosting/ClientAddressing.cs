using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace TickPulse.Api.Hosting;

/// <summary>
/// Takes the client IP from <c>CF-Connecting-IP</c> (and the scheme from <c>X-Forwarded-Proto</c>), but only when the
/// request comes from a trusted proxy: loopback, plus the addresses in <c>ForwardedHeaders:KnownProxies</c> (an array,
/// or one comma-separated value). From any other peer the headers are ignored and the peer address is the client.
/// </summary>
internal static class ClientAddressing
{
    public const string ClientIpHeader = "CF-Connecting-IP";
    public const string KnownProxiesKey = "ForwardedHeaders:KnownProxies";

    public static IServiceCollection AddTickPulseForwardedHeaders(this IServiceCollection services)
    {
        services.AddOptions<ForwardedHeadersOptions>().Configure<IConfiguration>((options, configuration) =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardedForHeaderName = ClientIpHeader;
            foreach (var proxy in KnownProxies(configuration))
            {
                options.KnownProxies.Add(proxy);
            }
        });
        return services;
    }

    /// <summary>The client address with IPv4-mapped IPv6 addresses reduced to IPv4.</summary>
    public static IPAddress? ClientAddress(HttpContext context)
    {
        var address = context.Connection.RemoteIpAddress;
        return address is { IsIPv4MappedToIPv6: true } ? address.MapToIPv4() : address;
    }

    private static IEnumerable<IPAddress> KnownProxies(IConfiguration configuration)
    {
        var section = configuration.GetSection(KnownProxiesKey);
        var values = section.Value is { } single
            ? single.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : section.GetChildren().Select(c => c.Value?.Trim()).OfType<string>().Where(v => v.Length > 0);

        foreach (var value in values)
        {
            yield return IPAddress.TryParse(value, out var address)
                ? address
                : throw new InvalidOperationException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{KnownProxiesKey} contains '{value}', which is not an IP address."));
        }
    }
}

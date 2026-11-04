using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.HttpOverrides;

namespace TickPulse.Api.Hosting;

/// <summary>
/// Takes the client IP from <c>CF-Connecting-IP</c> (and the scheme from <c>X-Forwarded-Proto</c>), but only when the
/// request comes from a trusted proxy. The trusted proxies are loopback, the addresses in
/// <c>ForwardedHeaders:KnownProxies</c>, and the CIDR ranges in <c>ForwardedHeaders:KnownNetworks</c> (each an
/// array, or one comma-separated value). In a container the proxy is the Docker bridge gateway (e.g.
/// <c>172.18.0.1</c>), so the deployment lists that network. From any other peer the headers are ignored and the
/// peer address is the client. <c>X-Forwarded-For</c> is never read.
/// </summary>
internal static class ClientAddressing
{
    public const string ClientIpHeader = "CF-Connecting-IP";
    public const string KnownProxiesKey = "ForwardedHeaders:KnownProxies";
    public const string KnownNetworksKey = "ForwardedHeaders:KnownNetworks";

    /// <summary>IPv6 clients are grouped by this prefix: one subscriber usually holds a whole /64.</summary>
    public const int Ipv6PrefixLength = 64;

    public static IServiceCollection AddTickPulseForwardedHeaders(this IServiceCollection services)
    {
        services.AddOptions<ForwardedHeadersOptions>().Configure<IConfiguration>((options, configuration) =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardedForHeaderName = ClientIpHeader;
            foreach (var value in Values(configuration, KnownProxiesKey))
            {
                options.KnownProxies.Add(IPAddress.TryParse(value, out var address) ? address : throw Invalid(KnownProxiesKey, value, "an IP address"));
            }

            foreach (var value in Values(configuration, KnownNetworksKey))
            {
                options.KnownIPNetworks.Add(System.Net.IPNetwork.TryParse(value, out var network) ? network : throw Invalid(KnownNetworksKey, value, "a CIDR range"));
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

    /// <summary>The rate-limit partition of a client: the IPv4 address, or the IPv6 /64 prefix (<c>2001:db8:1:2::/64</c>).</summary>
    public static string PartitionKey(IPAddress? address)
    {
        if (address is null)
        {
            return "unknown";
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        Span<byte> bytes = stackalloc byte[16];
        address.TryWriteBytes(bytes, out _);
        bytes[(Ipv6PrefixLength / 8)..].Clear();
        return string.Create(CultureInfo.InvariantCulture, $"{new IPAddress(bytes)}/{Ipv6PrefixLength}");
    }

    private static IEnumerable<string> Values(IConfiguration configuration, string key)
    {
        var section = configuration.GetSection(key);
        return section.Value is { } single
            ? single.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : section.GetChildren().Select(c => c.Value?.Trim()).OfType<string>().Where(v => v.Length > 0);
    }

    private static InvalidOperationException Invalid(string key, string value, string expected) =>
        new(string.Create(CultureInfo.InvariantCulture, $"{key} contains '{value}', which is not {expected}."));
}

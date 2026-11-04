using System.Text.Json;
using TickPulse.Api.Hosting;

namespace TickPulse.Api.Hubs;

/// <summary>Wiring of the market hub, its broadcaster and the statistics they share.</summary>
internal static class MarketHubRegistration
{
    /// <summary>SignalR with the JSON protocol only, camelCase like the REST JSON, plus the broadcaster.</summary>
    public static IServiceCollection AddTickPulseMarketHub(this IServiceCollection services)
    {
        services.AddSingleton<SystemStatsProvider>();
        services.AddSignalR()
            .AddJsonProtocol(o =>
            {
                o.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
                o.PayloadSerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.CamelCase;
            });
        services.AddHostedService<MarketBroadcaster>();
        return services;
    }

    /// <summary>Maps <see cref="MarketHub.Path"/>. Public, and outside the API rate limiter.</summary>
    public static IEndpointRouteBuilder MapTickPulseMarketHub(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHub<MarketHub>(MarketHub.Path);
        return endpoints;
    }
}

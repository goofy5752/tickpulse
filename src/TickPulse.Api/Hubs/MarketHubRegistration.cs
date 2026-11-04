using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using TickPulse.Api.Hosting;

namespace TickPulse.Api.Hubs;

/// <summary>Settings of the market hub (section <c>MarketHub</c>).</summary>
public sealed class MarketHubOptions
{
    public const string SectionName = "MarketHub";

    /// <summary>Connection attempts (negotiate, WebSocket or SSE connect) one client IP may make per window.</summary>
    [Range(1, int.MaxValue)]
    public int ConnectPermitLimit { get; set; } = 60;

    /// <summary>Length of the fixed window of <see cref="ConnectPermitLimit"/>, in seconds.</summary>
    [Range(1, 86_400)]
    public int ConnectWindowSeconds { get; set; } = 60;
}

/// <summary>Wiring of the market hub, its broadcaster and the statistics they share.</summary>
internal static class MarketHubRegistration
{
    /// <summary>Rate-limiter policy of the hub endpoints; see <see cref="IsConnectAttempt"/>.</summary>
    public const string ConnectPolicy = "hub-connect";

    /// <summary>Largest message a client may send, in bytes; <c>Subscribe("BTCUSDT")</c> needs well under 100.</summary>
    public const int MaxReceiveMessageSize = 1024;

    private const string SessionPartition = "hub-session";

    /// <summary>SignalR with the JSON protocol only, camelCase like the REST JSON, plus the broadcaster and statistics.</summary>
    public static IServiceCollection AddTickPulseMarketHub(this IServiceCollection services)
    {
        services.AddOptions<SystemStatsOptions>()
            .BindConfiguration(SystemStatsOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<MarketHubOptions>()
            .BindConfiguration(MarketHubOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddSingleton<IStoredCounts, DatabaseStoredCounts>();
        services.AddSingleton<SystemStatsProvider>();
        services.AddSingleton<MarketHubConnections>();
        services.AddSignalR(o => o.MaximumReceiveMessageSize = MaxReceiveMessageSize)
            .AddJsonProtocol(o =>
            {
                o.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
                o.PayloadSerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.CamelCase;
            });
        services.AddHostedService<MarketBroadcaster>();

        // Added to the limiter that AddTickPulseRateLimiting sets up; its rejection (429 problem, Retry-After) applies.
        services.Configure<RateLimiterOptions>(o => o.AddPolicy(ConnectPolicy, context =>
        {
            if (!IsConnectAttempt(context))
            {
                return RateLimitPartition.GetNoLimiter(SessionPartition);
            }

            var settings = context.RequestServices.GetRequiredService<IOptions<MarketHubOptions>>().Value;
            return RateLimitPartition.GetFixedWindowLimiter(
                ClientAddressing.PartitionKey(context.Connection.RemoteIpAddress),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = settings.ConnectPermitLimit,
                    Window = TimeSpan.FromSeconds(settings.ConnectWindowSeconds),
                    QueueLimit = 0,
                });
        }));
        return services;
    }

    /// <summary>Maps <see cref="MarketHub.Path"/>: public, with connection attempts limited per client IP.</summary>
    public static IEndpointRouteBuilder MapTickPulseMarketHub(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHub<MarketHub>(MarketHub.Path).RequireRateLimiting(ConnectPolicy);
        return endpoints;
    }

    /// <summary>
    /// A request that opens a connection: <c>negotiate</c>, a WebSocket upgrade, or a server-sent-events stream. The
    /// polls and sends of an established long-polling connection are not counted; they need a connection id that only
    /// a (limited) negotiate hands out.
    /// </summary>
    internal static bool IsConnectAttempt(HttpContext context) =>
        context.Request.Path.Value?.EndsWith("/negotiate", StringComparison.OrdinalIgnoreCase) == true
        || context.WebSockets.IsWebSocketRequest
        || (HttpMethods.IsGet(context.Request.Method)
            && context.Request.Headers.Accept.ToString().Contains("text/event-stream", StringComparison.OrdinalIgnoreCase));
}

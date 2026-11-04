using Microsoft.Extensions.Logging;
using TickPulse.Infrastructure.Binance;

namespace TickPulse.Infrastructure.Ingestion;

/// <summary>Creates the WebSocket connections the ingestion worker streams from.</summary>
public interface IStreamConnectionFactory
{
    /// <summary>A new, not yet opened connection.</summary>
    BinanceWebSocketConnection Create();
}

/// <summary>
/// Connections to <c>Binance:WsBaseUrl</c>. Each one gets its own command throttle, because Binance limits incoming
/// messages per connection, and the two connections that overlap during a rotation are limited separately.
/// </summary>
/// <param name="wsBaseUrl">WebSocket base URL.</param>
/// <param name="clock">Clock of the connections, their throttles and timeouts.</param>
/// <param name="loggerFactory">Logger source for the connections.</param>
/// <param name="commandTimeout">How long a command waits for its ack; the connection's default (10 s) when null.</param>
public sealed class StreamConnectionFactory(
    Uri wsBaseUrl,
    TimeProvider clock,
    ILoggerFactory loggerFactory,
    TimeSpan? commandTimeout = null) : IStreamConnectionFactory
{
    /// <summary>Configuration key of the WebSocket base URL.</summary>
    public const string WsBaseUrlKey = "Binance:WsBaseUrl";

    /// <summary>Used when <see cref="WsBaseUrlKey"/> is not set.</summary>
    public const string DefaultWsBaseUrl = "wss://stream.binance.com:9443";

    public BinanceWebSocketConnection Create() =>
        new(wsBaseUrl, clock, throttle: null, loggerFactory.CreateLogger<BinanceWebSocketConnection>(), commandTimeout);
}

using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace TickPulse.Integration.Tests.Fixtures;

/// <summary>One aggregated trade held by <see cref="FakeBinanceServer"/>.</summary>
public sealed record FakeTrade(string Symbol, long Id, decimal Price, decimal Quantity, DateTime TradeTimeUtc);

/// <summary>
/// In-process stand-in for Binance spot: the combined WebSocket stream at <c>/stream</c> (runtime
/// SUBSCRIBE / UNSUBSCRIBE with acks) and REST stubs for <c>exchangeInfo</c>, <c>klines</c> and
/// <c>aggTrades</c>, all served from one in-memory trade list. Kestrel listens on a random loopback port.
/// </summary>
/// <remarks>
/// Use it per test (<c>new()</c> + <see cref="InitializeAsync"/>, or <see cref="StartAsync"/>) or as an xUnit
/// fixture. Await conditions with <see cref="WaitUntilAsync"/> instead of sleeping.
/// </remarks>
public sealed class FakeBinanceServer : IAsyncLifetime
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private readonly Lock _gate = new();
    private readonly List<string> _commands = [];
    private readonly List<string> _requests = [];
    private readonly List<FakeTrade> _trades = [];
    private readonly HashSet<Connection> _connections = [];
    private readonly Dictionary<string, string> _symbolStatus = new(StringComparer.Ordinal)
    {
        ["BTCUSDT"] = "TRADING",
        ["ETHUSDT"] = "TRADING",
        ["ADAUSDT"] = "TRADING",
    };

    private TaskCompletionSource _changed = NewSignal();
    private (int Code, string Msg)? _rejectNext;
    private bool _suppressNextAck;
    private int _rateLimitDisconnects;
    private WebApplication? _app;

    /// <summary>Base URL for <c>Binance:WsBaseUrl</c>, e.g. <c>ws://127.0.0.1:51234</c>.</summary>
    public Uri WsBaseUrl { get; private set; } = null!;

    /// <summary>Base URL for <c>Binance:RestBaseUrl</c>, e.g. <c>http://127.0.0.1:51234</c>.</summary>
    public Uri RestBaseUrl { get; private set; } = null!;

    /// <summary>Raw text of every WebSocket command received, across all connections, in arrival order.</summary>
    public IReadOnlyList<string> ReceivedCommands
    {
        get
        {
            lock (_gate)
            {
                return [.. _commands];
            }
        }
    }

    /// <summary>Path and query of every REST request received, in arrival order.</summary>
    public IReadOnlyList<string> ReceivedRequests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>Number of open WebSocket connections.</summary>
    public int ActiveConnections
    {
        get
        {
            lock (_gate)
            {
                return _connections.Count;
            }
        }
    }

    /// <summary>Union of the streams subscribed on the open connections, e.g. <c>btcusdt@aggTrade</c>.</summary>
    public IReadOnlySet<string> SubscribedStreams
    {
        get
        {
            var all = new HashSet<string>(StringComparer.Ordinal);
            foreach (var connection in SnapshotConnections())
            {
                all.UnionWith(connection.Streams());
            }

            return all;
        }
    }

    /// <summary>Every trade added so far, ordered by symbol and id.</summary>
    public IReadOnlyList<FakeTrade> Trades
    {
        get
        {
            lock (_gate)
            {
                return [.. _trades.OrderBy(t => t.Symbol, StringComparer.Ordinal).ThenBy(t => t.Id)];
            }
        }
    }

    public static async Task<FakeBinanceServer> StartAsync()
    {
        var server = new FakeBinanceServer();
        await server.InitializeAsync();
        return server;
    }

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(FakeBinanceServer).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));

        var app = builder.Build();
        app.UseWebSockets();
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/api", StringComparison.Ordinal))
            {
                lock (_gate)
                {
                    _requests.Add(context.Request.Path + context.Request.QueryString);
                }

                Signal();
            }

            await next(context);
        });
        app.Map("/stream", HandleStreamAsync);
        app.MapGet("/api/v3/exchangeInfo", HandleExchangeInfoAsync);
        app.MapGet("/api/v3/aggTrades", HandleAggTradesAsync);
        app.MapGet("/api/v3/klines", HandleKlinesAsync);

        await app.StartAsync();
        _app = app;

        var address = new Uri(app.Urls.Single());
        RestBaseUrl = address;
        WsBaseUrl = new UriBuilder(address) { Scheme = "ws" }.Uri;
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is null)
        {
            return;
        }

        foreach (var connection in SnapshotConnections())
        {
            connection.Abort();
        }

        await _app.StopAsync();
        await _app.DisposeAsync();
        _app = null;
    }

    /// <summary>Sets the <c>exchangeInfo</c> status of a symbol (<c>TRADING</c>, <c>BREAK</c>, …).</summary>
    public void SetSymbolStatus(string symbol, string status)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        lock (_gate)
        {
            _symbolStatus[symbol.ToUpperInvariant()] = status;
        }
    }

    /// <summary>Makes <c>exchangeInfo</c> answer <c>-1121 Invalid symbol</c> for the symbol.</summary>
    public void RemoveSymbol(string symbol)
    {
        lock (_gate)
        {
            _symbolStatus.Remove(symbol.ToUpperInvariant());
        }
    }

    /// <summary>
    /// When set, a connection that sends more than this many messages within one (real) second is closed with
    /// <see cref="WebSocketCloseStatus.PolicyViolation"/>, as Binance does at 5. Off (<c>null</c>) by default.
    /// Only data frames are counted; the pings and pongs that the socket stack handles are invisible here.
    /// </summary>
    public int? MaxIncomingMessagesPerSecond { get; set; }

    /// <summary>Connections closed so far for exceeding <see cref="MaxIncomingMessagesPerSecond"/>.</summary>
    public int RateLimitDisconnects
    {
        get
        {
            lock (_gate)
            {
                return _rateLimitDisconnects;
            }
        }
    }

    /// <summary>The next WebSocket command is applied but gets no reply, so the client's ack wait never ends.</summary>
    public void SuppressNextAck()
    {
        lock (_gate)
        {
            _suppressNextAck = true;
        }
    }

    /// <summary>The next WebSocket command gets an error reply instead of an ack and changes nothing.</summary>
    public void RejectNextCommand(int code, string msg)
    {
        lock (_gate)
        {
            _rejectNext = (code, msg);
        }
    }

    /// <summary>Adds a trade to the REST data only (as if it happened while no client was listening).</summary>
    public FakeTrade AddTrade(string symbol, long id, decimal price, DateTime t, decimal quantity = 1m)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        var trade = new FakeTrade(symbol.ToUpperInvariant(), id, price, quantity, AsUtc(t));
        lock (_gate)
        {
            _trades.Add(trade);
            _symbolStatus.TryAdd(trade.Symbol, "TRADING");
        }

        Signal();
        return trade;
    }

    /// <summary>
    /// Adds a trade to the REST data and sends it to every connection subscribed to
    /// <c>{symbol}@aggTrade</c>. Returns once it has been written to those sockets.
    /// </summary>
    public async Task PushTradeAsync(string symbol, long id, decimal price, DateTime t, decimal quantity = 1m)
    {
        var trade = AddTrade(symbol, id, price, t, quantity);
        var stream = StreamName(trade.Symbol);
        var payload = AggTradeMessage(stream, trade);
        foreach (var connection in SnapshotConnections())
        {
            if (connection.IsSubscribed(stream))
            {
                await connection.TrySendAsync(payload);
            }
        }
    }

    /// <summary>Sends raw text to every open connection, regardless of subscriptions.</summary>
    public async Task PushRawAsync(string text)
    {
        var payload = Encoding.UTF8.GetBytes(text);
        foreach (var connection in SnapshotConnections())
        {
            await connection.TrySendAsync(payload);
        }
    }

    /// <summary>Aborts every connection without a close handshake (a network drop) and waits until they are gone.</summary>
    public async Task DropAllConnectionsAsync()
    {
        var connections = SnapshotConnections();
        foreach (var connection in connections)
        {
            connection.Abort();
        }

        await Task.WhenAll(connections.Select(c => c.Closed)).WaitAsync(DefaultTimeout);
    }

    /// <summary>Starts a normal close handshake on every connection and waits until they are gone.</summary>
    public async Task CloseAllConnectionsAsync()
    {
        var connections = SnapshotConnections();
        foreach (var connection in connections)
        {
            await connection.TryCloseAsync();
        }

        await Task.WhenAll(connections.Select(c => c.Closed)).WaitAsync(DefaultTimeout);
    }

    /// <summary>
    /// Completes when <paramref name="condition"/> holds. It is re-checked whenever the server's state changes
    /// (connections, commands, trades, requests); throws <see cref="TimeoutException"/> after the timeout.
    /// </summary>
    public async Task WaitUntilAsync(
        Func<FakeBinanceServer, bool> condition,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(condition);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout ?? DefaultTimeout);
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                changed = _changed.Task;
            }

            if (condition(this))
            {
                return;
            }

            try
            {
                await changed.WaitAsync(cts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("FakeBinanceServer condition was not met in time.");
            }
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static string StreamName(string symbol) => symbol.ToLowerInvariant() + "@aggTrade";

    private static DateTime AsUtc(DateTime t) => t.Kind switch
    {
        DateTimeKind.Utc => t,
        DateTimeKind.Local => t.ToUniversalTime(),
        _ => DateTime.SpecifyKind(t, DateTimeKind.Utc),
    };

    private static long UnixMs(DateTime utc) => new DateTimeOffset(utc).ToUnixTimeMilliseconds();

    private static string Format(decimal value) => value.ToString("0.00000000", CultureInfo.InvariantCulture);

    private static byte[] AggTradeMessage(string stream, FakeTrade trade) => Json(writer =>
    {
        writer.WriteStartObject();
        writer.WriteString("stream", stream);
        writer.WritePropertyName("data");
        writer.WriteStartObject();
        writer.WriteString("e", "aggTrade");
        writer.WriteNumber("E", UnixMs(trade.TradeTimeUtc));
        writer.WriteString("s", trade.Symbol);
        WriteAggTradeFields(writer, trade);
        writer.WriteEndObject();
        writer.WriteEndObject();
    });

    private static void WriteAggTradeFields(Utf8JsonWriter writer, FakeTrade trade)
    {
        writer.WriteNumber("a", trade.Id);
        writer.WriteString("p", Format(trade.Price));
        writer.WriteString("q", Format(trade.Quantity));
        writer.WriteNumber("f", trade.Id);
        writer.WriteNumber("l", trade.Id);
        writer.WriteNumber("T", UnixMs(trade.TradeTimeUtc));
        writer.WriteBoolean("m", false);
        writer.WriteBoolean("M", true);
    }

    private static byte[] Json(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            write(writer);
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static byte[] Error(int code, string msg, long? id = null, bool includeId = false) => Json(writer =>
    {
        writer.WriteStartObject();
        writer.WriteNumber("code", code);
        writer.WriteString("msg", msg);
        if (includeId)
        {
            if (id is { } value)
            {
                writer.WriteNumber("id", value);
            }
            else
            {
                writer.WriteNull("id");
            }
        }

        writer.WriteEndObject();
    });

    private static async Task WriteAsync(HttpContext context, int status, byte[] body)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json;charset=UTF-8";
        await context.Response.Body.WriteAsync(body, context.RequestAborted);
    }

    private static bool TryLong(IQueryCollection query, string name, out long? value)
    {
        value = null;
        var raw = query[name].ToString();
        if (raw.Length == 0)
        {
            return true;
        }

        if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            value = parsed;
            return true;
        }

        return false;
    }

    private static int Limit(long? requested) => (int)Math.Clamp(requested ?? 500, 1, 1000);

    private void Signal()
    {
        TaskCompletionSource previous;
        lock (_gate)
        {
            previous = _changed;
            _changed = NewSignal();
        }

        previous.TrySetResult();
    }

    private Connection[] SnapshotConnections()
    {
        lock (_gate)
        {
            return [.. _connections];
        }
    }

    private async Task HandleStreamAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var connection = new Connection(socket, context);
        lock (_gate)
        {
            _connections.Add(connection);
        }

        Signal();
        try
        {
            await ReceiveCommandsAsync(connection, socket, context.RequestAborted);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException)
        {
            // Dropped connection: nothing to do.
        }
        finally
        {
            lock (_gate)
            {
                _connections.Remove(connection);
            }

            connection.MarkClosed();
            Signal();
        }
    }

    private async Task ReceiveCommandsAsync(Connection connection, WebSocket socket, CancellationToken ct)
    {
        var chunk = new byte[4096];
        var message = new ArrayBufferWriter<byte>();
        var closing = false;
        using var closeDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        while (true)
        {
            var result = await socket.ReceiveAsync(chunk.AsMemory(), closeDeadline.Token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                if (socket.State == WebSocketState.CloseReceived)
                {
                    await connection.TryCloseAsync();
                }

                return;
            }

            message.Write(chunk.AsSpan(0, result.Count));
            if (!result.EndOfMessage)
            {
                continue;
            }

            var text = Encoding.UTF8.GetString(message.WrittenSpan);
            message.ResetWrittenCount();
            if (closing)
            {
                // Close frame sent; ignore anything else until the client answers it.
                continue;
            }

            if (MaxIncomingMessagesPerSecond is { } limit && connection.CountIncoming() > limit)
            {
                // Binance drops connections that send more than 5 messages per second.
                lock (_gate)
                {
                    _rateLimitDisconnects++;
                }

                closing = true;
                closeDeadline.CancelAfter(TimeSpan.FromSeconds(2)); // Binance does not wait long for the reply
                await connection.TryCloseAsync(WebSocketCloseStatus.PolicyViolation, "Too many messages");
                Signal();
                continue;
            }

            var reply = HandleCommand(connection, text);
            if (reply is not null)
            {
                await connection.TrySendAsync(reply);
            }

            Signal();
        }
    }

    // Null means "send nothing" (SuppressNextAck).
    private byte[]? HandleCommand(Connection connection, string text)
    {
        (int Code, string Msg)? reject;
        bool suppressAck;
        lock (_gate)
        {
            _commands.Add(text);
            reject = _rejectNext;
            _rejectNext = null;
            suppressAck = _suppressNextAck;
            _suppressNextAck = false;
        }

        var reply = BuildReply(connection, text, reject);
        return suppressAck ? null : reply;
    }

    private static byte[] BuildReply(Connection connection, string text, (int Code, string Msg)? reject)
    {
        long? id = null;
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Error(3, "Invalid JSON: expected an object", null, includeId: true);
            }

            if (root.TryGetProperty("id", out var idElement) && idElement.TryGetInt64(out var parsedId))
            {
                id = parsedId;
            }

            if (reject is { } r)
            {
                return Error(r.Code, r.Msg, id, includeId: true);
            }

            var method = root.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
            var streams = root.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Array
                ? p.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToArray()
                : [];

            switch (method)
            {
                case "SUBSCRIBE":
                    connection.Subscribe(streams);
                    return Ack(id);
                case "UNSUBSCRIBE":
                    connection.Unsubscribe(streams);
                    return Ack(id);
                case "LIST_SUBSCRIPTIONS":
                    return Json(writer =>
                    {
                        writer.WriteStartObject();
                        writer.WriteStartArray("result");
                        foreach (var stream in connection.Streams().Order(StringComparer.Ordinal))
                        {
                            writer.WriteStringValue(stream);
                        }

                        writer.WriteEndArray();
                        WriteId(writer, id);
                        writer.WriteEndObject();
                    });
                default:
                    return Error(2, "Invalid request: unknown method", id, includeId: true);
            }
        }
        catch (JsonException)
        {
            return Error(3, "Invalid JSON", null, includeId: true);
        }
    }

    private static byte[] Ack(long? id) => Json(writer =>
    {
        writer.WriteStartObject();
        writer.WriteNull("result");
        WriteId(writer, id);
        writer.WriteEndObject();
    });

    private static void WriteId(Utf8JsonWriter writer, long? id)
    {
        if (id is { } value)
        {
            writer.WriteNumber("id", value);
        }
        else
        {
            writer.WriteNull("id");
        }
    }

    private bool TryResolveSymbol(HttpContext context, out string symbol, out byte[]? error)
    {
        symbol = context.Request.Query["symbol"].ToString().ToUpperInvariant();
        error = null;
        if (symbol.Length == 0)
        {
            error = Error(-1102, "Mandatory parameter 'symbol' was not sent, was empty/null, or malformed.");
            return false;
        }

        lock (_gate)
        {
            if (!_symbolStatus.ContainsKey(symbol))
            {
                error = Error(-1121, "Invalid symbol.");
                return false;
            }
        }

        return true;
    }

    private async Task HandleExchangeInfoAsync(HttpContext context)
    {
        KeyValuePair<string, string>[] symbols;
        if (context.Request.Query.ContainsKey("symbol"))
        {
            if (!TryResolveSymbol(context, out var symbol, out var error))
            {
                await WriteAsync(context, StatusCodes.Status400BadRequest, error!);
                return;
            }

            lock (_gate)
            {
                symbols = [new(symbol, _symbolStatus[symbol])];
            }
        }
        else
        {
            lock (_gate)
            {
                symbols = [.. _symbolStatus.OrderBy(s => s.Key, StringComparer.Ordinal)];
            }
        }

        await WriteAsync(context, StatusCodes.Status200OK, Json(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("timezone", "UTC");
            writer.WriteStartArray("rateLimits");
            writer.WriteEndArray();
            writer.WriteStartArray("symbols");
            foreach (var (name, status) in symbols)
            {
                writer.WriteStartObject();
                writer.WriteString("symbol", name);
                writer.WriteString("status", status);
                writer.WriteBoolean("isSpotTradingAllowed", true);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }));
    }

    // GET /api/v3/aggTrades?symbol&fromId&startTime&endTime&limit (default 500, max 1000)
    private async Task HandleAggTradesAsync(HttpContext context)
    {
        if (!TryResolveSymbol(context, out var symbol, out var error))
        {
            await WriteAsync(context, StatusCodes.Status400BadRequest, error!);
            return;
        }

        var query = context.Request.Query;
        if (!TryLong(query, "fromId", out var fromId) || !TryLong(query, "startTime", out var startTime)
            || !TryLong(query, "endTime", out var endTime) || !TryLong(query, "limit", out var limit))
        {
            await WriteAsync(context, StatusCodes.Status400BadRequest, Error(-1100, "Illegal characters found in a parameter."));
            return;
        }

        var take = Limit(limit);
        FakeTrade[] trades;
        lock (_gate)
        {
            var ordered = _trades.Where(t => t.Symbol == symbol).OrderBy(t => t.Id);
            trades = fromId is { } from
                ? [.. ordered.Where(t => t.Id >= from).Take(take)]
                : startTime is not null || endTime is not null
                    ? [.. ordered.Where(t => UnixMs(t.TradeTimeUtc) >= (startTime ?? long.MinValue)
                        && UnixMs(t.TradeTimeUtc) <= (endTime ?? long.MaxValue)).Take(take)]
                    : [.. ordered.TakeLast(take)];
        }

        await WriteAsync(context, StatusCodes.Status200OK, Json(writer =>
        {
            writer.WriteStartArray();
            foreach (var trade in trades)
            {
                writer.WriteStartObject();
                WriteAggTradeFields(writer, trade);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }));
    }

    // GET /api/v3/klines?symbol&interval=1m&startTime&endTime&limit (default 500, max 1000), built from the trades.
    // A minute without trades has no kline, which is also what Binance returns for minutes with no trades.
    private async Task HandleKlinesAsync(HttpContext context)
    {
        if (!TryResolveSymbol(context, out var symbol, out var error))
        {
            await WriteAsync(context, StatusCodes.Status400BadRequest, error!);
            return;
        }

        var query = context.Request.Query;
        if (query["interval"].ToString() != "1m")
        {
            await WriteAsync(context, StatusCodes.Status400BadRequest, Error(-1120, "Invalid interval."));
            return;
        }

        if (!TryLong(query, "startTime", out var startTime) || !TryLong(query, "endTime", out var endTime)
            || !TryLong(query, "limit", out var limit))
        {
            await WriteAsync(context, StatusCodes.Status400BadRequest, Error(-1100, "Illegal characters found in a parameter."));
            return;
        }

        const long MinuteMs = 60_000;
        var take = Limit(limit);
        IGrouping<long, FakeTrade>[] buckets;
        lock (_gate)
        {
            var all = _trades
                .Where(t => t.Symbol == symbol)
                .GroupBy(t => UnixMs(t.TradeTimeUtc) / MinuteMs * MinuteMs)
                .Where(g => g.Key >= (startTime ?? long.MinValue) && g.Key <= (endTime ?? long.MaxValue))
                .OrderBy(g => g.Key);

            // Binance: with startTime, ascending from it up to limit; otherwise the latest `limit` ending at endTime (or now).
            buckets = startTime is not null ? [.. all.Take(take)] : [.. all.TakeLast(take)];
        }

        await WriteAsync(context, StatusCodes.Status200OK, Json(writer =>
        {
            writer.WriteStartArray();
            foreach (var bucket in buckets)
            {
                var byId = bucket.OrderBy(t => t.Id).ToArray();
                writer.WriteStartArray();
                writer.WriteNumberValue(bucket.Key);
                writer.WriteStringValue(Format(byId[0].Price));
                writer.WriteStringValue(Format(byId.Max(t => t.Price)));
                writer.WriteStringValue(Format(byId.Min(t => t.Price)));
                writer.WriteStringValue(Format(byId[^1].Price));
                writer.WriteStringValue(Format(byId.Sum(t => t.Quantity)));
                writer.WriteNumberValue(bucket.Key + MinuteMs - 1);
                writer.WriteStringValue(Format(byId.Sum(t => t.Price * t.Quantity)));
                writer.WriteNumberValue(byId.Length);
                writer.WriteStringValue(Format(0m));
                writer.WriteStringValue(Format(0m));
                writer.WriteStringValue("0");
                writer.WriteEndArray();
            }

            writer.WriteEndArray();
        }));
    }

    private sealed class Connection(WebSocket socket, HttpContext context)
    {
        private readonly Lock _sendGate = new();
        private readonly HashSet<string> _streams = new(StringComparer.Ordinal);
        private readonly TaskCompletionSource _closed = NewSignal();
        private readonly Queue<long> _incoming = new();
        private Task _sendTail = Task.CompletedTask;

        public Task Closed => _closed.Task;

        public bool IsSubscribed(string stream)
        {
            lock (_streams)
            {
                return _streams.Contains(stream);
            }
        }

        public string[] Streams()
        {
            lock (_streams)
            {
                return [.. _streams];
            }
        }

        public void Subscribe(IEnumerable<string> streams)
        {
            lock (_streams)
            {
                _streams.UnionWith(streams);
            }
        }

        public void Unsubscribe(IEnumerable<string> streams)
        {
            lock (_streams)
            {
                _streams.ExceptWith(streams);
            }
        }

        // Sends are chained so that at most one is in flight per socket (WebSocket allows one sender).
        public Task TrySendAsync(byte[] payload) =>
            Enqueue(() => socket.State == WebSocketState.Open
                ? socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None)
                : Task.CompletedTask);

        public Task TryCloseAsync(
            WebSocketCloseStatus status = WebSocketCloseStatus.NormalClosure,
            string description = "closing") =>
            Enqueue(() => socket.State is WebSocketState.Open or WebSocketState.CloseReceived
                ? socket.CloseOutputAsync(status, description, CancellationToken.None)
                : Task.CompletedTask);

        /// <summary>Records one incoming message and returns how many arrived in the last real second.</summary>
        public int CountIncoming()
        {
            // Real time on purpose: the client under test may run on a fake clock, Binance does not.
            var now = TimeProvider.System.GetTimestamp();
            _incoming.Enqueue(now);
            while (TimeProvider.System.GetElapsedTime(_incoming.Peek(), now) >= TimeSpan.FromSeconds(1))
            {
                _incoming.Dequeue();
            }

            return _incoming.Count;
        }

        public void Abort()
        {
            try
            {
                context.Abort();
            }
            catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or IOException)
            {
                // The request has already finished.
            }
        }

        public void MarkClosed() => _closed.TrySetResult();

        private Task Enqueue(Func<Task> operation)
        {
            lock (_sendGate)
            {
                _sendTail = RunAfterAsync(_sendTail, operation);
                return _sendTail;
            }
        }

        private static async Task RunAfterAsync(Task previous, Func<Task> operation)
        {
            await previous;
            try
            {
                await operation();
            }
            catch (Exception)
            {
                // Whatever went wrong (usually the connection went away), the chain must stay usable for the
                // next send; the receive side notices a dead socket and unregisters it.
            }
        }
    }
}

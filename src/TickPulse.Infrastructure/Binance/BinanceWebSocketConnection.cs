using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace TickPulse.Infrastructure.Binance;

/// <summary>Binance answered a SUBSCRIBE/UNSUBSCRIBE with an error object.</summary>
public sealed class BinanceCommandException : Exception
{
    public BinanceCommandException()
    {
    }

    public BinanceCommandException(string message)
        : base(message)
    {
    }

    public BinanceCommandException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public BinanceCommandException(long? commandId, int code, string message)
        : base($"Binance rejected command {commandId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"}: {code} {message}")
    {
        CommandId = commandId;
        Code = code;
    }

    public long? CommandId { get; }

    public int Code { get; }
}

/// <summary>
/// One connection to the Binance combined stream (<c>{WsBaseUrl}/stream</c>) on a raw <see cref="ClientWebSocket"/>,
/// which answers the server's pings by itself. Streams are added and removed at runtime with SUBSCRIBE /
/// UNSUBSCRIBE; every command goes through a <see cref="SubscribeThrottle"/> and is awaited until Binance acks it.
/// </summary>
/// <remarks>
/// <para>The connection is single-use: when the socket closes or breaks, <see cref="Trades"/> completes and
/// <see cref="Completion"/> completes (server close handshake or <see cref="DisposeAsync"/>) or faults (anything
/// else). The owner then creates a new connection.</para>
/// <para>Malformed or unrecognised messages are logged and skipped.</para>
/// </remarks>
public sealed partial class BinanceWebSocketConnection : IAsyncDisposable
{
    private const int InitialBufferSize = 16 * 1024;
    private const int MaxMessageSize = 1024 * 1024;
    private const int DefaultTradeCapacity = 10_000;

    private static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(2);

    private readonly Uri _streamUrl;
    private readonly TimeProvider _timeProvider;
    private readonly SubscribeThrottle _throttle;
    private readonly bool _ownsThrottle;
    private readonly TimeSpan _commandTimeout;
    private readonly ILogger _logger;
    private readonly Channel<AggTradeEvent> _trades;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<long, TaskCompletionSource> _pending = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();

    private ClientWebSocket? _socket;
    private Task _receiveLoop = Task.CompletedTask;
    private long _lastCommandId;
    private int _connectStarted;
    private int _closed;
    private int _disposed;

    /// <param name="wsBaseUrl">Base URL, e.g. <c>wss://stream.binance.com:9443</c>.</param>
    /// <param name="timeProvider">Clock for <see cref="ConnectedAtUtc"/>, the throttle and timeouts.</param>
    /// <param name="throttle">Command rate limit; defaults to 4 per second on <paramref name="timeProvider"/>.</param>
    /// <param name="logger">Logger for skipped messages and unmatched errors.</param>
    /// <param name="commandTimeout">How long to wait for an ack; defaults to 10 s.</param>
    /// <param name="tradeCapacity">Bound of <see cref="Trades"/>; the receive loop waits when it is full.</param>
    public BinanceWebSocketConnection(
        Uri wsBaseUrl,
        TimeProvider timeProvider,
        SubscribeThrottle? throttle = null,
        ILogger<BinanceWebSocketConnection>? logger = null,
        TimeSpan? commandTimeout = null,
        int tradeCapacity = DefaultTradeCapacity)
    {
        ArgumentNullException.ThrowIfNull(wsBaseUrl);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentOutOfRangeException.ThrowIfLessThan(tradeCapacity, 1);

        _streamUrl = new Uri(wsBaseUrl.AbsoluteUri.TrimEnd('/') + "/stream");
        _timeProvider = timeProvider;
        _ownsThrottle = throttle is null;
        _throttle = throttle ?? new SubscribeThrottle(4, timeProvider);
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _commandTimeout = commandTimeout ?? DefaultCommandTimeout;
        _trades = Channel.CreateBounded<AggTradeEvent>(new BoundedChannelOptions(tradeCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = true,
        });
    }

    /// <summary>Trades in arrival order. Completes when the connection ends.</summary>
    public ChannelReader<AggTradeEvent> Trades => _trades.Reader;

    /// <summary>Completes when the connection closes normally; faults when it breaks.</summary>
    public Task Completion => _completion.Task;

    /// <summary>When the WebSocket handshake finished (UTC); <see cref="DateTime.MinValue"/> before that.</summary>
    public DateTime ConnectedAtUtc { get; private set; }

    /// <summary>Opens the socket and starts the receive loop. Call once.</summary>
    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _connectStarted, 1) != 0)
        {
            throw new InvalidOperationException("The connection has already been opened; create a new one to reconnect.");
        }

        var socket = new ClientWebSocket();
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            await socket.ConnectAsync(_streamUrl, linked.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            socket.Dispose();
            _trades.Writer.TryComplete();
            _completion.TrySetException(ex);
            throw;
        }

        _socket = socket;
        ConnectedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
        LogConnected(_streamUrl);
        _receiveLoop = ReceiveLoopAsync(socket, _lifetime.Token);
    }

    /// <summary>Subscribes to <c>{symbol}@aggTrade</c> for each symbol with one command, and waits for the ack.</summary>
    /// <exception cref="BinanceCommandException">Binance rejected the command.</exception>
    public Task SubscribeAsync(IReadOnlyCollection<string> symbols, CancellationToken cancellationToken) =>
        SendCommandAsync("SUBSCRIBE", symbols, cancellationToken);

    /// <summary>Unsubscribes from <c>{symbol}@aggTrade</c> for each symbol with one command, and waits for the ack.</summary>
    /// <exception cref="BinanceCommandException">Binance rejected the command.</exception>
    public Task UnsubscribeAsync(IReadOnlyCollection<string> symbols, CancellationToken cancellationToken) =>
        SendCommandAsync("UNSUBSCRIBE", symbols, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var socket = _socket;

        // Best-effort close handshake, skipped while a command is mid-send; the socket is aborted below anyway.
        if (socket is { State: WebSocketState.Open } && await _sendLock.WaitAsync(TimeSpan.Zero).ConfigureAwait(false))
        {
            var sent = false;
            try
            {
                using var timeout = new CancellationTokenSource(CloseTimeout, _timeProvider);
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token).ConfigureAwait(false);
                sent = true;
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
            {
                // The server is gone already.
            }
            finally
            {
                _sendLock.Release();
            }

            if (sent)
            {
                // Give the server a moment to answer the close frame, so the receive loop ends cleanly.
                await Task.WhenAny(_receiveLoop, Task.Delay(CloseTimeout, _timeProvider)).ConfigureAwait(false);
            }
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        await _receiveLoop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        // Never connected (or the connect failed): still settle Completion and Trades.
        _trades.Writer.TryComplete();
        _completion.TrySetResult();
        FailPending(new ObjectDisposedException(nameof(BinanceWebSocketConnection)));

        socket?.Dispose();
        _lifetime.Dispose();
        _sendLock.Dispose();
        if (_ownsThrottle)
        {
            _throttle.Dispose();
        }
    }

    private static byte[] BuildCommand(string method, string[] streams, long id)
    {
        var buffer = new ArrayBufferWriter<byte>(64 + (streams.Length * 24));
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("method"u8, method);
            writer.WriteStartArray("params"u8);
            foreach (var stream in streams)
            {
                writer.WriteStringValue(stream);
            }

            writer.WriteEndArray();
            writer.WriteNumber("id"u8, id);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static string StreamName(string symbol)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        return symbol.Trim().ToLowerInvariant() + "@aggTrade";
    }

    private async Task SendCommandAsync(string method, IReadOnlyCollection<string> symbols, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(symbols);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var socket = _socket ?? throw new InvalidOperationException("ConnectAsync must complete before sending commands.");
        if (symbols.Count == 0)
        {
            return;
        }

        if (Volatile.Read(ref _closed) != 0)
        {
            throw new InvalidOperationException("The connection has closed.");
        }

        var streams = symbols.Select(StreamName).ToArray();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        TaskCompletionSource? ack = null;
        long id = 0;
        await _sendLock.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            await _throttle.WaitAsync(linked.Token).ConfigureAwait(false);

            // Ids are assigned under the send lock, so they increase in the order the commands go out.
            id = ++_lastCommandId;
            ack = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = ack;
            if (Volatile.Read(ref _closed) != 0)
            {
                // The receive loop may have failed the pending commands just before this one was registered.
                throw new InvalidOperationException("The connection has closed.");
            }

            await socket.SendAsync(BuildCommand(method, streams, id), WebSocketMessageType.Text, endOfMessage: true, linked.Token)
                .ConfigureAwait(false);
        }
        catch
        {
            if (ack is not null)
            {
                _pending.TryRemove(id, out _);
            }

            throw;
        }
        finally
        {
            _sendLock.Release();
        }

        try
        {
            await ack.Task.WaitAsync(_commandTimeout, _timeProvider, linked.Token).ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        // Let ConnectAsync return before the first receive runs.
        await Task.Yield();

        var buffer = ArrayPool<byte>.Shared.Rent(InitialBufferSize);
        Exception? failure = null;
        try
        {
            var count = 0;
            var discarding = false;
            while (true)
            {
                if (count == buffer.Length)
                {
                    if (buffer.Length >= MaxMessageSize)
                    {
                        // Oversized message: drop what we have and skip the rest of it.
                        discarding = true;
                        count = 0;
                    }
                    else
                    {
                        var larger = ArrayPool<byte>.Shared.Rent(buffer.Length * 2);
                        buffer.AsSpan(0, count).CopyTo(larger);
                        ArrayPool<byte>.Shared.Return(buffer);
                        buffer = larger;
                    }
                }

                var result = await socket.ReceiveAsync(buffer.AsMemory(count), cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    LogServerClosed(socket.CloseStatus, socket.CloseStatusDescription);
                    await TryAnswerCloseAsync(socket).ConfigureAwait(false);
                    break;
                }

                count += result.Count;
                if (!result.EndOfMessage)
                {
                    continue;
                }

                if (discarding)
                {
                    LogSkipped("oversized message");
                    discarding = false;
                }
                else if (result.MessageType == WebSocketMessageType.Text)
                {
                    await DispatchAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    LogSkipped("binary message");
                }

                count = 0;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Disposed locally.
        }
        catch (Exception ex)
        {
            // Any other end of the loop is a broken connection; the owner reconnects.
            failure = ex;
            LogConnectionLost(ex);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            Interlocked.Exchange(ref _closed, 1);
            _trades.Writer.TryComplete();
            FailPending(failure ?? new WebSocketException(WebSocketError.ConnectionClosedPrematurely, "The connection closed."));
            if (failure is null)
            {
                _completion.TrySetResult();
            }
            else
            {
                _completion.TrySetException(failure);
            }
        }
    }

    private async ValueTask DispatchAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken)
    {
        switch (StreamMessageParser.Parse(message.Span))
        {
            case StreamMessage.AggTrade { Trade: var trade }:
                if (!_trades.Writer.TryWrite(trade))
                {
                    await _trades.Writer.WriteAsync(trade, cancellationToken).ConfigureAwait(false);
                }

                break;
            case StreamMessage.CommandAck { Id: var id }:
                if (_pending.TryGetValue(id, out var ack))
                {
                    ack.TrySetResult();
                }

                break;
            case StreamMessage.CommandError error:
                if (error.Id is { } errorId && _pending.TryGetValue(errorId, out var rejected))
                {
                    rejected.TrySetException(new BinanceCommandException(error.Id, error.Code, error.Msg));
                }
                else
                {
                    LogUnmatchedError(error.Id, error.Code, error.Msg);
                }

                break;
            default:
                LogSkipped("unrecognised message");
                break;
        }
    }

    private async Task TryAnswerCloseAsync(ClientWebSocket socket)
    {
        if (socket.State != WebSocketState.CloseReceived || !await _sendLock.WaitAsync(CloseTimeout).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(CloseTimeout, _timeProvider);
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            // The server is gone already.
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private void FailPending(Exception reason)
    {
        foreach (var (id, pending) in _pending)
        {
            if (_pending.TryRemove(id, out _))
            {
                pending.TrySetException(reason);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Binance stream connected to {Url}")]
    private partial void LogConnected(Uri url);

    [LoggerMessage(Level = LogLevel.Information, Message = "Binance stream closed by server: {Status} {Description}")]
    private partial void LogServerClosed(WebSocketCloseStatus? status, string? description);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Binance stream connection lost")]
    private partial void LogConnectionLost(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipped Binance stream message: {Reason}")]
    private partial void LogSkipped(string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Binance stream error without a pending command: id {Id}, code {Code}, {Msg}")]
    private partial void LogUnmatchedError(long? id, int code, string msg);
}

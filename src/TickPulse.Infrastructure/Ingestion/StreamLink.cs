using System.Collections.Concurrent;
using TickPulse.Infrastructure.Binance;

namespace TickPulse.Infrastructure.Ingestion;

/// <summary>
/// One WebSocket connection with the bookkeeping the ingestion worker keeps for it: the subscription state as
/// confirmed by Binance, the task that pumps its trades, and the first and last trade id seen per symbol (the
/// boundaries for gap-fill).
/// </summary>
/// <remarks>
/// <see cref="Subscribed"/>, <see cref="Unknown"/> and <see cref="GapFillHandled"/> belong to the supervisor's control loop, the only code that
/// sends commands; nothing else may touch them. The trade ids are written by the pump and read concurrently.
/// </remarks>
internal sealed class StreamLink(BinanceWebSocketConnection connection) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, long> _firstIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _lastIds = new(StringComparer.OrdinalIgnoreCase);
    private int _end = -1; // a LinkEnd once retired

    public BinanceWebSocketConnection Connection { get; } = connection;

    public DateTime ConnectedAtUtc => Connection.ConnectedAtUtc;

    /// <summary>Symbols whose SUBSCRIBE Binance acknowledged (and no UNSUBSCRIBE since).</summary>
    public HashSet<string> Subscribed { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Symbols whose last command timed out: Binance may or may not have applied it.</summary>
    public HashSet<string> Unknown { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Subscribed symbols already considered for gap-fill on this link. A symbol acked later (for example after its
    /// first SUBSCRIBE timed out) is considered when it is acked.
    /// </summary>
    public HashSet<string> GapFillHandled { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Pumps <see cref="BinanceWebSocketConnection.Trades"/> into the tick channel; ends when the connection does.</summary>
    public Task Pump { get; set; } = Task.CompletedTask;

    /// <summary>Last trade id per symbol that this link delivered into the tick channel.</summary>
    public IEnumerable<KeyValuePair<string, long>> LastIds => _lastIds;

    /// <summary>Notes a trade that reached the tick channel.</summary>
    public void Record(string symbol, long tradeId)
    {
        _firstIds.TryAdd(symbol, tradeId);
        _lastIds.AddOrUpdate(symbol, static (_, id) => id, static (_, last, id) => Math.Max(last, id), tradeId);
    }

    /// <summary>
    /// Whether the link broke: retired as <see cref="LinkEnd.Broken"/>, or its connection ended before the control loop
    /// retired it. A link that a rotation replaced did not break; the new one overlapped it. Thread-safe.
    /// </summary>
    public bool EndedBroken
    {
        get
        {
            var end = Volatile.Read(ref _end);
            return end == (int)LinkEnd.Broken || (end < 0 && Connection.Completion.IsCompleted);
        }
    }

    /// <summary>Records how the link's life ended, before it is disposed. Control loop only.</summary>
    public void MarkEnded(LinkEnd end) => Volatile.Write(ref _end, (int)end);

    /// <summary>The last trade id this link delivered for the symbol, if any yet.</summary>
    public long? LastId(string symbol) => _lastIds.TryGetValue(symbol, out var id) ? id : null;

    /// <summary>The first trade id this link delivered for the symbol, if any yet.</summary>
    public long? FirstId(string symbol) => _firstIds.TryGetValue(symbol, out var id) ? id : null;

    /// <summary>Drops the bookkeeping of a symbol that is no longer monitored. Control loop only.</summary>
    public void Forget(string symbol)
    {
        GapFillHandled.Remove(symbol);
        _firstIds.TryRemove(symbol, out _);
        _lastIds.TryRemove(symbol, out _);
    }

    public ValueTask DisposeAsync() => Connection.DisposeAsync();
}

using System.Collections.Concurrent;
using TickPulse.Infrastructure.Binance;

namespace TickPulse.Infrastructure.Ingestion;

/// <summary>
/// One WebSocket connection with the bookkeeping the ingestion worker keeps for it: the subscription state as
/// confirmed by Binance, the task that pumps its trades, and the span of trade ids it delivered per symbol (the
/// boundaries for gap-fill).
/// </summary>
/// <remarks>
/// <see cref="Subscribed"/>, <see cref="Unknown"/> and <see cref="GapFillHandled"/> belong to the supervisor's control loop, the only code that
/// sends commands; nothing else may touch them. The spans are written by the pump and read concurrently.
/// </remarks>
internal sealed class StreamLink(BinanceWebSocketConnection connection) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, IdSpan> _spans = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _handedOver = new(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>Per symbol, the first and last trade id this link delivered into the tick channel so far.</summary>
    public IEnumerable<KeyValuePair<string, IdSpan>> Spans => _spans;

    /// <summary>Notes a trade that reached the tick channel.</summary>
    public void Record(string symbol, long tradeId) =>
        _spans.AddOrUpdate(symbol, static (_, id) => new IdSpan(id, id), static (_, span, id) => span with { Last = Math.Max(span.Last, id) }, tradeId);

    /// <summary>The last trade id this link delivered for the symbol, if any yet.</summary>
    public long? LastId(string symbol) => _spans.TryGetValue(symbol, out var span) ? span.Last : null;

    /// <summary>The first trade id this link delivered for the symbol, if any yet.</summary>
    public long? FirstId(string symbol) => _spans.TryGetValue(symbol, out var span) ? span.First : null;

    /// <summary>
    /// The last trade id delivered for the symbol by the links this one replaced in clean rotations (the retired link,
    /// and what that one had taken over in turn), if any. This link was subscribed before the retired one closed, so it
    /// carries every trade after that id, whether or not it has delivered one yet.
    /// </summary>
    public long? HandedOverId(string symbol) => _handedOver.TryGetValue(symbol, out var id) ? id : null;

    /// <summary>
    /// Takes over the last ids of <paramref name="retired"/>, a link this one replaced cleanly, once its pump has ended
    /// (its spans are final then). Called before the retirement completes, so a gap-fill that waits for it sees them.
    /// </summary>
    public void TakeOver(StreamLink retired)
    {
        ArgumentNullException.ThrowIfNull(retired);
        foreach (var (symbol, id) in retired._handedOver.Concat(retired._spans.Select(s => KeyValuePair.Create(s.Key, s.Value.Last))))
        {
            _handedOver.AddOrUpdate(symbol, static (_, id) => id, static (_, known, id) => Math.Max(known, id), id);
        }
    }

    /// <summary>Drops the bookkeeping of a symbol that is no longer monitored. Control loop only.</summary>
    public void Forget(string symbol)
    {
        GapFillHandled.Remove(symbol);
        _spans.TryRemove(symbol, out _);
        _handedOver.TryRemove(symbol, out _);
    }

    public ValueTask DisposeAsync() => Connection.DisposeAsync();
}

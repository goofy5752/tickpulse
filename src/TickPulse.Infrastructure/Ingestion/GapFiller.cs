using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using TickPulse.Application.Abstractions;
using TickPulse.Domain;
using TickPulse.Infrastructure.Binance;
using TickPulse.Infrastructure.Telemetry;

namespace TickPulse.Infrastructure.Ingestion;

/// <summary>
/// Recovers the trades a symbol missed while the stream was down, from REST <c>aggTrades?fromId=last+1</c>, and
/// feeds them into the same tick channel as the stream. Writes are idempotent, so overlap with the stream is harmless.
/// </summary>
/// <remarks>
/// It pages forward until a page reaches the first id the new connection delivered, the exchange has nothing more
/// (an empty or short page that reaches the stream: from then on the already subscribed stream carries every trade),
/// or <see cref="MaxPages"/> pages were read. Only trades below the stream's first id are emitted. The result says
/// whether the gap was closed. REST can lag behind the stream: a short page that ends below the stream's boundary is
/// not the end of the data, so the gap stays open and the next pass continues from the last emitted id.
/// <para>The <c>tickpulse.gapfill.trades</c> counter counts the trades emitted into the channel. That includes any
/// overlap with trades already stored (a fill that starts below a stored stretch, a replayed range), which the primary
/// key then skips, so it can exceed the number of trades that were actually missing. The tick writer reports inserted
/// rows per symbol and batch only, without telling recovered from streamed ticks.</para>
/// </remarks>
public sealed partial class GapFiller(
    IAggTradeSource source,
    TimeProvider clock,
    ILogger<GapFiller> logger,
    TickPulseMetrics metrics,
    int maxPages = GapFiller.DefaultMaxPages)
{
    /// <summary>Trades per REST page (the Binance maximum).</summary>
    public const int PageSize = 1000;

    /// <summary>Upper bound on pages per symbol and gap: at most 50,000 trades are recovered.</summary>
    public const int DefaultMaxPages = 50;

    private const int MaxTransientRetries = 2;

    private static readonly TimeSpan TransientRetryDelay = TimeSpan.FromSeconds(1);

    public int MaxPages { get; } = maxPages >= 1 ? maxPages : throw new ArgumentOutOfRangeException(nameof(maxPages));

    /// <summary>Fills the gap after <paramref name="lastKnownId"/> for one symbol.</summary>
    /// <param name="symbolId">Database id of the symbol.</param>
    /// <param name="symbol">Symbol name.</param>
    /// <param name="lastKnownId">The last trade id stored before the gap.</param>
    /// <param name="firstStreamId">
    /// The id from which the live connection's stream carries every trade of the symbol (normally the first id it
    /// delivered), or <see langword="null"/> while that is not known yet. Read again for every page.
    /// </param>
    /// <param name="output">The tick channel.</param>
    /// <param name="ct">Cancels the fill (leadership lost, shutdown).</param>
    public Task<GapFillResult> FillAsync(
        int symbolId,
        string symbol,
        long lastKnownId,
        Func<long?> firstStreamId,
        ChannelWriter<Tick> output,
        CancellationToken ct) =>
        FillAsync(symbolId, symbol, lastKnownId, firstStreamId, static () => null, output, ct);

    /// <summary>Fills the gap after <paramref name="lastKnownId"/> for one symbol.</summary>
    /// <param name="symbolId">Database id of the symbol.</param>
    /// <param name="symbol">Symbol name.</param>
    /// <param name="lastKnownId">The last trade id stored before the gap.</param>
    /// <param name="firstStreamId">As in the overload without <paramref name="deliveredUpTo"/>.</param>
    /// <param name="deliveredUpTo">
    /// While <paramref name="firstStreamId"/> is not known: the last id that connections this one replaced delivered
    /// (or <see langword="null"/>). The live connection carries every trade after it, so a short REST page counts as
    /// the end of the data only once it reached past that id; before, REST lags. It never stops what is emitted.
    /// Read again for every page.
    /// </param>
    /// <param name="output">The tick channel.</param>
    /// <param name="ct">Cancels the fill (leadership lost, shutdown).</param>
    public async Task<GapFillResult> FillAsync(
        int symbolId,
        string symbol,
        long lastKnownId,
        Func<long?> firstStreamId,
        Func<long?> deliveredUpTo,
        ChannelWriter<Tick> output,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(firstStreamId);
        ArgumentNullException.ThrowIfNull(deliveredUpTo);
        ArgumentNullException.ThrowIfNull(output);
        if (!SymbolName.TryCreate(symbol, out var name))
        {
            throw new ArgumentException($"'{symbol}' is not a valid symbol.", nameof(symbol));
        }

        using var activity = TickPulseMetrics.ActivitySource.StartActivity(TickPulseMetrics.GapFillSpan);
        activity?.SetTag("tickpulse.symbol", name.Value);
        activity?.SetTag("tickpulse.gapfill.from_id", lastKnownId + 1);
        try
        {
            var result = await FillPagesAsync(symbolId, name, lastKnownId, firstStreamId, deliveredUpTo, output, ct).ConfigureAwait(false);
            activity?.SetTag("tickpulse.gapfill.emitted", result.Emitted);
            activity?.SetTag("tickpulse.gapfill.complete", result.Complete);
            return result;
        }
        catch (Exception ex) when (activity is not null)
        {
            activity.SetStatus(ActivityStatusCode.Error, ex.GetType().Name);
            throw;
        }
    }

    private async Task<GapFillResult> FillPagesAsync(
        int symbolId,
        SymbolName name,
        long lastKnownId,
        Func<long?> firstStreamId,
        Func<long?> deliveredUpTo,
        ChannelWriter<Tick> output,
        CancellationToken ct)
    {
        var emitted = 0;
        long? lastEmitted = null;
        var next = lastKnownId + 1;
        for (var page = 1; page <= MaxPages; page++)
        {
            var pageStart = emitted;
            try
            {
                // The boundary as it was before the request decides whether a short page is the end of the data. While
                // the start lies inside the live stream, the boundary moves with it, and a snapshot taken after the
                // request would always be ahead of what REST answered on a busy symbol: the fill could never finish.
                var before = firstStreamId();
                var trades = await GetPageAsync(name, next, ct).ConfigureAwait(false);
                if (trades is null)
                {
                    return new GapFillResult(emitted, lastEmitted, Complete: false);
                }

                var boundary = firstStreamId();
                var receivedAt = clock.GetUtcNow().UtcDateTime;
                foreach (var (id, price, quantity, timeUtc) in trades)
                {
                    if (id >= boundary)
                    {
                        return Done(name, emitted, lastEmitted, page);
                    }

                    await output.WriteAsync(new Tick(symbolId, id, price, quantity, timeUtc, receivedAt), ct).ConfigureAwait(false);
                    emitted++;
                    lastEmitted = id;
                }

                if (trades.Count > 0)
                {
                    next = trades[^1].Id + 1;
                }

                if (trades.Count < PageSize)
                {
                    // Nothing more on REST. That is the end of the data only when REST has caught up with the stream
                    // (or, before the live connection delivered anything, with what the connections it replaced
                    // delivered); otherwise it lags behind, and the ids up to the boundary are still missing.
                    var reached = before ?? boundary ?? deliveredUpTo() + 1;
                    if (reached is null || next >= reached)
                    {
                        return Done(name, emitted, lastEmitted, page);
                    }

                    LogRestBehind(name.Value, next, reached.Value);
                    return new GapFillResult(emitted, lastEmitted, Complete: false);
                }
            }
            finally
            {
                // Once per page, including one cut short by a cancellation: those trades already reached the channel.
                if (emitted > pageStart)
                {
                    metrics.GapFillTrades(name.Value, emitted - pageStart);
                }
            }
        }

        LogCapReached(name.Value, MaxPages, emitted);
        return new GapFillResult(emitted, lastEmitted, Complete: false);
    }

    /// <summary>
    /// Completes the minute in which a symbol stopped streaming (it was removed): emits the trades after
    /// <paramref name="lastDeliveredId"/> that traded before <paramref name="untilUtc"/>, the end of that minute. Without
    /// it the minute's candle would keep only the trades streamed before the UNSUBSCRIBE, with a wrong close.
    /// </summary>
    /// <remarks>
    /// One pass, the mirror of the lead-in: it pages forward from <c>lastDeliveredId + 1</c> until a trade at or after
    /// <paramref name="untilUtc"/>, a short page (nothing more on REST; the caller waits until the minute is over), the
    /// page cap or a failure. Writes are idempotent, so overlap with trades the stream still delivered is harmless.
    /// </remarks>
    /// <returns><c>Complete</c> is <see langword="false"/> after the page cap or a REST failure.</returns>
    public async Task<GapFillResult> TailOutAsync(
        int symbolId,
        string symbol,
        long lastDeliveredId,
        DateTime untilUtc,
        ChannelWriter<Tick> output,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!SymbolName.TryCreate(symbol, out var name))
        {
            throw new ArgumentException($"'{symbol}' is not a valid symbol.", nameof(symbol));
        }

        using var activity = TickPulseMetrics.ActivitySource.StartActivity(TickPulseMetrics.GapFillSpan);
        activity?.SetTag("tickpulse.symbol", name.Value);
        activity?.SetTag("tickpulse.gapfill.from_id", lastDeliveredId + 1);
        activity?.SetTag("tickpulse.gapfill.until", untilUtc.ToString("O", CultureInfo.InvariantCulture));
        var emitted = 0;
        long? lastEmitted = null;
        var next = lastDeliveredId + 1;
        try
        {
            for (var page = 1; page <= MaxPages; page++)
            {
                var pageStart = emitted;
                try
                {
                    var trades = await GetPageAsync(name, next, ct).ConfigureAwait(false);
                    if (trades is null)
                    {
                        return Tagged(new GapFillResult(emitted, lastEmitted, Complete: false));
                    }

                    var receivedAt = clock.GetUtcNow().UtcDateTime;
                    foreach (var (id, price, quantity, timeUtc) in trades)
                    {
                        if (timeUtc >= untilUtc)
                        {
                            return Tagged(Done(name, emitted, lastEmitted, page));
                        }

                        await output.WriteAsync(new Tick(symbolId, id, price, quantity, timeUtc, receivedAt), ct).ConfigureAwait(false);
                        emitted++;
                        lastEmitted = id;
                    }

                    if (trades.Count < PageSize)
                    {
                        return Tagged(Done(name, emitted, lastEmitted, page));
                    }

                    next = trades[^1].Id + 1;
                }
                finally
                {
                    if (emitted > pageStart)
                    {
                        metrics.GapFillTrades(name.Value, emitted - pageStart);
                    }
                }
            }

            LogCapReached(name.Value, MaxPages, emitted);
            return Tagged(new GapFillResult(emitted, lastEmitted, Complete: false));
        }
        catch (Exception ex) when (activity is not null)
        {
            activity.SetStatus(ActivityStatusCode.Error, ex.GetType().Name);
            throw;
        }

        GapFillResult Tagged(GapFillResult result)
        {
            activity?.SetTag("tickpulse.gapfill.emitted", result.Emitted);
            activity?.SetTag("tickpulse.gapfill.complete", result.Complete);
            return result;
        }
    }

    /// <summary>
    /// The id of the first trade of <paramref name="symbol"/> at or after <paramref name="fromUtc"/> (and within the
    /// hour after it), from REST <c>aggTrades?startTime</c>. A symbol that starts streaming fills from there to its first
    /// streamed trade, so the minute it started in is complete.
    /// </summary>
    /// <remarks>
    /// One hour (the longest range Binance accepts with both bounds) is enough: <paramref name="fromUtc"/> is at most a
    /// minute and a few seconds before the subscription took effect, and every trade after that is streamed. So the
    /// trades the lead-in exists for all lie within the first minutes of the window, even when the lookup is retried much
    /// later; when the window holds no trade at all, none of them exists.
    /// </remarks>
    /// <returns>
    /// <c>Succeeded</c> is <see langword="false"/> when the request failed (worth trying again later);
    /// <c>FirstId</c> is <see langword="null"/> when nothing traded in that hour.
    /// </returns>
    public async Task<(bool Succeeded, long? FirstId)> FirstTradeIdFromAsync(string symbol, DateTime fromUtc, CancellationToken ct)
    {
        if (!SymbolName.TryCreate(symbol, out var name))
        {
            throw new ArgumentException($"'{symbol}' is not a valid symbol.", nameof(symbol));
        }

        var trades = await WithRetriesAsync(
            name,
            "time " + fromUtc.ToString("O", CultureInfo.InvariantCulture),
            token => source.GetAggTradesFromTimeAsync(name, fromUtc, 1, token),
            ct).ConfigureAwait(false);
        return trades is null ? (false, null) : (true, trades is [var first, ..] ? first.Id : null);
    }

    // Null when the symbol has to be given up: a permanent error, a ban, or transient errors that persisted.
    private Task<IReadOnlyList<(long Id, decimal Price, decimal Qty, DateTime TimeUtc)>?> GetPageAsync(
        SymbolName name, long fromId, CancellationToken ct) =>
        WithRetriesAsync(
            name,
            "id " + fromId.ToString(CultureInfo.InvariantCulture),
            token => source.GetAggTradesFromAsync(name, fromId, PageSize, token),
            ct);

    private async Task<IReadOnlyList<(long Id, decimal Price, decimal Qty, DateTime TimeUtc)>?> WithRetriesAsync(
        SymbolName name,
        string from,
        Func<CancellationToken, Task<IReadOnlyList<(long Id, decimal Price, decimal Qty, DateTime TimeUtc)>>> request,
        CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await request(ct).ConfigureAwait(false);
            }
            catch (BinanceApiException ex) when (ex.IsTransient && attempt < MaxTransientRetries)
            {
                LogRetrying(ex, name.Value, from);
                await Task.Delay(TransientRetryDelay, clock, ct).ConfigureAwait(false);
            }
            catch (BinanceApiException ex)
            {
                LogFailed(ex, name.Value, from);
                return null;
            }
            catch (BinanceBannedException ex)
            {
                LogBanned(ex, name.Value, ex.BannedUntilUtc);
                return null;
            }
        }
    }

    private GapFillResult Done(SymbolName name, int emitted, long? lastEmitted, int pages)
    {
        if (emitted > 0)
        {
            LogRecovered(name.Value, emitted, pages);
        }

        return new GapFillResult(emitted, lastEmitted, Complete: true);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Gap-fill recovered {Count} {Symbol} trade(s) from {Pages} page(s)")]
    private partial void LogRecovered(string symbol, int count, int pages);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Gap-fill for {Symbol} stopped at the {Pages}-page cap after {Count} trade(s)")]
    private partial void LogCapReached(string symbol, int pages, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Gap-fill for {Symbol} stopped at id {Next}: REST is behind the stream (which is at {Boundary}); the gap stays open")]
    private partial void LogRestBehind(string symbol, long next, long boundary);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Gap-fill for {Symbol} from {From} failed transiently; retrying")]
    private partial void LogRetrying(Exception exception, string symbol, string from);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Gap-fill for {Symbol} from {From} failed; the gap stays open")]
    private partial void LogFailed(Exception exception, string symbol, string from);

    [LoggerMessage(Level = LogLevel.Error, Message = "Gap-fill for {Symbol} skipped: Binance banned this IP until {BannedUntilUtc:o}")]
    private partial void LogBanned(Exception exception, string symbol, DateTime bannedUntilUtc);
}

/// <summary>What one gap-fill did.</summary>
/// <param name="Emitted">Trades written into the tick channel.</param>
/// <param name="LastEmittedId">The highest of them, if any.</param>
/// <param name="Complete">
/// The fill reached the stream's first id or the end of the data, so the gap is closed. <see langword="false"/> after
/// the page cap, a REST failure, or a short page that ended below the stream's boundary (REST lagging behind the
/// stream): the gap stays open from <paramref name="LastEmittedId"/>.
/// </param>
public sealed record GapFillResult(int Emitted, long? LastEmittedId, bool Complete);

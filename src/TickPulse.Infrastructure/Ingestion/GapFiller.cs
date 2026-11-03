using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using TickPulse.Application.Abstractions;
using TickPulse.Domain;
using TickPulse.Infrastructure.Binance;

namespace TickPulse.Infrastructure.Ingestion;

/// <summary>
/// Recovers the trades a symbol missed while the stream was down, from REST <c>aggTrades?fromId=last+1</c>, and
/// feeds them into the same tick channel as the stream. Writes are idempotent, so overlap with the stream is harmless.
/// </summary>
/// <remarks>
/// It pages forward until a page reaches the first id the new connection delivered, the exchange has nothing more
/// (an empty or short page: from then on the already subscribed stream carries every trade), or
/// <see cref="MaxPages"/> pages were read. Only trades below the stream's first id are emitted.
/// </remarks>
public sealed partial class GapFiller(IAggTradeSource source, TimeProvider clock, ILogger<GapFiller> logger, int maxPages = GapFiller.DefaultMaxPages)
{
    /// <summary>Trades per REST page (the Binance maximum).</summary>
    public const int PageSize = 1000;

    /// <summary>Upper bound on pages per symbol and gap: at most 50,000 trades are recovered.</summary>
    public const int DefaultMaxPages = 50;

    private const int MaxTransientRetries = 2;

    private static readonly TimeSpan TransientRetryDelay = TimeSpan.FromSeconds(1);

    public int MaxPages { get; } = maxPages >= 1 ? maxPages : throw new ArgumentOutOfRangeException(nameof(maxPages));

    /// <summary>Fills the gap after <paramref name="lastKnownId"/> for one symbol. Returns the number of trades emitted.</summary>
    /// <param name="symbolId">Database id of the symbol.</param>
    /// <param name="symbol">Symbol name.</param>
    /// <param name="lastKnownId">The last trade id stored before the gap.</param>
    /// <param name="firstStreamId">
    /// The first trade id the new connection delivered for the symbol, or <see langword="null"/> while none arrived.
    /// Read again for every page.
    /// </param>
    /// <param name="output">The tick channel.</param>
    /// <param name="ct">Cancels the fill (leadership lost, shutdown).</param>
    public async Task<int> FillAsync(
        int symbolId,
        string symbol,
        long lastKnownId,
        Func<long?> firstStreamId,
        ChannelWriter<Tick> output,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(firstStreamId);
        ArgumentNullException.ThrowIfNull(output);
        if (!SymbolName.TryCreate(symbol, out var name))
        {
            throw new ArgumentException($"'{symbol}' is not a valid symbol.", nameof(symbol));
        }

        var emitted = 0;
        var next = lastKnownId + 1;
        for (var page = 1; page <= MaxPages; page++)
        {
            var trades = await GetPageAsync(name, next, ct).ConfigureAwait(false);
            if (trades is null || trades.Count == 0)
            {
                return Done(name, emitted, page);
            }

            var boundary = firstStreamId();
            var receivedAt = clock.GetUtcNow().UtcDateTime;
            foreach (var (id, price, quantity, timeUtc) in trades)
            {
                if (id >= boundary)
                {
                    return Done(name, emitted, page);
                }

                await output.WriteAsync(new Tick(symbolId, id, price, quantity, timeUtc, receivedAt), ct).ConfigureAwait(false);
                emitted++;
            }

            if (trades.Count < PageSize)
            {
                return Done(name, emitted, page);
            }

            next = trades[^1].Id + 1;
        }

        LogCapReached(name.Value, MaxPages, emitted);
        return emitted;
    }

    // Null when the symbol has to be given up: a permanent error, a ban, or transient errors that persisted.
    private async Task<IReadOnlyList<(long Id, decimal Price, decimal Qty, DateTime TimeUtc)>?> GetPageAsync(
        SymbolName name, long fromId, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await source.GetAggTradesFromAsync(name, fromId, PageSize, ct).ConfigureAwait(false);
            }
            catch (BinanceApiException ex) when (ex.IsTransient && attempt < MaxTransientRetries)
            {
                LogRetrying(ex, name.Value, fromId);
                await Task.Delay(TransientRetryDelay, clock, ct).ConfigureAwait(false);
            }
            catch (BinanceApiException ex)
            {
                LogFailed(ex, name.Value, fromId);
                return null;
            }
            catch (BinanceBannedException ex)
            {
                LogBanned(ex, name.Value, ex.BannedUntilUtc);
                return null;
            }
        }
    }

    private int Done(SymbolName name, int emitted, int pages)
    {
        if (emitted > 0)
        {
            LogRecovered(name.Value, emitted, pages);
        }

        return emitted;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Gap-fill recovered {Count} {Symbol} trade(s) from {Pages} page(s)")]
    private partial void LogRecovered(string symbol, int count, int pages);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Gap-fill for {Symbol} stopped at the {Pages}-page cap after {Count} trade(s)")]
    private partial void LogCapReached(string symbol, int pages, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Gap-fill for {Symbol} from id {FromId} failed transiently; retrying")]
    private partial void LogRetrying(Exception exception, string symbol, long fromId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Gap-fill for {Symbol} from id {FromId} failed; the gap stays")]
    private partial void LogFailed(Exception exception, string symbol, long fromId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Gap-fill for {Symbol} skipped: Binance banned this IP until {BannedUntilUtc:o}")]
    private partial void LogBanned(Exception exception, string symbol, DateTime bannedUntilUtc);
}

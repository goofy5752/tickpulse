using TickPulse.Domain;

namespace TickPulse.Application.Abstractions;

/// <summary>The arithmetic mean of the ticks in a time range.</summary>
public sealed record AverageWindow(decimal Average, DateTime FirstTickUtc, DateTime LastTickUtc, long SampleCount);

/// <summary>One aggregated candle of a period.</summary>
public sealed record CandleRow(DateTime BucketStart, decimal Open, decimal High, decimal Low, decimal Close, decimal Volume);

/// <summary>The first and last minute (bucket starts, UTC) of a set of candles.</summary>
public sealed record MinuteRange(DateTime First, DateTime Last);

/// <summary>Read-side port over ticks and candles.</summary>
public interface IMarketDataQueries
{
    Task<AverageWindow?> GetAverageAsync(int symbolId, DateTime fromUtc, DateTime toUtc, CancellationToken ct);

    Task<IReadOnlyList<PricePoint>> GetClosesAsync(int symbolId, Period p, SmaWindow w, CancellationToken ct);

    Task<IReadOnlyList<CandleRow>> GetCandlesAsync(int symbolId, Period p, int limit, CancellationToken ct);

    Task<decimal?> GetCloseAtOrBeforeAsync(int symbolId, DateTime utc, CancellationToken ct);

    /// <summary>
    /// For each of <paramref name="symbolIds"/>, the close and start of its latest 1-minute candle starting at or before
    /// <paramref name="utc"/>, in one round trip. Symbols without such a candle are absent from the result.
    /// </summary>
    Task<IReadOnlyDictionary<int, PricePoint>> GetClosesAtOrBeforeAsync(IReadOnlyCollection<int> symbolIds, DateTime utc, CancellationToken ct);

    Task<long> ApproximateTickCountAsync(CancellationToken ct);

    Task<DateTime?> FirstStreamMinuteAsync(int symbolId, CancellationToken ct);

    /// <summary>
    /// The earliest 1-minute bucket holding streamed ticks that starts at or after <paramref name="atOrAfterUtc"/>, or
    /// <see langword="null"/>.
    /// </summary>
    Task<DateTime?> FirstStreamMinuteAtOrAfterAsync(int symbolId, DateTime atOrAfterUtc, CancellationToken ct);

    /// <summary>The latest stored 1-minute bucket (streamed or backfilled) starting before <paramref name="beforeUtc"/>, or <see langword="null"/>.</summary>
    Task<DateTime?> LastMinuteBeforeAsync(int symbolId, DateTime beforeUtc, CancellationToken ct);

    /// <summary>The latest 1-minute bucket holding streamed ticks that starts before <paramref name="beforeUtc"/>, or <see langword="null"/>.</summary>
    Task<DateTime?> LastStreamMinuteBeforeAsync(int symbolId, DateTime beforeUtc, CancellationToken ct);

    /// <summary>
    /// The earliest and latest backfilled (exchange) minutes strictly before <paramref name="beforeUtc"/>, or
    /// <see langword="null"/> when there are none.
    /// </summary>
    Task<MinuteRange?> BackfillRangeBeforeAsync(int symbolId, DateTime beforeUtc, CancellationToken ct);

    /// <summary>
    /// For every active symbol, the trade id of its newest stored tick traded after <paramref name="sinceUtc"/> (the
    /// highest id, as exchange ids grow with trade time; never higher than the highest),
    /// keyed by symbol name. Symbols without such a tick are absent.
    /// </summary>
    Task<IReadOnlyDictionary<string, long>> GetLatestTradeIdsAsync(DateTime sinceUtc, CancellationToken ct);
}

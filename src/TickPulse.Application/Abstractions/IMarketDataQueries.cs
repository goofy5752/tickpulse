using TickPulse.Domain;

namespace TickPulse.Application.Abstractions;

/// <summary>The arithmetic mean of the ticks in a time range.</summary>
public sealed record AverageWindow(decimal Average, DateTime FirstTickUtc, DateTime LastTickUtc, long SampleCount);

/// <summary>One aggregated candle of a period.</summary>
public sealed record CandleRow(DateTime BucketStart, decimal Open, decimal High, decimal Low, decimal Close, decimal Volume);

/// <summary>Read-side port over ticks and candles.</summary>
public interface IMarketDataQueries
{
    Task<AverageWindow?> GetAverageAsync(int symbolId, DateTime fromUtc, DateTime toUtc, CancellationToken ct);

    Task<IReadOnlyList<PricePoint>> GetClosesAsync(int symbolId, Period p, SmaWindow w, CancellationToken ct);

    Task<IReadOnlyList<CandleRow>> GetCandlesAsync(int symbolId, Period p, int limit, CancellationToken ct);

    Task<decimal?> GetCloseAtOrBeforeAsync(int symbolId, DateTime utc, CancellationToken ct);

    Task<long> ApproximateTickCountAsync(CancellationToken ct);

    Task<DateTime?> FirstStreamMinuteAsync(int symbolId, CancellationToken ct);
}

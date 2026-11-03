using TickPulse.Domain;

namespace TickPulse.Application.Abstractions;

/// <summary>Writes historical 1-minute candles that never overwrite streamed ones.</summary>
public interface ICandleWriter
{
    /// <summary>Returns the number of candles that were newly inserted.</summary>
    Task<int> InsertBackfillAsync(int symbolId, IReadOnlyList<Kline> klines, CancellationToken ct);
}

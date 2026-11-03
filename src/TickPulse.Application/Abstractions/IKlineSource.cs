using TickPulse.Domain;

namespace TickPulse.Application.Abstractions;

/// <summary>Source of historical 1-minute candles.</summary>
public interface IKlineSource
{
    Task<IReadOnlyList<Kline>> GetMinuteKlinesAsync(SymbolName s, DateTime fromUtc, DateTime toUtc, int limit, CancellationToken ct);
}

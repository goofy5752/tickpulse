using TickPulse.Domain;

namespace TickPulse.Application.Abstractions;

/// <summary>Source of historical aggregated trades, used to fill gaps after a reconnect.</summary>
public interface IAggTradeSource
{
    Task<IReadOnlyList<(long Id, decimal Price, decimal Qty, DateTime TimeUtc)>> GetAggTradesFromAsync(SymbolName s, long fromId, int limit, CancellationToken ct);
}

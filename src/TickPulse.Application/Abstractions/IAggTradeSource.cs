using TickPulse.Domain;

namespace TickPulse.Application.Abstractions;

/// <summary>Source of historical aggregated trades, used to fill gaps after a reconnect.</summary>
public interface IAggTradeSource
{
    Task<IReadOnlyList<(long Id, decimal Price, decimal Qty, DateTime TimeUtc)>> GetAggTradesFromAsync(SymbolName s, long fromId, int limit, CancellationToken ct);

    /// <summary>
    /// The first trades (ascending, at most <paramref name="limit"/>) at or after <paramref name="startUtc"/> and less
    /// than an hour after it.
    /// </summary>
    Task<IReadOnlyList<(long Id, decimal Price, decimal Qty, DateTime TimeUtc)>> GetAggTradesFromTimeAsync(SymbolName s, DateTime startUtc, int limit, CancellationToken ct);
}

namespace TickPulse.Domain;

/// <summary>A single aggregated trade.</summary>
public sealed record Tick(
    int SymbolId,
    long AggTradeId,
    decimal Price,
    decimal Quantity,
    DateTime TradeTimeUtc,
    DateTime ReceivedAtUtc);

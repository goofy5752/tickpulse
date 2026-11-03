namespace TickPulse.Domain;

/// <summary>An exchange candle (open/high/low/close with volumes).</summary>
public sealed record Kline(
    DateTime OpenTimeUtc,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume,
    decimal QuoteVolume);

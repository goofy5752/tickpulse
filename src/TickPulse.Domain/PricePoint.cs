namespace TickPulse.Domain;

/// <summary>The closing price of one bucket.</summary>
public readonly record struct PricePoint(DateTime BucketStart, decimal Close);

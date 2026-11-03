namespace TickPulse.Application.Abstractions;

/// <summary>Progress of the historical candle backfill for a symbol. Stored as a <c>smallint</c>.</summary>
public enum BackfillStatus : short
{
    NotRequested = 0,
    Pending = 1,
    Running = 2,
    Completed = 3,
    Failed = 4,
}

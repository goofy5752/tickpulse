using TickPulse.Domain;

namespace TickPulse.Application.Abstractions;

/// <summary>Writes trade ticks idempotently and folds the newly inserted ones into the 1-minute candles.</summary>
public interface ITickWriter
{
    /// <summary>Returns the number of ticks that were newly inserted (replayed ticks are ignored).</summary>
    Task<int> WriteBatchAsync(IReadOnlyList<Tick> ticks, CancellationToken ct);
}

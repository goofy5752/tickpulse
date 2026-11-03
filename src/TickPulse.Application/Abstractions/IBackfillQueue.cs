using System.Diagnostics.CodeAnalysis;

namespace TickPulse.Application.Abstractions;

/// <summary>Queues a symbol for historical candle backfill.</summary>
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "It is a work queue; the name describes exactly that.")]
public interface IBackfillQueue
{
    void Enqueue(int symbolId);
}

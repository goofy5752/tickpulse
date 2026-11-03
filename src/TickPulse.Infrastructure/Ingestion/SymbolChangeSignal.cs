using System.Threading.Channels;
using TickPulse.Application.Abstractions;

namespace TickPulse.Infrastructure.Ingestion;

/// <summary>
/// Coalescing in-process signal from the symbol admin use case to the ingestion reconciler. Any number of
/// <see cref="Notify"/> calls before the next <see cref="WaitAsync"/> wake it once: the reconciler always reads the
/// full active set from the database, so one pass covers every change made before it started.
/// </summary>
/// <remarks>Meant for a single waiter (the reconciler). Thread-safe.</remarks>
public sealed class SymbolChangeSignal : ISymbolChangeSignal
{
    // Capacity 1 with DropWrite: a pending notification absorbs every further one.
    private readonly Channel<bool> _pending = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = false,
        SingleWriter = false,
    });

    /// <inheritdoc />
    public void Notify() => _pending.Writer.TryWrite(true);

    /// <inheritdoc />
    public async ValueTask WaitAsync(CancellationToken ct) => await _pending.Reader.ReadAsync(ct).ConfigureAwait(false);
}

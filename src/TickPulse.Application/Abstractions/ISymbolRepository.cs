using TickPulse.Domain;

namespace TickPulse.Application.Abstractions;

/// <summary>Persistence port for monitored symbols.</summary>
public interface ISymbolRepository
{
    Task<IReadOnlyList<MonitoredSymbol>> ListAsync(CancellationToken ct);

    Task<IReadOnlyList<MonitoredSymbol>> ListActiveAsync(CancellationToken ct);

    Task<MonitoredSymbol?> FindAsync(SymbolName name, CancellationToken ct);

    /// <summary>
    /// Creates the symbol, or reactivates it, unless that would take the number of active symbols above
    /// <paramref name="maxActiveSymbols"/>. The limit check and the write happen atomically, so concurrent
    /// adds cannot exceed the limit. An already active symbol is returned unchanged. Reactivating keeps
    /// <see cref="MonitoredSymbol.DeactivatedAtUtc"/>, so the backfill can find the stretch the symbol was inactive.
    /// </summary>
    Task<SymbolAddOutcome> AddOrReactivateAsync(SymbolName name, int maxActiveSymbols, CancellationToken ct);

    /// <summary>Marks the symbol inactive. Returns <see langword="false"/> when it does not exist.</summary>
    Task<bool> DeactivateAsync(SymbolName name, CancellationToken ct);

    /// <summary>Number of active symbols, for statistics. Not used to enforce the limit.</summary>
    Task<int> CountActiveAsync(CancellationToken ct);

    Task SetBackfillStatusAsync(int symbolId, BackfillStatus status, DateTime? fromUtc, CancellationToken ct);

    /// <summary>
    /// Marks the symbol <see cref="BackfillStatus.Pending"/>, keeping <see cref="MonitoredSymbol.BackfilledFromUtc"/>, but only
    /// while it is active and its backfill has not completed: a symbol removed since it was read is not left Pending,
    /// and a completion written meanwhile (by another instance) is not overwritten. Returns whether it was marked.
    /// </summary>
    Task<bool> MarkPendingIfUnfinishedAsync(int symbolId, CancellationToken ct);
}

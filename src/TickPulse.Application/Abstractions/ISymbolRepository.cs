using TickPulse.Domain;

namespace TickPulse.Application.Abstractions;

/// <summary>Persistence port for monitored symbols.</summary>
public interface ISymbolRepository
{
    Task<IReadOnlyList<MonitoredSymbol>> ListAsync(CancellationToken ct);

    Task<IReadOnlyList<MonitoredSymbol>> ListActiveAsync(CancellationToken ct);

    Task<MonitoredSymbol?> FindAsync(SymbolName name, CancellationToken ct);

    Task<MonitoredSymbol> AddOrReactivateAsync(SymbolName name, CancellationToken ct);

    /// <summary>Marks the symbol inactive. Returns <see langword="false"/> when it does not exist.</summary>
    Task<bool> DeactivateAsync(SymbolName name, CancellationToken ct);

    Task<int> CountActiveAsync(CancellationToken ct);

    Task SetBackfillStatusAsync(int symbolId, BackfillStatus status, DateTime? fromUtc, CancellationToken ct);
}

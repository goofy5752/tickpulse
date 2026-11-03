namespace TickPulse.Application.Abstractions;

/// <summary>A symbol TickPulse monitors (or used to monitor, when inactive).</summary>
public sealed record MonitoredSymbol(
    int Id,
    string Name,
    bool IsActive,
    DateTime CreatedAtUtc,
    DateTime? DeactivatedAtUtc,
    BackfillStatus BackfillStatus,
    DateTime? BackfilledFromUtc);

/// <summary>The result of adding a symbol: the stored symbol and whether it was newly created (vs. reactivated or already active).</summary>
public sealed record SymbolAddOutcome(MonitoredSymbol Symbol, bool Created);

namespace TickPulse.Application.Abstractions;

/// <summary>
/// A symbol TickPulse monitors (or used to monitor, when inactive). <c>DeactivatedAtUtc</c> is when it was last
/// deactivated, or <see langword="null"/> if it never was; reactivating keeps it, because the backfill needs it to find
/// the stretch the symbol was inactive. It is internal state: the admin contract shows it only while the symbol is
/// inactive. <c>ReactivatedAtUtc</c> is when it was last reactivated (null if never): the inactive stretch ends at the
/// first minute streamed since then.
/// </summary>
public sealed record MonitoredSymbol(
    int Id,
    string Name,
    bool IsActive,
    DateTime CreatedAtUtc,
    DateTime? DeactivatedAtUtc,
    BackfillStatus BackfillStatus,
    DateTime? BackfilledFromUtc,
    DateTime? ReactivatedAtUtc = null);

/// <summary>What adding a symbol did.</summary>
public enum SymbolAddStatus
{
    Created,
    Reactivated,
    AlreadyActive,
    LimitReached,
}

/// <summary>
/// The result of adding a symbol. <see cref="Symbol"/> is the stored symbol, or <see langword="null"/> when
/// <see cref="Status"/> is <see cref="SymbolAddStatus.LimitReached"/>.
/// </summary>
public sealed record SymbolAddOutcome(MonitoredSymbol? Symbol, SymbolAddStatus Status);

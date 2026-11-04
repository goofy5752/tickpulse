using TickPulse.Application.Abstractions;
using TickPulse.Application.Results;
using TickPulse.Domain;

namespace TickPulse.Application.Symbols;

/// <summary>Settings of <see cref="SymbolAdminService"/>.</summary>
/// <param name="MaxActiveSymbols">The most symbols that may be active at once (at least 1).</param>
/// <param name="BackfillEnabled">Whether a newly active symbol is queued for the historical candle backfill.</param>
public sealed record SymbolAdminSettings(int MaxActiveSymbols, bool BackfillEnabled);

/// <summary>An add that succeeded: the stored symbol, and whether it became active just now.</summary>
/// <param name="Symbol">The symbol as stored after the add.</param>
/// <param name="Created">
/// True when the symbol was created or reactivated by this call; false when it was already active (nothing changed).
/// </param>
public sealed record SymbolAddResult(MonitoredSymbol Symbol, bool Created);

/// <summary>
/// The admin use cases: list every symbol, add (or reactivate) one, and deactivate one. Adding validates the name, then
/// asks the exchange whether it trades, then activates it atomically against <see cref="SymbolAdminSettings.MaxActiveSymbols"/>.
/// Every change wakes the ingestion worker through <see cref="ISymbolChangeSignal"/>.
/// </summary>
/// <remarks>
/// <para>An already active symbol is answered from the database, without asking the exchange. The exchange check comes
/// before the write, so a symbol the exchange does not trade is rejected whatever the active count.</para>
/// <para>Removing deactivates: the history stays stored and queryable, and adding the symbol again reactivates it. A
/// symbol that is unknown, malformed or already inactive is <see cref="AppError.UnknownSymbol"/>, so a repeated remove
/// is reported rather than silently accepted.</para>
/// </remarks>
public sealed class SymbolAdminService
{
    private readonly ISymbolRepository _symbols;
    private readonly IExchangeMetadata _exchange;
    private readonly ISymbolChangeSignal _changes;
    private readonly IBackfillQueue _backfill;
    private readonly SymbolAdminSettings _settings;

    /// <exception cref="ArgumentOutOfRangeException"><see cref="SymbolAdminSettings.MaxActiveSymbols"/> is below 1.</exception>
    public SymbolAdminService(
        ISymbolRepository symbols,
        IExchangeMetadata exchange,
        ISymbolChangeSignal changes,
        IBackfillQueue backfill,
        SymbolAdminSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentOutOfRangeException.ThrowIfLessThan(settings.MaxActiveSymbols, 1, nameof(settings));
        _symbols = symbols;
        _exchange = exchange;
        _changes = changes;
        _backfill = backfill;
        _settings = settings;
    }

    /// <summary>Every symbol TickPulse monitors or used to monitor, in id order.</summary>
    public async Task<AppResult<IReadOnlyList<MonitoredSymbol>>> ListAsync(CancellationToken ct) =>
        AppResult<IReadOnlyList<MonitoredSymbol>>.Ok(await _symbols.ListAsync(ct));

    /// <summary>
    /// Activates <paramref name="raw"/>: <see cref="AppError.InvalidSymbol"/> when it is malformed or not trading on the
    /// exchange, <see cref="AppError.UpstreamUnavailable"/> when the exchange cannot tell, and
    /// <see cref="AppError.SymbolLimit"/> when the active limit is reached.
    /// </summary>
    public async Task<AppResult<SymbolAddResult>> AddAsync(string? raw, CancellationToken ct)
    {
        if (!SymbolName.TryCreate(raw, out var name))
        {
            return AppResult<SymbolAddResult>.Fail(
                AppError.InvalidSymbol,
                $"'{raw?.Trim()}' is not a valid symbol: use 5 to 20 letters or digits, e.g. BTCUSDT.");
        }

        if (await _symbols.FindAsync(name, ct) is { IsActive: true } active)
        {
            return AppResult<SymbolAddResult>.Ok(new SymbolAddResult(active, Created: false));
        }

        bool trading;
        try
        {
            trading = await _exchange.IsTradingSymbolAsync(name, ct);
        }
        catch (ExchangeUnavailableException)
        {
            return AppResult<SymbolAddResult>.Fail(
                AppError.UpstreamUnavailable,
                $"The exchange cannot confirm '{name}' right now; try again later.");
        }

        if (!trading)
        {
            return AppResult<SymbolAddResult>.Fail(AppError.InvalidSymbol, $"'{name}' is not trading on Binance spot.");
        }

        var outcome = await _symbols.AddOrReactivateAsync(name, _settings.MaxActiveSymbols, ct);
        switch (outcome)
        {
            case { Status: SymbolAddStatus.LimitReached }:
                return AppResult<SymbolAddResult>.Fail(
                    AppError.SymbolLimit,
                    $"At most {_settings.MaxActiveSymbols} symbols can be active; remove one before adding '{name}'.");
            case { Status: SymbolAddStatus.AlreadyActive, Symbol: { } existing }:
                // Activated by a concurrent request between the lookup and the write.
                return AppResult<SymbolAddResult>.Ok(new SymbolAddResult(existing, Created: false));
            case { Status: SymbolAddStatus.Created or SymbolAddStatus.Reactivated, Symbol: { } stored }:
                _changes.Notify();
                return AppResult<SymbolAddResult>.Ok(new SymbolAddResult(await QueueBackfillAsync(stored, ct), Created: true));
            default:
                throw new InvalidOperationException($"The repository returned {outcome.Status} without a symbol.");
        }
    }

    /// <summary>
    /// Deactivates <paramref name="raw"/> and returns its normalised name; <see cref="AppError.UnknownSymbol"/> when it
    /// is not an active symbol.
    /// </summary>
    public async Task<AppResult<string>> RemoveAsync(string? raw, CancellationToken ct)
    {
        if (!SymbolName.TryCreate(raw, out var name)
            || await _symbols.FindAsync(name, ct) is not { IsActive: true }
            || !await _symbols.DeactivateAsync(name, ct))
        {
            var shown = SymbolName.TryCreate(raw, out var normalised) ? normalised.Value : raw?.Trim();
            return AppResult<string>.Fail(AppError.UnknownSymbol, $"Symbol '{shown}' is not an active symbol.");
        }

        _changes.Notify();
        return AppResult<string>.Ok(name.Value);
    }

    // Pending is stored before the worker sees the id, so the list never shows NotRequested for a queued symbol.
    private async Task<MonitoredSymbol> QueueBackfillAsync(MonitoredSymbol symbol, CancellationToken ct)
    {
        if (!_settings.BackfillEnabled)
        {
            return symbol;
        }

        await _symbols.SetBackfillStatusAsync(symbol.Id, BackfillStatus.Pending, symbol.BackfilledFromUtc, ct);
        _backfill.Enqueue(symbol.Id);
        return symbol with { BackfillStatus = BackfillStatus.Pending };
    }
}

using TickPulse.Application.Abstractions;
using TickPulse.Application.Analytics;
using TickPulse.Contracts;

namespace TickPulse.Api.Controllers;

/// <summary>Maps the use-case results onto the contracts.</summary>
internal static class ContractMapping
{
    public static AveragePriceResponse ToContract(this AveragePriceResult r) => new()
    {
        Symbol = r.Symbol,
        AveragePrice = r.AveragePrice,
        From = r.FromUtc,
        To = r.ToUtc,
        CoveredSeconds = r.CoveredSeconds,
        SampleCount = r.SampleCount,
        WindowHours = r.WindowHours,
    };

    public static SmaResponse ToContract(this SmaResult r) => new()
    {
        Symbol = r.Symbol,
        Period = r.Period.Code,
        RequestedDataPoints = r.RequestedDataPoints,
        ActualDataPoints = r.ActualDataPoints,
        Value = r.Value,
        WindowStart = r.Window.Start,
        WindowEnd = r.Window.EndExclusive,
        ReferenceTime = r.ReferenceTimeUtc,
        StartParameter = r.StartParameterUtc,
        DataPoints = [.. r.DataPoints.Select(p => new SmaDataPoint { BucketStart = p.BucketStart, Close = p.Close })],
    };

    public static SymbolListResponse ToContract(this IReadOnlyList<SymbolSummary> symbols) =>
        new(symbols.Select(s => new SymbolResponse
        {
            Symbol = s.Symbol,
            IsActive = s.IsActive,
            LatestPrice = s.LatestPrice,
            Change24hPercent = s.Change24hPercent,
            BackfillStatus = s.BackfillStatus.ToString(),
        }));

    /// <remarks>
    /// The repository keeps <see cref="MonitoredSymbol.DeactivatedAtUtc"/> after a reactivation (the backfill uses it
    /// to find the stretch the symbol was inactive); the contract shows it only while the symbol is inactive.
    /// </remarks>
    public static AdminSymbolResponse ToAdminContract(this MonitoredSymbol s) => new()
    {
        Symbol = s.Name,
        IsActive = s.IsActive,
        CreatedAt = s.CreatedAtUtc,
        DeactivatedAt = s.IsActive ? null : s.DeactivatedAtUtc,
        BackfillStatus = s.BackfillStatus.ToString(),
        BackfilledFrom = s.BackfilledFromUtc,
    };

    public static AdminSymbolListResponse ToAdminContract(this IReadOnlyList<MonitoredSymbol> symbols) =>
        new(symbols.Select(s => s.ToAdminContract()));

    public static CandleListResponse ToContract(this IReadOnlyList<CandleRow> candles) =>
        new(candles.Select(c => new CandleResponse
        {
            BucketStart = c.BucketStart,
            Open = c.Open,
            High = c.High,
            Low = c.Low,
            Close = c.Close,
            Volume = c.Volume,
        }));
}

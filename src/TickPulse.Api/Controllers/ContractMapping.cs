using TickPulse.Application.Abstractions;
using TickPulse.Application.Analytics;
using TickPulse.Contracts;

namespace TickPulse.Api.Controllers;

/// <summary>Maps the analytics use-case results onto the public contracts.</summary>
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

using TickPulse.Application.Abstractions;
using TickPulse.Domain;

namespace TickPulse.Application.Analytics;

/// <summary>
/// The arithmetic mean of the trades collected in the last <see cref="WindowHours"/> hours. <see cref="FromUtc"/> and
/// <see cref="ToUtc"/> are the first and last trade actually found, so they describe the covered span, which is
/// shorter than the window while less data has been collected.
/// </summary>
public sealed record AveragePriceResult(
    string Symbol,
    decimal AveragePrice,
    DateTime FromUtc,
    DateTime ToUtc,
    long CoveredSeconds,
    long SampleCount,
    int WindowHours);

/// <summary>
/// A simple moving average of bucket closes. <see cref="Window"/> is the half-open range of bucket starts that was
/// requested; <see cref="DataPoints"/> are the non-empty buckets in it, oldest first.
/// </summary>
public sealed record SmaResult(
    string Symbol,
    Period Period,
    int RequestedDataPoints,
    decimal Value,
    SmaWindow Window,
    DateTime ReferenceTimeUtc,
    DateTime? StartParameterUtc,
    IReadOnlyList<PricePoint> DataPoints)
{
    public int ActualDataPoints => DataPoints.Count;
}

/// <summary>A monitored (or formerly monitored) symbol with its latest price and its change over 24 hours.</summary>
public sealed record SymbolSummary(
    string Symbol,
    bool IsActive,
    decimal? LatestPrice,
    decimal? Change24hPercent,
    BackfillStatus BackfillStatus);

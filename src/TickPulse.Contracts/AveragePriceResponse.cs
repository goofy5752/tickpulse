using System.Runtime.Serialization;

namespace TickPulse.Contracts;

/// <summary>
/// <c>GET /api/{symbol}/24hAvgPrice</c>: the arithmetic mean of the trades collected in the last
/// <see cref="WindowHours"/> hours. <see cref="From"/> and <see cref="To"/> are the first and last trade found (UTC),
/// so <see cref="CoveredSeconds"/> is shorter than the window while less data has been collected.
/// </summary>
[DataContract(Name = "averagePrice", Namespace = Schema.Namespace)]
public sealed class AveragePriceResponse
{
    [DataMember(Order = 1)]
    public string Symbol { get; init; } = string.Empty;

    /// <summary>Rounded to 8 decimal places (banker's rounding).</summary>
    [DataMember(Order = 2)]
    public decimal AveragePrice { get; init; }

    [DataMember(Order = 3)]
    public DateTime From { get; init; }

    [DataMember(Order = 4)]
    public DateTime To { get; init; }

    [DataMember(Order = 5)]
    public long CoveredSeconds { get; init; }

    [DataMember(Order = 6)]
    public long SampleCount { get; init; }

    [DataMember(Order = 7)]
    public int WindowHours { get; init; }
}

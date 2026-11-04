using System.Diagnostics.CodeAnalysis;
using System.Runtime.Serialization;

namespace TickPulse.Contracts;

/// <summary>
/// <c>GET /api/{symbol}/SimpleMovingAverage</c>: the mean of the closes of up to <see cref="RequestedDataPoints"/>
/// complete <see cref="Period"/> buckets whose starts lie in <c>[WindowStart, WindowEnd)</c> (UTC). Empty buckets
/// are skipped, so <see cref="ActualDataPoints"/> may be smaller.
/// </summary>
[DataContract(Name = "sma", Namespace = Schema.Namespace)]
public sealed class SmaResponse
{
    [DataMember(Order = 1)]
    public string Symbol { get; init; } = string.Empty;

    /// <summary>One of <c>1w</c>, <c>1d</c>, <c>30m</c>, <c>5m</c>, <c>1m</c>.</summary>
    [DataMember(Order = 2)]
    public string Period { get; init; } = string.Empty;

    [DataMember(Order = 3)]
    public int RequestedDataPoints { get; init; }

    [DataMember(Order = 4)]
    public int ActualDataPoints { get; init; }

    /// <summary>Rounded to 8 decimal places (banker's rounding).</summary>
    [DataMember(Order = 5)]
    public decimal Value { get; init; }

    /// <summary>Start of the first bucket in the window.</summary>
    [DataMember(Order = 6)]
    public DateTime WindowStart { get; init; }

    /// <summary>End of the last bucket in the window (exclusive).</summary>
    [DataMember(Order = 7)]
    public DateTime WindowEnd { get; init; }

    /// <summary>The server time the window was computed against.</summary>
    [DataMember(Order = 8)]
    public DateTime ReferenceTime { get; init; }

    /// <summary>The <c>s</c> parameter converted to UTC, or <see langword="null"/> when it was not given.</summary>
    [DataMember(Order = 9)]
    public DateTime? StartParameter { get; init; }

    /// <summary>The non-empty buckets of the window, oldest first.</summary>
    [DataMember(Order = 10)]
    [SuppressMessage("Design", "CA1002:Do not expose generic lists", Justification = "A serialisable DTO; DataContractSerializer needs a concrete list to deserialise.")]
    public List<SmaDataPoint> DataPoints { get; init; } = [];
}

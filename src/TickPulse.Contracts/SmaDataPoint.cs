using System.Runtime.Serialization;

namespace TickPulse.Contracts;

/// <summary>The close of one complete bucket that starts at <see cref="BucketStart"/> (UTC).</summary>
[DataContract(Name = "dataPoint", Namespace = Schema.Namespace)]
public sealed class SmaDataPoint
{
    [DataMember(Order = 1)]
    public DateTime BucketStart { get; init; }

    [DataMember(Order = 2)]
    public decimal Close { get; init; }
}

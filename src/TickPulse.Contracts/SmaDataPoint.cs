using System.Runtime.Serialization;

namespace TickPulse.Contracts;

/// <summary>The close of one complete bucket that starts at <see cref="BucketStart"/> (UTC).</summary>
[DataContract(Name = "dataPoint", Namespace = Schema.Namespace)]
public sealed class SmaDataPoint
{
    [DataMember(Name = "bucketStart", Order = 1)]
    public DateTime BucketStart { get; init; }

    [DataMember(Name = "close", Order = 2)]
    public decimal Close { get; init; }
}

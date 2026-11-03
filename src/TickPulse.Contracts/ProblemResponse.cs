using System.Runtime.Serialization;

namespace TickPulse.Contracts;

/// <summary>An RFC 9457 problem body as TickPulse serialises it in JSON and XML.</summary>
[DataContract(Name = "problem", Namespace = "https://tickpulse.dev/schema/v1")]
public sealed class ProblemResponse
{
    [DataMember(Order = 1)]
    public string? Type { get; init; }

    [DataMember(Order = 2)]
    public string? Title { get; init; }

    [DataMember(Order = 3)]
    public int Status { get; init; }

    [DataMember(Order = 4)]
    public string? Detail { get; init; }

    [DataMember(Order = 5)]
    public string? TraceId { get; init; }
}

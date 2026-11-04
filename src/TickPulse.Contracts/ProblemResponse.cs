using System.Runtime.Serialization;

namespace TickPulse.Contracts;

/// <summary>An RFC 9457 problem body as TickPulse serialises it in JSON and XML.</summary>
[DataContract(Name = "problem", Namespace = "https://tickpulse.dev/schema/v1")]
public sealed class ProblemResponse
{
    [DataMember(Name = "type", Order = 1)]
    public string? Type { get; init; }

    [DataMember(Name = "title", Order = 2)]
    public string? Title { get; init; }

    [DataMember(Name = "status", Order = 3)]
    public int Status { get; init; }

    [DataMember(Name = "detail", Order = 4)]
    public string? Detail { get; init; }

    [DataMember(Name = "traceId", Order = 5)]
    public string? TraceId { get; init; }
}

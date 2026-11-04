using System.Runtime.Serialization;

namespace TickPulse.Contracts;

/// <summary>One entry of <c>GET /api/{symbol}/candles</c>: the OHLCV of the period starting at <see cref="BucketStart"/> (UTC).</summary>
[DataContract(Name = "candle", Namespace = Schema.Namespace)]
public sealed class CandleResponse
{
    [DataMember(Name = "bucketStart", Order = 1)]
    public DateTime BucketStart { get; init; }

    [DataMember(Name = "open", Order = 2)]
    public decimal Open { get; init; }

    [DataMember(Name = "high", Order = 3)]
    public decimal High { get; init; }

    [DataMember(Name = "low", Order = 4)]
    public decimal Low { get; init; }

    [DataMember(Name = "close", Order = 5)]
    public decimal Close { get; init; }

    [DataMember(Name = "volume", Order = 6)]
    public decimal Volume { get; init; }
}

/// <summary><c>GET /api/{symbol}/candles</c>: a JSON array, or <c>&lt;candles&gt;</c> of <c>&lt;candle&gt;</c> in XML, oldest first.</summary>
[CollectionDataContract(Name = "candles", ItemName = "candle", Namespace = Schema.Namespace)]
public sealed class CandleListResponse : List<CandleResponse>
{
    public CandleListResponse()
    {
    }

    public CandleListResponse(IEnumerable<CandleResponse> items)
        : base(items)
    {
    }
}

using System.Runtime.Serialization;

namespace TickPulse.Contracts;

/// <summary>One entry of <c>GET /api/symbols</c>: a monitored, or formerly monitored, symbol.</summary>
[DataContract(Name = "symbol", Namespace = Schema.Namespace)]
public sealed class SymbolResponse
{
    [DataMember(Name = "symbol", Order = 1)]
    public string Symbol { get; init; } = string.Empty;

    /// <summary>Whether trades are being collected; an inactive symbol keeps its history and can still be queried.</summary>
    [DataMember(Name = "isActive", Order = 2)]
    public bool IsActive { get; init; }

    /// <summary>The live price, or the last stored close; <see langword="null"/> without any data.</summary>
    [DataMember(Name = "latestPrice", Order = 3)]
    public decimal? LatestPrice { get; init; }

    /// <summary>
    /// <c>(latest − close 24 h ago) / close 24 h ago × 100</c>, to 2 decimal places; <see langword="null"/> when
    /// either price is unknown.
    /// </summary>
    [DataMember(Name = "change24hPercent", Order = 4)]
    public decimal? Change24hPercent { get; init; }

    /// <summary><c>NotRequested</c>, <c>Pending</c>, <c>Running</c>, <c>Completed</c> or <c>Failed</c>.</summary>
    [DataMember(Name = "backfillStatus", Order = 5)]
    public string BackfillStatus { get; init; } = string.Empty;
}

/// <summary><c>GET /api/symbols</c>: a JSON array, or <c>&lt;symbols&gt;</c> of <c>&lt;symbol&gt;</c> in XML.</summary>
[CollectionDataContract(Name = "symbols", ItemName = "symbol", Namespace = Schema.Namespace)]
public sealed class SymbolListResponse : List<SymbolResponse>
{
    public SymbolListResponse()
    {
    }

    public SymbolListResponse(IEnumerable<SymbolResponse> items)
        : base(items)
    {
    }
}

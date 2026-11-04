using System.Runtime.Serialization;

namespace TickPulse.Contracts;

/// <summary>The market hub's <c>price</c> message: the latest stored trade of one subscribed symbol.</summary>
[DataContract(Name = "livePrice", Namespace = Schema.Namespace)]
public sealed class LivePriceMessage
{
    [DataMember(Name = "symbol", Order = 1)]
    public string Symbol { get; init; } = string.Empty;

    [DataMember(Name = "price", Order = 2)]
    public decimal Price { get; init; }

    /// <summary>The trade's time (UTC).</summary>
    [DataMember(Name = "tradeTime", Order = 3)]
    public DateTime TradeTime { get; init; }
}

using System.Runtime.Serialization;

namespace TickPulse.Contracts;

/// <summary>
/// <c>POST /api/admin/symbols</c>: <c>{"symbol":"BNBUSDT"}</c>, or
/// <c>&lt;addSymbolRequest xmlns="https://tickpulse.dev/schema/v1"&gt;&lt;symbol&gt;BNBUSDT&lt;/symbol&gt;&lt;/addSymbolRequest&gt;</c>.
/// </summary>
[DataContract(Name = "addSymbolRequest", Namespace = Schema.Namespace)]
public sealed class AddSymbolRequest
{
    /// <summary>The symbol to monitor, case-insensitive, e.g. <c>bnbusdt</c>. Validated by the server.</summary>
    [DataMember(Name = "symbol", Order = 1)]
    public string? Symbol { get; init; }
}

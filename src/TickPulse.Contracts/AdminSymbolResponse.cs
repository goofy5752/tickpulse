using System.Runtime.Serialization;

namespace TickPulse.Contracts;

/// <summary>
/// A symbol as the admin API shows it: one entry of <c>GET /api/admin/symbols</c>, and the body of a successful
/// <c>POST /api/admin/symbols</c>.
/// </summary>
[DataContract(Name = "adminSymbol", Namespace = Schema.Namespace)]
public sealed class AdminSymbolResponse
{
    [DataMember(Name = "symbol", Order = 1)]
    public string Symbol { get; init; } = string.Empty;

    /// <summary>Whether trades are being collected; an inactive symbol keeps its history and can still be queried.</summary>
    [DataMember(Name = "isActive", Order = 2)]
    public bool IsActive { get; init; }

    /// <summary>When the symbol was first added (UTC).</summary>
    [DataMember(Name = "createdAt", Order = 3)]
    public DateTime CreatedAt { get; init; }

    /// <summary>When the inactive symbol was deactivated (UTC); <see langword="null"/> while the symbol is active.</summary>
    [DataMember(Name = "deactivatedAt", Order = 4)]
    public DateTime? DeactivatedAt { get; init; }

    /// <summary><c>NotRequested</c>, <c>Pending</c>, <c>Running</c>, <c>Completed</c> or <c>Failed</c>.</summary>
    [DataMember(Name = "backfillStatus", Order = 5)]
    public string BackfillStatus { get; init; } = string.Empty;

    /// <summary>The earliest backfilled minute (UTC), once a backfill has completed; otherwise <see langword="null"/>.</summary>
    [DataMember(Name = "backfilledFrom", Order = 6)]
    public DateTime? BackfilledFrom { get; init; }
}

/// <summary><c>GET /api/admin/symbols</c>: a JSON array, or <c>&lt;adminSymbols&gt;</c> of <c>&lt;adminSymbol&gt;</c> in XML.</summary>
[CollectionDataContract(Name = "adminSymbols", ItemName = "adminSymbol", Namespace = Schema.Namespace)]
public sealed class AdminSymbolListResponse : List<AdminSymbolResponse>
{
    public AdminSymbolListResponse()
    {
    }

    public AdminSymbolListResponse(IEnumerable<AdminSymbolResponse> items)
        : base(items)
    {
    }
}

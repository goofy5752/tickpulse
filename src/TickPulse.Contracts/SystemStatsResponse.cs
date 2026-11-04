using System.Runtime.Serialization;

namespace TickPulse.Contracts;

/// <summary>
/// <c>GET /api/system/stats</c>, also pushed over the market hub as <c>stats</c> every 2 s: the ingestion status of the
/// instance that answers. Timestamps are UTC.
/// </summary>
[DataContract(Name = "systemStats", Namespace = Schema.Namespace)]
public sealed class SystemStatsResponse
{
    /// <summary><c>Standby</c>, <c>Connecting</c>, <c>Connected</c> or <c>Disconnected</c>.</summary>
    [DataMember(Name = "wsStatus", Order = 1)]
    public string WsStatus { get; init; } = string.Empty;

    /// <summary>Whether this instance holds the leader lock and therefore ingests.</summary>
    [DataMember(Name = "isLeader", Order = 2)]
    public bool IsLeader { get; init; }

    /// <summary>Stored ticks per second, a moving average over the last 10 s or so.</summary>
    [DataMember(Name = "ticksPerSecond", Order = 3)]
    public double TicksPerSecond { get; init; }

    /// <summary>Time from trade to storage of the newest tick in the last stored batch, in milliseconds.</summary>
    [DataMember(Name = "ingestLagMs", Order = 4)]
    public double IngestLagMs { get; init; }

    /// <summary>Approximate number of stored ticks (from table statistics, not an exact count).</summary>
    [DataMember(Name = "storedTicksApprox", Order = 5)]
    public long StoredTicksApprox { get; init; }

    /// <summary>Whole seconds since this instance started.</summary>
    [DataMember(Name = "uptimeSeconds", Order = 6)]
    public long UptimeSeconds { get; init; }

    /// <summary>When the current Binance connection opened; <see langword="null"/> while not connected.</summary>
    [DataMember(Name = "connectedSince", Order = 7)]
    public DateTime? ConnectedSince { get; init; }

    /// <summary>When the last reconnect after a lost connection succeeded; <see langword="null"/> if there was none.</summary>
    [DataMember(Name = "lastReconnect", Order = 8)]
    public DateTime? LastReconnect { get; init; }

    /// <summary>When the current connection is due to be replaced; <see langword="null"/> while not connected.</summary>
    [DataMember(Name = "nextRotation", Order = 9)]
    public DateTime? NextRotation { get; init; }

    /// <summary>Number of symbols being collected.</summary>
    [DataMember(Name = "activeSymbols", Order = 10)]
    public int ActiveSymbols { get; init; }

    /// <summary>The build's informational version.</summary>
    [DataMember(Name = "version", Order = 11)]
    public string Version { get; init; } = string.Empty;
}

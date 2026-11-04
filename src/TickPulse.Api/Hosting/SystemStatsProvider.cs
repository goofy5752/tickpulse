using System.Reflection;
using TickPulse.Application.Abstractions;
using TickPulse.Application.Ingestion;
using TickPulse.Contracts;

namespace TickPulse.Api.Hosting;

/// <summary>
/// Builds <see cref="SystemStatsResponse"/> from the live <see cref="IngestionState"/> and two cheap database reads
/// (approximate tick count, number of active symbols). Shared by <c>GET /api/system/stats</c> and the hub's
/// <c>stats</c> push.
/// </summary>
/// <remarks>A singleton created at host start-up; the uptime counts from its creation, on the app's clock.</remarks>
public sealed class SystemStatsProvider(
    IngestionState state,
    IMarketDataQueries queries,
    ISymbolRepository symbols,
    TimeProvider clock)
{
    /// <summary>The API assembly's informational version, e.g. <c>1.0.0+3f2a…</c>.</summary>
    public static readonly string Version =
        typeof(SystemStatsProvider).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(SystemStatsProvider).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    private readonly long _started = clock.GetTimestamp();

    /// <exception cref="Exception">A database read failed.</exception>
    public async Task<SystemStatsResponse> GetAsync(CancellationToken ct)
    {
        var storedTicks = await queries.ApproximateTickCountAsync(ct).ConfigureAwait(false);
        var activeSymbols = await symbols.CountActiveAsync(ct).ConfigureAwait(false);
        return new SystemStatsResponse
        {
            WsStatus = state.Status.ToString(),
            IsLeader = state.IsLeader,
            TicksPerSecond = state.TicksPerSecond,
            IngestLagMs = state.LastLagMs,
            StoredTicksApprox = storedTicks,
            UptimeSeconds = (long)clock.GetElapsedTime(_started).TotalSeconds,
            ConnectedSince = state.ConnectedSinceUtc,
            LastReconnect = state.LastReconnectUtc,
            NextRotation = state.NextRotationUtc,
            ActiveSymbols = activeSymbols,
            Version = Version,
        };
    }
}

using System.ComponentModel.DataAnnotations;
using System.Reflection;
using Microsoft.Extensions.Options;
using TickPulse.Application.Ingestion;
using TickPulse.Contracts;

namespace TickPulse.Api.Hosting;

/// <summary>Settings of <see cref="SystemStatsProvider"/> (section <c>SystemStats</c>).</summary>
public sealed class SystemStatsOptions
{
    public const string SectionName = "SystemStats";

    /// <summary>How long the database counts are reused before they are read again.</summary>
    [Range(typeof(TimeSpan), "00:00:00", "01:00:00")]
    public TimeSpan CacheTtl { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>How long one read of the database counts may take before the last-known values are used.</summary>
    [Range(typeof(TimeSpan), "00:00:00.001", "00:10:00")]
    public TimeSpan ReadTimeout { get; set; } = TimeSpan.FromSeconds(1);
}

/// <summary>
/// Builds <see cref="SystemStatsResponse"/> from the live <see cref="IngestionState"/> and the
/// <see cref="StoredCounts"/>. Shared by <c>GET /api/system/stats</c> and the hub's <c>stats</c> push.
/// </summary>
/// <remarks>
/// <para>The counts are cached for <see cref="SystemStatsOptions.CacheTtl"/> and read by one caller at a time; others
/// get the cached values meanwhile. A read that fails or exceeds <see cref="SystemStatsOptions.ReadTimeout"/> is logged
/// as a warning (at most every 30 s) and the last-known counts are used (zero before the first success), so the
/// statistics are always available: the ingestion fields are in memory and always current.</para>
/// <para>A singleton created at host start-up; the uptime counts from its creation, on the app's clock.</para>
/// </remarks>
public sealed partial class SystemStatsProvider : IDisposable
{
    /// <summary>The API's informational version with the commit shortened to 7 characters, e.g. <c>1.0.0+3f2a1b9</c>.</summary>
    public static readonly string Version = ShortVersion(
        typeof(SystemStatsProvider).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(SystemStatsProvider).Assembly.GetName().Version?.ToString()
        ?? "unknown");

    private const int ShortCommitLength = 7;

    private static readonly TimeSpan WarningInterval = TimeSpan.FromSeconds(30);

    private static readonly StoredCounts Zero = new(0, 0);

    private readonly IngestionState _state;
    private readonly IStoredCounts _counts;
    private readonly TimeProvider _clock;
    private readonly ILogger<SystemStatsProvider> _logger;
    private readonly SystemStatsOptions _options;
    private readonly long _started;
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private readonly LogThrottle _warnings;

    // The last-known counts and when they were last attempted (success or failure); null before the first attempt.
    private volatile CountsSnapshot? _snapshot;

    public SystemStatsProvider(
        IngestionState state,
        IStoredCounts counts,
        IOptions<SystemStatsOptions> options,
        TimeProvider clock,
        ILogger<SystemStatsProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        _state = state;
        _counts = counts;
        _clock = clock;
        _logger = logger;
        _options = options.Value;
        _started = clock.GetTimestamp();
        _warnings = new LogThrottle(clock, WarningInterval);
    }

    /// <summary><c>1.0.0+3f2a1b9c…</c> becomes <c>1.0.0+3f2a1b9</c>; a version without build metadata is unchanged.</summary>
    public static string ShortVersion(string informationalVersion)
    {
        ArgumentNullException.ThrowIfNull(informationalVersion);
        var plus = informationalVersion.IndexOf('+', StringComparison.Ordinal);
        return plus >= 0 && informationalVersion.Length - plus - 1 > ShortCommitLength
            ? informationalVersion[..(plus + 1 + ShortCommitLength)]
            : informationalVersion;
    }

    public void Dispose() => _refresh.Dispose();

    /// <summary>The current statistics. Throws only when <paramref name="ct"/> is cancelled.</summary>
    public async Task<SystemStatsResponse> GetAsync(CancellationToken ct)
    {
        var counts = await CountsAsync(ct).ConfigureAwait(false);
        return new SystemStatsResponse
        {
            WsStatus = _state.Status.ToString(),
            IsLeader = _state.IsLeader,
            TicksPerSecond = _state.TicksPerSecond,
            IngestLagMs = _state.LastLagMs,
            StoredTicksApprox = counts.StoredTicksApprox,
            UptimeSeconds = (long)_clock.GetElapsedTime(_started).TotalSeconds,
            ConnectedSince = _state.ConnectedSinceUtc,
            LastReconnect = _state.LastReconnectUtc,
            NextRotation = _state.NextRotationUtc,
            ActiveSymbols = counts.ActiveSymbols,
            Version = Version,
        };
    }

    private async Task<StoredCounts> CountsAsync(CancellationToken ct)
    {
        var snapshot = _snapshot;
        if (IsFresh(snapshot))
        {
            return snapshot!.Counts;
        }

        // Someone else is reading: use what is known rather than queue up behind a slow database.
        if (!await _refresh.WaitAsync(0, ct).ConfigureAwait(false))
        {
            return snapshot?.Counts ?? Zero;
        }

        try
        {
            snapshot = _snapshot;
            if (IsFresh(snapshot))
            {
                return snapshot!.Counts;
            }

            var known = snapshot?.Counts ?? Zero;
            using var timeout = new CancellationTokenSource(_options.ReadTimeout, _clock);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            try
            {
                // WaitAsync too, so a read that ignores its token cannot hold the statistics either.
                known = await _counts.GetAsync(linked.Token).WaitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                if (_warnings.TryAcquire())
                {
                    LogCountsUnavailable(ex);
                }
                else
                {
                    LogCountsStillUnavailable(ex);
                }
            }

            _snapshot = new CountsSnapshot(known, _clock.GetTimestamp());
            return known;
        }
        finally
        {
            _refresh.Release();
        }
    }

    private bool IsFresh(CountsSnapshot? snapshot) =>
        snapshot is not null && _clock.GetElapsedTime(snapshot.AttemptedAt) < _options.CacheTtl;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stored counts could not be read; the statistics use the last-known values")]
    private partial void LogCountsUnavailable(Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Stored counts could not be read; the statistics use the last-known values")]
    private partial void LogCountsStillUnavailable(Exception exception);

    private sealed record CountsSnapshot(StoredCounts Counts, long AttemptedAt);
}

using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace TickPulse.Infrastructure.Binance;

/// <summary>
/// Process-wide gate in front of the Binance REST API. Binance meters request weight per IP in one-minute
/// windows (limit 6000 on <c>api.binance.com</c>), answers <c>429</c> when the limit is exceeded and <c>418</c>
/// (IP ban) when requests keep coming after a 429. One guard is shared by every <see cref="BinanceRestClient"/>
/// so that a pause seen by one caller holds back all of them.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>A response reporting <c>X-MBX-USED-WEIGHT-1M</c> above <see cref="UsedWeightLimit"/> pauses requests
/// until the start of the next minute, when the window resets.</item>
/// <item>A <c>429</c> pauses requests for its <c>Retry-After</c> (until the next minute if absent).</item>
/// <item>A <c>418</c> marks the IP as banned for its <c>Retry-After</c> (2 minutes if absent, the shortest
/// Binance ban). While banned, <see cref="WaitAsync"/> throws <see cref="BinanceBannedException"/> instead of
/// waiting, because every request during a ban extends it.</item>
/// </list>
/// <para>Durations are measured on Binance's clock when the response carries a <c>Date</c> header: the minute
/// boundary and an HTTP-date <c>Retry-After</c> are relative to that <c>Date</c>, not to the local clock, so clock
/// skew does not shift the pause. The resulting duration is then applied to the injected
/// <see cref="TimeProvider"/>, on which all deadlines and waits run.</para>
/// </remarks>
public sealed partial class BinanceWeightGuard
{
    /// <summary>Used weight above which requests pause until the next minute (80 % of 6000).</summary>
    public const int UsedWeightLimit = 4800;

    internal const string UsedWeightHeader = "X-MBX-USED-WEIGHT-1M";

    private static readonly TimeSpan DefaultBanDuration = TimeSpan.FromMinutes(2);

    private readonly Lock _gate = new();
    private readonly ILogger _logger;
    private DateTimeOffset _resumeAt = DateTimeOffset.MinValue;
    private DateTimeOffset _bannedUntil = DateTimeOffset.MinValue;

    public BinanceWeightGuard(TimeProvider timeProvider, ILogger<BinanceWeightGuard>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        TimeProvider = timeProvider;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    public TimeProvider TimeProvider { get; }

    /// <summary>Completes when a request may be sent. Completes synchronously when nothing is paused.</summary>
    /// <exception cref="BinanceBannedException">The IP is currently banned.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled while waiting.</exception>
    public async ValueTask WaitAsync(CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            DateTimeOffset resumeAt, bannedUntil;
            lock (_gate)
            {
                resumeAt = _resumeAt;
                bannedUntil = _bannedUntil;
            }

            var now = TimeProvider.GetUtcNow();
            if (now < bannedUntil)
            {
                throw new BinanceBannedException(bannedUntil.UtcDateTime);
            }

            var wait = resumeAt - now;
            if (wait <= TimeSpan.Zero)
            {
                return;
            }

            // Loop: another response may have moved the deadline while this caller waited.
            await Task.Delay(wait, TimeProvider, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reads <c>X-MBX-USED-WEIGHT-1M</c>; above the limit, pauses until the next minute. Safe to call more than
    /// once for the same response (per attempt and per call): the deadline only moves forward and the pause is
    /// logged once, when it begins.
    /// </summary>
    internal void ObserveUsedWeight(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues(UsedWeightHeader, out var values)
            || !int.TryParse(values.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var used)
            || used <= UsedWeightLimit)
        {
            return;
        }

        var now = TimeProvider.GetUtcNow();
        var resumeAt = Extend(ref _resumeAt, now + UntilNextMinute(response), out var previous);
        if (previous <= now)
        {
            LogWeightPause(used, UsedWeightLimit, resumeAt);
        }
    }

    /// <summary>Records a <c>429</c>: pauses for its <c>Retry-After</c>, or until the next minute.</summary>
    /// <returns>The instant requests resume.</returns>
    internal DateTimeOffset RateLimited(HttpResponseMessage response) =>
        Extend(ref _resumeAt, TimeProvider.GetUtcNow() + (RetryAfter(response) ?? UntilNextMinute(response)), out _);

    /// <summary>Records a <c>418</c>: banned for its <c>Retry-After</c>, or 2 minutes.</summary>
    /// <returns>The instant the ban ends.</returns>
    internal DateTimeOffset Banned(HttpResponseMessage response) =>
        Extend(ref _bannedUntil, TimeProvider.GetUtcNow() + (RetryAfter(response) ?? DefaultBanDuration), out _);

    // Binance's "now": the response Date header (1 s resolution, so the wait errs long, never short), else ours.
    private DateTimeOffset ServerNow(HttpResponseMessage response) => response.Headers.Date ?? TimeProvider.GetUtcNow();

    private TimeSpan UntilNextMinute(HttpResponseMessage response)
    {
        var now = ServerNow(response);
        var minuteStart = now.UtcTicks - (now.UtcTicks % TimeSpan.TicksPerMinute);
        return TimeSpan.FromTicks(minuteStart + TimeSpan.TicksPerMinute - now.UtcTicks);
    }

    // Retry-After as delta-seconds (what Binance sends) or as an HTTP-date relative to the response Date.
    private TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var delay = response.Headers.RetryAfter switch
        {
            { Delta: { } delta } => delta,
            { Date: { } date } => date - ServerNow(response),
            _ => (TimeSpan?)null,
        };
        return delay > TimeSpan.Zero ? delay : null;
    }

    // Deadlines only move forward: a shorter Retry-After never cuts an existing pause short.
    private DateTimeOffset Extend(ref DateTimeOffset deadline, DateTimeOffset proposed, out DateTimeOffset previous)
    {
        lock (_gate)
        {
            previous = deadline;
            if (proposed > deadline)
            {
                deadline = proposed;
            }

            return deadline;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Binance used weight {UsedWeight} is above {Limit}; pausing REST calls until {ResumeAt:O}")]
    private partial void LogWeightPause(int usedWeight, int limit, DateTimeOffset resumeAt);
}

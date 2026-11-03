using System.Globalization;

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
/// until the start of the next UTC minute, when the window resets.</item>
/// <item>A <c>429</c> pauses requests for its <c>Retry-After</c> (until the next minute if absent).</item>
/// <item>A <c>418</c> marks the IP as banned for its <c>Retry-After</c> (2 minutes if absent, the shortest
/// Binance ban). While banned, <see cref="WaitAsync"/> throws <see cref="BinanceBannedException"/> instead of
/// waiting, because every request during a ban extends it.</item>
/// </list>
/// All deadlines are absolute instants on the injected <see cref="TimeProvider"/>.
/// </remarks>
public sealed class BinanceWeightGuard
{
    /// <summary>Used weight above which requests pause until the next minute (80 % of 6000).</summary>
    public const int UsedWeightLimit = 4800;

    internal const string UsedWeightHeader = "X-MBX-USED-WEIGHT-1M";

    private static readonly TimeSpan DefaultBanDuration = TimeSpan.FromMinutes(2);

    private readonly Lock _gate = new();
    private DateTimeOffset _resumeAt = DateTimeOffset.MinValue;
    private DateTimeOffset _bannedUntil = DateTimeOffset.MinValue;

    public BinanceWeightGuard(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        TimeProvider = timeProvider;
    }

    public TimeProvider TimeProvider { get; }

    /// <summary>Completes when a request may be sent. Completes synchronously when nothing is paused.</summary>
    /// <exception cref="BinanceBannedException">The IP is currently banned.</exception>
    public async ValueTask WaitAsync(CancellationToken ct)
    {
        while (true)
        {
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

    /// <summary>Reads <c>X-MBX-USED-WEIGHT-1M</c>; above the limit, pauses until the next minute.</summary>
    /// <returns>The resume instant when this response caused a pause; otherwise <see langword="null"/>.</returns>
    internal (int UsedWeight, DateTimeOffset ResumeAt)? ObserveUsedWeight(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues(UsedWeightHeader, out var values)
            || !int.TryParse(values.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var used)
            || used <= UsedWeightLimit)
        {
            return null;
        }

        var resumeAt = NextMinute(TimeProvider.GetUtcNow());
        Extend(ref _resumeAt, resumeAt);
        return (used, resumeAt);
    }

    /// <summary>Records a <c>429</c>: pauses for <paramref name="retryAfter"/>, or until the next minute.</summary>
    internal DateTimeOffset RateLimited(TimeSpan? retryAfter)
    {
        var now = TimeProvider.GetUtcNow();
        var resumeAt = retryAfter is { } delay && delay > TimeSpan.Zero ? now + delay : NextMinute(now);
        return Extend(ref _resumeAt, resumeAt);
    }

    /// <summary>Records a <c>418</c>: banned for <paramref name="retryAfter"/>, or 2 minutes.</summary>
    internal DateTimeOffset Banned(TimeSpan? retryAfter)
    {
        var duration = retryAfter is { } delay && delay > TimeSpan.Zero ? delay : DefaultBanDuration;
        return Extend(ref _bannedUntil, TimeProvider.GetUtcNow() + duration);
    }

    private static DateTimeOffset NextMinute(DateTimeOffset now) =>
        new(now.UtcTicks - (now.UtcTicks % TimeSpan.TicksPerMinute) + TimeSpan.TicksPerMinute, TimeSpan.Zero);

    // Deadlines only move forward: a shorter Retry-After never cuts an existing pause short.
    private DateTimeOffset Extend(ref DateTimeOffset deadline, DateTimeOffset proposed)
    {
        lock (_gate)
        {
            if (proposed > deadline)
            {
                deadline = proposed;
            }

            return deadline;
        }
    }
}

using System.Threading.RateLimiting;

namespace TickPulse.Api.Hosting;

/// <summary>
/// A fixed window of <c>permitLimit</c> requests per <c>window</c> for one client, without a queue. The window starts
/// with the client's first request. Unlike <see cref="FixedWindowRateLimiter"/>, whose rejected lease always reports
/// the full window, a rejected lease carries the time actually left in the window (<see cref="MetadataName.RetryAfter"/>)
/// and how many requests this window has rejected so far (<see cref="RejectionsInWindow"/>, 1 for the first).
/// </summary>
/// <remarks>
/// <para>Time comes from the injected <see cref="TimeProvider"/>, so tests can move it. A window that has ended is reset
/// by the next request; until then the limiter reports itself idle, which lets the partitioned limiter drop it.</para>
/// <para>The rate limiting middleware asks twice for a request it rejects: <c>AttemptAcquire</c> first, then
/// <c>AcquireAsync</c> (which could queue), and hands the second lease to <c>OnRejected</c>. So only a failed
/// <c>AcquireAsync</c> counts as a rejection, both in <see cref="RejectionsInWindow"/> and in
/// <see cref="RateLimiterStatistics.TotalFailedLeases"/>; a failed <c>AttemptAcquire</c> reports the count that
/// rejection would have and counts nothing.</para>
/// </remarks>
internal sealed class ClientWindowRateLimiter : RateLimiter
{
    /// <summary>The number of requests rejected in the current window, including the one this lease rejects.</summary>
    public static readonly MetadataName<int> RejectionsInWindow = MetadataName.Create<int>("TICKPULSE_REJECTIONS_IN_WINDOW");

    private static readonly RateLimitLease Acquired = new Lease(acquired: true, TimeSpan.Zero, 0);

    private readonly int _permitLimit;
    private readonly TimeSpan _window;
    private readonly TimeProvider _clock;
    private readonly long _createdAt;
    private readonly Lock _lock = new();

    private bool _started;
    private long _windowStart;
    private int _used;
    private int _rejected;
    private long _successful;
    private long _failed;

    public ClientWindowRateLimiter(int permitLimit, TimeSpan window, TimeProvider clock)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(permitLimit, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(clock);
        _permitLimit = permitLimit;
        _window = window;
        _clock = clock;
        _createdAt = clock.GetTimestamp();
    }

    /// <summary><see langword="null"/> while the current window has used permits; otherwise the time since it ended.</summary>
    public override TimeSpan? IdleDuration
    {
        get
        {
            lock (_lock)
            {
                if (!_started)
                {
                    return _clock.GetElapsedTime(_createdAt);
                }

                var elapsed = _clock.GetElapsedTime(_windowStart);
                if (elapsed >= _window)
                {
                    return elapsed - _window;
                }

                return _used > 0 ? null : elapsed;
            }
        }
    }

    public override RateLimiterStatistics? GetStatistics()
    {
        lock (_lock)
        {
            var current = _started && _clock.GetElapsedTime(_windowStart) < _window ? _used : 0;
            return new RateLimiterStatistics
            {
                CurrentAvailablePermits = _permitLimit - current,
                CurrentQueuedCount = 0,
                TotalSuccessfulLeases = _successful,
                TotalFailedLeases = _failed,
            };
        }
    }

    protected override RateLimitLease AttemptAcquireCore(int permitCount) => Acquire(permitCount, countRejection: false);

    // No queue: a request that cannot have its permits now is rejected now.
    protected override ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Acquire(permitCount, countRejection: true));

    private RateLimitLease Acquire(int permitCount, bool countRejection)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(permitCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(permitCount, _permitLimit);
        lock (_lock)
        {
            var now = _clock.GetTimestamp();
            if (!_started || _clock.GetElapsedTime(_windowStart, now) >= _window)
            {
                (_started, _windowStart, _used, _rejected) = (true, now, 0, 0);
            }

            if (_used + permitCount <= _permitLimit && (permitCount > 0 || _used < _permitLimit))
            {
                _used += permitCount;
                _successful++;
                return Acquired;
            }

            if (countRejection)
            {
                _rejected++;
                _failed++;
            }

            var retryAfter = _window - _clock.GetElapsedTime(_windowStart, now);
            return new Lease(acquired: false, retryAfter, countRejection ? _rejected : _rejected + 1);
        }
    }

    private sealed class Lease(bool acquired, TimeSpan retryAfter, int rejections) : RateLimitLease
    {
        private static readonly string[] RejectedMetadata = [MetadataName.RetryAfter.Name, RejectionsInWindow.Name];

        public override bool IsAcquired => acquired;

        public override IEnumerable<string> MetadataNames => acquired ? [] : RejectedMetadata;

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            if (!acquired && metadataName == MetadataName.RetryAfter.Name)
            {
                metadata = retryAfter;
                return true;
            }

            if (!acquired && metadataName == RejectionsInWindow.Name)
            {
                metadata = rejections;
                return true;
            }

            metadata = null;
            return false;
        }
    }
}

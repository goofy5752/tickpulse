namespace TickPulse.Api.Hosting;

/// <summary>
/// Lets a repeating condition be logged loudly at most once per <paramref name="interval"/> (and quietly otherwise),
/// so an outage does not flood the logs. Thread-safe.
/// </summary>
internal sealed class LogThrottle(TimeProvider clock, TimeSpan interval)
{
    private const long Never = long.MinValue;

    private long _last = Never;

    /// <summary>Whether this occurrence should be logged loudly; at most one caller per interval gets <see langword="true"/>.</summary>
    public bool TryAcquire()
    {
        var now = clock.GetTimestamp();
        var last = Interlocked.Read(ref _last);
        if (last != Never && clock.GetElapsedTime(last, now) < interval)
        {
            return false;
        }

        return Interlocked.CompareExchange(ref _last, now, last) == last;
    }
}

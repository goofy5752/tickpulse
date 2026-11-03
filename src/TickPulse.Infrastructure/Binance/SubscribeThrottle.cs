namespace TickPulse.Infrastructure.Binance;

/// <summary>
/// Sliding-window rate limit for outgoing WebSocket commands. Binance allows 5 incoming messages per second
/// per connection; the default of 4 leaves headroom (for example for the pongs the socket sends in reply to
/// server pings). Waiters are served in arrival order.
/// </summary>
/// <remarks>
/// The window is measured with the monotonic <see cref="TimeProvider.GetTimestamp"/>, so wall-clock corrections
/// cannot stall or open it. A frozen <c>FakeTimeProvider</c> therefore holds every command past the limit until
/// the test advances it.
/// </remarks>
public sealed class SubscribeThrottle : IDisposable
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(1);

    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _queue = new(1, 1);

    // Ring of the last N grant timestamps; _grants[_next] is the oldest once the ring is full.
    private readonly long[] _grants;
    private int _next;
    private int _count;

    /// <param name="perSecond">Commands allowed in any one-second window.</param>
    /// <param name="timeProvider">Clock; defaults to <see cref="TimeProvider.System"/>.</param>
    public SubscribeThrottle(int perSecond = 4, TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(perSecond, 1);
        _grants = new long[perSecond];
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Completes when one more command may be sent.</summary>
    public async ValueTask WaitAsync(CancellationToken cancellationToken)
    {
        await _queue.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (true)
            {
                var now = _timeProvider.GetTimestamp();
                if (_count < _grants.Length)
                {
                    Grant(now);
                    _count++;
                    return;
                }

                var elapsed = _timeProvider.GetElapsedTime(_grants[_next], now);
                if (elapsed >= Window)
                {
                    Grant(now);
                    return;
                }

                await Task.Delay(Window - elapsed, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _queue.Release();
        }
    }

    public void Dispose() => _queue.Dispose();

    private void Grant(long timestamp)
    {
        _grants[_next] = timestamp;
        _next = (_next + 1) % _grants.Length;
    }
}

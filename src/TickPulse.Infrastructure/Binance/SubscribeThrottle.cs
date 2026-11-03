namespace TickPulse.Infrastructure.Binance;

/// <summary>
/// Sliding-window rate limit for outgoing WebSocket commands. Binance allows 5 incoming messages per second
/// per connection; the default of 4 leaves headroom. Waiters are served in arrival order, and time comes from
/// the injected <see cref="TimeProvider"/>.
/// </summary>
public sealed class SubscribeThrottle : IDisposable
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(1);

    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _queue = new(1, 1);

    // Ring of the last N grant times; _grants[_next] is the oldest once the ring is full.
    private readonly DateTimeOffset[] _grants;
    private int _next;
    private int _count;

    /// <param name="perSecond">Commands allowed in any one-second window.</param>
    /// <param name="timeProvider">Clock; defaults to <see cref="TimeProvider.System"/>.</param>
    public SubscribeThrottle(int perSecond = 4, TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(perSecond, 1);
        _grants = new DateTimeOffset[perSecond];
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
                var now = _timeProvider.GetUtcNow();
                if (_count < _grants.Length)
                {
                    Grant(now);
                    _count++;
                    return;
                }

                var freeAt = _grants[_next] + Window;
                if (now >= freeAt)
                {
                    Grant(now);
                    return;
                }

                await Task.Delay(freeAt - now, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _queue.Release();
        }
    }

    public void Dispose() => _queue.Dispose();

    private void Grant(DateTimeOffset now)
    {
        _grants[_next] = now;
        _next = (_next + 1) % _grants.Length;
    }
}

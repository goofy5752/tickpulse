namespace TickPulse.Api.Hubs;

/// <summary>
/// The number of open <see cref="MarketHub"/> connections on this instance. With none, the broadcaster neither flushes
/// prices nor reads statistics.
/// </summary>
public sealed class MarketHubConnections
{
    private int _count;

    public int Count => Volatile.Read(ref _count);

    internal void Opened() => Interlocked.Increment(ref _count);

    internal void Closed() => Interlocked.Decrement(ref _count);
}

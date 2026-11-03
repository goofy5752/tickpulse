namespace TickPulse.Infrastructure.Ingestion;

/// <summary>
/// Exponential reconnect backoff with full jitter: attempt <c>n</c> waits a uniformly random time in
/// <c>[0, min(max, initial · 2ⁿ)]</c>. <see cref="Reset"/> starts over after a healthy connection.
/// Not thread-safe; one instance belongs to one reconnect loop.
/// </summary>
public sealed class ReconnectBackoff
{
    private readonly TimeSpan _initial;
    private readonly TimeSpan _max;
    private readonly Random _random;
    private TimeSpan _ceiling;

    /// <param name="initial">First ceiling; defaults to 1 s.</param>
    /// <param name="max">Largest ceiling; defaults to 30 s.</param>
    /// <param name="random">Jitter source; defaults to <see cref="Random.Shared"/>.</param>
    public ReconnectBackoff(TimeSpan? initial = null, TimeSpan? max = null, Random? random = null)
    {
        _initial = initial ?? TimeSpan.FromSeconds(1);
        _max = max ?? TimeSpan.FromSeconds(30);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_initial, TimeSpan.Zero, nameof(initial));
        ArgumentOutOfRangeException.ThrowIfLessThan(_max, _initial, nameof(max));
        _random = random ?? Random.Shared;
        _ceiling = _initial;
    }

    /// <summary>The delay before the next attempt; each call doubles the ceiling up to the maximum.</summary>
    public TimeSpan Next()
    {
        var ceiling = _ceiling;
        _ceiling = ceiling >= _max / 2 ? _max : ceiling * 2;

        // Jitter only spreads reconnects across replicas; it is not security-sensitive.
        var fraction = _random.NextDouble();
        return TimeSpan.FromTicks((long)(ceiling.Ticks * fraction));
    }

    /// <summary>Starts the doubling again from the initial delay.</summary>
    public void Reset() => _ceiling = _initial;
}

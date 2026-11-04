using System.Collections.Concurrent;

namespace TickPulse.Application.Ingestion;

/// <summary>State of the ingestion WebSocket, as seen by this process.</summary>
public enum WsState
{
    /// <summary>Another instance holds the leader lock (or this one has not taken it yet); nothing is ingested here.</summary>
    Standby,

    /// <summary>Leader, opening a connection.</summary>
    Connecting,

    /// <summary>Leader, streaming trades.</summary>
    Connected,

    /// <summary>Leader, the connection was lost; waiting to reconnect.</summary>
    Disconnected,
}

/// <summary>The latest stored trade price of a symbol.</summary>
public sealed record LivePrice(string Symbol, decimal Price, DateTime TradeTimeUtc);

/// <summary>
/// Live ingestion status and prices, written by the ingestion worker and read by the API (health, statistics,
/// live prices). A thread-safe singleton.
/// </summary>
public sealed class IngestionState(TimeProvider clock)
{
    /// <summary>Time constant of the <see cref="TicksPerSecond"/> moving average.</summary>
    public static readonly TimeSpan RateWindow = TimeSpan.FromSeconds(10);

    private readonly Lock _gate = new();
    private readonly ConcurrentDictionary<string, LivePrice> _latest = new(StringComparer.Ordinal);

    private WsState _status = WsState.Standby;
    private bool _isLeader;
    private DateTime? _connectedSinceUtc;
    private DateTime? _lastReconnectUtc;
    private DateTime? _nextRotationUtc;
    private double _lastLagMs;

    // Exponentially decaying event rate: every tick adds 1/τ, and the sum decays by e^(−Δt/τ).
    private double _rate;
    private long _rateTimestamp = clock.GetTimestamp();

    /// <summary>Raised after a batch is stored, once per symbol whose latest price moved forward.</summary>
    /// <remarks>Handlers run on the batch writer; they must be fast and must not block.</remarks>
    public event Action<LivePrice>? PriceUpdated;

    public WsState Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    /// <summary>Whether this process holds the leader lock and therefore ingests.</summary>
    /// <remarks>
    /// The single source of leadership for every other component (for example the backfill worker): only the ingestion
    /// worker owns the lock; nobody else may touch it. It turns <see langword="false"/> as soon as a session starts to
    /// end, before the remaining ticks are written; a lost lock is noticed within the leader check interval.
    /// </remarks>
    public bool IsLeader
    {
        get
        {
            lock (_gate)
            {
                return _isLeader;
            }
        }
    }

    /// <summary>When the current connection opened (UTC); <see langword="null"/> while not connected.</summary>
    public DateTime? ConnectedSinceUtc
    {
        get
        {
            lock (_gate)
            {
                return _connectedSinceUtc;
            }
        }
    }

    /// <summary>When the last reconnect after a lost connection succeeded (UTC).</summary>
    public DateTime? LastReconnectUtc
    {
        get
        {
            lock (_gate)
            {
                return _lastReconnectUtc;
            }
        }
    }

    /// <summary>When the current connection is due to be replaced (UTC); <see langword="null"/> while not connected.</summary>
    public DateTime? NextRotationUtc
    {
        get
        {
            lock (_gate)
            {
                return _nextRotationUtc;
            }
        }
    }

    /// <summary>Stored ticks per second, an exponential moving average over <see cref="RateWindow"/>.</summary>
    /// <remarks>Decays towards zero while nothing is stored.</remarks>
    public double TicksPerSecond
    {
        get
        {
            lock (_gate)
            {
                return _rate * Decay(clock.GetTimestamp());
            }
        }
    }

    /// <summary>Time from trade to storage of the newest tick in the last stored batch, in milliseconds.</summary>
    public double LastLagMs
    {
        get
        {
            lock (_gate)
            {
                return _lastLagMs;
            }
        }
    }

    /// <summary>The latest stored price per symbol (upper-case names). A live, thread-safe view.</summary>
    public IReadOnlyDictionary<string, LivePrice> Latest => _latest;

    /// <summary>Not the leader: nothing is ingested here.</summary>
    public void SetStandby()
    {
        lock (_gate)
        {
            _isLeader = false;
            _status = WsState.Standby;
            _connectedSinceUtc = null;
            _nextRotationUtc = null;
        }
    }

    /// <summary>Leader, opening a connection.</summary>
    public void SetConnecting() => SetLeaderStatus(WsState.Connecting);

    /// <summary>Leader, the connection was lost.</summary>
    public void SetDisconnected() => SetLeaderStatus(WsState.Disconnected);

    /// <summary>Leader, streaming on a connection opened at <paramref name="connectedSinceUtc"/>.</summary>
    public void SetConnected(DateTime connectedSinceUtc, DateTime nextRotationUtc)
    {
        lock (_gate)
        {
            _isLeader = true;
            _status = WsState.Connected;
            _connectedSinceUtc = connectedSinceUtc;
            _nextRotationUtc = nextRotationUtc;
        }
    }

    /// <summary>Drops the live price of a symbol that is no longer monitored.</summary>
    public void ForgetSymbol(string symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        _latest.TryRemove(symbol, out _);
    }

    /// <summary>A connection replaced one that was lost.</summary>
    public void MarkReconnected(DateTime utc)
    {
        lock (_gate)
        {
            _lastReconnectUtc = utc;
        }
    }

    /// <summary>
    /// Records a stored batch: <paramref name="stored"/> newly stored ticks, the lag of its newest tick, and the last
    /// price per symbol in it. A price older than the one already known (e.g. from a gap-fill) is ignored.
    /// </summary>
    /// <exception cref="AggregateException">One or more <see cref="PriceUpdated"/> handlers threw; all were still called.</exception>
    public void RecordBatch(int stored, double lagMs, IEnumerable<LivePrice> latest)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(stored);
        ArgumentNullException.ThrowIfNull(latest);

        lock (_gate)
        {
            var now = clock.GetTimestamp();
            _rate = (_rate * Decay(now)) + (stored / RateWindow.TotalSeconds);
            _rateTimestamp = now;
            _lastLagMs = lagMs;
        }

        List<LivePrice>? advanced = null;
        foreach (var price in latest)
        {
            if (TryAdvance(price))
            {
                (advanced ??= []).Add(price);
            }
        }

        if (advanced is not null)
        {
            Raise(advanced);
        }
    }

    // Caller holds _gate.
    private double Decay(long now) => Math.Exp(-clock.GetElapsedTime(_rateTimestamp, now).TotalSeconds / RateWindow.TotalSeconds);

    private void SetLeaderStatus(WsState status)
    {
        lock (_gate)
        {
            _isLeader = true;
            _status = status;
            _connectedSinceUtc = null;
            _nextRotationUtc = null;
        }
    }

    private bool TryAdvance(LivePrice price)
    {
        while (true)
        {
            if (!_latest.TryGetValue(price.Symbol, out var current))
            {
                if (_latest.TryAdd(price.Symbol, price))
                {
                    return true;
                }

                continue;
            }

            if (price.TradeTimeUtc < current.TradeTimeUtc || price == current)
            {
                return false;
            }

            if (_latest.TryUpdate(price.Symbol, price, current))
            {
                return true;
            }
        }
    }

    private void Raise(List<LivePrice> prices)
    {
        var handlers = PriceUpdated;
        if (handlers is null)
        {
            return;
        }

        List<Exception>? failures = null;
        foreach (var price in prices)
        {
            foreach (var handler in handlers.GetInvocationList().Cast<Action<LivePrice>>())
            {
                try
                {
                    handler(price);
                }
                catch (Exception ex)
                {
                    (failures ??= []).Add(ex);
                }
            }
        }

        if (failures is not null)
        {
            throw new AggregateException("A PriceUpdated handler failed.", failures);
        }
    }
}

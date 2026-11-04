using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using TickPulse.Api.Hosting;
using TickPulse.Application.Ingestion;
using TickPulse.Contracts;

namespace TickPulse.Api.Hubs;

/// <summary>
/// Pushes live prices and statistics to <see cref="MarketHub"/> clients, in two independent loops:
/// <list type="bullet">
/// <item>Prices come from <see cref="IngestionState.PriceUpdated"/> and are coalesced per symbol (the latest trade
/// wins). Every <see cref="PriceInterval"/> the pending ones go to their symbol groups, so a symbol is sent at most once
/// per interval. A price older than the last one sent for its symbol is dropped, so a client never sees a price go
/// back in time.</item>
/// <item>Every <see cref="StatsInterval"/> every client gets the <see cref="SystemStatsResponse"/>. A slow statistics
/// read therefore never delays prices, and <see cref="SystemStatsProvider"/> bounds the read itself.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>Neither loop does anything while <see cref="MarketHubConnections.Count"/> is zero: no flush and no database
/// read. Pending prices stay coalesced meanwhile, at most one per symbol.</para>
/// <para>Each send is awaited for at most <see cref="SendTimeout"/>, so one slow client cannot hold up a loop; a send
/// that is still running is left to finish, and its failures are logged (Warning at most every 30 s, Debug otherwise).</para>
/// <para>The handler subscribes in <see cref="StartAsync"/> and unsubscribes in <see cref="StopAsync"/>. It only stores
/// the price, so it never blocks the batch writer that raises the event.</para>
/// <para>Any other exception is a bug: it is logged as Critical and that loop starts over after
/// <see cref="RestartDelay"/>. <see cref="ExecuteAsync"/> never ends with an exception, which would stop the host.</para>
/// </remarks>
internal sealed partial class MarketBroadcaster : BackgroundService
{
    /// <summary>How often pending prices are flushed; the per-symbol throttle.</summary>
    public static readonly TimeSpan PriceInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>How often the statistics are pushed to every client.</summary>
    public static readonly TimeSpan StatsInterval = TimeSpan.FromSeconds(2);

    /// <summary>The longest a loop waits for one flush or push to be written.</summary>
    public static readonly TimeSpan SendTimeout = TimeSpan.FromMilliseconds(200);

    /// <summary>How long a loop waits before it starts over after an unexpected error.</summary>
    public static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan WarningInterval = TimeSpan.FromSeconds(30);

    private readonly IngestionState _state;
    private readonly IHubContext<MarketHub> _hub;
    private readonly SystemStatsProvider _stats;
    private readonly MarketHubConnections _connections;
    private readonly TimeProvider _clock;
    private readonly ILogger<MarketBroadcaster> _logger;
    private readonly LogThrottle _warnings;
    private readonly Action<LivePrice> _onPriceUpdated;

    // The latest unsent price per symbol.
    private readonly ConcurrentDictionary<string, LivePrice> _pending = new(StringComparer.Ordinal);

    // Trade time of the last price sent per symbol; only the price loop touches it.
    private readonly Dictionary<string, DateTime> _lastSent = new(StringComparer.Ordinal);

    public MarketBroadcaster(
        IngestionState state,
        IHubContext<MarketHub> hub,
        SystemStatsProvider stats,
        MarketHubConnections connections,
        TimeProvider clock,
        ILogger<MarketBroadcaster> logger)
    {
        _state = state;
        _hub = hub;
        _stats = stats;
        _connections = connections;
        _clock = clock;
        _logger = logger;
        _warnings = new LogThrottle(clock, WarningInterval);
        _onPriceUpdated = OnPriceUpdated;
    }

    /// <summary>Number of symbols with a price waiting for the next flush.</summary>
    internal int PendingCount => _pending.Count;

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _state.PriceUpdated += _onPriceUpdated;
        return base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _state.PriceUpdated -= _onPriceUpdated;
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(
            SuperviseAsync("prices", RunPricesAsync, stoppingToken),
            SuperviseAsync("stats", RunStatsAsync, stoppingToken));

    private static LivePriceMessage ToMessage(LivePrice price) => new()
    {
        Symbol = price.Symbol,
        Price = price.Price,
        TradeTime = price.TradeTimeUtc,
    };

    private void OnPriceUpdated(LivePrice price) =>
        _pending.AddOrUpdate(
            price.Symbol,
            static (_, update) => update,
            static (_, current, update) => update.TradeTimeUtc >= current.TradeTimeUtc ? update : current,
            price);

    private async Task SuperviseAsync(string loop, Func<CancellationToken, Task> run, CancellationToken stoppingToken)
    {
        await Task.Yield();
        try
        {
            while (true)
            {
                try
                {
                    await run(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    var restart = Task.Delay(RestartDelay, _clock, stoppingToken);
                    LogUnexpectedError(ex, loop, RestartDelay.TotalSeconds);
                    await restart.ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    private async Task RunPricesAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(PriceInterval, _clock);
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            if (_connections.Count > 0 && !_pending.IsEmpty)
            {
                await FlushPricesAsync(ct).ConfigureAwait(false);
            }
        }
    }

    private async Task RunStatsAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(StatsInterval, _clock);
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            if (_connections.Count > 0)
            {
                var current = await _stats.GetAsync(ct).ConfigureAwait(false);
                await BoundedAsync(_hub.Clients.All.SendAsync(MarketHub.StatsMethod, current, ct), MarketHub.StatsMethod, ct)
                    .ConfigureAwait(false);
            }
        }
    }

    private async Task FlushPricesAsync(CancellationToken ct)
    {
        List<Task>? sends = null;
        foreach (var symbol in _pending.Keys)
        {
            if (!_pending.TryRemove(symbol, out var price))
            {
                continue;
            }

            if (_lastSent.TryGetValue(symbol, out var lastSent) && price.TradeTimeUtc < lastSent)
            {
                continue;
            }

            _lastSent[symbol] = price.TradeTimeUtc;
            (sends ??= []).Add(_hub.Clients.Group(MarketHub.GroupName(symbol)).SendAsync(MarketHub.PriceMethod, ToMessage(price), ct));
        }

        if (sends is not null)
        {
            await BoundedAsync(Task.WhenAll(sends), MarketHub.PriceMethod, ct).ConfigureAwait(false);
        }
    }

    // Waits up to SendTimeout. A send still running is left to finish; failures are logged by the continuation, which
    // also observes them, so the loop never waits on, or throws for, a client.
    private async Task BoundedAsync(Task send, string method, CancellationToken ct)
    {
        _ = send.ContinueWith(
            static (task, state) =>
            {
                var (self, name) = ((MarketBroadcaster, string))state!;
                self.OnSendFailed(task.Exception!, name);
            },
            (this, method),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        try
        {
            await send.WaitAsync(SendTimeout, _clock, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            LogSendSlow(method, SendTimeout.TotalMilliseconds);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Logged by the continuation above.
        }
    }

    private void OnSendFailed(AggregateException exception, string method)
    {
        if (_warnings.TryAcquire())
        {
            LogSendFailed(exception, method);
        }
        else
        {
            LogSendFailedQuietly(exception, method);
        }
    }

    [LoggerMessage(Level = LogLevel.Critical, Message = "Market broadcaster {Loop} loop failed unexpectedly; restarting in {DelaySeconds} s")]
    private partial void LogUnexpectedError(Exception exception, string loop, double delaySeconds);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Hub {Method} send took longer than {TimeoutMs} ms; continuing without it")]
    private partial void LogSendSlow(string method, double timeoutMs);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Hub {Method} send failed")]
    private partial void LogSendFailed(Exception exception, string method);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Hub {Method} send failed")]
    private partial void LogSendFailedQuietly(Exception exception, string method);
}

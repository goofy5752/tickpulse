using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using TickPulse.Api.Hosting;
using TickPulse.Application.Ingestion;
using TickPulse.Contracts;

namespace TickPulse.Api.Hubs;

/// <summary>
/// Pushes live prices and statistics to <see cref="MarketHub"/> clients. Prices come from
/// <see cref="IngestionState.PriceUpdated"/> and are coalesced per symbol (the latest trade wins); every
/// <see cref="PriceInterval"/> the pending ones go to their symbol groups, so a symbol is sent at most once per
/// interval. Every <see cref="StatsInterval"/> all clients get the <see cref="SystemStatsResponse"/>.
/// </summary>
/// <remarks>
/// <para>The handler subscribes in <see cref="StartAsync"/> and unsubscribes in <see cref="StopAsync"/>. It only stores
/// the price, so it never blocks the batch writer that raises the event.</para>
/// <para>A failed statistics read (e.g. the database is down) skips that push and is logged as a warning. Any other
/// exception is a bug: it is logged as Critical and the loop starts over after <see cref="RestartDelay"/>; it never
/// ends <see cref="ExecuteAsync"/> with an exception, which would stop the host.</para>
/// </remarks>
internal sealed partial class MarketBroadcaster(
    IngestionState state,
    IHubContext<MarketHub> hub,
    SystemStatsProvider stats,
    TimeProvider clock,
    ILogger<MarketBroadcaster> logger) : BackgroundService
{
    /// <summary>How often pending prices are flushed; the per-symbol throttle.</summary>
    public static readonly TimeSpan PriceInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>How often the statistics are pushed to every client.</summary>
    public static readonly TimeSpan StatsInterval = TimeSpan.FromSeconds(2);

    /// <summary>How long the loop waits before it starts over after an unexpected error.</summary>
    public static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(5);

    private static readonly int FlushesPerStats = (int)(StatsInterval / PriceInterval);

    // The latest unsent price per symbol.
    private readonly ConcurrentDictionary<string, LivePrice> _pending = new(StringComparer.Ordinal);

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        state.PriceUpdated += OnPriceUpdated;
        return base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        state.PriceUpdated -= OnPriceUpdated;
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (true)
            {
                try
                {
                    await RunAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    var restart = Task.Delay(RestartDelay, clock, stoppingToken);
                    LogUnexpectedError(ex, RestartDelay.TotalSeconds);
                    await restart.ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

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

    private async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(PriceInterval, clock);
        var flushes = 0;
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            await FlushPricesAsync(ct).ConfigureAwait(false);
            if (++flushes >= FlushesPerStats)
            {
                flushes = 0;
                await PushStatsAsync(ct).ConfigureAwait(false);
            }
        }
    }

    private async Task FlushPricesAsync(CancellationToken ct)
    {
        if (_pending.IsEmpty)
        {
            return;
        }

        List<Task> sends = [];
        foreach (var symbol in _pending.Keys)
        {
            if (_pending.TryRemove(symbol, out var price))
            {
                sends.Add(hub.Clients.Group(MarketHub.GroupName(price.Symbol)).SendAsync(MarketHub.PriceMethod, ToMessage(price), ct));
            }
        }

        await Task.WhenAll(sends).ConfigureAwait(false);
    }

    private async Task PushStatsAsync(CancellationToken ct)
    {
        SystemStatsResponse current;
        try
        {
            current = await stats.GetAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            LogStatsUnavailable(ex);
            return;
        }

        await hub.Clients.All.SendAsync(MarketHub.StatsMethod, current, ct).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Critical, Message = "Market broadcaster failed unexpectedly; restarting in {DelaySeconds} s")]
    private partial void LogUnexpectedError(Exception exception, double delaySeconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "System statistics could not be read; skipping this push")]
    private partial void LogStatsUnavailable(Exception exception);
}

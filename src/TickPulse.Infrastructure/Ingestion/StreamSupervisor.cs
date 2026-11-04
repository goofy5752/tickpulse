using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using TickPulse.Application.Abstractions;
using TickPulse.Application.Ingestion;
using TickPulse.Domain;
using TickPulse.Infrastructure.Telemetry;

namespace TickPulse.Infrastructure.Ingestion;

/// <summary>
/// Keeps one subscribed WebSocket connection streaming into the tick channel for the length of one leadership
/// session: connect, subscribe, reconcile on signal or timer, rotate make-before-break, reconnect with backoff and
/// gap-fill after a reconnect.
/// </summary>
/// <remarks>
/// <para><b>Concurrency.</b> One control loop (<see cref="RunAsync"/>) sends every SUBSCRIBE/UNSUBSCRIBE, so commands
/// never race each other and a connection is disposed only after the loop has moved off it (no command can be inside
/// its send lock then). Each connection has a pump task that only moves trades from its socket into the tick channel
/// and never sends anything. Gap-fills and retirements run in the background; the supervisor tracks every background
/// task so <see cref="StopAsync"/> can wait for them.</para>
/// <para><b>Gap-fill.</b> A connection that ends unexpectedly opens a gap for every known symbol
/// (<see cref="GapFillWatermarks"/>), and so does the start of a session for every symbol with stored trades (the
/// worker seeds the watermarks from the database). Each symbol is gap-filled on the next connection as soon as its
/// SUBSCRIBE is acknowledged there, which may be a later reconcile pass than the first one, and the gap closes only
/// when the fill completed on a connection that is still up (a fill bounded by a connection that broke meanwhile
/// leaves it open for the next one).</para>
/// <para><b>Lead-in.</b> A symbol that starts streaming in this session (first subscription, an added or reactivated
/// symbol) with no watermark at all is filled from the start of the minute its SUBSCRIBE was acknowledged in, up to its
/// first streamed trade. Without it that minute's candle would only hold the trades after the subscription, and the
/// backfill (which stops before the first streamed minute) never completes it. A lead-in whose lookup failed stays due
/// and is tried again on the next connection.</para>
/// <para><b>Shutdown</b> is split so the worker can order it: cancel the token passed to <see cref="RunAsync"/> (no
/// more commands, pumps and gap-fills stop writing), <see cref="StopAsync"/> (wait for them), then the worker
/// completes and flushes the channel, then <see cref="DisposeAsync"/> closes the connections.</para>
/// </remarks>
internal sealed partial class StreamSupervisor(
    IStreamConnectionFactory connections,
    SymbolReconciler reconciler,
    GapFiller gapFiller,
    SymbolIdCache symbols,
    ISymbolChangeSignal signal,
    IngestionState state,
    IngestionOptions options,
    TimeProvider clock,
    ILogger logger,
    ChannelWriter<Tick> output,
    GapFillWatermarks watermarks,
    TickPulseMetrics metrics) : IAsyncDisposable
{
    /// <summary>A connect that takes longer is abandoned and retried.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    /// <summary>A connection that lived this long resets the reconnect backoff.</summary>
    public static readonly TimeSpan HealthyAfter = TimeSpan.FromSeconds(60);

    /// <summary>Next reconcile after a pass that left some state unknown.</summary>
    public static readonly TimeSpan ReconcileRetryDelay = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Next rotation attempt after one that could not open or fully subscribe its new connection (or the rotation
    /// interval, if that is shorter).
    /// </summary>
    public static readonly TimeSpan RotationRetryDelay = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The lead-in starts at the minute of <c>ack time − LeadInClockSkew</c>, so a local clock slightly ahead of the
    /// exchange's trade times cannot start it after the first streamed trade's minute.
    /// </summary>
    public static readonly TimeSpan LeadInClockSkew = TimeSpan.FromSeconds(5);

    // Log a trade for a symbol without an id once, then once per this many.
    private const int UnknownSymbolLogEvery = 10_000;

    private readonly Lock _gate = new();
    private readonly HashSet<StreamLink> _links = [];
    private readonly List<Task> _background = [];
    private readonly List<Task> _retiring = [];

    // Symbols that started streaming in this session (control loop only): only their first ack gets a lead-in.
    private readonly HashSet<string> _started = new(StringComparer.OrdinalIgnoreCase);

    // Lead-ins not done yet, with the minute each starts at: set by the control loop, cleared by a gap-fill once the
    // lookup succeeded, so a failed one is tried again on the next connection.
    private readonly ConcurrentDictionary<string, DateTime> _leadInDue = new(StringComparer.OrdinalIgnoreCase);
    private Task _signalWait = Task.CompletedTask;

    private DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    /// <summary>Streams until <paramref name="ct"/> is cancelled. Never returns otherwise.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        var backoff = new ReconnectBackoff();
        var reconnecting = false;
        _signalWait = signal.WaitAsync(ct).AsTask();
        try
        {
            while (true)
            {
                state.SetConnecting();
                var opened = await OpenLinkAsync(reconnecting ? WsConnectionReason.Reconnect : WsConnectionReason.Initial, ct).ConfigureAwait(false);
                if (opened is null)
                {
                    state.SetDisconnected();
                    await Task.Delay(backoff.Next(), clock, ct).ConfigureAwait(false);
                    continue;
                }

                var (link, outcome) = opened.Value;
                if (reconnecting)
                {
                    state.MarkReconnected(UtcNow);
                    LogReconnected();
                }

                reconnecting = true;
                var upSince = UtcNow;
                StartGapFills(link, pendingOnly: true, ct);

                var ended = await ServeAsync(link, outcome, ct).ConfigureAwait(false);
                state.SetDisconnected();
                var upFor = UtcNow - upSince;
                if (upFor >= HealthyAfter)
                {
                    backoff.Reset();
                }

                LogConnectionEnded(upFor.TotalSeconds, ended.Connection.Completion.IsFaulted);
                Retire(ended, LinkEnd.Broken);
                await Task.Delay(backoff.Next(), clock, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Leadership lost or shutting down.
        }
    }

    /// <summary>Waits until every pump, gap-fill and retirement has finished. Call after cancelling <see cref="RunAsync"/>.</summary>
    public async Task StopAsync()
    {
        while (true)
        {
            Task[] pending;
            lock (_gate)
            {
                pending = [.. _background.Where(t => !t.IsCompleted)];
            }

            if (pending.Length == 0)
            {
                break;
            }

            await Task.WhenAll(pending).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        // The pumps are done. Keep the open links' last ids, and since this session ends with the stream still
        // running on the exchange, open a gap for the next session to fill.
        watermarks.Merge(OpenLinks().SelectMany(l => l.LastIds), gap: true);
    }

    /// <summary>Closes the connections that are still open.</summary>
    public async ValueTask DisposeAsync()
    {
        foreach (var link in OpenLinks())
        {
            await link.DisposeAsync().ConfigureAwait(false);
            Observe(link);
        }

        lock (_gate)
        {
            _links.Clear();
        }
    }

    private static void Observe(StreamLink link)
    {
        // Mark a fault as observed; the connection has logged it already.
        _ = link.Connection.Completion.Exception;
    }

    private StreamLink[] OpenLinks()
    {
        lock (_gate)
        {
            return [.. _links];
        }
    }

    // Serves the link until the current connection ends (rotations replace it on the way). Returns the one that ended.
    private async Task<StreamLink> ServeAsync(StreamLink current, ReconcileOutcome initial, CancellationToken ct)
    {
        var interval = options.RotationInterval;
        var rotationRetry = interval < RotationRetryDelay ? interval : RotationRetryDelay;
        state.SetConnected(current.ConnectedAtUtc, current.ConnectedAtUtc + interval);
        LogConnected(current.Subscribed.Count, current.ConnectedAtUtc + interval);
        var nextRotation = current.ConnectedAtUtc + interval;
        var nextReconcile = NextReconcile(initial);

        while (true)
        {
            var due = (nextRotation < nextReconcile ? nextRotation : nextReconcile) - UtcNow;
            using (var wake = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                var timer = Task.Delay(due > TimeSpan.Zero ? due : TimeSpan.Zero, clock, wake.Token);
                await Task.WhenAny(current.Connection.Completion, current.Pump, _signalWait, timer).ConfigureAwait(false);
                await wake.CancelAsync().ConfigureAwait(false);
            }

            ct.ThrowIfCancellationRequested();

            // A pump only ends with its connection, unless it failed; either way this link is done.
            if (current.Connection.Completion.IsCompleted || current.Pump.IsCompleted)
            {
                return current;
            }

            var signalled = _signalWait.IsCompleted;
            if (signalled)
            {
                _signalWait = signal.WaitAsync(ct).AsTask();
            }

            if (UtcNow >= nextRotation)
            {
                // The new connection's subscribe reads the active symbols afresh, which also covers a signal.
                var rotated = await RotateAsync(current, ct).ConfigureAwait(false);
                if (rotated is { } r)
                {
                    current = r.Link;
                    nextRotation = r.Link.ConnectedAtUtc + interval;
                    nextReconcile = NextReconcile(r.Outcome);
                    continue;
                }

                nextRotation = UtcNow + rotationRetry;
                state.SetConnected(current.ConnectedAtUtc, nextRotation);
                if (!signalled)
                {
                    continue;
                }

                // The rotation consumed a signal but changed nothing: the current connection still has to follow it.
            }

            if (signalled || UtcNow >= nextReconcile)
            {
                var outcome = await reconciler.ReconcileAsync(current, ct).ConfigureAwait(false);
                Forget(current, outcome.Unsubscribed);
                StartGapFills(current, pendingOnly: true, ct);
                nextReconcile = NextReconcile(outcome);
            }
        }
    }

    private DateTime NextReconcile(ReconcileOutcome outcome) =>
        UtcNow + (outcome.RetrySoon ? ReconcileRetryDelay : options.ReconcileInterval);

    // Make-before-break: the new connection is open, subscribed and pumping before the old one is closed.
    private async Task<(StreamLink Link, ReconcileOutcome Outcome)?> RotateAsync(StreamLink old, CancellationToken ct)
    {
        LogRotating(old.ConnectedAtUtc);
        var opened = await OpenLinkAsync(WsConnectionReason.Rotation, ct).ConfigureAwait(false);
        if (opened is null)
        {
            LogRotationFailed(0);
            return null;
        }

        var (next, _) = opened.Value;

        // Every still-active symbol the old connection streams must be confirmed on the new one, or closing the old
        // one would lose trades (a timed-out or rejected SUBSCRIBE).
        var missing = old.Subscribed.Count(s => symbols.IsActive(s) && !next.Subscribed.Contains(s));
        if (missing > 0)
        {
            LogRotationFailed(missing);

            // Its last ids must not be merged: if the old connection broke before this one subscribed, they would
            // move the gap-fill start past the trades missed in between. The old connection still covers them.
            Retire(next, LinkEnd.Discarded);
            return null;
        }

        // The old connection is idle from here on: only this loop sends commands, and it has moved to the new one.
        var oldEndedEarly = old.Connection.Completion.IsCompleted;
        state.SetConnected(next.ConnectedAtUtc, next.ConnectedAtUtc + options.RotationInterval);
        Retire(old, oldEndedEarly ? LinkEnd.Broken : LinkEnd.Replaced);
        LogRotated(next.Subscribed.Count);

        // The overlap covers a clean rotation; only gaps that are still open get filled. If the old connection broke
        // before the new one was subscribed, Retire opened a gap for every symbol.
        StartGapFills(next, pendingOnly: true, ct);
        return opened;
    }

    // Null when the connection could not be opened; the subscribe may still be incomplete (see the outcome).
    private async Task<(StreamLink Link, ReconcileOutcome Outcome)?> OpenLinkAsync(WsConnectionReason reason, CancellationToken ct)
    {
        var connection = connections.Create();
        var connected = false;
        try
        {
            using var timeout = new CancellationTokenSource(ConnectTimeout, clock);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            await connection.ConnectAsync(linked.Token).ConfigureAwait(false);
            connected = true;
            metrics.WsConnected(reason);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            LogConnectFailed(ex);
        }
        finally
        {
            if (!connected)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }

        if (!connected)
        {
            return null;
        }

        var link = new StreamLink(connection);
        lock (_gate)
        {
            _links.Add(link);
        }

        // Pump before subscribing, so that acks never wait behind a full trade buffer.
        link.Pump = PumpAsync(link, ct);
        Track(link.Pump);
        var outcome = await reconciler.ReconcileAsync(link, ct).ConfigureAwait(false);
        return (link, outcome);
    }

    private async Task PumpAsync(StreamLink link, CancellationToken ct)
    {
        await Task.Yield();
        var trades = link.Connection.Trades;
        Dictionary<string, long>? unknown = null;

        // Received trades are counted per run of one symbol within a burst, not per trade.
        var runSymbol = 0;
        var run = 0L;
        try
        {
            while (await trades.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (trades.TryRead(out var trade))
                {
                    if (!symbols.TryGetId(trade.Symbol, out var symbolId))
                    {
                        unknown ??= new(StringComparer.OrdinalIgnoreCase);
                        var count = unknown[trade.Symbol] = unknown.GetValueOrDefault(trade.Symbol) + 1;
                        if (count % UnknownSymbolLogEvery == 1)
                        {
                            LogUnknownSymbol(trade.Symbol, count);
                        }

                        continue;
                    }

                    if (symbolId != runSymbol)
                    {
                        CountReceived(runSymbol, ref run);
                        runSymbol = symbolId;
                    }

                    var tick = new Tick(symbolId, trade.AggTradeId, trade.Price, trade.Quantity, trade.TradeTimeUtc, UtcNow);
                    if (!output.TryWrite(tick))
                    {
                        // The channel is full: wait (back-pressure), never drop.
                        CountReceived(runSymbol, ref run);
                        await output.WriteAsync(tick, ct).ConfigureAwait(false);
                    }

                    run++;
                    link.Record(trade.Symbol, trade.AggTradeId);
                }

                CountReceived(runSymbol, ref run);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Stop accepting: the session is ending.
        }
        catch (Exception ex)
        {
            // A bug, not a network failure. The control loop sees the pump end and replaces the link.
            LogPumpFailed(ex);
        }
        finally
        {
            CountReceived(runSymbol, ref run);
        }
    }

    private void CountReceived(int symbolId, ref long count)
    {
        if (count > 0 && symbols.TryGetName(symbolId, out var name))
        {
            metrics.TicksReceived(name, count);
        }

        count = 0;
    }

    // Considers every subscribed symbol not yet considered on this link (control loop only).
    private void StartGapFills(StreamLink link, bool pendingOnly, CancellationToken ct)
    {
        // Taken now, right after the acks: the fills below may run long after.
        var leadInFrom = Period.OneMinute.AlignDown(UtcNow - LeadInClockSkew);
        var toFill = new List<string>();
        foreach (var symbol in link.Subscribed)
        {
            if (link.GapFillHandled.Add(symbol))
            {
                toFill.Add(symbol);

                // Starts streaming in this session with nothing known about it: its first minute needs a lead-in.
                if (_started.Add(symbol) && !watermarks.Contains(symbol))
                {
                    _leadInDue[symbol] = leadInFrom;
                }
            }
        }

        if (toFill.Count == 0)
        {
            return;
        }

        Task[] retiring;
        lock (_gate)
        {
            retiring = [.. _retiring];
        }

        Track(GapFillAsync(link, retiring, [.. toFill], pendingOnly, ct));
    }

    private async Task GapFillAsync(StreamLink link, Task[] retiring, string[] toFill, bool pendingOnly, CancellationToken ct)
    {
        // The retired connections' pumps must have finished, so their last ids (and the gaps they opened) are final.
        await Task.WhenAll(retiring).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        foreach (var symbol in toFill)
        {
            if (ct.IsCancellationRequested)
            {
                return;
            }

            if (!symbols.TryGetId(symbol, out var symbolId))
            {
                continue;
            }

            try
            {
                await LeadInAsync(symbol, ct).ConfigureAwait(false);
                if (!watermarks.TryGetStart(symbol, pendingOnly, out var lastId, out var version))
                {
                    continue;
                }

                var result = await gapFiller.FillAsync(symbolId, symbol, lastId, () => link.FirstId(symbol), output, ct).ConfigureAwait(false);

                // A fill bounded by a connection that broke meanwhile has not reached the stream that carries on:
                // the trades after that connection's last id are missing too, so the gap stays open.
                if (result.Complete && !link.EndedBroken)
                {
                    watermarks.Filled(symbol, version);
                    continue;
                }

                if (result.LastEmittedId is { } recovered)
                {
                    watermarks.Advance(symbol, recovered, version);
                }

                GiveUp(link, symbol);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                GiveUp(link, symbol);
                return; // the session is ending; the gap stays open
            }
            catch (Exception ex)
            {
                GiveUp(link, symbol);
                LogGapFillFailed(ex, symbol);
            }
        }
    }

    // The gap stays open. If this link streamed the symbol past it, those trades are stored above the gap, which makes
    // it a hole that a later seed must not raise past.
    private void GiveUp(StreamLink link, string symbol)
    {
        if (link.LastId(symbol) is { } streamed)
        {
            watermarks.MarkStoredAbove(symbol, streamed);
        }
    }

    // A due lead-in opens a gap just below the first trade of the symbol's first minute. Lower only ever moves the
    // start down, so it composes with whatever a racing Merge or a dropped batch set meanwhile. A failed lookup stays
    // due and is tried again on the next connection.
    private async Task LeadInAsync(string symbol, CancellationToken ct)
    {
        if (!_leadInDue.TryGetValue(symbol, out var from))
        {
            return;
        }

        var (succeeded, firstId) = await gapFiller.FirstTradeIdFromAsync(symbol, from, ct).ConfigureAwait(false);
        if (!succeeded)
        {
            return;
        }

        if (firstId is { } id)
        {
            watermarks.Lower(symbol, id);
        }

        // Only this one: a re-add since then has its own.
        _leadInDue.TryRemove(new KeyValuePair<string, DateTime>(symbol, from));
    }

    private void Retire(StreamLink link, LinkEnd end)
    {
        link.MarkEnded(end); // before the dispose ends its connection
        var retire = RetireAsync(link, end);
        lock (_gate)
        {
            _retiring.RemoveAll(t => t.IsCompleted);
            _retiring.Add(retire);
        }

        Track(retire);
    }

    private async Task RetireAsync(StreamLink link, LinkEnd end)
    {
        await link.DisposeAsync().ConfigureAwait(false);
        Observe(link);

        // The pump drains what the socket had buffered, then ends.
        await link.Pump.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (end != LinkEnd.Discarded)
        {
            watermarks.Merge(link.LastIds, gap: end == LinkEnd.Broken);
        }
        lock (_gate)
        {
            _links.Remove(link);
        }
    }

    private void Forget(StreamLink link, IReadOnlyList<string> unsubscribed)
    {
        // A symbol that is re-added later starts fresh instead of gap-filling from where it was dropped.
        foreach (var symbol in unsubscribed)
        {
            _started.Remove(symbol);
            _leadInDue.TryRemove(symbol, out _);
            link.Forget(symbol);
            watermarks.Forget(symbol);
            state.ForgetSymbol(symbol);
        }
    }

    private void Track(Task task)
    {
        lock (_gate)
        {
            _background.RemoveAll(t => t.IsCompleted);
            _background.Add(task);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Binance stream connected with {Count} subscription(s); rotation due at {NextRotationUtc:o}")]
    private partial void LogConnected(int count, DateTime nextRotationUtc);

    [LoggerMessage(Level = LogLevel.Information, Message = "Reconnected to the Binance stream")]
    private partial void LogReconnected();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Binance stream connection ended after {Seconds:F0} s (faulted: {Faulted}); reconnecting")]
    private partial void LogConnectionEnded(double seconds, bool faulted);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not connect to the Binance stream")]
    private partial void LogConnectFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Rotating the Binance stream connection opened at {ConnectedAtUtc:o}")]
    private partial void LogRotating(DateTime connectedAtUtc);

    [LoggerMessage(Level = LogLevel.Information, Message = "Rotated the Binance stream connection ({Count} subscription(s))")]
    private partial void LogRotated(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rotation failed ({Missing} subscription(s) not confirmed on the new connection); keeping the current one and retrying later")]
    private partial void LogRotationFailed(int missing);

    [LoggerMessage(Level = LogLevel.Error, Message = "Trade pump failed; replacing the connection")]
    private partial void LogPumpFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Gap-fill for {Symbol} failed; the gap stays open")]
    private partial void LogGapFillFailed(Exception exception, string symbol);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dropped trade(s) for {Symbol}, which has no known id ({Count} so far on this connection)")]
    private partial void LogUnknownSymbol(string symbol, long count);
}

/// <summary>How a connection's life ended, which decides what its last trade ids mean for gap-fill.</summary>
internal enum LinkEnd
{
    /// <summary>It broke (or the session ended with it): its last ids are merged and every symbol has an open gap.</summary>
    Broken,

    /// <summary>A rotation replaced it after the new connection was subscribed: its last ids are merged, no gap.</summary>
    Replaced,

    /// <summary>A rotation gave it up before it took over: its last ids are ignored.</summary>
    Discarded,
}

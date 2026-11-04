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
/// SUBSCRIBE is acknowledged there, which may be a later reconcile pass than the first one. A fill bounded by a
/// connection that broke meanwhile cannot close the gap for good: that connection's end reopens it. A gap that is
/// still open on the live connection (a fill stopped at the page cap or failed, the writer dropped a batch) is filled
/// again on every reconcile pass, one run per symbol at a time, until it closes, so it never waits for the next
/// connection. Every pass also checks the live connection's streams against the watermarks: one that started above
/// the last accounted id (its predecessor stalled before the rotation replaced it) opens a gap for the ids between. A
/// clean rotation hands the retired connection's last ids to its successor: until the successor delivers a symbol, a
/// short REST page ends that symbol's fill only once it reached past them, so lagging REST cannot close a gap below ids
/// the retired connection delivered.</para>
/// <para><b>Lead-in.</b> A symbol that starts streaming in this session (first subscription, an added or reactivated
/// symbol) with no watermark at all is filled from the start of the minute its SUBSCRIBE was acknowledged in, up to its
/// first streamed trade. Without it that minute's candle would only hold the trades after the subscription, and the
/// backfill (which stops before the first streamed minute) never completes it. A lead-in whose lookup failed stays due
/// and is tried again on the next connection.</para>
/// <para><b>Tail-out.</b> The mirror of the lead-in: a symbol that is removed (its UNSUBSCRIBE acknowledged) stopped
/// streaming mid-minute. A few seconds after that minute ends, one bounded REST pass fetches its trades from the last
/// id this link delivered (or the id a rotation handed over, if this link has delivered none yet) up to the minute's end,
/// so that candle is not partial either. It runs in the background and
/// never delays the unsubscribe; a failure only leaves the minute partial, as before. It stays inside the removal
/// minute: it never stores a trade past the minute the stream stopped in, so a later reactivation's backfill of the
/// inactive stretch still starts after it.</para>
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

    /// <summary>
    /// The tail-out of a removed symbol runs this long after the end of its last streamed minute, so that the minute is
    /// over on the exchange too (a local clock slightly behind it) and REST has caught up with it.
    /// </summary>
    public static readonly TimeSpan TailOutDelay = TimeSpan.FromSeconds(5);

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

    // Per symbol, gap-fill runs scheduled or in flight: incremented by the control loop when it hands the symbol to a
    // run, decremented by that run. An open gap is re-armed on a reconcile pass only while this is zero, so at most one
    // re-armed fill per symbol runs at a time.
    private readonly ConcurrentDictionary<string, int> _scheduled = new(StringComparer.OrdinalIgnoreCase);
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
        watermarks.Merge(OpenLinks().SelectMany(l => l.Spans), gap: true);
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
                StartTailOuts(current, outcome.Unsubscribed, ct);
                Forget(current, outcome.Unsubscribed);
                watermarks.Verify(current.Spans);
                RearmOpenGaps(current);
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

            // The old connection stays the one followed, and its end decides about gaps. This one's trades were
            // delivered and stored all the same, so retiring records them (a later seed must not jump past a gap
            // below them).
            Retire(next, LinkEnd.Discarded);
            return null;
        }

        // The old connection is idle from here on: only this loop sends commands, and it has moved to the new one.
        var oldEndedEarly = old.Connection.Completion.IsCompleted;
        state.SetConnected(next.ConnectedAtUtc, next.ConnectedAtUtc + options.RotationInterval);
        if (oldEndedEarly)
        {
            Retire(old, LinkEnd.Broken);
        }
        else
        {
            // The new link carries every trade after the old one's last: it hands that boundary over.
            Retire(old, LinkEnd.Replaced, successor: next);
        }
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
                    link.Record(trade.Symbol, trade.AggTradeId, trade.TradeTimeUtc);
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

        foreach (var symbol in toFill)
        {
            _scheduled.AddOrUpdate(symbol, 1, static (_, n) => n + 1);
        }

        Track(GapFillAsync(link, retiring, [.. toFill], pendingOnly, ct));
    }

    private async Task GapFillAsync(StreamLink link, Task[] retiring, string[] toFill, bool pendingOnly, CancellationToken ct)
    {
        var done = 0;
        try
        {
            // The retired connections' pumps must have finished, so their last ids (and the gaps they opened) are final.
            await Task.WhenAll(retiring).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            for (; done < toFill.Length; done++)
            {
                var symbol = toFill[done];
                var goOn = !ct.IsCancellationRequested && await FillOneAsync(link, symbol, pendingOnly, ct).ConfigureAwait(false);
                Unschedule(symbol);
                if (!goOn)
                {
                    done++;
                    return;
                }
            }
        }
        finally
        {
            // Symbols this run will not get to (the session is ending) are no longer scheduled either.
            for (var i = done; i < toFill.Length; i++)
            {
                Unschedule(toFill[i]);
            }
        }
    }

    // False when the session is ending.
    private async Task<bool> FillOneAsync(StreamLink link, string symbol, bool pendingOnly, CancellationToken ct)
    {
        if (!symbols.TryGetId(symbol, out var symbolId))
        {
            return true;
        }

        try
        {
            await LeadInAsync(symbol, ct).ConfigureAwait(false);
            if (!watermarks.TryGetStart(symbol, pendingOnly, out var lastId, out var version))
            {
                return true;
            }

            // Normally the gap ends where this link's stream starts. When the start lies inside this link's own stream
            // (a batch of it was dropped, or the start was seeded above where it began), every id up to what the link
            // has delivered may be missing, and the stream carries everything after that.
            long? Boundary() => link.FirstId(symbol) is { } first && first > lastId ? first : link.LastId(symbol) + 1;

            // Until this link delivers the symbol, what the link it replaced in a rotation delivered is the floor: a
            // lagging REST page below it is not the end of the data (that link's ids may be the very ones missing).
            long? HandedOver() => link.HandedOverId(symbol);

            var result = await gapFiller.FillAsync(symbolId, symbol, lastId, Boundary, HandedOver, output, ct).ConfigureAwait(false);

            // If this link broke meanwhile, the fill stopped at a stream that no longer carries on; its retirement
            // reopens the gap (and the version changed), so closing here is harmless.
            if (result.Complete)
            {
                watermarks.Filled(symbol, version, result.LastEmittedId);
            }
            else if (result.LastEmittedId is { } recovered)
            {
                watermarks.Advance(symbol, recovered, version);
            }

            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false; // the session is ending; the gap stays open
        }
        catch (Exception ex)
        {
            LogGapFillFailed(ex, symbol);
            return true; // the gap stays open
        }
    }

    private void Unschedule(string symbol) => _scheduled.AddOrUpdate(symbol, 0, static (_, n) => Math.Max(0, n - 1));

    // A gap still open on this live link (a fill stopped at the page cap or was given up, a batch was dropped) is
    // filled again on the next reconcile pass instead of waiting for the next connection, as long as no fill for the
    // symbol is scheduled or running. Control loop only.
    private void RearmOpenGaps(StreamLink link)
    {
        foreach (var symbol in link.GapFillHandled.ToArray())
        {
            if ((!_scheduled.TryGetValue(symbol, out var scheduled) || scheduled == 0) && watermarks.IsPending(symbol))
            {
                link.GapFillHandled.Remove(symbol);
            }
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

    private void Retire(StreamLink link, LinkEnd end, StreamLink? successor = null)
    {
        var retire = RetireAsync(link, end, successor);
        lock (_gate)
        {
            _retiring.RemoveAll(t => t.IsCompleted);
            _retiring.Add(retire);
        }

        Track(retire);
    }

    private async Task RetireAsync(StreamLink link, LinkEnd end, StreamLink? successor)
    {
        await link.DisposeAsync().ConfigureAwait(false);
        Observe(link);

        // The pump drains what the socket had buffered, then ends, so the spans are final. A gap-fill on the successor
        // waits for this retirement, so it sees the handed-over ids from its first page on.
        await link.Pump.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        successor?.TakeOver(link);
        if (end == LinkEnd.Discarded)
        {
            watermarks.NoteStored(link.Spans);
        }
        else
        {
            watermarks.Merge(link.Spans, gap: end == LinkEnd.Broken);
        }
        lock (_gate)
        {
            _links.Remove(link);
        }
    }

    // A removed symbol stopped streaming mid-minute: once that minute is over, fetch the rest of it over REST, so its
    // candle (and the close the 1m SMA uses) is the exchange's. Control loop only, right after the UNSUBSCRIBE ack and
    // before Forget drops the link's ids; the fill runs in the background and never holds up the control loop.
    private void StartTailOuts(StreamLink link, IReadOnlyList<string> unsubscribed, CancellationToken ct)
    {
        var ackedAt = UtcNow;
        foreach (var symbol in unsubscribed)
        {
            // Nothing streamed on this link nor on the links it replaced in clean rotations: no minute was left half done.
            // A symbol removed right after a rotation, before the new link delivered it, tails out from the retired
            // link's last id (the new link carries every trade after it, so nothing was streamed past it since).
            if ((link.LastId(symbol) ?? link.HandedOverId(symbol)) is not { } lastId || !symbols.TryGetId(symbol, out var symbolId))
            {
                continue;
            }

            // The end of the minute the stream stopped in: the ack's, or the last trade's when that is later (a local
            // clock behind the exchange's).
            var until = Period.OneMinute.AlignDown(ackedAt) + Period.OneMinute.Length;
            if (link.LastTradeTimeUtc(symbol) is { } last && Period.OneMinute.AlignDown(last) + Period.OneMinute.Length > until)
            {
                until = Period.OneMinute.AlignDown(last) + Period.OneMinute.Length;
            }

            Track(TailOutAsync(symbolId, symbol, lastId, until, ct));
        }
    }

    private async Task TailOutAsync(int symbolId, string symbol, long lastId, DateTime untilUtc, CancellationToken ct)
    {
        try
        {
            var wait = untilUtc + TailOutDelay - UtcNow;
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, clock, ct).ConfigureAwait(false);
            }

            var result = await gapFiller.TailOutAsync(symbolId, symbol, lastId, untilUtc, output, ct).ConfigureAwait(false);
            LogTailedOut(symbol, result.Emitted, untilUtc, result.Complete);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The session is ending; the tail-out is best-effort.
        }
        catch (Exception ex)
        {
            LogTailOutFailed(ex, symbol);
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Tail-out of removed {Symbol}: {Count} trade(s) of its last minute (to {UntilUtc:o}) recovered (complete: {Complete})")]
    private partial void LogTailedOut(string symbol, int count, DateTime untilUtc, bool complete);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tail-out of removed {Symbol} failed; its last minute stays partial")]
    private partial void LogTailOutFailed(Exception exception, string symbol);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dropped trade(s) for {Symbol}, which has no known id ({Count} so far on this connection)")]
    private partial void LogUnknownSymbol(string symbol, long count);
}

/// <summary>How a connection's life ended, which decides what its last trade ids mean for gap-fill.</summary>
internal enum LinkEnd
{
    /// <summary>It broke (or the session ended with it): its spans are merged and every symbol has an open gap.</summary>
    Broken,

    /// <summary>
    /// A rotation replaced it after the new connection was subscribed: its spans are merged, and a gap opens only where
    /// the new connection's stream turns out to start above them.
    /// </summary>
    Replaced,

    /// <summary>A rotation gave it up before it took over: its spans are recorded as stored, and move no gap-fill start.</summary>
    Discarded,
}

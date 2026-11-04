using System.Data.Common;
using System.Globalization;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TickPulse.Application.Abstractions;
using TickPulse.Application.Ingestion;
using TickPulse.Domain;
using TickPulse.Infrastructure.Binance;

namespace TickPulse.Infrastructure.Backfill;

/// <summary>
/// Fills <c>candles_1m</c> with historical Binance 1-minute klines (<c>source = 2</c>) so long-period SMAs have data
/// from the first day. Also the <see cref="IBackfillQueue"/> the admin use case enqueues new symbols into.
/// </summary>
/// <remarks>
/// <para>Off unless <see cref="BackfillOptions.Enabled"/>: then <see cref="ExecuteAsync"/> returns at once and
/// <see cref="Enqueue"/> ignores requests.</para>
/// <para>Works only while <see cref="IngestionState.IsLeader"/> is true, and never touches the leader lock itself.
/// Each time this instance becomes leader (including at start-up) it queues every active symbol whose status is not
/// <see cref="BackfillStatus.Completed"/>. Symbols are processed one at a time, in queue order; a symbol that is
/// already queued or running is not queued again.</para>
/// <para>A symbol is fetched from <c>AlignDown(now − Depth, 1m)</c> up to, but excluding, its first streamed minute
/// (or the current minute when nothing was streamed yet), in ascending pages of <see cref="PageSize"/> by
/// <c>startTime</c>. A run that was interrupted (shutdown, lost leadership, failure) continues after the latest minute
/// it already stored, provided the stored stretch reaches back to the start; otherwise (e.g. after the depth was
/// increased) it starts again from the beginning. Inserts are <c>ON CONFLICT DO NOTHING</c>, so overlap is harmless
/// and streamed minutes are never overwritten. Klines with zero volume are stored as Binance returns them. On
/// completion <c>backfilled_from</c> records the earliest backfilled minute actually stored.</para>
/// <para>Failures: a transient Binance error is retried up to <see cref="MaxTransientRetries"/> times with
/// <see cref="RetryDelay"/>, unless leadership was lost (then the run pauses). A permanent Binance error, persisting
/// transient errors or a database error mark the symbol <see cref="BackfillStatus.Failed"/> (the reason is logged)
/// and the queue moves on. A ban marks the current symbol Failed, waits until the ban ends, and then scans again, so
/// Failed symbols are queued again; the scan at start-up and on every change of leadership also includes them. Any
/// other exception is a bug and stops the worker.</para>
/// </remarks>
internal sealed partial class BackfillWorker(
    IKlineSource klines,
    ICandleWriter candles,
    ISymbolRepository symbols,
    IMarketDataQueries queries,
    IngestionState state,
    IOptions<BackfillOptions> options,
    TimeProvider clock,
    ILogger<BackfillWorker> logger) : BackgroundService, IBackfillQueue
{
    /// <summary>Klines per REST call (the Binance maximum).</summary>
    public const int PageSize = BinanceRestClient.MaxLimit;

    /// <summary>How many times one page is retried after a transient error before the symbol fails.</summary>
    public const int MaxTransientRetries = 5;

    /// <summary>How often a non-leader checks whether it has become leader.</summary>
    public static readonly TimeSpan LeaderPollInterval = TimeSpan.FromSeconds(5);

    /// <summary>Upper bound of <see cref="RetryDelay"/>.</summary>
    public static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

    /// <summary>How long to wait after a ban that carries no end time (the shortest Binance ban).</summary>
    public static readonly TimeSpan DefaultBanWait = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan Minute = Period.OneMinute.Length;

    private readonly Channel<int> _queue = Channel.CreateUnbounded<int>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Lock _gate = new();

    // Symbols queued or running; guarded by _gate.
    private readonly HashSet<int> _pending = [];

    private readonly bool _enabled = options.Value.Enabled;
    private readonly TimeSpan _depth = options.Value.Depth;

    // Set whenever leadership was (possibly) lost; the next leader iteration scans for unfinished symbols.
    private bool _scanNeeded = true;

    /// <summary>The wait before retry <paramref name="attempt"/> (1-based): 1 s, doubling, at most <see cref="MaxRetryDelay"/>.</summary>
    public static TimeSpan RetryDelay(int attempt)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
        var delay = attempt <= 6 ? TimeSpan.FromSeconds(1 << (attempt - 1)) : MaxRetryDelay;
        return delay < MaxRetryDelay ? delay : MaxRetryDelay;
    }

    /// <summary>Queues a symbol for backfill; ignored when backfill is disabled or the symbol is already queued or running.</summary>
    public void Enqueue(int symbolId)
    {
        if (!_enabled)
        {
            return;
        }

        lock (_gate)
        {
            if (!_pending.Add(symbolId))
            {
                return;
            }
        }

        // Unbounded and never completed, so the write always succeeds.
        _queue.Writer.TryWrite(symbolId);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            LogDisabled();
            return;
        }

        await Task.Yield();
        try
        {
            while (true)
            {
                if (!state.IsLeader)
                {
                    await WaitForLeadershipAsync(symbol: null, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                if (_scanNeeded && !await TryQueuePendingAsync(stoppingToken).ConfigureAwait(false))
                {
                    await Task.Delay(LeaderPollInterval, clock, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                if (_queue.Reader.TryRead(out var symbolId))
                {
                    try
                    {
                        await ProcessAsync(symbolId, stoppingToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        lock (_gate)
                        {
                            _pending.Remove(symbolId);
                        }
                    }

                    continue;
                }

                await WaitForWorkAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down. A symbol that was running stays Running and is resumed on the next start.
        }
    }

    // Waits until this instance is leader, polling every LeaderPollInterval. The timer is registered before the log
    // entry, so whoever observes the entry can advance a fake clock past it.
    private async Task WaitForLeadershipAsync(string? symbol, CancellationToken ct)
    {
        _scanNeeded = true;
        var logged = false;
        while (!state.IsLeader)
        {
            var wait = Task.Delay(LeaderPollInterval, clock, ct);
            if (!logged)
            {
                logged = true;
                if (symbol is null)
                {
                    LogWaitingForLeadership();
                }
                else
                {
                    LogPaused(symbol);
                }
            }

            await wait.ConfigureAwait(false);
        }

        if (symbol is not null)
        {
            LogResumed(symbol);
        }
    }

    // Wakes up when a symbol is queued, or after LeaderPollInterval to notice a change of leadership.
    private async Task WaitForWorkAsync(CancellationToken ct)
    {
        using var poll = new CancellationTokenSource(LeaderPollInterval, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, poll.Token);
        try
        {
            await _queue.Reader.WaitToReadAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Poll interval elapsed.
        }
    }

    // Queues the active symbols that have not completed, in id order. False when the database could not be read.
    private async Task<bool> TryQueuePendingAsync(CancellationToken ct)
    {
        IReadOnlyList<MonitoredSymbol> active;
        try
        {
            active = await symbols.ListActiveAsync(ct).ConfigureAwait(false);
        }
        catch (DbException ex)
        {
            LogScanFailed(ex);
            return false;
        }

        _scanNeeded = false;
        var unfinished = active.Where(s => s.BackfillStatus != BackfillStatus.Completed).ToArray();
        foreach (var symbol in unfinished)
        {
            Enqueue(symbol.Id);
        }

        LogQueuedPending(unfinished.Length);
        return true;
    }

    private async Task ProcessAsync(int symbolId, CancellationToken ct)
    {
        MonitoredSymbol? symbol = null;
        try
        {
            symbol = (await symbols.ListActiveAsync(ct).ConfigureAwait(false)).FirstOrDefault(s => s.Id == symbolId);
            if (symbol is null)
            {
                LogSkippedInactive(symbolId);
                return;
            }

            if (symbol.BackfillStatus == BackfillStatus.Completed)
            {
                LogSkippedCompleted(symbol.Name);
                return;
            }

            if (!SymbolName.TryCreate(symbol.Name, out var name))
            {
                throw new InvalidOperationException($"'{symbol.Name}' is not a valid symbol name.");
            }

            await BackfillAsync(symbolId, name, ct).ConfigureAwait(false);
        }
        catch (BinanceBannedException ex)
        {
            // While banned every request fails at once (and would extend the ban): wait it out before the next
            // symbol, then scan again so this one, now Failed, is queued again.
            await FailAsync(symbolId, symbol?.Name, $"Binance has banned this IP until {ex.BannedUntilUtc:O}", ex, ct).ConfigureAwait(false);
            await WaitOutBanAsync(ex, ct).ConfigureAwait(false);
        }
        catch (BinanceApiException ex)
        {
            // A permanent error, or transient errors that persisted past the retries.
            await FailAsync(symbolId, symbol?.Name, ex.Message, ex, ct).ConfigureAwait(false);
        }
        catch (DbException ex)
        {
            await FailAsync(symbolId, symbol?.Name, ex.Message, ex, ct).ConfigureAwait(false);
        }

        // Anything else is a bug and propagates; so does the cancellation on shutdown.
    }

    private async Task WaitOutBanAsync(BinanceBannedException ex, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var wait = ex.BannedUntilUtc == default ? DefaultBanWait : ex.BannedUntilUtc - now;
        if (wait > TimeSpan.Zero)
        {
            // The timer is registered before the log entry (see WaitForLeadershipAsync).
            var delay = Task.Delay(wait, clock, ct);
            LogBanWait(now + wait);
            await delay.ConfigureAwait(false);
        }

        _scanNeeded = true;
    }

    private async Task BackfillAsync(int symbolId, SymbolName name, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var from = Period.OneMinute.AlignDown(now - _depth);
        var end = await queries.FirstStreamMinuteAsync(symbolId, ct).ConfigureAwait(false) ?? Period.OneMinute.AlignDown(now);

        await symbols.SetBackfillStatusAsync(symbolId, BackfillStatus.Running, null, ct).ConfigureAwait(false);
        var next = await ResumePointAsync(symbolId, from, end, from, ct).ConfigureAwait(false);
        LogStarted(name.Value, from, end, next);

        var progress = new Progress(from, end, next);
        var pages = 0;
        var fetched = 0;
        var inserted = 0;
        while (next < end)
        {
            if (!state.IsLeader)
            {
                await WaitForLeadershipAsync(name.Value, ct).ConfigureAwait(false);

                // Another leader may have stored more in the meantime.
                next = await ResumePointAsync(symbolId, from, end, next, ct).ConfigureAwait(false);
                continue;
            }

            var page = await FetchPageAsync(name, next, end, ct).ConfigureAwait(false);
            if (page is null)
            {
                continue; // leadership was lost during the retries: pause above
            }

            pages++;
            if (page.Count == 0)
            {
                break;
            }

            fetched += page.Count;
            inserted += await candles.InsertBackfillAsync(symbolId, page, ct).ConfigureAwait(false);

            var after = page[^1].OpenTimeUtc + Minute;
            if (after <= next || page.Count < PageSize)
            {
                // Nothing more in [next, end) (a short page), or no progress (never expected from Binance).
                next = end;
                break;
            }

            next = after;
            if (progress.Advance(next) is { } percent)
            {
                LogProgress(name.Value, percent, next);
            }
        }

        // backfilled_from is what is actually stored: later than `from` when the symbol's history is shorter.
        var stored = await queries.BackfillRangeBeforeAsync(symbolId, end, ct).ConfigureAwait(false);
        await symbols.SetBackfillStatusAsync(symbolId, BackfillStatus.Completed, stored?.First, ct).ConfigureAwait(false);
        LogCompleted(name.Value, stored?.First, end, pages, fetched, inserted);
    }

    // Where to continue in [from, end) when [from, next) is already covered: right after the latest backfilled minute,
    // but only if the stored stretch begins at or before `from`. A stretch that starts later (left by a run with a
    // shorter depth) would leave a hole before it, so the walk starts at `next` and overlaps it harmlessly.
    private async Task<DateTime> ResumePointAsync(int symbolId, DateTime from, DateTime end, DateTime next, CancellationToken ct)
    {
        if (end <= next)
        {
            return next;
        }

        var range = await queries.BackfillRangeBeforeAsync(symbolId, end, ct).ConfigureAwait(false);
        return range is { } stored && stored.First <= from && stored.Last + Minute > next ? stored.Last + Minute : next;
    }

    // One page of klines with open times in [start, end). Retries transient errors while leader; null when leadership
    // was lost instead of waiting for another retry. Everything else propagates.
    private async Task<IReadOnlyList<Kline>?> FetchPageAsync(SymbolName name, DateTime start, DateTime end, CancellationToken ct)
    {
        var last = end - TimeSpan.FromMilliseconds(1);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await klines.GetMinuteKlinesAsync(name, start, last, PageSize, ct).ConfigureAwait(false);
            }
            catch (BinanceApiException ex) when (ex.IsTransient && (attempt <= MaxTransientRetries || !state.IsLeader))
            {
                if (!state.IsLeader)
                {
                    return null;
                }

                var delay = RetryDelay(attempt);
                var wait = Task.Delay(delay, clock, ct);
                LogRetrying(ex, name.Value, start, attempt, delay.TotalSeconds);
                await wait.ConfigureAwait(false);
            }
        }
    }

    private async Task FailAsync(int symbolId, string? symbol, string reason, Exception ex, CancellationToken ct)
    {
        LogFailed(ex, symbol ?? symbolId.ToString(CultureInfo.InvariantCulture), reason);
        try
        {
            await symbols.SetBackfillStatusAsync(symbolId, BackfillStatus.Failed, null, ct).ConfigureAwait(false);
        }
        catch (DbException statusEx)
        {
            LogStatusFailed(statusEx, symbolId);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Kline backfill is disabled (Backfill:Enabled=false)")]
    private partial void LogDisabled();

    [LoggerMessage(Level = LogLevel.Debug, Message = "Kline backfill is waiting for leadership")]
    private partial void LogWaitingForLeadership();

    [LoggerMessage(Level = LogLevel.Debug, Message = "Kline backfill queued {Count} unfinished symbol(s)")]
    private partial void LogQueuedPending(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Kline backfill could not read the symbols; retrying")]
    private partial void LogScanFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Kline backfill skipped symbol {SymbolId}: not an active symbol")]
    private partial void LogSkippedInactive(int symbolId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Kline backfill skipped {Symbol}: already completed")]
    private partial void LogSkippedCompleted(string symbol);

    [LoggerMessage(Level = LogLevel.Information, Message = "Kline backfill of {Symbol} started: {From:O} to {End:O}, resuming at {Next:O}")]
    private partial void LogStarted(string symbol, DateTime from, DateTime end, DateTime next);

    [LoggerMessage(Level = LogLevel.Information, Message = "Kline backfill of {Symbol}: {Percent}% done, at {Next:O}")]
    private partial void LogProgress(string symbol, int percent, DateTime next);

    [LoggerMessage(Level = LogLevel.Information, Message = "Kline backfill of {Symbol} completed: {From:O} to {End:O}, {Pages} page(s), {Fetched} kline(s), {Inserted} new")]
    private partial void LogCompleted(string symbol, DateTime? from, DateTime end, int pages, int fetched, int inserted);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Kline backfill is waiting for the Binance ban to end at {Until:O}")]
    private partial void LogBanWait(DateTime until);

    [LoggerMessage(Level = LogLevel.Information, Message = "Kline backfill of {Symbol} paused: this instance is no longer leader")]
    private partial void LogPaused(string symbol);

    [LoggerMessage(Level = LogLevel.Information, Message = "Kline backfill of {Symbol} resumed: leader again")]
    private partial void LogResumed(string symbol);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Kline backfill of {Symbol} at {Start:O} failed transiently (attempt {Attempt}); retrying in {DelaySeconds} s")]
    private partial void LogRetrying(Exception ex, string symbol, DateTime start, int attempt, double delaySeconds);

    [LoggerMessage(Level = LogLevel.Error, Message = "Kline backfill of {Symbol} failed: {Reason}")]
    private partial void LogFailed(Exception ex, string symbol, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Kline backfill could not record the failure of symbol {SymbolId}")]
    private partial void LogStatusFailed(Exception ex, int symbolId);

    // Reports progress over [from, end) each time another 10 % has been passed.
    private sealed class Progress(DateTime from, DateTime end, DateTime start)
    {
        private int _nextReport = (Percent(from, end, start) / 10 * 10) + 10;

        public int? Advance(DateTime next)
        {
            var percent = Percent(from, end, next);
            if (percent < _nextReport || percent >= 100)
            {
                return null;
            }

            _nextReport = (percent / 10 * 10) + 10;
            return percent;
        }

        private static int Percent(DateTime from, DateTime end, DateTime at) =>
            end <= from ? 100 : (int)Math.Clamp((at - from).Ticks * 100 / Math.Max(1, (end - from).Ticks), 0, 100);
    }
}

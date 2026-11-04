using System.Diagnostics;
using System.Net.Sockets;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using TickPulse.Application.Abstractions;
using TickPulse.Application.Ingestion;
using TickPulse.Domain;
using TickPulse.Infrastructure.Telemetry;

namespace TickPulse.Infrastructure.Ingestion;

/// <summary>
/// Drains the tick channel into <see cref="ITickWriter"/> in batches: a batch is written when it holds
/// <see cref="IngestionOptions.BatchMaxSize"/> ticks or <see cref="IngestionOptions.BatchMaxDelay"/> after its first
/// tick, whichever comes first. After every stored batch it updates <see cref="IngestionState"/> (rate, lag, latest
/// prices).
/// </summary>
/// <remarks>
/// <para>Failures, all safe to retry because the write is idempotent:</para>
/// <list type="bullet">
/// <item>Transient ones (the database restarting or unreachable, connection errors, timeouts) are retried for as long
/// as they last, with exponential backoff capped at <see cref="MaxTransientDelay"/>. Meanwhile the bounded channel
/// fills and holds the stream back.</item>
/// <item>Deadlocks (<c>40P01</c>) and serialization failures (<c>40001</c>) are retried <see cref="MaxConflictAttempts"/>
/// times with a short backoff.</item>
/// <item>Anything else, or conflicts that persist, drop the batch with an error log.</item>
/// </list>
/// <para>Ticks that were dropped, or abandoned when <see cref="RunAsync"/> is cancelled, are reported to
/// <see cref="IGapFillWatermark"/>, so the next gap-fill fetches them again.</para>
/// </remarks>
public sealed partial class TickBatchWriter(
    ITickWriter writer,
    IngestionState state,
    SymbolIdCache symbols,
    IGapFillWatermark watermark,
    IOptions<IngestionOptions> options,
    TimeProvider clock,
    ILogger<TickBatchWriter> logger,
    TickPulseMetrics metrics)
{
    /// <summary>Attempts per batch for deadlocks and serialization failures.</summary>
    public const int MaxConflictAttempts = 4;

    /// <summary>Upper bound on the wait between retries of a transient failure.</summary>
    public static readonly TimeSpan MaxTransientDelay = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A tick received this long after its trade time was recovered (gap-fill), not streamed; a batch whose newest
    /// tick is one records no ingest lag sample.
    /// </summary>
    public static readonly TimeSpan RecoveredTickAge = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan ConflictBaseDelay = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan TransientBaseDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Writes batches until <paramref name="reader"/> completes, then writes what is left and returns. Cancelling
    /// <paramref name="ct"/> abandons the remaining ticks (and records them as a gap); a graceful stop completes the
    /// channel instead.
    /// </summary>
    public async Task RunAsync(ChannelReader<Tick> reader, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var maxSize = options.Value.BatchMaxSize;
        var maxDelay = options.Value.BatchMaxDelay;
        var batch = new List<Tick>(Math.Min(maxSize, 4096));
        var inserted = new Dictionary<int, int>();

        try
        {
            while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                var started = clock.GetTimestamp();
                while (batch.Count < maxSize)
                {
                    if (reader.TryRead(out var tick))
                    {
                        batch.Add(tick);
                        continue;
                    }

                    var remaining = maxDelay - clock.GetElapsedTime(started);
                    if (remaining <= TimeSpan.Zero || !await WaitForMoreAsync(reader, remaining, ct).ConfigureAwait(false))
                    {
                        break;
                    }
                }

                if (batch.Count > 0)
                {
                    await FlushAsync(batch, inserted, ct).ConfigureAwait(false);
                    batch.Clear();
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            while (reader.TryRead(out var tick))
            {
                batch.Add(tick);
            }

            if (batch.Count > 0)
            {
                LogAbandoned(batch.Count);
                ReportGap(batch);
            }
        }
    }

    private static bool IsConflict(Exception ex) =>
        ex is PostgresException { SqlState: PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure };

    private static bool IsTransient(Exception ex) => ex switch
    {
        PostgresException pg => pg.SqlState.StartsWith("08", StringComparison.Ordinal) // connection exceptions
            || pg.SqlState is PostgresErrorCodes.AdminShutdown or PostgresErrorCodes.CrashShutdown
                or PostgresErrorCodes.CannotConnectNow or PostgresErrorCodes.TooManyConnections
            || pg.IsTransient,
        NpgsqlException npgsql => npgsql.IsTransient || npgsql.InnerException is IOException or SocketException or TimeoutException,
        TimeoutException => true,
        _ => false,
    };

    // False when the delay ran out or the channel completed.
    private async Task<bool> WaitForMoreAsync(ChannelReader<Tick> reader, TimeSpan delay, CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(delay, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            return await reader.WaitToReadAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }

    // Returns once the batch is stored or dropped; throws OperationCanceledException when ct is cancelled.
    private async Task FlushAsync(List<Tick> batch, Dictionary<int, int> inserted, CancellationToken ct)
    {
        using var activity = TickPulseMetrics.ActivitySource.StartActivity(TickPulseMetrics.FlushSpan);
        activity?.SetTag("tickpulse.batch.size", batch.Count);
        var started = clock.GetTimestamp();
        int stored;
        var attempts = 0;
        var conflicts = 0;
        var transient = 0;
        while (true)
        {
            attempts++;
            try
            {
                stored = await writer.WriteBatchAsync(batch, inserted, ct).ConfigureAwait(false);
                break;
            }
            catch (PostgresException ex) when (IsConflict(ex) && conflicts + 1 < MaxConflictAttempts && !ct.IsCancellationRequested)
            {
                conflicts++;
                LogConflict(ex, batch.Count, attempts, ex.SqlState);
                await Task.Delay(ConflictBaseDelay * (1 << (conflicts - 1)), clock, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!IsConflict(ex) && IsTransient(ex) && !ct.IsCancellationRequested)
            {
                var delay = TransientDelay(transient++);
                LogTransient(ex, batch.Count, attempts, delay.TotalSeconds);
                await Task.Delay(delay, clock, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                LogDropped(ex, batch.Count, attempts);
                activity?.SetStatus(ActivityStatusCode.Error, "batch dropped");
                ReportGap(batch);
                return;
            }
        }

        activity?.SetTag("tickpulse.batch.stored", stored);
        activity?.SetTag("tickpulse.batch.attempts", attempts);
        RecordStored(batch, stored, inserted, clock.GetElapsedTime(started));
    }

    private static TimeSpan TransientDelay(int attempt)
    {
        var delay = TransientBaseDelay * Math.Pow(2, Math.Min(attempt, 16));
        return delay < MaxTransientDelay ? delay : MaxTransientDelay;
    }

    private void RecordStored(List<Tick> batch, int stored, Dictionary<int, int> inserted, TimeSpan duration)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var newest = DateTime.MinValue;
        var newestReceived = DateTime.MinValue;
        var last = new Dictionary<int, Tick>();
        foreach (var tick in batch)
        {
            if (tick.TradeTimeUtc > newest)
            {
                newest = tick.TradeTimeUtc;
                newestReceived = tick.ReceivedAtUtc;
            }

            if (!last.TryGetValue(tick.SymbolId, out var seen) || tick.AggTradeId > seen.AggTradeId)
            {
                last[tick.SymbolId] = tick;
            }
        }

        // Only active symbols: a removed symbol's last ticks must not bring it back into the live prices.
        var prices = new List<LivePrice>(last.Count);
        foreach (var (symbolId, tick) in last)
        {
            if (symbols.TryGetName(symbolId, out var name) && symbols.IsActive(name))
            {
                prices.Add(new LivePrice(name, tick.Price, tick.TradeTimeUtc));
            }
        }

        LogFlushed(batch.Count, stored);
        var lagMs = (now - newest).TotalMilliseconds;
        foreach (var (symbolId, count) in inserted)
        {
            if (symbols.TryGetName(symbolId, out var name))
            {
                metrics.TicksPersisted(name, count);
            }
        }

        // Not ingest lag: a batch with nothing new (a gap-fill or rotation replay), or one whose newest tick was
        // recovered over REST long after it traded (a gap-fill batch, which carries old trade times). Neither the
        // metric nor the status and health lag take a sample from those.
        var recovered = newestReceived - newest > RecoveredTickAge;
        double? lagSample = stored > 0 && !recovered ? lagMs : null;
        metrics.BatchStored(batch.Count, duration, lagSample);
        try
        {
            state.RecordBatch(stored, lagSample, prices);
        }
        catch (AggregateException ex)
        {
            LogSubscriberFailed(ex);
        }
    }

    // The lowest unstored trade id per symbol becomes the gap-fill start.
    private void ReportGap(List<Tick> ticks)
    {
        foreach (var group in ticks.GroupBy(t => t.SymbolId))
        {
            if (symbols.TryGetName(group.Key, out var name))
            {
                watermark.Lower(name, group.Min(t => t.AggTradeId));
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Wrote a batch of {Count} tick(s), {Stored} new")]
    private partial void LogFlushed(int count, int stored);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Batch of {Count} tick(s) failed on attempt {Attempt} with {SqlState}; retrying")]
    private partial void LogConflict(Exception exception, int count, int attempt, string sqlState);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Batch of {Count} tick(s) failed transiently (attempt {Attempt}); retrying in {DelaySeconds:F1} s")]
    private partial void LogTransient(Exception exception, int count, int attempt, double delaySeconds);

    [LoggerMessage(Level = LogLevel.Error, Message = "Dropped a batch of {Count} tick(s) after {Attempts} attempt(s); gap-fill will fetch them again")]
    private partial void LogDropped(Exception exception, int count, int attempts);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Abandoned {Count} unwritten tick(s) at the end of the session; gap-fill will fetch them again")]
    private partial void LogAbandoned(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A live price subscriber failed")]
    private partial void LogSubscriberFailed(Exception exception);
}

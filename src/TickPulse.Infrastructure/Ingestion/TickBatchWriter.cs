using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using TickPulse.Application.Abstractions;
using TickPulse.Application.Ingestion;
using TickPulse.Domain;

namespace TickPulse.Infrastructure.Ingestion;

/// <summary>
/// Drains the tick channel into <see cref="ITickWriter"/> in batches: a batch is written when it holds
/// <see cref="IngestionOptions.BatchMaxSize"/> ticks or <see cref="IngestionOptions.BatchMaxDelay"/> after its first
/// tick, whichever comes first. After every stored batch it updates <see cref="IngestionState"/> (rate, lag, latest
/// prices).
/// </summary>
/// <remarks>
/// <para>A batch that fails with a deadlock (<c>40P01</c>) or a serialization failure (<c>40001</c>) is retried a few
/// times with a short backoff; the write is idempotent, so a retry is safe. Any other failure drops the batch with an
/// error log, so that one bad batch cannot stop ingestion.</para>
/// <para>Back-pressure: while a batch is being written nothing is read, the bounded channel fills, and its writers
/// wait.</para>
/// </remarks>
public sealed partial class TickBatchWriter(
    ITickWriter writer,
    IngestionState state,
    SymbolIdCache symbols,
    IOptions<IngestionOptions> options,
    TimeProvider clock,
    ILogger<TickBatchWriter> logger)
{
    /// <summary>Attempts per batch for deadlocks and serialization failures.</summary>
    public const int MaxAttempts = 4;

    private static readonly TimeSpan RetryBaseDelay = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Writes batches until <paramref name="reader"/> completes, then writes what is left and returns. Cancelling
    /// <paramref name="ct"/> abandons the remaining ticks; a graceful stop completes the channel instead.
    /// </summary>
    public async Task RunAsync(ChannelReader<Tick> reader, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var maxSize = options.Value.BatchMaxSize;
        var maxDelay = options.Value.BatchMaxDelay;
        var batch = new List<Tick>(Math.Min(maxSize, 4096));

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
                await FlushAsync(batch, ct).ConfigureAwait(false);
                batch.Clear();
            }
        }
    }

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

    private async Task FlushAsync(List<Tick> batch, CancellationToken ct)
    {
        int stored;
        var attempt = 1;
        while (true)
        {
            try
            {
                stored = await writer.WriteBatchAsync(batch, ct).ConfigureAwait(false);
                break;
            }
            catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure
                && attempt < MaxAttempts)
            {
                LogRetrying(ex, batch.Count, attempt, ex.SqlState);
                await Task.Delay(RetryBaseDelay * (1 << (attempt - 1)), clock, ct).ConfigureAwait(false);
                attempt++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                LogDropped(ex, batch.Count, attempt);
                return;
            }
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var newest = DateTime.MinValue;
        var last = new Dictionary<int, Tick>();
        foreach (var tick in batch)
        {
            if (tick.TradeTimeUtc > newest)
            {
                newest = tick.TradeTimeUtc;
            }

            if (!last.TryGetValue(tick.SymbolId, out var seen) || tick.AggTradeId > seen.AggTradeId)
            {
                last[tick.SymbolId] = tick;
            }
        }

        var prices = new List<LivePrice>(last.Count);
        foreach (var (symbolId, tick) in last)
        {
            if (symbols.TryGetName(symbolId, out var name))
            {
                prices.Add(new LivePrice(name, tick.Price, tick.TradeTimeUtc));
            }
        }

        LogFlushed(batch.Count, stored);
        try
        {
            state.RecordBatch(stored, (now - newest).TotalMilliseconds, prices);
        }
        catch (AggregateException ex)
        {
            LogSubscriberFailed(ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Wrote a batch of {Count} tick(s), {Stored} new")]
    private partial void LogFlushed(int count, int stored);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Batch of {Count} tick(s) failed on attempt {Attempt} with {SqlState}; retrying")]
    private partial void LogRetrying(Exception exception, int count, int attempt, string sqlState);

    [LoggerMessage(Level = LogLevel.Error, Message = "Dropped a batch of {Count} tick(s) after {Attempts} attempt(s)")]
    private partial void LogDropped(Exception exception, int count, int attempts);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A live price subscriber failed")]
    private partial void LogSubscriberFailed(Exception exception);
}

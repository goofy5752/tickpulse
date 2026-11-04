using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using TickPulse.Application.Abstractions;
using TickPulse.Application.Ingestion;
using TickPulse.Domain;
using TickPulse.Infrastructure.Persistence;

namespace TickPulse.Infrastructure.Ingestion;

/// <summary>
/// Leader-elected ingestion of Binance trades. A standby instance tries the leader lock every
/// <see cref="IngestionOptions.LeaderRetryInterval"/>. The leader runs the pipeline
/// WebSocket → bounded <see cref="Channel{T}"/> → <see cref="TickBatchWriter"/>, with the connection lifecycle in
/// <see cref="StreamSupervisor"/>, and probes its lock every <see cref="IngestionOptions.LeaderCheckInterval"/>: a lost
/// lock stops ingestion and the instance goes back to standby.
/// </summary>
/// <remarks>
/// <para>The worker is the only owner of its <see cref="PostgresLeaderLock"/> (it is not in the container). Other
/// components that must run only on the leader, such as the backfill worker, read
/// <see cref="IngestionState.IsLeader"/> and never touch the lock.</para>
/// <para>Ending a session (shutdown or lost leadership) clears <see cref="IngestionState.IsLeader"/>, stops accepting
/// trades (no more commands, the pumps and gap-fills stop), completes the channel and writes what it still holds within
/// <see cref="FlushDeadline"/>, closes the connections and only then releases the lock. Ticks that could not be written
/// in time are recorded as a gap for the next session.</para>
/// </remarks>
internal sealed partial class IngestionWorker(
    PostgresLeaderLock leaderLock,
    IStreamConnectionFactory connections,
    SymbolReconciler reconciler,
    TickBatchWriter batchWriter,
    GapFiller gapFiller,
    SymbolIdCache symbols,
    GapFillWatermarks watermarks,
    ISymbolChangeSignal signal,
    IngestionState state,
    IOptions<IngestionOptions> options,
    TimeProvider clock,
    ILoggerFactory loggerFactory) : BackgroundService
{
    /// <summary>How long the end of a session waits for the remaining ticks to be written.</summary>
    /// <remarks>
    /// Bounded because the writer retries transient database failures indefinitely, and a session often ends exactly
    /// because the database is gone.
    /// </remarks>
    public static readonly TimeSpan FlushDeadline = TimeSpan.FromSeconds(10);

    private readonly ILogger _logger = loggerFactory.CreateLogger<IngestionWorker>();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        state.SetStandby();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (await TryAcquireAsync(stoppingToken).ConfigureAwait(false))
                {
                    await LeadAsync(stoppingToken).ConfigureAwait(false);
                }

                await Task.Delay(options.Value.LeaderRetryInterval, clock, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
        finally
        {
            await leaderLock.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<bool> TryAcquireAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (await leaderLock.TryAcquireAsync(stoppingToken).ConfigureAwait(false))
            {
                return true;
            }

            LogLeaderLockBusy();
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            LogLeaderLockFailed(ex);
        }

        return false;
    }

    private async Task LeadAsync(CancellationToken stoppingToken)
    {
        LogLeadershipAcquired();
        state.SetConnecting();
        using var session = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        using var abandon = new CancellationTokenSource();
        var settings = options.Value;
        var channel = Channel.CreateBounded<Tick>(new BoundedChannelOptions(settings.ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });

        // Not cancelled by the session: a graceful end completes the channel, and the writer flushes what is left.
        var writer = batchWriter.RunAsync(channel.Reader, abandon.Token);
        var supervisor = new StreamSupervisor(
            connections, reconciler, gapFiller, symbols, signal, state, settings, clock,
            loggerFactory.CreateLogger<StreamSupervisor>(), channel.Writer, watermarks);
        var watchdog = WatchLeadershipAsync(session);
        try
        {
            await supervisor.RunAsync(session.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (!session.IsCancellationRequested)
        {
            LogSessionFailed(ex);
        }
        finally
        {
            // 1. Stop accepting: no more commands; the pumps and gap-fills stop writing. Other leader-only work
            //    (backfill) stops on IsLeader.
            await session.CancelAsync().ConfigureAwait(false);
            state.SetStandby();
            await watchdog.ConfigureAwait(false);
            await supervisor.StopAsync().ConfigureAwait(false);

            // 2. Complete the channel and write what it still holds, within the deadline.
            channel.Writer.TryComplete();
            await FlushAsync(writer, abandon).ConfigureAwait(false);

            // 3. Close the connections, then give the lock up.
            await supervisor.DisposeAsync().ConfigureAwait(false);
            await leaderLock.ReleaseAsync().ConfigureAwait(false);
            LogSessionEnded();
        }
    }

    private async Task FlushAsync(Task writer, CancellationTokenSource abandon)
    {
        try
        {
            await writer.WaitAsync(FlushDeadline, clock).ConfigureAwait(false);
            return;
        }
        catch (TimeoutException)
        {
            LogFlushDeadline(FlushDeadline.TotalSeconds);
            await abandon.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogFlushFailed(ex);
            return;
        }

        // The writer records the abandoned ticks as a gap and returns.
        try
        {
            await writer.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogFlushFailed(ex);
        }
    }

    private async Task WatchLeadershipAsync(CancellationTokenSource session)
    {
        var ct = session.Token;
        try
        {
            while (true)
            {
                await Task.Delay(options.Value.LeaderCheckInterval, clock, ct).ConfigureAwait(false);
                if (!await leaderLock.CheckAsync(ct).ConfigureAwait(false))
                {
                    LogLeadershipLost();
                    await session.CancelAsync().ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The session ended for another reason.
        }
        catch (Exception ex)
        {
            // Leadership can no longer be verified: stop rather than risk two leaders.
            LogWatchdogFailed(ex);
            await session.CancelAsync().ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Became the ingestion leader")]
    private partial void LogLeadershipAcquired();

    [LoggerMessage(Level = LogLevel.Debug, Message = "Another instance holds the leader lock; standing by")]
    private partial void LogLeaderLockBusy();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not try the leader lock; standing by")]
    private partial void LogLeaderLockFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Leadership lost; stopping ingestion")]
    private partial void LogLeadershipLost();

    [LoggerMessage(Level = LogLevel.Error, Message = "Leadership check failed; stopping ingestion")]
    private partial void LogWatchdogFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Ingestion session failed; standing by and retrying")]
    private partial void LogSessionFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Remaining ticks not written within {Seconds:F0} s; abandoning them as a gap")]
    private partial void LogFlushDeadline(double seconds);

    [LoggerMessage(Level = LogLevel.Error, Message = "Writing the remaining ticks failed")]
    private partial void LogFlushFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Ingestion stopped; leader lock released")]
    private partial void LogSessionEnded();
}

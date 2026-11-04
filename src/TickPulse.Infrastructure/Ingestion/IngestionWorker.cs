using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using TickPulse.Application.Abstractions;
using TickPulse.Application.Ingestion;
using TickPulse.Domain;
using TickPulse.Infrastructure.Persistence;
using TickPulse.Infrastructure.Telemetry;

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
/// <para>Starting a session seeds <see cref="GapFillWatermarks"/> from the database (each active symbol's last stored
/// trade id within <see cref="SeedWindow"/>) before anything is subscribed, so the first connection gap-fills the trades
/// that happened while no instance led: a restart, a redeploy, a crash or a handover loses nothing.</para>
/// <para>Ending a session (shutdown or lost leadership) clears <see cref="IngestionState.IsLeader"/>, stops accepting
/// trades (no more commands, the pumps and gap-fills stop), completes the channel and writes what it still holds within
/// <see cref="FlushDeadline"/>, closes the connections and only then releases the lock. Ticks that could not be written
/// in time are recorded as a gap for the next session.</para>
/// <para>An unexpected exception never ends <see cref="ExecuteAsync"/> (which would stop the host): it is logged as
/// Critical, the instance stands by and releases the lock, and the leadership loop starts over after
/// <see cref="RestartDelay"/>.</para>
/// </remarks>
internal sealed partial class IngestionWorker(
    ILeaderLock leaderLock,
    IStreamConnectionFactory connections,
    SymbolReconciler reconciler,
    TickBatchWriter batchWriter,
    GapFiller gapFiller,
    SymbolIdCache symbols,
    GapFillWatermarks watermarks,
    IMarketDataQueries queries,
    ISymbolChangeSignal signal,
    IngestionState state,
    IOptions<IngestionOptions> options,
    TimeProvider clock,
    ILoggerFactory loggerFactory,
    TickPulseMetrics metrics) : BackgroundService
{
    /// <summary>How long the end of a session waits for the remaining ticks to be written.</summary>
    /// <remarks>
    /// Bounded because the writer retries transient database failures indefinitely, and a session often ends exactly
    /// because the database is gone.
    /// </remarks>
    public static readonly TimeSpan FlushDeadline = TimeSpan.FromSeconds(10);

    /// <summary>Upper bound of the wait before the leadership loop starts over after an unexpected error.</summary>
    public static readonly TimeSpan MaxRestartDelay = TimeSpan.FromSeconds(30);

    /// <summary>How far back the start of a session looks for each symbol's last stored trade.</summary>
    /// <remarks>
    /// Well inside <see cref="TickWriter.ReplayWindow"/>, so every trade the gap-fill recovers is still insertable, and
    /// inside the uncompressed chunks (compression starts at two days), so the lookup is a primary-key probe of the
    /// newest one or two daily chunks per symbol. A symbol that stored nothing for longer starts like a new one.
    /// </remarks>
    public static readonly TimeSpan SeedWindow = TimeSpan.FromDays(1);

    private readonly ILogger _logger = loggerFactory.CreateLogger<IngestionWorker>();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        state.SetStandby();
        var unexpected = 0;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (await TryAcquireAsync(stoppingToken).ConfigureAwait(false))
                    {
                        await LeadAsync(stoppingToken).ConfigureAwait(false);
                    }

                    unexpected = 0;
                    await Task.Delay(options.Value.LeaderRetryInterval, clock, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    // A bug must not stop the host (an unhandled BackgroundService exception does): stand by, give the
                    // lock up, and try for leadership again after a growing, bounded delay.
                    var delay = RestartDelay(options.Value.LeaderRetryInterval, ++unexpected);
                    LogUnexpectedError(ex, delay.TotalSeconds);
                    state.SetStandby();
                    await ReleaseAfterFailureAsync().ConfigureAwait(false);
                    await Task.Delay(delay, clock, stoppingToken).ConfigureAwait(false);
                }
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

    /// <summary>
    /// The wait after the <paramref name="failures"/>-th unexpected error in a row: the leader retry interval, doubling,
    /// at most <see cref="MaxRestartDelay"/> (or the retry interval when that is longer).
    /// </summary>
    internal static TimeSpan RestartDelay(TimeSpan retryInterval, int failures)
    {
        var cap = retryInterval > MaxRestartDelay ? retryInterval : MaxRestartDelay;
        var delay = retryInterval * Math.Pow(2, Math.Clamp(failures - 1, 0, 16));
        return delay < cap ? delay : cap;
    }

    private async Task ReleaseAfterFailureAsync()
    {
        try
        {
            await leaderLock.ReleaseAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogCleanupFailed(ex);
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
            loggerFactory.CreateLogger<StreamSupervisor>(), channel.Writer, watermarks, metrics);
        metrics.TrackChannel(channel.Reader);
        var watchdog = WatchLeadershipAsync(session);
        try
        {
            // 0. Before anything streams: no instance received the stream while none led (a restart, redeploy or
            //    handover), so open a gap after each symbol's last stored trade. A failure ends the session; it is
            //    retried rather than streaming on with the gap unnoticed.
            await SeedWatermarksAsync(session.Token).ConfigureAwait(false);
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

            // Each step is guarded so a failing one cannot skip the rest, above all the release of the lock.
            try
            {
                await supervisor.StopAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogCleanupFailed(ex);
            }

            // 2. Complete the channel and write what it still holds, within the deadline.
            channel.Writer.TryComplete();
            await FlushAsync(writer, abandon).ConfigureAwait(false);
            metrics.TrackChannel(null);

            // 3. Close the connections, then give the lock up.
            try
            {
                await supervisor.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogCleanupFailed(ex);
            }

            await leaderLock.ReleaseAsync().ConfigureAwait(false);
            LogSessionEnded();
        }
    }

    private async Task SeedWatermarksAsync(CancellationToken ct)
    {
        var lastIds = await queries.GetLatestTradeIdsAsync(clock.GetUtcNow().UtcDateTime - SeedWindow, ct).ConfigureAwait(false);
        watermarks.Seed(lastIds);
        LogSeeded(lastIds.Count);
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Gap-fill resumes after the last stored trade of {Count} symbol(s)")]
    private partial void LogSeeded(int count);

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

    [LoggerMessage(Level = LogLevel.Critical, Message = "Ingestion hit an unexpected error; standing by and retrying in {DelaySeconds:F1} s")]
    private partial void LogUnexpectedError(Exception exception, double delaySeconds);

    [LoggerMessage(Level = LogLevel.Error, Message = "Cleaning up the ingestion session failed")]
    private partial void LogCleanupFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Ingestion stopped; leader lock released")]
    private partial void LogSessionEnded();
}

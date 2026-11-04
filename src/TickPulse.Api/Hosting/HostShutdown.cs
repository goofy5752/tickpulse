namespace TickPulse.Api.Hosting;

/// <summary>
/// How long a stop may take. Both compose files give the container <c>stop_grace_period: 30s</c>, and everything here
/// must fit inside it, or Docker kills the process (exit 137) before the leader lock is released and the logs are
/// flushed.
/// </summary>
/// <remarks>
/// <para>A normal stop takes about 2 s: the Binance WebSocket close handshake (about 1.4 s), the last tick batch and the
/// lock release. The worst case is the ingestion worker's: up to 2 × 2 s to close the stream connections (during a
/// rotation there are two), up to <c>IngestionWorker.FlushDeadline</c> (10 s) to write the ticks still in the channel,
/// then the release of the lock, which is at most <see cref="Timeout"/> = 20 s for all hosted services together.
/// After the host stops, the trace and metric pipelines get <c>TelemetrySetup.ShutdownFlushTimeout</c> (2 s, in
/// parallel) and the OTLP log sink <c>TelemetrySetup.LogFlushTimeout</c> (2 s), even when the collector is down. A normal
/// stop therefore ends within about 6 s, inside Docker's default 10 s, and the worst case (20 s + 4 s) stays under the
/// 30 s grace period.</para>
/// <para>Compose's own default is not the 10 s of <c>docker stop</c>: Compose v5 writes <c>StopTimeout=1</c> into
/// containers that have no <c>stop_grace_period</c>, which kills even a normal 2 s stop.</para>
/// </remarks>
internal static class HostShutdown
{
    /// <summary>The host's deadline for stopping all hosted services (the .NET default is 30 s).</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
}

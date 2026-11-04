using Serilog.Core;
using Serilog.Events;

namespace TickPulse.Api.Telemetry;

/// <summary>
/// Passes every event to the wrapped sink, and disposes it (which flushes what it still holds) on a worker thread,
/// waiting at most <paramref name="timeout"/>. A batching sink that cannot reach its endpoint otherwise retries each
/// queued batch until its request timeout while the process stops; whatever is not sent in time is lost.
/// </summary>
internal sealed class BoundedDisposalSink(ILogEventSink inner, TimeSpan timeout) : ILogEventSink, IDisposable
{
    public void Emit(LogEvent logEvent) => inner.Emit(logEvent);

    public void Dispose()
    {
        if (inner is not IDisposable disposable)
        {
            return;
        }

        var flush = Task.Run(disposable.Dispose);
        try
        {
            // false means the flush is abandoned; the worker thread ends with the process.
            _ = flush.Wait(timeout);
        }
        catch (AggregateException)
        {
            // A sink that fails while flushing loses the rest; stopping goes on.
        }
    }
}

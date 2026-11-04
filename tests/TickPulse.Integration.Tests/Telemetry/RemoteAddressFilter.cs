using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace TickPulse.Integration.Tests.Telemetry;

/// <summary>Gives every request a remote address (the test server has none).</summary>
internal sealed class RemoteAddressFilter(IPAddress address) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((HttpContext context, Func<Task> nextMiddleware) =>
        {
            context.Connection.RemoteIpAddress = address;
            return nextMiddleware();
        });
        next(app);
    };
}

/// <summary>Records every stopped activity of the given sources, whatever the app's own sampler decides.</summary>
internal sealed class SpanCapture : IDisposable
{
    private readonly ActivityListener _listener;
    private readonly System.Collections.Concurrent.ConcurrentQueue<Activity> _stopped = new();

    public SpanCapture(params string[] sources)
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => sources.Contains(source.Name),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = _stopped.Enqueue,
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public IReadOnlyList<Activity> Stopped => [.. _stopped];

    public IReadOnlyList<Activity> Named(string operationName) => [.. _stopped.Where(a => a.OperationName == operationName)];

    public void Dispose() => _listener.Dispose();
}

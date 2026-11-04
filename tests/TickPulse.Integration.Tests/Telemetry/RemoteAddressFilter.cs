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

/// <summary>
/// Records, for every request, whether its server span ends up sampled (exported), as seen from outside the whole
/// pipeline, after the request log has made its decision.
/// </summary>
internal sealed class RequestTraceRecorder : IStartupFilter
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<(string Path, int Status, bool Sampled)> _requests = new();

    public IReadOnlyList<(string Path, int Status, bool Sampled)> Requests => [.. _requests];

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (HttpContext context, Func<Task> nextMiddleware) =>
        {
            await nextMiddleware();
            var activity = context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpActivityFeature>()?.Activity;
            _requests.Enqueue((context.Request.Path.Value ?? string.Empty, context.Response.StatusCode, activity?.Recorded == true));
        });
        next(app);
    };
}

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using TickPulse.Api.Hosting;
using TickPulse.Api.Telemetry;
using TickPulse.Infrastructure.Ingestion;
using TickPulse.Integration.Tests.Fixtures;

namespace TickPulse.Integration.Tests.Api;

/// <summary>A stop fits the containers' <c>stop_grace_period: 30s</c> (asserted on the compose files elsewhere).</summary>
public sealed class HostShutdownTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    private static readonly TimeSpan StopGracePeriod = TimeSpan.FromSeconds(30);

    [Fact]
    public void Host_UsesTheShutdownTimeout()
    {
        api.Services.GetRequiredService<IOptions<HostOptions>>().Value.ShutdownTimeout.ShouldBe(HostShutdown.Timeout);
    }

    [Fact]
    public void WorstCaseStop_FitsTheGracePeriod()
    {
        // The ingestion worker's worst case: two stream closes (2 s each, during a rotation) and the flush deadline.
        (IngestionWorker.FlushDeadline + TimeSpan.FromSeconds(4)).ShouldBeLessThan(HostShutdown.Timeout);

        // After the hosted services: the trace and metric pipelines (in parallel), then the log sink.
        var telemetry = TelemetrySetup.ShutdownFlushTimeout + TelemetrySetup.LogFlushTimeout;
        (HostShutdown.Timeout + telemetry).ShouldBeLessThan(StopGracePeriod);
    }

    [Fact]
    public void NormalStop_FitsDockersDefaultTimeout_EvenWithTheCollectorDown()
    {
        // A normal stop of the hosted services takes about 2 s (measured); telemetry then waits at most this long.
        var telemetry = TelemetrySetup.ShutdownFlushTimeout + TelemetrySetup.LogFlushTimeout;
        (TimeSpan.FromSeconds(2) + telemetry).ShouldBeLessThan(TimeSpan.FromSeconds(10));
    }
}

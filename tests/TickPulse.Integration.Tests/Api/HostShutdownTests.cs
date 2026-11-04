using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using TickPulse.Api.Hosting;
using TickPulse.Api.Telemetry;
using TickPulse.Infrastructure.Ingestion;
using TickPulse.Integration.Tests.Fixtures;

namespace TickPulse.Integration.Tests.Api;

/// <summary>
/// The host's stop budget fits the grace period that each compose file gives the api: if either side changes (a longer
/// flush deadline, a shorter <c>stop_grace_period</c>), Docker would kill a stop that is still within its own deadlines.
/// </summary>
public sealed class HostShutdownTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    // The ingestion worker's slowest stop besides the flush: two stream closes (2 s each, during a rotation).
    private static readonly TimeSpan StreamCloses = TimeSpan.FromSeconds(4);

    [Fact]
    public void Host_UsesTheShutdownTimeout()
    {
        api.Services.GetRequiredService<IOptions<HostOptions>>().Value.ShutdownTimeout.ShouldBe(HostShutdown.Timeout);
    }

    [Fact]
    public void IngestionWorstCase_FitsTheHostsShutdownTimeout()
    {
        (IngestionWorker.FlushDeadline + StreamCloses).ShouldBeLessThan(HostShutdown.Timeout);
    }

    [Theory]
    [InlineData(ComposeFile.Local)]
    [InlineData(ComposeFile.Production)]
    public void WorstCaseStop_FitsTheApisStopGracePeriod(string file)
    {
        var grace = ComposeFile.Load(file).StopGracePeriod("api");
        grace.ShouldNotBeNull($"{file}: the api has no stop_grace_period (Compose then kills it after 1 s)");

        // The hosted services, then the trace and metric pipelines (in parallel), then the log sink.
        var stop = HostShutdown.Timeout + TelemetrySetup.ShutdownFlushTimeout + TelemetrySetup.LogFlushTimeout;
        stop.ShouldBeLessThan(grace.Value);
    }
}

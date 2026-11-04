using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting.Internal;
using Serilog;
using TickPulse.Api.Telemetry;
using TickPulse.Infrastructure.Telemetry;
using TickPulse.Integration.Tests.Fixtures;

namespace TickPulse.Integration.Tests.Telemetry;

/// <summary>
/// Stopping must not wait on a collector that is down: the container's stop grace period is finite, and a process
/// still flushing telemetry when it ends is killed (exit 137). Before the bounds, the log sink alone waited ~20 s for a
/// collector that did not answer (100 s for one that accepted and stayed silent) and the metric pipeline 5 s. The
/// bounded handler must still deliver to a collector that works.
/// </summary>
/// <remarks>
/// Each phase is held to its own bound plus <see cref="Slack"/>, so a slow CI machine does not fail them, while the
/// 5 s, 20 s and 100 s waits they guard against still do. The assembly runs its tests one at a time (TimescaleFixture).
/// </remarks>
public sealed class TelemetryShutdownTests
{
    private static readonly TimeSpan Slack = TimeSpan.FromSeconds(2);

    public static TheoryData<string> Collectors => ["silent", "http://otel-collector.invalid:4317"];

    [Theory]
    [MemberData(nameof(Collectors))]
    public void Dispose_WithACollectorThatDoesNotAnswer_EachPhaseStaysWithinItsBound(string collector)
    {
        using var silent = new SilentTcpServer();
        var endpoint = collector == "silent" ? silent.Endpoint : new Uri(collector);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [TelemetrySetup.EndpointKey] = endpoint.ToString() })
            .Build();
        var export = new OtlpExport(configuration, new HostingEnvironment { EnvironmentName = "Production" });
        var logger = TelemetrySetup.WriteToOtlp(new LoggerConfiguration(), endpoint, "test", "production").CreateLogger();

        using (var source = new ActivitySource(TickPulseMetrics.ActivitySourceName))
        using (source.StartActivity("probe"))
        {
            // A backlog of several batches, as a stop under load leaves behind.
            for (var i = 0; i < 5_000; i++)
            {
                logger.Information("probe {Index}", i);
            }
        }

        var clock = Stopwatch.StartNew();
        export.Dispose();
        var pipelines = clock.Elapsed;
        clock.Restart();
        logger.Dispose();
        var logSink = clock.Elapsed;

        pipelines.ShouldBeLessThan(TelemetrySetup.ShutdownFlushTimeout + Slack, "trace and metric pipelines");
        logSink.ShouldBeLessThan(TelemetrySetup.LogFlushTimeout + Slack, "OTLP log sink");
    }

    [Fact]
    public void ExportHandler_BoundsEveryRequest()
    {
        using var silent = new SilentTcpServer();
        using var client = new HttpClient(TelemetrySetup.CreateOtlpHandler());

        var clock = Stopwatch.StartNew();
        Should.Throw<OperationCanceledException>(() =>
            client.GetAsync(silent.Endpoint, TestContext.Current.CancellationToken).GetAwaiter().GetResult());

        clock.Elapsed.ShouldBeLessThan(TelemetrySetup.ExportRequestTimeout + Slack);
    }

    [Fact]
    public async Task Logs_ReachAWorkingCollector_WithTheirTraceId()
    {
        await using var collector = await OtlpGrpcReceiver.StartAsync();
        var logger = TelemetrySetup.WriteToOtlp(new LoggerConfiguration(), collector.Endpoint, "test", "production").CreateLogger();
        var marker = "otlp-probe-" + Guid.NewGuid().ToString("N");
        var traceId = new byte[16];

        using (var activity = new Activity("probe").SetIdFormat(ActivityIdFormat.W3C).Start())
        {
            activity.TraceId.CopyTo(traceId);
            logger.Information("Delivered {Marker}", marker);
        }

        var clock = Stopwatch.StartNew();
        logger.Dispose(); // the final flush sends the batch
        clock.Elapsed.ShouldBeLessThan(TelemetrySetup.LogFlushTimeout, "a working collector is not cut off by the bound");

        var exports = collector.Calls.Where(c => c.Path == OtlpGrpcReceiver.LogsExportPath).ToList();
        exports.ShouldNotBeEmpty("no OTLP log export reached the collector");
        var delivered = exports.Single(c => c.Contains(Encoding.UTF8.GetBytes(marker)));
        delivered.Contains(traceId).ShouldBeTrue("the log record carries the trace id of the activity it was written in");
        delivered.Contains("tickpulse-api"u8).ShouldBeTrue("the resource carries service.name");
    }
}

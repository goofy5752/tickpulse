using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting.Internal;
using Serilog;
using TickPulse.Api.Telemetry;
using TickPulse.Infrastructure.Telemetry;

namespace TickPulse.Integration.Tests.Telemetry;

/// <summary>
/// Stopping must not wait on a collector that is down: the container's stop grace period is finite, and a process
/// still flushing telemetry when it ends is killed (exit 137). Before the bound, the log sink alone waited ~20 s for a
/// collector that did not answer and the metric pipeline 5 s.
/// </summary>
public sealed class TelemetryShutdownTests
{
    // Telemetry shutdown budget: the trace and metric pipelines (in parallel), then the log sink, with slack for a slow
    // CI machine.
    private static readonly TimeSpan Budget = TelemetrySetup.ShutdownFlushTimeout + TelemetrySetup.LogFlushTimeout + TimeSpan.FromSeconds(1.5);

    public static TheoryData<string> Collectors => ["silent", "http://otel-collector.invalid:4317"];

    [Theory]
    [MemberData(nameof(Collectors))]
    public void Dispose_WithACollectorThatDoesNotAnswer_FinishesWithinTheBudget(string collector)
    {
        using var silent = new SilentCollector();
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
        logger.Dispose();

        clock.Elapsed.ShouldBeLessThan(Budget, $"pipelines {pipelines.TotalSeconds:F1} s, log sink {(clock.Elapsed - pipelines).TotalSeconds:F1} s");
    }

    [Fact]
    public void ExportHandler_BoundsEveryRequest()
    {
        using var silent = new SilentCollector();
        using var client = new HttpClient(TelemetrySetup.CreateOtlpHandler());

        var clock = Stopwatch.StartNew();
        Should.Throw<OperationCanceledException>(() =>
            client.GetAsync(silent.Endpoint, TestContext.Current.CancellationToken).GetAwaiter().GetResult());

        clock.Elapsed.ShouldBeLessThan(TelemetrySetup.ExportRequestTimeout + TimeSpan.FromSeconds(2));
    }

    /// <summary>Accepts TCP connections on loopback and never sends a byte back.</summary>
    private sealed class SilentCollector : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly List<TcpClient> _clients = [];
        private readonly CancellationTokenSource _stop = new();

        public SilentCollector()
        {
            _listener.Start();
            _ = AcceptAsync();
        }

        public Uri Endpoint => new($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}");

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            lock (_clients)
            {
                _clients.ForEach(c => c.Dispose());
            }

            _stop.Dispose();
        }

        private async Task AcceptAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    lock (_clients)
                    {
                        _clients.Add(client);
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                // Stopped.
            }
        }
    }
}

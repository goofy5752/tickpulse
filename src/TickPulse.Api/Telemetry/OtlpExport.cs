using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using TickPulse.Api.Health;
using TickPulse.Infrastructure.Telemetry;

namespace TickPulse.Api.Telemetry;

/// <summary>
/// The OpenTelemetry trace and metric pipelines with their OTLP exporters, built only when <c>Otel:Endpoint</c> is
/// set (and not blank). <see cref="Tracing"/> and <see cref="Metrics"/> are <see langword="null"/> otherwise, so
/// without a collector no span is recorded and no metric is aggregated.
/// </summary>
/// <remarks>
/// <para>Built from the host's final configuration when the singleton is first resolved (right after the app is
/// built), not from the configuration that is visible while the host is still being composed.</para>
/// <para>Exports run on background threads. An unreachable or unresolvable collector only makes them drop data,
/// reported through the SDK's own diagnostics and never through the application log, so requests never wait for
/// them.</para>
/// </remarks>
internal sealed class OtlpExport : IDisposable
{
    public OtlpExport(IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (TelemetrySetup.Endpoint(configuration) is not { } endpoint)
        {
            return;
        }

        Endpoint = endpoint;
        var resource = ResourceBuilder.CreateDefault()
            .AddService(TelemetrySetup.ServiceName, serviceVersion: TelemetrySetup.ServiceVersion())
            .AddAttributes([new("deployment.environment", TelemetrySetup.EnvironmentName(environment))]);

        Tracing = Sdk.CreateTracerProviderBuilder()
            .SetResourceBuilder(resource)
            .SetSampler(TickPulseSampler.Create(TelemetrySetup.FlushSampleRatio(configuration)))
            .AddSource(TickPulseMetrics.ActivitySourceName)
            .AddAspNetCoreInstrumentation(o =>
            {
                // Container health checks poll every few seconds; their traces would only be noise.
                o.Filter = context => !context.Request.Path.StartsWithSegments(HealthEndpoints.PathPrefix);
                // Never store the client address or the query string (it may carry anything a client typed).
                o.EnrichWithHttpRequest = (activity, _) =>
                {
                    activity.SetTag("client.address", null);
                    activity.SetTag("url.query", null);
                };
            })
            .AddHttpClientInstrumentation()
            .AddNpgsql()
            .AddOtlpExporter(o => o.Endpoint = endpoint)
            .Build();

        Metrics = Sdk.CreateMeterProviderBuilder()
            .SetResourceBuilder(resource)
            .AddMeter(TickPulseMetrics.MeterName, "Microsoft.AspNetCore.RateLimiting")
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddRuntimeInstrumentation()
            .AddNpgsqlInstrumentation()
            .AddOtlpExporter((o, reader) =>
            {
                o.Endpoint = endpoint;
                reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = (int)TelemetrySetup.MetricExportInterval.TotalMilliseconds;
            })
            .Build();
    }

    /// <summary>The collector, or <see langword="null"/> when export is off.</summary>
    public Uri? Endpoint { get; }

    public TracerProvider? Tracing { get; }

    public MeterProvider? Metrics { get; }

    /// <summary>
    /// Flushes what is pending and stops both pipelines in parallel, waiting at most
    /// <see cref="TelemetrySetup.ShutdownFlushTimeout"/>, so a collector that is down cannot hold the stopping process
    /// (the providers' own wait is 5 s each, plus whatever an export in flight takes).
    /// </summary>
    public void Dispose()
    {
        var timeout = (int)TelemetrySetup.ShutdownFlushTimeout.TotalMilliseconds;
        Parallel.Invoke(() => Tracing?.Shutdown(timeout), () => Metrics?.Shutdown(timeout));
        Tracing?.Dispose();
        Metrics?.Dispose();
    }
}

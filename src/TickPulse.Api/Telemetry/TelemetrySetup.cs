using System.Globalization;
using System.Reflection;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;
using Serilog.Sinks.OpenTelemetry;
using TickPulse.Api.Health;
using TickPulse.Api.Hosting;

namespace TickPulse.Api.Telemetry;

/// <summary>
/// Logs, metrics and traces. Serilog writes JSON to the console and, when <c>Otel:Endpoint</c> is set, OTLP to the
/// collector; the OpenTelemetry SDK exports traces and metrics to the same endpoint. Without an endpoint (unset or
/// empty) no exporter exists and the app runs exactly the same.
/// </summary>
/// <remarks>
/// <para>Exporting never affects requests: the exporters batch in the background, drop what they cannot deliver
/// (an unreachable or unresolvable collector) and report that only through their own diagnostics, never through the
/// application log.</para>
/// <para>Names follow the telemetry contract in <c>deploy/observability/README.md</c>: <c>service.name</c> is
/// <see cref="ServiceName"/>, and metrics are exported every <see cref="MetricExportInterval"/>.</para>
/// </remarks>
internal static class TelemetrySetup
{
    public const string ServiceName = "tickpulse-api";

    /// <summary>OTLP/gRPC endpoint of the collector, e.g. <c>http://otel-collector:4317</c>.</summary>
    public const string EndpointKey = "Otel:Endpoint";

    public static readonly TimeSpan MetricExportInterval = TimeSpan.FromSeconds(15);

    private const string RequestLogTemplate = "HTTP {RequestMethod:l} {RequestPath:l} responded {StatusCode} in {Elapsed:0.0} ms";

    public static WebApplicationBuilder AddTickPulseTelemetry(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var version = ServiceVersion();
        var environment = EnvironmentName(builder.Environment);

        // Serilog is the only logging pipeline. Providers added later (tests) still receive every event. Its sinks
        // are configured from the host's final configuration when the logger is first resolved.
        builder.Logging.ClearProviders();
        builder.Services.AddSerilog(
            (services, logger) => ConfigureLogging(logger, services, version, environment),
            preserveStaticLogger: true,
            writeToProviders: true);
        builder.Services.AddSingleton<OtlpExport>();
        return builder;
    }

    /// <summary>
    /// Starts the trace and metric export (when configured), then adds the request log and the API response counter.
    /// Call right after <c>UseForwardedHeaders</c>, so both see the real client, and before everything else, so both
    /// see the final status.
    /// </summary>
    public static WebApplication UseTickPulseTelemetry(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        _ = app.Services.GetRequiredService<OtlpExport>();
        app.UseTickPulseRequestLogging();
        app.UseMiddleware<ApiMetricsMiddleware>();
        return app;
    }

    /// <summary>The <c>deployment.environment</c> value: the host environment in lower case.</summary>
    public static string EnvironmentName(IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return environment.EnvironmentName.ToLowerInvariant();
    }

    /// <summary>
    /// One structured log line per request with <c>client_ip_hash</c> (never the address) and the route template.
    /// Health probes log at Debug, server errors at Error.
    /// </summary>
    private static IApplicationBuilder UseTickPulseRequestLogging(this IApplicationBuilder app)
    {
        var hasher = app.ApplicationServices.GetRequiredService<ClientIpHasher>();
        return app.UseSerilogRequestLogging(o =>
        {
            // The host's own logger, not the static one: several hosts may share a process (tests).
            o.Logger = app.ApplicationServices.GetRequiredService<Serilog.ILogger>();
            o.MessageTemplate = RequestLogTemplate;
            o.GetLevel = (context, _, exception) =>
                exception is not null || context.Response.StatusCode >= StatusCodes.Status500InternalServerError ? LogEventLevel.Error
                : context.Request.Path.StartsWithSegments(HealthEndpoints.PathPrefix) ? LogEventLevel.Debug
                : LogEventLevel.Information;
            o.EnrichDiagnosticContext = (diagnostics, context) =>
            {
                diagnostics.Set("client_ip_hash", hasher.Hash(ClientAddressing.ClientAddress(context)));
                if (ApiMetricsMiddleware.Route(context) is { } route)
                {
                    diagnostics.Set("route", route);
                }
            };
        });
    }

    /// <summary>The collector endpoint, or <see langword="null"/> when <c>Otel:Endpoint</c> is unset or blank.</summary>
    /// <exception cref="InvalidOperationException">The value is not an absolute http(s) URL.</exception>
    public static Uri? Endpoint(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var raw = configuration[EndpointKey];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri
            : throw new InvalidOperationException(string.Create(
                CultureInfo.InvariantCulture,
                $"{EndpointKey} must be an absolute http(s) URL such as http://otel-collector:4317, or empty to disable export."));
    }

    /// <summary>
    /// The share of <c>ingest.flush</c> traces kept (<c>Otel:FlushSampleRatio</c>, 0 to 1, invariant culture), or
    /// <see cref="TickPulseSampler.DefaultFlushRatio"/> when unset or blank.
    /// </summary>
    /// <exception cref="InvalidOperationException">The value is not a number from 0 to 1.</exception>
    public static double FlushSampleRatio(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var raw = configuration[TickPulseSampler.FlushRatioKey];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return TickPulseSampler.DefaultFlushRatio;
        }

        return double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var ratio) && ratio is >= 0 and <= 1
            ? ratio
            : throw new InvalidOperationException(string.Create(
                CultureInfo.InvariantCulture,
                $"{TickPulseSampler.FlushRatioKey} must be a number from 0 to 1, such as 0.1."));
    }

    /// <summary>The informational version, with build metadata (the commit) cut to 7 characters.</summary>
    public static string ServiceVersion()
    {
        var version = typeof(TelemetrySetup).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        return plus >= 0 && version.Length > plus + 8 ? version[..(plus + 8)] : version;
    }

    private static void ConfigureLogging(LoggerConfiguration logger, IServiceProvider services, string version, string environment)
    {
        var configuration = services.GetRequiredService<IConfiguration>();
        logger
            .MinimumLevel.Information()
            .ReadFrom.Configuration(configuration) // Serilog:MinimumLevel (Default and per-category overrides)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .WriteTo.Logger(console => console
                .Enrich.With(new TickPulseLogEnricher(ServiceName, version, environment))
                .WriteTo.Console(new RenderedCompactJsonFormatter()));

        if (Endpoint(configuration) is { } endpoint)
        {
            WriteToOtlp(logger, endpoint, version, environment);
        }
    }

    /// <summary>The OTLP log sink, whose final flush on dispose takes at most <see cref="LogFlushTimeout"/>.</summary>
    /// <remarks>
    /// The sink lives in its own sub-logger, which owns its batching and export queue. Disposing that sub-logger flushes
    /// all of it, so bounding that one dispose bounds the whole flush. The parent logger has already applied the levels.
    /// </remarks>
    internal static LoggerConfiguration WriteToOtlp(LoggerConfiguration logger, Uri endpoint, string version, string environment)
    {
        ArgumentNullException.ThrowIfNull(logger);
        var otlp = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.OpenTelemetry(o => ConfigureOtlpLogs(o, endpoint, version, environment))
            .CreateLogger();
        return logger.WriteTo.Sink(new BoundedDisposalSink(otlp, LogFlushTimeout));
    }

    /// <summary>
    /// How long stopping waits for the trace and metric pipelines (in parallel) to flush. A collector that is down or
    /// does not resolve must not hold the process past the container's stop grace period; what is not sent by then is lost.
    /// </summary>
    internal static readonly TimeSpan ShutdownFlushTimeout = TimeSpan.FromSeconds(2);

    /// <summary>How long stopping waits for the OTLP log sink to flush.</summary>
    internal static readonly TimeSpan LogFlushTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Upper bound on one OTLP log export, connection included. Exports to a healthy collector take milliseconds.</summary>
    internal static readonly TimeSpan ExportRequestTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Upper bound on resolving and connecting to the collector.</summary>
    internal static readonly TimeSpan ExportConnectTimeout = TimeSpan.FromSeconds(2);

    /// <summary>The Serilog OTLP sink: gRPC to <paramref name="endpoint"/>, every request bounded (<see cref="CreateOtlpHandler"/>).</summary>
    internal static void ConfigureOtlpLogs(BatchedOpenTelemetrySinkOptions options, Uri endpoint, string version, string environment)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(endpoint);
        options.Endpoint = endpoint.ToString().TrimEnd('/');
        options.Protocol = OtlpProtocol.Grpc;
        // Without it the sink waits up to the HttpClient default (100 s) per export, also while the process stops.
        options.HttpMessageHandler = CreateOtlpHandler();
        options.ResourceAttributes = new Dictionary<string, object>
        {
            ["service.name"] = ServiceName,
            ["service.version"] = version,
            ["deployment.environment"] = environment,
        };
    }

    /// <summary>
    /// A handler that gives every request <see cref="ExportRequestTimeout"/> and every connection attempt (DNS included)
    /// <see cref="ExportConnectTimeout"/>.
    /// </summary>
    internal static HttpMessageHandler CreateOtlpHandler() => new TimeoutHandler(ExportRequestTimeout, TimeProvider.System)
    {
        InnerHandler = new SocketsHttpHandler { ConnectTimeout = ExportConnectTimeout },
    };

    private sealed class TimeoutHandler(TimeSpan timeout, TimeProvider clock) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var deadline = new CancellationTokenSource(timeout, clock);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
            return await base.SendAsync(request, linked.Token).ConfigureAwait(false);
        }
    }
}

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Compact;
using Serilog.Parsing;
using TickPulse.Api.Telemetry;
using TickPulse.Integration.Tests.Fixtures;
using static TickPulse.Integration.Tests.Ingestion.IngestionTestHost;

namespace TickPulse.Integration.Tests.Telemetry;

/// <summary>
/// How the host wires telemetry: structured request logs that carry <c>client_ip_hash</c> and never a raw IP or a
/// secret, the console enrichment, and OTLP exporters that exist only when <c>Otel:Endpoint</c> is set.
/// </summary>
public sealed class TelemetryHostTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    private const string RequestLogContext = "Serilog.AspNetCore.RequestLoggingMiddleware";
    private const string Secret = "k3y-for-the-log-test-6c1f0a9e5b7d4e21";
    private const string WrongSecret = "wrong-key-for-the-log-test-93b2e8d1";
    private static readonly IPAddress ClientIp = IPAddress.Parse("203.0.113.77");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RequestLog_ContainsIpHash_NotRawIp()
    {
        var sink = new CapturingSink();
        await using var host = LoggedHost(sink);
        await host.InitializeAsync();
        using var client = host.CreateClient();

        (await client.GetAsync("/api/symbols", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var entry = await RequestLogAsync(sink, "/api/symbols");
        var expected = host.Services.GetRequiredService<ClientIpHasher>().Hash(ClientIp);
        entry.Properties["client_ip_hash"].ShouldBe(new ScalarValue(expected));
        entry.Properties["StatusCode"].ShouldBe(new ScalarValue(200));
        entry.Properties["route"].ShouldBe(new ScalarValue("api/symbols"));
        entry.Level.ShouldBe(LogEventLevel.Information);
        sink.Rendered().ShouldNotContain(line => line.Contains(ClientIp.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Logs_NeverContainTheApiKeyOrTheRawIp()
    {
        var sink = new CapturingSink();
        await using var host = LoggedHost(sink).WithSetting("Admin:ApiKey", Secret);
        await host.InitializeAsync();
        using var client = host.CreateClient();

        (await SendAdminAsync(client, Secret)).ShouldBe(HttpStatusCode.OK);
        (await SendAdminAsync(client, WrongSecret)).ShouldBe(HttpStatusCode.Unauthorized);
        (await SendAdminAsync(client, null)).ShouldBe(HttpStatusCode.Unauthorized);
        (await client.GetAsync("/api/NOPEUSDT/24hAvgPrice?token=" + WrongSecret, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        await WaitForAsync(() => sink.Events.Count(e => IsRequestLog(e)) >= 4, "four request logs");
        var rendered = sink.Rendered();
        rendered.ShouldNotBeEmpty();
        rendered.ShouldNotContain(line => line.Contains(Secret, StringComparison.Ordinal));
        rendered.ShouldNotContain(line => line.Contains(WrongSecret, StringComparison.Ordinal));
        rendered.ShouldNotContain(line => line.Contains(ClientIp.ToString(), StringComparison.Ordinal));

        // The failed attempts are still visible, tied to the hashed client.
        var hash = host.Services.GetRequiredService<ClientIpHasher>().Hash(ClientIp);
        sink.Events.Where(IsRequestLog).ShouldAllBe(e => e.Properties["client_ip_hash"].Equals(new ScalarValue(hash)));
        sink.Events.Count(e => e.MessageTemplate.Text.StartsWith("Admin authentication failed", StringComparison.Ordinal)).ShouldBe(2);
    }

    [Fact]
    public void ConsoleEnricher_AddsServiceAndTraceContext()
    {
        var traceId = ActivityTraceId.CreateRandom();
        var spanId = ActivitySpanId.CreateRandom();
        var entry = new LogEvent(
            DateTimeOffset.UnixEpoch, LogEventLevel.Information, null, new MessageTemplate("hello", []), [], traceId, spanId);

        new TickPulseLogEnricher("tickpulse-api", "1.2.3", "production").Enrich(entry, new PropertyFactory());

        entry.Properties["service.name"].ShouldBe(new ScalarValue("tickpulse-api"));
        entry.Properties["service.version"].ShouldBe(new ScalarValue("1.2.3"));
        entry.Properties["deployment.environment"].ShouldBe(new ScalarValue("production"));
        entry.Properties["trace_id"].ShouldBe(new ScalarValue(traceId.ToHexString()));
        entry.Properties["span_id"].ShouldBe(new ScalarValue(spanId.ToHexString()));
    }

    [Fact]
    public void ConsoleEnricher_WithoutTraceContext_AddsNoTraceIds()
    {
        var entry = new LogEvent(DateTimeOffset.UnixEpoch, LogEventLevel.Information, null, new MessageTemplate("hello", []), []);

        new TickPulseLogEnricher("tickpulse-api", "1.2.3", "production").Enrich(entry, new PropertyFactory());

        entry.Properties.ShouldNotContainKey("trace_id");
        entry.Properties.ShouldNotContainKey("span_id");
    }

    [Fact]
    public void OtelEndpointUnset_RegistersNoExporters()
    {
        api.Services.GetRequiredService<OtlpExport>().Metrics.ShouldBeNull();
        api.Services.GetRequiredService<OtlpExport>().Tracing.ShouldBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task OtelEndpointEmpty_CountsAsUnset(string endpoint)
    {
        await using var host = new ApiFactory(api.Database).WithSetting("Otel:Endpoint", endpoint);
        await host.InitializeAsync();
        using var client = host.CreateClient();

        (await client.GetAsync("/health/ready", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        host.Services.GetRequiredService<OtlpExport>().Metrics.ShouldBeNull();
        host.Services.GetRequiredService<OtlpExport>().Tracing.ShouldBeNull();
    }

    [Theory]
    [InlineData("http://127.0.0.1:9")] // nothing listens
    [InlineData("http://otel-collector.invalid:4317")] // does not resolve, like compose without the observability profile
    public async Task OtelEndpointUnreachable_StartsWithExportersAndServes(string endpoint)
    {
        var sink = new CapturingSink();
        await using var host = LoggedHost(sink).WithSetting("Otel:Endpoint", endpoint).WithSetting("Admin:ApiKey", Secret);
        await host.InitializeAsync();
        using var client = host.CreateClient();

        var timer = Stopwatch.StartNew();
        for (var i = 0; i < 20; i++)
        {
            (await client.GetAsync("/api/symbols", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        (await client.GetAsync("/health/ready", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        timer.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10), "requests never wait for the exporters");
        var export = host.Services.GetRequiredService<OtlpExport>();
        export.Endpoint.ShouldBe(new Uri(endpoint));
        export.Metrics.ShouldNotBeNull();
        export.Tracing.ShouldNotBeNull();

        // A forced flush hits the dead endpoint; the failure stays out of the application log.
        export.Metrics.ForceFlush(5000);
        export.Tracing.ForceFlush(5000);
        sink.Events.Where(e => e.Level >= LogEventLevel.Warning).Select(e => e.RenderMessage()).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("Npgsql.Command", false)]
    [InlineData("System.Net.Http.HttpClient.Binance.LogicalHandler", false)]
    [InlineData("Polly", false)]
    [InlineData("Microsoft.Extensions.Http.Resilience.ResilienceHandler", false)]
    [InlineData("Microsoft.AspNetCore.Hosting.Diagnostics", false)]
    [InlineData("Microsoft.EntityFrameworkCore.Database.Command", false)]
    [InlineData("TickPulse.Infrastructure.Backfill.BackfillWorker", true)]
    [InlineData("Serilog.AspNetCore.RequestLoggingMiddleware", true)]
    public void LogLevels_QuietTheChattyLibraries(string category, bool information)
    {
        var logger = api.Services.GetRequiredService<Serilog.ILogger>().ForContext(Constants.SourceContextPropertyName, category);

        logger.IsEnabled(LogEventLevel.Information).ShouldBe(information);
        logger.IsEnabled(LogEventLevel.Warning).ShouldBeTrue();
    }

    [Fact]
    public async Task OtelEndpointInvalid_FailsStartup()
    {
        await using var host = new ApiFactory(api.Database).WithSetting("Otel:Endpoint", "not a url");
        await host.InitializeAsync();

        Should.Throw<Exception>(() => host.Services).Message.ShouldContain("Otel:Endpoint");
    }

    private static bool IsRequestLog(LogEvent entry) =>
        entry.Properties.TryGetValue("SourceContext", out var context) && context.Equals(new ScalarValue(RequestLogContext));

    private static async Task<LogEvent> RequestLogAsync(CapturingSink sink, string path)
    {
        LogEvent? found = null;
        await WaitForAsync(
            () => (found = sink.Events.FirstOrDefault(e => IsRequestLog(e) && e.Properties["RequestPath"].Equals(new ScalarValue(path)))) is not null,
            "the request log of " + path);
        return found!;
    }

    private static async Task<HttpStatusCode> SendAdminAsync(HttpClient client, string? key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/symbols");
        if (key is not null)
        {
            request.Headers.Add("X-Api-Key", key);
        }

        using var response = await client.SendAsync(request, Ct);
        return response.StatusCode;
    }

    // A host whose requests come from ClientIp and whose log events also reach the sink.
    private ApiFactory LoggedHost(CapturingSink sink) =>
        new ApiFactory(api.Database).WithServices(services =>
        {
            services.AddSingleton<ILogEventSink>(sink);
            services.AddSingleton<IStartupFilter>(new RemoteAddressFilter(ClientIp));
        });

    /// <summary>Records every log event; <see cref="Rendered"/> formats them as the JSON console sink does.</summary>
    private sealed class CapturingSink : ILogEventSink
    {
        private readonly ConcurrentQueue<LogEvent> _events = new();

        public IReadOnlyList<LogEvent> Events => [.. _events];

        public void Emit(LogEvent logEvent) => _events.Enqueue(logEvent);

        public IReadOnlyList<string> Rendered()
        {
            var formatter = new RenderedCompactJsonFormatter();
            return [.. Events.Select(e =>
            {
                using var writer = new StringWriter();
                formatter.Format(e, writer);
                return writer.ToString() + e.Exception;
            })];
        }
    }

    private sealed class PropertyFactory : ILogEventPropertyFactory
    {
        public LogEventProperty CreateProperty(string name, object? value, bool destructureObjects = false) =>
            new(name, new ScalarValue(value));
    }

    /// <summary>Gives every request a remote address (the test server has none).</summary>
    private sealed class RemoteAddressFilter(IPAddress address) : IStartupFilter
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
}

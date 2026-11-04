using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Threading.Channels;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TickPulse.Application.Abstractions;
using TickPulse.Application.Ingestion;
using TickPulse.Domain;
using TickPulse.Infrastructure.Ingestion;
using TickPulse.Infrastructure.Telemetry;
using TickPulse.Integration.Tests.Fixtures;
using static TickPulse.Integration.Tests.Ingestion.IngestionTestHost;

namespace TickPulse.Integration.Tests.Telemetry;

/// <summary>What the spans carry: the <c>ingest.flush</c> attributes and status, and a server span without the client
/// address or the query string.</summary>
public sealed class SpanTests(ApiFactory api) : IClassFixture<ApiFactory>, IDisposable
{
    private const int Btc = 1;
    private static readonly DateTime T0 = new(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);

    private readonly ServiceProvider _meters = new ServiceCollection().AddMetrics().BuildServiceProvider();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(T0.AddSeconds(2)));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _meters.Dispose();

    [Fact]
    public async Task Flush_SpanCarriesSizeStoredAndAttempts()
    {
        using var spans = new SpanCapture(TickPulseMetrics.ActivitySourceName);

        // Trade 1 is already stored: 3 ticks in the batch, 2 new.
        await RunBatchAsync(new StubTickWriter(alreadyStored: 1), Tick(1), Tick(2), Tick(3));

        var span = spans.Named(TickPulseMetrics.FlushSpan).ShouldHaveSingleItem();
        span.GetTagItem("tickpulse.batch.size").ShouldBe(3);
        span.GetTagItem("tickpulse.batch.stored").ShouldBe(2);
        span.GetTagItem("tickpulse.batch.attempts").ShouldBe(1);
        span.Status.ShouldBe(ActivityStatusCode.Unset);
    }

    [Fact]
    public async Task Flush_DroppedBatch_SpanHasErrorStatus()
    {
        using var spans = new SpanCapture(TickPulseMetrics.ActivitySourceName);

        await RunBatchAsync(new StubTickWriter(failure: new InvalidOperationException("not retryable")), Tick(1), Tick(2));

        var span = spans.Named(TickPulseMetrics.FlushSpan).ShouldHaveSingleItem();
        span.Status.ShouldBe(ActivityStatusCode.Error);
        span.GetTagItem("tickpulse.batch.size").ShouldBe(2);
        span.GetTagItem("tickpulse.batch.stored").ShouldBeNull();
    }

    [Fact]
    public async Task ServerSpan_HasNoClientAddressOrQuery()
    {
        using var spans = new SpanCapture("Microsoft.AspNetCore");
        await using var host = new ApiFactory(api.Database)
            .WithSetting("Otel:Endpoint", "http://127.0.0.1:9") // the trace pipeline exists; nothing receives
            .WithSetting("Admin:ApiKey", "span-test-key-0123456789abcdef0123")
            .WithServices(s => s.AddSingleton<IStartupFilter>(new RemoteAddressFilter(IPAddress.Parse("198.51.100.9"))));
        await host.InitializeAsync();
        using var client = host.CreateClient();

        (await client.GetAsync("/api/symbols?token=span-secret-value", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        Activity? server = null;
        await WaitForAsync(
            () => (server = spans.Stopped.FirstOrDefault(a => a.Kind == ActivityKind.Server && Equals(a.GetTagItem("http.route"), "api/symbols"))) is not null,
            "the server span of /api/symbols");
        var tags = server!.TagObjects.ToDictionary(t => t.Key, t => t.Value);
        tags.ShouldContainKey("http.request.method"); // enriched by the OpenTelemetry instrumentation
        tags.ShouldNotContainKey("client.address");
        tags.ShouldNotContainKey("url.query");
        tags.Values.Select(v => v?.ToString() ?? string.Empty).ShouldNotContain(v => v.Contains("span-secret-value", StringComparison.Ordinal));
        tags.Values.Select(v => v?.ToString() ?? string.Empty).ShouldNotContain(v => v.Contains("198.51.100.9", StringComparison.Ordinal));
    }

    private static Tick Tick(long id) => new(Btc, id, 100m + id, 1m, T0.AddMilliseconds(id), T0.AddMilliseconds(id));

    private async Task RunBatchAsync(ITickWriter writer, params Tick[] ticks)
    {
        var state = new IngestionState(_clock);
        var symbols = new SymbolIdCache();
        symbols.Update([new MonitoredSymbol(Btc, "BTCUSDT", true, T0, null, BackfillStatus.NotRequested, null)]);
        var channel = Channel.CreateUnbounded<Tick>();
        foreach (var tick in ticks)
        {
            channel.Writer.TryWrite(tick);
        }

        channel.Writer.Complete();
        var batchWriter = new TickBatchWriter(
            writer,
            state,
            symbols,
            new NoWatermark(),
            Options.Create(new IngestionOptions { BatchMaxSize = 5000, BatchMaxDelay = TimeSpan.FromMilliseconds(500) }),
            _clock,
            NullLogger<TickBatchWriter>.Instance,
            new TickPulseMetrics(_meters.GetRequiredService<IMeterFactory>(), state, symbols));
        await batchWriter.RunAsync(channel.Reader, Ct);
    }

    private sealed class NoWatermark : IGapFillWatermark
    {
        public void Lower(string symbol, long firstMissingId)
        {
        }
    }

    /// <summary>Treats trade ids up to <c>alreadyStored</c> as replays, or throws <c>failure</c>.</summary>
    private sealed class StubTickWriter(long alreadyStored = 0, Exception? failure = null) : ITickWriter
    {
        public Task<int> WriteBatchAsync(IReadOnlyList<Tick> ticks, IDictionary<int, int> insertedPerSymbol, CancellationToken ct)
        {
            if (failure is not null)
            {
                throw failure;
            }

            insertedPerSymbol.Clear();
            var inserted = ticks.Count(t => t.AggTradeId > alreadyStored);
            insertedPerSymbol[Btc] = inserted;
            return Task.FromResult(inserted);
        }
    }
}

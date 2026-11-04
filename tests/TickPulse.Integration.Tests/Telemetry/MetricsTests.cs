using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TickPulse.Application.Abstractions;
using TickPulse.Application.Ingestion;
using TickPulse.Domain;
using TickPulse.Infrastructure.Binance;
using TickPulse.Infrastructure.Ingestion;
using TickPulse.Infrastructure.Telemetry;
using TickPulse.Integration.Tests.Fixtures;
using static TickPulse.Integration.Tests.Ingestion.IngestionTestHost;

namespace TickPulse.Integration.Tests.Telemetry;

/// <summary>
/// The <c>TickPulse</c> meter: the instrument names and units the dashboards and alerts depend on
/// (<c>deploy/observability/README.md</c>), and what the ingestion pipeline and the API record into them.
/// </summary>
public sealed class MetricsTests(ApiFactory api) : IClassFixture<ApiFactory>, IDisposable
{
    private const int Btc = 1;
    private const int Eth = 3;
    private static readonly DateTime T0 = new(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);

    private readonly ServiceProvider _meters = new ServiceCollection().AddMetrics().BuildServiceProvider();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(T0.AddSeconds(2)));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IMeterFactory MeterFactory => _meters.GetRequiredService<IMeterFactory>();

    public void Dispose() => _meters.Dispose();

    [Fact]
    public void Instruments_MatchTheNamingContract()
    {
        var published = new ConcurrentDictionary<string, Instrument>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, _) =>
            {
                if (instrument.Meter.Name == TickPulseMetrics.MeterName && ReferenceEquals(instrument.Meter.Scope, MeterFactory))
                {
                    published[instrument.Name] = instrument;
                }
            },
        };
        listener.Start();

        _ = NewMetrics(new IngestionState(_clock), new SymbolIdCache());

        var contract = new Dictionary<string, (Type Kind, string? Unit)>
        {
            ["tickpulse.ingest.ticks.received"] = (typeof(Counter<long>), "{tick}"),
            ["tickpulse.ingest.ticks.persisted"] = (typeof(Counter<long>), "{tick}"),
            ["tickpulse.ingest.lag"] = (typeof(Histogram<double>), "ms"),
            ["tickpulse.ingest.channel.depth"] = (typeof(ObservableGauge<int>), "{tick}"),
            ["tickpulse.ingest.batch.size"] = (typeof(Histogram<long>), "{tick}"),
            ["tickpulse.ingest.batch.duration"] = (typeof(Histogram<double>), "s"),
            ["tickpulse.ingest.symbol.active"] = (typeof(ObservableGauge<int>), null),
            ["tickpulse.ws.connections"] = (typeof(Counter<long>), "{connection}"),
            ["tickpulse.ws.state"] = (typeof(ObservableGauge<int>), null),
            ["tickpulse.leader.is_leader"] = (typeof(ObservableGauge<int>), null),
            ["tickpulse.backfill.candles"] = (typeof(Counter<long>), "{candle}"),
            ["tickpulse.gapfill.trades"] = (typeof(Counter<long>), "{trade}"),
            ["tickpulse.api.responses"] = (typeof(Counter<long>), "{response}"),
        };
        published.Keys.Order(StringComparer.Ordinal).ShouldBe(contract.Keys.Order(StringComparer.Ordinal));
        foreach (var (name, (kind, unit)) in contract)
        {
            published[name].ShouldBeOfType(kind, name);
            published[name].Unit.ShouldBe(unit, name);
        }
    }

    [Fact]
    public void LagHistogram_HasExplicitBucketsAroundTheAlertThreshold()
    {
        var metrics = NewMetrics(new IngestionState(_clock), new SymbolIdCache());

        metrics.IngestLag.Advice.ShouldNotBeNull().HistogramBucketBoundaries
            .ShouldBe([50d, 100d, 250d, 500d, 1000d, 2500d, 5000d, 10000d, 30000d]);
        metrics.BatchDuration.Advice.ShouldNotBeNull().HistogramBucketBoundaries.ShouldNotBeNull().ShouldContain(5d);
    }

    [Fact]
    public async Task Flush_RecordsPersistedAndLag()
    {
        var state = new IngestionState(_clock);
        var symbols = Symbols();
        var metrics = NewMetrics(state, symbols);
        using var persisted = Collector<long>("tickpulse.ingest.ticks.persisted");
        using var lag = Collector<double>("tickpulse.ingest.lag");
        using var size = Collector<long>("tickpulse.ingest.batch.size");
        using var duration = Collector<double>("tickpulse.ingest.batch.duration");

        // BTC trade 1 is already stored: only trade 2 is new for BTC.
        var writer = new FakeTickWriter(alreadyStored: [(Btc, 1)]);
        var channel = Channel.CreateUnbounded<Tick>();
        channel.Writer.TryWrite(Tick(Btc, 1, ms: 500));
        channel.Writer.TryWrite(Tick(Btc, 2, ms: 900));
        channel.Writer.TryWrite(Tick(Eth, 7, ms: 1500));
        channel.Writer.Complete();

        await BatchWriter(writer, state, symbols, metrics).RunAsync(channel.Reader, Ct);

        Totals(persisted).ShouldBe(new Dictionary<string, long> { ["BTCUSDT"] = 1, ["ETHUSDT"] = 1 }, ignoreOrder: true);

        // One sample per batch: the newest tick (T0 + 1.5 s) was stored at T0 + 2 s.
        var lagSample = lag.GetMeasurementSnapshot().ShouldHaveSingleItem();
        lagSample.Value.ShouldBe(500d);
        lagSample.Tags.ShouldBeEmpty();
        size.GetMeasurementSnapshot().ShouldHaveSingleItem().Value.ShouldBe(3);
        duration.GetMeasurementSnapshot().ShouldHaveSingleItem().Value.ShouldBeGreaterThanOrEqualTo(0d);
    }

    [Fact]
    public async Task Flush_NothingNew_RecordsNoLagSample()
    {
        var state = new IngestionState(_clock);
        var symbols = Symbols();
        var metrics = NewMetrics(state, symbols);
        using var persisted = Collector<long>("tickpulse.ingest.ticks.persisted");
        using var lag = Collector<double>("tickpulse.ingest.lag");

        // A replayed batch (e.g. a gap-fill overlap) carries old trade times; it must not show up as ingest lag.
        var writer = new FakeTickWriter(alreadyStored: [(Btc, 1)]);
        var channel = Channel.CreateUnbounded<Tick>();
        channel.Writer.TryWrite(Tick(Btc, 1, ms: -600_000));
        channel.Writer.Complete();

        await BatchWriter(writer, state, symbols, metrics).RunAsync(channel.Reader, Ct);

        persisted.GetMeasurementSnapshot().ShouldBeEmpty();
        lag.GetMeasurementSnapshot().ShouldBeEmpty();
    }

    [Fact]
    public void Observables_ReportStateLeadershipChannelAndActiveSymbols()
    {
        var state = new IngestionState(_clock);
        var symbols = Symbols();
        var metrics = NewMetrics(state, symbols);
        using var wsState = Collector<int>("tickpulse.ws.state");
        using var leader = Collector<int>("tickpulse.leader.is_leader");
        using var depth = Collector<int>("tickpulse.ingest.channel.depth");
        using var active = Collector<int>("tickpulse.ingest.symbol.active");

        // Standby: not leader, no channel, no active symbols reported.
        Observe(wsState, leader, depth, active);
        wsState.LastMeasurement.ShouldNotBeNull().Value.ShouldBe((int)WsState.Standby);
        leader.LastMeasurement.ShouldNotBeNull().Value.ShouldBe(0);
        depth.LastMeasurement.ShouldNotBeNull().Value.ShouldBe(0);
        active.GetMeasurementSnapshot().ShouldBeEmpty();

        // Leader and streaming, with three ticks waiting; ETH was removed by an admin since the last refresh.
        state.SetConnected(T0, T0.AddHours(23));
        symbols.Update([Symbol(Btc, "BTCUSDT")]);
        var channel = Channel.CreateBounded<Tick>(10);
        for (var i = 1; i <= 3; i++)
        {
            channel.Writer.TryWrite(Tick(Btc, i, ms: 0));
        }

        metrics.TrackChannel(channel.Reader);
        wsState.Clear();
        leader.Clear();
        depth.Clear();
        Observe(wsState, leader, depth, active);

        wsState.LastMeasurement.ShouldNotBeNull().Value.ShouldBe(2);
        leader.LastMeasurement.ShouldNotBeNull().Value.ShouldBe(1);
        depth.LastMeasurement.ShouldNotBeNull().Value.ShouldBe(3);
        active.GetMeasurementSnapshot()
            .ToDictionary(m => (string)m.Tags["symbol"]!, m => m.Value)
            .ShouldBe(new Dictionary<string, int> { ["BTCUSDT"] = 1, ["ETHUSDT"] = 0 }, ignoreOrder: true);

        // The removed symbol reported 0 once; from now on it is not reported, until it is added again.
        active.Clear();
        active.RecordObservableInstruments();
        active.GetMeasurementSnapshot().Select(m => (string?)m.Tags["symbol"]).ShouldBe(["BTCUSDT"]);
        symbols.Update([Symbol(Btc, "BTCUSDT"), Symbol(Eth, "ETHUSDT")]);
        active.Clear();
        active.RecordObservableInstruments();
        active.GetMeasurementSnapshot()
            .ToDictionary(m => (string)m.Tags["symbol"]!, m => m.Value)
            .ShouldBe(new Dictionary<string, int> { ["BTCUSDT"] = 1, ["ETHUSDT"] = 1 }, ignoreOrder: true);

        // The session ends: the channel is no longer tracked.
        metrics.TrackChannel(null);
        depth.Clear();
        depth.RecordObservableInstruments();
        depth.LastMeasurement.ShouldNotBeNull().Value.ShouldBe(0);
    }

    [Fact]
    public async Task GapFill_CountsRecoveredTradesPerSymbol()
    {
        var metrics = NewMetrics(new IngestionState(_clock), Symbols());
        using var trades = Collector<long>("tickpulse.gapfill.trades");
        var output = Channel.CreateUnbounded<Tick>();
        var filler = new GapFiller(new FakeAggTrades(1, 1500), _clock, NullLogger<GapFiller>.Instance, metrics);

        var result = await filler.FillAsync(Btc, "BTCUSDT", 0, () => 1201, output.Writer, Ct);

        result.Emitted.ShouldBe(1200);
        Totals(trades).ShouldBe(new Dictionary<string, long> { ["BTCUSDT"] = 1200 }, ignoreOrder: true);
    }

    [Fact]
    public async Task ApiResponse_RecordsContentTypeTag()
    {
        using var responses = new MetricCollector<long>(
            api.Services.GetRequiredService<IMeterFactory>(), TickPulseMetrics.MeterName, "tickpulse.api.responses");
        using var client = api.CreateClient();

        (await GetAsync(client, "/api/symbols", "application/xml")).ShouldBe(HttpStatusCode.OK);
        (await GetAsync(client, "/api/symbols", "application/json")).ShouldBe(HttpStatusCode.OK);
        (await GetAsync(client, "/api/NOPEUSDT/24hAvgPrice", "application/xml")).ShouldBe(HttpStatusCode.NotFound);
        (await GetAsync(client, "/health/live", null)).ShouldBe(HttpStatusCode.OK);

        // Recorded once the response has completed, which can be just after the client has it.
        await WaitForAsync(() => responses.GetMeasurementSnapshot().Count >= 3, "three API responses counted");
        var recorded = responses.GetMeasurementSnapshot()
            .Select(m => (Route: (string?)m.Tags["route"], Status: (string?)m.Tags["status"], ContentType: (string?)m.Tags["content_type"], m.Value))
            .ToList();

        recorded.ShouldBe(
            [
                ("api/symbols", "200", "application/xml", 1L),
                ("api/symbols", "200", "application/json", 1L),
                ("api/{symbol}/24hAvgPrice", "404", "application/problem+xml", 1L),
            ],
            ignoreOrder: true);
    }

    [Fact]
    public async Task Flush_RecoveredTicks_RecordNoLagSample()
    {
        var state = new IngestionState(_clock);
        var symbols = Symbols();
        var metrics = NewMetrics(state, symbols);
        using var persisted = Collector<long>("tickpulse.ingest.ticks.persisted");
        using var lag = Collector<double>("tickpulse.ingest.lag");

        // A gap-fill batch: new trades from ten minutes ago, fetched over REST just now.
        var writer = new FakeTickWriter(alreadyStored: []);
        var channel = Channel.CreateUnbounded<Tick>();
        channel.Writer.TryWrite(new Tick(Btc, 1, 100m, 1m, T0.AddMinutes(-10), T0.AddSeconds(1)));
        channel.Writer.TryWrite(new Tick(Btc, 2, 100m, 1m, T0.AddMinutes(-10).AddMilliseconds(5), T0.AddSeconds(1)));
        channel.Writer.Complete();

        await BatchWriter(writer, state, symbols, metrics).RunAsync(channel.Reader, Ct);

        Totals(persisted).ShouldBe(new Dictionary<string, long> { ["BTCUSDT"] = 2 }, ignoreOrder: true);
        lag.GetMeasurementSnapshot().ShouldBeEmpty();
        TickBatchWriter.RecoveredTickAge.ShouldBe(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task ApiResponse_UnhandledException_KeepsTheRoute()
    {
        using var responses = new MetricCollector<long>(
            api.Services.GetRequiredService<IMeterFactory>(), TickPulseMetrics.MeterName, "tickpulse.api.responses");
        using var client = api.CreateClient();

        (await GetAsync(client, "/api/probe/throw", null)).ShouldBe(HttpStatusCode.InternalServerError);

        await WaitForAsync(() => responses.GetMeasurementSnapshot().Count >= 1, "the 500 counted");
        var recorded = responses.GetMeasurementSnapshot().ShouldHaveSingleItem();
        recorded.Tags["route"].ShouldBe("api/probe/throw");
        recorded.Tags["status"].ShouldBe("500");
        recorded.Tags["content_type"].ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task Stream_CountsReceivedTicksAndTheInitialConnection()
    {
        await api.Database.ResetAsync();
        await using var server = new FakeBinanceServer();
        await server.InitializeAsync();
        using var received = Collector<long>("tickpulse.ingest.ticks.received");
        using var connections = Collector<long>("tickpulse.ws.connections");
        using var persisted = Collector<long>("tickpulse.ingest.ticks.persisted");

        // The host's meters come from this test's factory, so the collectors see the very first connection.
        await using var host = await StartAsync(api.Database, server, configureServices: s => s.AddSingleton(MeterFactory));
        await server.WaitUntilAsync(s => s.ActiveConnections == 1 && s.SubscribedStreams.Count == 3, WaitTimeout, Ct);
        await WaitForAsync(() => host.State.Status == WsState.Connected, "the worker connected");
        var t0 = Now.AddSeconds(-1);
        for (var i = 1; i <= 5; i++)
        {
            await server.PushTradeAsync("BTCUSDT", i, 100m + i, t0.AddMilliseconds(i));
        }

        await server.PushTradeAsync("ETHUSDT", 900, 3000m, t0);
        await WaitForAsync(() => Totals(persisted).Values.Sum() == 6, "six ticks persisted");

        Totals(received).ShouldBe(new Dictionary<string, long> { ["BTCUSDT"] = 5, ["ETHUSDT"] = 1 }, ignoreOrder: true);
        Totals(persisted).ShouldBe(new Dictionary<string, long> { ["BTCUSDT"] = 5, ["ETHUSDT"] = 1 }, ignoreOrder: true);

        connections.GetMeasurementSnapshot().ShouldHaveSingleItem().Tags["reason"].ShouldBe("initial");
    }

    [Fact]
    public void ConnectionReasons_AreTaggedAsInTheContract()
    {
        var metrics = NewMetrics(new IngestionState(_clock), Symbols());
        using var connections = Collector<long>("tickpulse.ws.connections");

        metrics.WsConnected(WsConnectionReason.Initial);
        metrics.WsConnected(WsConnectionReason.Reconnect);
        metrics.WsConnected(WsConnectionReason.Rotation);

        connections.GetMeasurementSnapshot().Select(m => (string?)m.Tags["reason"]).ShouldBe(["initial", "reconnect", "rotation"]);
    }

    private static void Observe(params IDisposable[] collectors)
    {
        foreach (var collector in collectors)
        {
            switch (collector)
            {
                case MetricCollector<int> ints:
                    ints.RecordObservableInstruments();
                    break;
                case MetricCollector<long> longs:
                    longs.RecordObservableInstruments();
                    break;
            }
        }
    }

    private static async Task<HttpStatusCode> GetAsync(HttpClient client, string path, string? accept)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (accept is not null)
        {
            request.Headers.Accept.ParseAdd(accept);
        }

        using var response = await client.SendAsync(request, Ct);
        return response.StatusCode;
    }

    private static Dictionary<string, long> Totals(MetricCollector<long> collector) =>
        collector.GetMeasurementSnapshot()
            .GroupBy(m => (string)m.Tags["symbol"]!)
            .ToDictionary(g => g.Key, g => g.Sum(m => m.Value));

    private static MonitoredSymbol Symbol(int id, string name) =>
        new(id, name, true, T0, null, BackfillStatus.NotRequested, null);

    private static SymbolIdCache Symbols()
    {
        var symbols = new SymbolIdCache();
        symbols.Update([Symbol(Btc, "BTCUSDT"), Symbol(Eth, "ETHUSDT")]);
        return symbols;
    }

    private static Tick Tick(int symbolId, long id, int ms) =>
        new(symbolId, id, 100m + id, 1m, T0.AddMilliseconds(ms), T0.AddMilliseconds(ms));

    private TickPulseMetrics NewMetrics(IngestionState state, SymbolIdCache symbols) => new(MeterFactory, state, symbols);

    private MetricCollector<T> Collector<T>(string instrument)
        where T : struct => new(MeterFactory, TickPulseMetrics.MeterName, instrument);

    private TickBatchWriter BatchWriter(ITickWriter writer, IngestionState state, SymbolIdCache symbols, TickPulseMetrics metrics) => new(
        writer,
        state,
        symbols,
        new NoWatermark(),
        Options.Create(new IngestionOptions { BatchMaxSize = 5000, BatchMaxDelay = TimeSpan.FromMilliseconds(500) }),
        _clock,
        NullLogger<TickBatchWriter>.Instance,
        metrics);

    private sealed class NoWatermark : IGapFillWatermark
    {
        public void Lower(string symbol, long firstMissingId)
        {
        }
    }

    /// <summary>Stores ticks in memory with the same idempotency as the real writer.</summary>
    private sealed class FakeTickWriter((int SymbolId, long AggTradeId)[] alreadyStored) : ITickWriter
    {
        private readonly HashSet<(int, long)> _stored = [.. alreadyStored];

        public Task<int> WriteBatchAsync(IReadOnlyList<Tick> ticks, IDictionary<int, int> insertedPerSymbol, CancellationToken ct)
        {
            insertedPerSymbol.Clear();
            var inserted = 0;
            foreach (var tick in ticks)
            {
                if (_stored.Add((tick.SymbolId, tick.AggTradeId)))
                {
                    inserted++;
                    insertedPerSymbol[tick.SymbolId] = insertedPerSymbol.TryGetValue(tick.SymbolId, out var n) ? n + 1 : 1;
                }
            }

            return Task.FromResult(inserted);
        }
    }

    /// <summary>Trades <c>first..last</c>, served like <c>aggTrades?fromId</c>.</summary>
    private sealed class FakeAggTrades(long first, long last) : IAggTradeSource
    {
        public Task<IReadOnlyList<(long Id, decimal Price, decimal Qty, DateTime TimeUtc)>> GetAggTradesFromAsync(
            SymbolName s, long fromId, int limit, CancellationToken ct)
        {
            var ids = new List<(long, decimal, decimal, DateTime)>();
            for (var id = Math.Max(first, fromId); id <= last && ids.Count < limit; id++)
            {
                ids.Add((id, id, 1m, T0.AddMilliseconds(id)));
            }

            return Task.FromResult<IReadOnlyList<(long, decimal, decimal, DateTime)>>(ids);
        }

        public Task<IReadOnlyList<(long Id, decimal Price, decimal Qty, DateTime TimeUtc)>> GetAggTradesFromTimeAsync(
            SymbolName s, DateTime startUtc, int limit, CancellationToken ct) => throw new NotSupportedException();
    }
}

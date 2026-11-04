using System.Diagnostics.Metrics;
using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using TickPulse.Application.Abstractions;
using TickPulse.Application.Ingestion;
using TickPulse.Domain;
using TickPulse.Infrastructure;
using TickPulse.Infrastructure.Backfill;
using TickPulse.Infrastructure.Binance;
using TickPulse.Infrastructure.Persistence;
using TickPulse.Infrastructure.Telemetry;
using TickPulse.Integration.Tests.Fixtures;
using TickPulse.Integration.Tests.Telemetry;
using static TickPulse.Integration.Tests.Ingestion.IngestionTestHost;

namespace TickPulse.Integration.Tests.Ingestion;

/// <summary>
/// The backfill worker resolved from the real DI registration, against the shared TimescaleDB and a
/// <see cref="FakeBinanceServer"/>, on a <see cref="FakeTimeProvider"/>. Leadership is set directly on
/// <see cref="IngestionState"/>; the ingestion worker is not started.
/// </summary>
/// <remarks>
/// The clock only moves when a test advances it, and only after the worker has logged that it is waiting (its timer is
/// registered before that log entry), so no REST call is ever in flight while fake time passes.
/// </remarks>
public sealed class BackfillWorkerTests(TimescaleFixture db) : IAsyncLifetime
{
    private const int Btc = 1;
    private const int Ada = 2;
    private const int Eth = 3;
    private const int DepthMinutes = 3 * 24 * 60;

    // Five seconds into a minute: AlignDown(now, 1m) is End, and AlignDown(now − 3 days, 1m) is From.
    private static readonly DateTime Start = new(2026, 11, 4, 12, 0, 5, DateTimeKind.Utc);
    private static readonly DateTime End = new(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime From = End.AddMinutes(-DepthMinutes);

    private readonly FakeBinanceServer _server = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(Start));
    private readonly LogCapture _logs = new();
    private readonly List<Run> _runs = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await db.ResetAsync();
        await _server.InitializeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var run in _runs)
        {
            await run.DisposeAsync();
        }

        await _server.DisposeAsync();
    }

    [Fact]
    public async Task Disabled_DoesNothing()
    {
        SeedTrades("BTCUSDT", From, 10);
        var run = CreateRun(enabled: false);
        await run.StartAsync();

        // The worker returns at once and the queue ignores requests.
        await run.Worker.ExecuteTask.ShouldNotBeNull().WaitAsync(WaitTimeout, Ct);
        run.Worker.Enqueue(Btc);

        _server.KlineRequests.ShouldBeEmpty();
        (await CandleStatsAsync(Btc)).Count.ShouldBe(0);
        (await run.Symbols.ListAsync(Ct)).ShouldAllBe(s => s.BackfillStatus == BackfillStatus.NotRequested);
        _logs.Count("LogDisabled").ShouldBe(1);
    }

    [Fact]
    public async Task Enabled_FillsDepth_InPages()
    {
        var prices = SeedTrades("BTCUSDT", From, DepthMinutes);
        _server.AddTrade("BTCUSDT", 1, 1m, From.AddMinutes(-1).AddSeconds(10)); // before the depth
        _server.AddTrade("BTCUSDT", 9_999_999, 1m, End.AddSeconds(2)); // the current, unfinished minute
        var run = CreateRun();
        await MarkCompletedAsync(run, Ada, Eth);

        await run.StartAsync();
        await WaitForStatusAsync(run, Btc, BackfillStatus.Completed);

        var stats = await CandleStatsAsync(Btc);
        stats.ShouldBe(new CandleStats(DepthMinutes, From, End.AddMinutes(-1), DepthMinutes, prices.Sum(), DepthMinutes));
        (await FindAsync(run, Btc)).BackfilledFromUtc.ShouldBe(From);

        var requests = _server.KlineRequests;
        requests.Count.ShouldBe(5);
        requests.Select(r => r["startTime"]).ShouldBe(Enumerable.Range(0, 5).Select(i => UnixMs(From.AddMinutes(i * 1000))));
        requests.ShouldAllBe(r => r["symbol"] == "BTCUSDT" && r["interval"] == "1m" && r["limit"] == "1000");
        requests.ShouldAllBe(r => r["endTime"] == (long.Parse(UnixMs(End), CultureInfo.InvariantCulture) - 1).ToString(CultureInfo.InvariantCulture));

        // Progress roughly every 10 %, never more often than once per page.
        _logs.Count("LogProgress").ShouldBeInRange(1, 10);
        _logs.Count("LogStarted").ShouldBe(1);
        _logs.Count("LogCompleted").ShouldBe(1);
    }

    [Fact]
    public async Task Pages_CountStoredCandles_AndAreTraced()
    {
        // 1,500 minutes of history: one full page and one short page.
        SeedTrades("BTCUSDT", From, 1500);
        using var meters = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var factory = meters.GetRequiredService<IMeterFactory>();
        using var candles = new MetricCollector<long>(factory, TickPulseMetrics.MeterName, "tickpulse.backfill.candles");
        using var spans = new SpanCapture(TickPulseMetrics.ActivitySourceName);
        var run = CreateRun(configureServices: s => s.AddSingleton(factory));
        await MarkCompletedAsync(run, Ada, Eth);

        await run.StartAsync();
        await WaitForStatusAsync(run, Btc, BackfillStatus.Completed);

        // One measurement per stored page, tagged with the symbol.
        var measured = candles.GetMeasurementSnapshot();
        measured.Select(m => m.Value).ShouldBe([1000L, 500L]);
        measured.ShouldAllBe(m => (string?)m.Tags["symbol"] == "BTCUSDT");

        var pages = spans.Named(TickPulseMetrics.BackfillPageSpan);
        pages.Count.ShouldBe(2);
        pages[0].GetTagItem("tickpulse.symbol").ShouldBe("BTCUSDT");
        pages[0].GetTagItem("tickpulse.backfill.start").ShouldBe(From.ToString("O", CultureInfo.InvariantCulture));
        pages[0].GetTagItem("tickpulse.backfill.fetched").ShouldBe(1000);
        pages[1].GetTagItem("tickpulse.backfill.inserted").ShouldBe(500);
    }

    [Fact]
    public async Task StopsAtFirstStreamMinute()
    {
        var prices = SeedTrades("BTCUSDT", From, DepthMinutes);
        var run = CreateRun();
        await MarkCompletedAsync(run, Ada, Eth);
        var streamMinute = From.AddMinutes(2000);
        var tickTime = streamMinute.AddSeconds(30);
        (await run.Services.GetRequiredService<ITickWriter>()
            .WriteBatchAsync([new Tick(Btc, 42_000_000, 123m, 1m, tickTime, tickTime)], Ct)).ShouldBe(1);

        await run.StartAsync();
        await WaitForStatusAsync(run, Btc, BackfillStatus.Completed);

        // 2,000 backfilled minutes up to the stream, plus the one stream minute; nothing after it.
        (await CandleStatsAsync(Btc)).ShouldBe(new CandleStats(2001, From, streamMinute, 2000, prices.Take(2000).Sum() + 123m, 2000));
        var requests = _server.KlineRequests;
        requests.Count.ShouldBe(2);
        requests.ShouldAllBe(r => r["endTime"] == (long.Parse(UnixMs(streamMinute), CultureInfo.InvariantCulture) - 1).ToString(CultureInfo.InvariantCulture));
        (await FindAsync(run, Btc)).BackfilledFromUtc.ShouldBe(From);
    }

    [Fact]
    public async Task Failure_SetsFailedStatus()
    {
        SeedTrades("ETHUSDT", From, 10);
        _server.RemoveSymbol("BTCUSDT"); // klines answers 400 -1121 Invalid symbol: not transient
        var run = CreateRun();
        await MarkCompletedAsync(run, Ada);

        await run.StartAsync();

        // BTC (id 1) fails first; the queue goes on with ETH (id 3).
        await WaitForStatusAsync(run, Eth, BackfillStatus.Completed);
        var btc = await FindAsync(run, Btc);
        btc.BackfillStatus.ShouldBe(BackfillStatus.Failed);
        btc.BackfilledFromUtc.ShouldBeNull();
        var failure = _logs.Entries.Where(e => e.EventName == "LogFailed").ShouldHaveSingleItem();
        failure.Level.ShouldBe(LogLevel.Error);
        failure.Message.ShouldContain("BTCUSDT");
        failure.Message.ShouldContain("-1121");
        _logs.Count("LogRetrying").ShouldBe(0);
        _server.KlineRequests.Count(r => r["symbol"] == "BTCUSDT").ShouldBe(1);
        (await CandleStatsAsync(Btc)).Count.ShouldBe(0);
        (await CandleStatsAsync(Eth)).Count.ShouldBe(10);
    }

    [Fact]
    public async Task Rerun_IsIdempotent()
    {
        SeedTrades("BTCUSDT", From, DepthMinutes);
        var first = CreateRun();
        await MarkCompletedAsync(first, Ada, Eth);
        await first.StartAsync();
        await WaitForStatusAsync(first, Btc, BackfillStatus.Completed);
        var stored = await CandleStatsAsync(Btc);
        await first.DisposeAsync();
        _server.KlineRequests.Count.ShouldBe(5);

        // Requested again (e.g. the previous run died before it could record Completed): nothing is fetched twice.
        var second = CreateRun();
        await second.Symbols.SetBackfillStatusAsync(Btc, BackfillStatus.Failed, null, Ct);
        await second.StartAsync();
        await WaitForStatusAsync(second, Btc, BackfillStatus.Completed);
        await second.DisposeAsync();
        (await CandleStatsAsync(Btc)).ShouldBe(stored);
        _server.KlineRequests.Count.ShouldBe(5);

        // Already completed: not queued at all.
        var third = CreateRun();
        await third.StartAsync();
        await WaitForAsync(() => _logs.Count("LogQueuedPending") == 3, "the third start-up scan");
        (await FindAsync(third, Btc)).ShouldSatisfyAllConditions(
            s => s.BackfillStatus.ShouldBe(BackfillStatus.Completed),
            s => s.BackfilledFromUtc.ShouldBe(From));
        (await CandleStatsAsync(Btc)).ShouldBe(stored);
        _server.KlineRequests.Count.ShouldBe(5);
        _logs.Count("LogStarted").ShouldBe(2);
    }

    [Fact]
    public async Task LeadershipLost_MidRun_PausesAndResumes()
    {
        SeedTrades("BTCUSDT", From, DepthMinutes);
        var run = CreateRun();
        await MarkCompletedAsync(run, Ada, Eth);
        _server.PauseKlines();

        await run.StartAsync();
        await _server.WaitUntilAsync(s => s.KlineRequests.Count == 1, WaitTimeout, Ct);
        run.State.SetStandby(); // lost while the first page is in flight
        _server.ResumeKlines();
        await WaitForAsync(() => _logs.Count("LogPaused") == 1, "the worker paused");

        // The page already fetched is stored; nothing more is requested while not leader.
        _server.KlineRequests.Count.ShouldBe(1);
        (await CandleStatsAsync(Btc)).Count.ShouldBe(1000);
        (await FindAsync(run, Btc)).BackfillStatus.ShouldBe(BackfillStatus.Running);

        run.State.SetConnecting();
        _clock.Advance(BackfillWorker.LeaderPollInterval);
        await WaitForStatusAsync(run, Btc, BackfillStatus.Completed);

        (await CandleStatsAsync(Btc)).Count.ShouldBe(DepthMinutes);
        var requests = _server.KlineRequests;
        requests.Count.ShouldBe(5);
        requests[1]["startTime"].ShouldBe(UnixMs(From.AddMinutes(1000)));
        _logs.Count("LogResumed").ShouldBe(1);
    }

    [Fact]
    public async Task Reactivated_Pending_FillsTheInactiveStretch_AfterBeingCompleted()
    {
        var prices = SeedTrades("BTCUSDT", From, DepthMinutes);
        var run = CreateRun();
        await MarkCompletedAsync(run, Ada, Eth);

        // First activation: backfilled [From, From+1000), then streamed for ten minutes, then deactivated at From+1010.
        Kline[] history = [.. Enumerable.Range(0, 1000).Select(i => new Kline(From.AddMinutes(i), 1m, 1m, 1m, 1m, 0m, 0m))];
        (await run.Services.GetRequiredService<ICandleWriter>().InsertBackfillAsync(Btc, history, Ct)).ShouldBe(1000);
        await WriteStreamAsync(run, From.AddMinutes(1000), 10, firstId: 50_000_000);
        await run.Symbols.SetBackfillStatusAsync(Btc, BackfillStatus.Completed, From, Ct);
        (await run.Symbols.DeactivateAsync(Symbol("BTCUSDT"), Ct)).ShouldBeTrue();
        await SetDeactivatedAtAsync(Btc, From.AddMinutes(1010));

        // Reactivated as the admin use case does it, and streaming again from From+3000.
        (await run.Symbols.AddOrReactivateAsync(Symbol("BTCUSDT"), 20, Ct)).Status.ShouldBe(SymbolAddStatus.Reactivated);
        await run.Symbols.SetBackfillStatusAsync(Btc, BackfillStatus.Pending, From, Ct);
        await WriteStreamAsync(run, From.AddMinutes(3000), 5, firstId: 60_000_000);

        await run.StartAsync();
        run.Worker.Enqueue(Btc);
        await WaitForStatusAsync(run, Btc, BackfillStatus.Completed);

        // Only the inactive stretch [From+1010, From+3000) is fetched: after the last stored minute, up to the new stream.
        var requests = _server.KlineRequests;
        requests.Select(r => r["startTime"]).ShouldBe([UnixMs(From.AddMinutes(1010)), UnixMs(From.AddMinutes(2010))]);
        requests.ShouldAllBe(r => r["endTime"] == (long.Parse(UnixMs(From.AddMinutes(3000)), CultureInfo.InvariantCulture) - 1).ToString(CultureInfo.InvariantCulture));
        var stats = await CandleStatsAsync(Btc);
        stats.Count.ShouldBe(1000 + 10 + 1990 + 5);
        stats.Backfilled.ShouldBe(1000 + 1990);
        stats.First.ShouldBe(From);
        stats.Last.ShouldBe(From.AddMinutes(3004));
        (await CloseAtAsync(Btc, From.AddMinutes(2999))).ShouldBe(prices[2999]);
        (await CloseAtAsync(Btc, From.AddMinutes(999))).ShouldBe(1m); // the first activation's rows were left alone
        (await FindAsync(run, Btc)).BackfilledFromUtc.ShouldBe(From);
    }

    [Fact]
    public async Task Reactivated_NoNewStreamYet_FillsUpToNow()
    {
        SeedTrades("BTCUSDT", From, DepthMinutes);
        var run = CreateRun();
        await MarkCompletedAsync(run, Ada, Eth);
        await WriteStreamAsync(run, From.AddMinutes(100), 3, firstId: 50_000_000);
        (await run.Symbols.DeactivateAsync(Symbol("BTCUSDT"), Ct)).ShouldBeTrue();
        await SetDeactivatedAtAsync(Btc, From.AddMinutes(103).AddSeconds(30));
        (await run.Symbols.AddOrReactivateAsync(Symbol("BTCUSDT"), 20, Ct)).Status.ShouldBe(SymbolAddStatus.Reactivated);
        await run.Symbols.SetBackfillStatusAsync(Btc, BackfillStatus.Pending, null, Ct);

        await run.StartAsync();
        await WaitForStatusAsync(run, Btc, BackfillStatus.Completed);

        // [From, From+100) before the first stream minute, then [From+103, End) after the last stored one.
        var stats = await CandleStatsAsync(Btc);
        stats.Count.ShouldBe(DepthMinutes);
        stats.Backfilled.ShouldBe(DepthMinutes - 3);
        stats.Last.ShouldBe(End.AddMinutes(-1));
        _server.KlineRequests[0]["startTime"].ShouldBe(UnixMs(From));
        _server.KlineRequests.Skip(1).First()["startTime"].ShouldBe(UnixMs(From.AddMinutes(103)));
    }

    [Fact]
    public async Task PartialRun_Resumes_WithoutRefetching()
    {
        SeedTrades("BTCUSDT", From, DepthMinutes);
        var run = CreateRun();
        await MarkCompletedAsync(run, Ada, Eth);

        // An earlier run stored the first 1,500 minutes and then the process stopped.
        Kline[] stored = [.. Enumerable.Range(0, 1500).Select(i => new Kline(From.AddMinutes(i), 1m, 1m, 1m, 1m, 0m, 0m))];
        (await run.Services.GetRequiredService<ICandleWriter>().InsertBackfillAsync(Btc, stored, Ct)).ShouldBe(1500);
        await run.Symbols.SetBackfillStatusAsync(Btc, BackfillStatus.Running, null, Ct);

        await run.StartAsync();
        await WaitForStatusAsync(run, Btc, BackfillStatus.Completed);

        var requests = _server.KlineRequests;
        requests.Select(r => r["startTime"]).ShouldBe([UnixMs(From.AddMinutes(1500)), UnixMs(From.AddMinutes(2500)), UnixMs(From.AddMinutes(3500))]);
        var stats = await CandleStatsAsync(Btc);
        stats.Count.ShouldBe(DepthMinutes);
        stats.First.ShouldBe(From);
        stats.Last.ShouldBe(End.AddMinutes(-1));
        (await CloseAtAsync(Btc, From.AddMinutes(1499))).ShouldBe(1m); // the stored minutes were left alone
    }

    [Fact]
    public async Task Banned_SetsFailedStatus()
    {
        SeedTrades("BTCUSDT", From, DepthMinutes);
        _server.FailNextKlines(418, retryAfterSeconds: 120);
        var run = CreateRun();
        await MarkCompletedAsync(run, Ada, Eth);

        await run.StartAsync();
        await WaitForStatusAsync(run, Btc, BackfillStatus.Failed);

        var failure = _logs.Entries.Where(e => e.EventName == "LogFailed").ShouldHaveSingleItem();
        failure.Message.ShouldContain("banned");
        _logs.Count("LogRetrying").ShouldBe(0);
        _server.KlineRequests.Count.ShouldBe(1);
        (await CandleStatsAsync(Btc)).Count.ShouldBe(0);
    }

    [Fact]
    public async Task Banned_WaitsOutBan_ThenRequeuesFailedAndContinues()
    {
        var first = From.AddMinutes(10);
        SeedTrades("BTCUSDT", first, 10);
        SeedTrades("ETHUSDT", first, 10);
        _server.FailNextKlines(418, retryAfterSeconds: 120);
        var run = CreateRun();
        await MarkCompletedAsync(run, Ada);

        await run.StartAsync();
        await WaitForAsync(() => _logs.Count("LogBanWait") == 1, "the worker waiting out the ban");

        // BTC failed; ETH is still queued, untouched: no request is sent during the ban.
        (await FindAsync(run, Btc)).BackfillStatus.ShouldBe(BackfillStatus.Failed);
        (await FindAsync(run, Eth)).BackfillStatus.ShouldBe(BackfillStatus.NotRequested);
        _server.KlineRequests.Count.ShouldBe(1);

        _clock.Advance(TimeSpan.FromSeconds(120));
        await WaitForStatusAsync(run, Btc, BackfillStatus.Completed);

        // ETH next, then BTC re-queued by the scan after the ban.
        (await FindAsync(run, Eth)).BackfillStatus.ShouldBe(BackfillStatus.Completed);
        _server.KlineRequests.Select(r => r["symbol"]).ShouldBe(["BTCUSDT", "ETHUSDT", "BTCUSDT"]);
        (await CandleStatsAsync(Btc)).Count.ShouldBe(10);
        (await CandleStatsAsync(Eth)).Count.ShouldBe(10);
        (await FindAsync(run, Btc)).BackfilledFromUtc.ShouldBe(first); // the earliest stored minute
        _logs.Count("LogFailed").ShouldBe(1);
    }

    [Fact]
    public async Task LargerDepth_OnlyRecentMinutesStored_FetchesFromStart()
    {
        SeedTrades("BTCUSDT", From, DepthMinutes);
        var run = CreateRun();
        await MarkCompletedAsync(run, Ada, Eth);

        // A partial run with a shorter depth stored a later stretch; resuming after it would leave [From, +3000) empty.
        Kline[] stored = [.. Enumerable.Range(3000, 500).Select(i => new Kline(From.AddMinutes(i), 1m, 1m, 1m, 1m, 0m, 0m))];
        (await run.Services.GetRequiredService<ICandleWriter>().InsertBackfillAsync(Btc, stored, Ct)).ShouldBe(500);
        await run.Symbols.SetBackfillStatusAsync(Btc, BackfillStatus.Running, null, Ct);

        await run.StartAsync();
        await WaitForStatusAsync(run, Btc, BackfillStatus.Completed);

        _server.KlineRequests.Select(r => r["startTime"]).ShouldBe(Enumerable.Range(0, 5).Select(i => UnixMs(From.AddMinutes(i * 1000))));
        var stats = await CandleStatsAsync(Btc);
        stats.Count.ShouldBe(DepthMinutes);
        stats.First.ShouldBe(From);
        (await CloseAtAsync(Btc, From.AddMinutes(3200))).ShouldBe(1m); // overlap is harmless: stored rows stay
        (await FindAsync(run, Btc)).BackfilledFromUtc.ShouldBe(From);
    }

    [Fact]
    public async Task TransientErrors_LeadershipLost_PausesInsteadOfRetrying()
    {
        SeedTrades("BTCUSDT", From, DepthMinutes);
        _server.FailNextKlines(500, count: 10_000);
        var run = CreateRun();
        await MarkCompletedAsync(run, Ada, Eth);

        await run.StartAsync();
        await WaitForAsync(() => _logs.Count("LogRetrying") == 1, "the first retry wait");
        run.State.SetStandby();
        _clock.Advance(BackfillWorker.RetryDelay(1));
        await WaitForAsync(() => _logs.Count("LogPaused") == 1, "the worker paused");

        // The failed retry led to the pause, not to another retry wait or a failure.
        _logs.Count("LogRetrying").ShouldBe(1);
        _logs.Count("LogFailed").ShouldBe(0);
        (await FindAsync(run, Btc)).BackfillStatus.ShouldBe(BackfillStatus.Running);

        _server.FailNextKlines(500, count: 1); // the pipeline's own retry absorbs it
        run.State.SetConnecting();
        _clock.Advance(BackfillWorker.LeaderPollInterval);
        await WaitForStatusAsync(run, Btc, BackfillStatus.Completed);

        (await CandleStatsAsync(Btc)).Count.ShouldBe(DepthMinutes);
        _logs.Count("LogRetrying").ShouldBe(1);
    }

    [Fact]
    public async Task TransientErrors_AreRetried()
    {
        SeedTrades("BTCUSDT", From, DepthMinutes);

        // One more than the resilience pipeline's own attempts, so the worker retries once.
        _server.FailNextKlines(500, count: 5);
        var run = CreateRun();
        await MarkCompletedAsync(run, Ada, Eth);

        await run.StartAsync();
        await WaitForAsync(() => _logs.Count("LogRetrying") == 1, "the first retry wait");
        _clock.Advance(BackfillWorker.RetryDelay(1));
        await WaitForStatusAsync(run, Btc, BackfillStatus.Completed);

        (await CandleStatsAsync(Btc)).Count.ShouldBe(DepthMinutes);
        _logs.Count("LogRetrying").ShouldBe(1);
        _logs.Count("LogFailed").ShouldBe(0);
    }

    [Fact]
    public async Task TransientErrors_Persisting_SetFailedStatus()
    {
        SeedTrades("BTCUSDT", From, DepthMinutes);
        _server.FailNextKlines(503, count: 10_000);
        var run = CreateRun();
        await MarkCompletedAsync(run, Ada, Eth);

        await run.StartAsync();
        for (var attempt = 1; attempt <= BackfillWorker.MaxTransientRetries; attempt++)
        {
            var expected = attempt;
            await WaitForAsync(() => _logs.Count("LogRetrying") == expected, $"retry wait {expected}");
            _clock.Advance(BackfillWorker.RetryDelay(attempt));
        }

        await WaitForStatusAsync(run, Btc, BackfillStatus.Failed);
        _logs.Count("LogRetrying").ShouldBe(BackfillWorker.MaxTransientRetries);
        _logs.Entries.Where(e => e.EventName == "LogFailed").ShouldHaveSingleItem().Message.ShouldContain("503");
    }

    [Fact]
    public async Task Enqueue_Repeated_ProcessesEachSymbolOnceInOrder()
    {
        SeedTrades("BTCUSDT", From, 5);
        SeedTrades("ADAUSDT", From, 5);
        SeedTrades("ETHUSDT", From, 5);
        var run = CreateRun(leader: false);
        await run.StartAsync();
        await WaitForAsync(() => _logs.Count("LogWaitingForLeadership") == 1, "the worker waiting for leadership");

        run.Worker.Enqueue(999); // unknown: skipped
        run.Worker.Enqueue(Eth);
        run.Worker.Enqueue(Btc);
        run.Worker.Enqueue(Eth);
        run.Worker.Enqueue(Btc);
        _server.KlineRequests.ShouldBeEmpty();

        // On leadership the start-up scan adds ADA behind the queued symbols; BTC and ETH are already queued.
        run.State.SetConnecting();
        _clock.Advance(BackfillWorker.LeaderPollInterval);
        await WaitForStatusAsync(run, Ada, BackfillStatus.Completed);

        _server.KlineRequests.Select(r => r["symbol"]).ShouldBe(["ETHUSDT", "BTCUSDT", "ADAUSDT"]);
        (await run.Symbols.ListAsync(Ct)).ShouldAllBe(s => s.BackfillStatus == BackfillStatus.Completed);
        _logs.Count("LogStarted").ShouldBe(3);
        _logs.Count("LogSkippedInactive").ShouldBe(1);
        _logs.Count("LogSkippedCompleted").ShouldBe(0);
    }

    [Fact]
    public async Task UnexpectedError_LogsCritical_RestartsAndRecovers()
    {
        SeedTrades("BTCUSDT", From, DepthMinutes);
        var run = CreateRun(configureServices: services => services.AddSingleton<ICandleWriter>(
            sp => new ThrowOnceCandleWriter(new CandleWriter(sp.GetRequiredService<NpgsqlDataSource>()))));
        await MarkCompletedAsync(run, Ada, Eth);

        await run.StartAsync();
        await WaitForAsync(() => _logs.Count("LogUnexpectedError") == 1, "the unexpected error logged");

        // Logged as Critical; the worker keeps running and the symbol is left Running, not Failed.
        _logs.Entries.Where(e => e.EventName == "LogUnexpectedError").ShouldHaveSingleItem().Level.ShouldBe(LogLevel.Critical);
        run.Worker.ExecuteTask.ShouldNotBeNull().IsCompleted.ShouldBeFalse();
        (await FindAsync(run, Btc)).BackfillStatus.ShouldBe(BackfillStatus.Running);

        // After the restart delay the scan queues it again and it completes.
        _clock.Advance(BackfillWorker.RestartDelay);
        await WaitForStatusAsync(run, Btc, BackfillStatus.Completed);
        (await CandleStatsAsync(Btc)).Count.ShouldBe(DepthMinutes);
        run.Worker.ExecuteTask.IsCompleted.ShouldBeFalse();
        _logs.Count("LogUnexpectedError").ShouldBe(1);
        _logs.Count("LogFailed").ShouldBe(0);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(5, 16)]
    [InlineData(6, 30)]
    [InlineData(40, 30)]
    public void RetryDelay_DoublesUpToCap(int attempt, int seconds) =>
        BackfillWorker.RetryDelay(attempt).ShouldBe(TimeSpan.FromSeconds(seconds));

    private static string UnixMs(DateTime utc) =>
        new DateTimeOffset(utc).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);

    /// <summary>One trade per minute from <paramref name="first"/>, ten seconds into it; returns the prices.</summary>
    private decimal[] SeedTrades(string symbol, DateTime first, int minutes)
    {
        var prices = new decimal[minutes];
        for (var i = 0; i < minutes; i++)
        {
            prices[i] = 100m + (i * 0.01m);
            _server.AddTrade(symbol, 10_000 + i, prices[i], first.AddMinutes(i).AddSeconds(10), quantity: 2m);
        }

        return prices;
    }

    private Run CreateRun(bool enabled = true, bool leader = true, Action<IServiceCollection>? configureServices = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:TickPulse"] = db.ConnectionString,
                ["Binance:WsBaseUrl"] = _server.WsBaseUrl.ToString(),
                ["Binance:RestBaseUrl"] = _server.RestBaseUrl.ToString(),
                ["Backfill:Enabled"] = enabled ? "true" : "false",
                ["Backfill:Depth"] = "3.00:00:00",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(_clock);
        services.AddLogging(logging => logging.AddProvider(_logs).SetMinimumLevel(LogLevel.Debug));
        services.AddTickPulseInfrastructure(configuration);
        services.AddTickPulseIngestion(configuration);
        services.ConfigureBinanceRestResilience(o => o.Retry.Delay = TimeSpan.Zero);
        configureServices?.Invoke(services); // last, so it replaces the default registrations

        var run = new Run(services.BuildServiceProvider());
        if (leader)
        {
            run.State.SetConnecting();
        }

        _runs.Add(run);
        return run;
    }

    /// <summary>One streamed tick per minute for <paramref name="minutes"/> minutes from <paramref name="first"/>.</summary>
    private static async Task WriteStreamAsync(Run run, DateTime first, int minutes, long firstId)
    {
        Tick[] ticks = [.. Enumerable.Range(0, minutes).Select(i => new Tick(Btc, firstId + i, 500m, 1m, first.AddMinutes(i).AddSeconds(20), first.AddMinutes(i).AddSeconds(20)))];
        (await run.Services.GetRequiredService<ITickWriter>().WriteBatchAsync(ticks, Ct)).ShouldBe(minutes);
    }

    private async Task SetDeactivatedAtAsync(int symbolId, DateTime utc)
    {
        await using var connection = new NpgsqlConnection(db.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("UPDATE symbols SET deactivated_at = $2 WHERE id = $1", connection);
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = symbolId });
        command.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = utc, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.TimestampTz });
        (await command.ExecuteNonQueryAsync(Ct)).ShouldBe(1);
    }

    private static SymbolName Symbol(string raw) =>
        SymbolName.TryCreate(raw, out var name) ? name : throw new ArgumentException(raw, nameof(raw));

    private static async Task MarkCompletedAsync(Run run, params int[] symbolIds)
    {
        foreach (var id in symbolIds)
        {
            await run.Symbols.SetBackfillStatusAsync(id, BackfillStatus.Completed, From, Ct);
        }
    }

    private static async Task<MonitoredSymbol> FindAsync(Run run, int symbolId) =>
        (await run.Symbols.ListAsync(Ct)).Single(s => s.Id == symbolId);

    private static Task WaitForStatusAsync(Run run, int symbolId, BackfillStatus status) =>
        WaitForAsync(
            async () => (await FindAsync(run, symbolId)).BackfillStatus == status,
            string.Create(CultureInfo.InvariantCulture, $"symbol {symbolId} backfill {status}"));

    private async Task<CandleStats> CandleStatsAsync(int symbolId)
    {
        await using var connection = new NpgsqlConnection(db.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            """
            SELECT count(*)::int, min(bucket_start), max(bucket_start),
                   (count(*) FILTER (WHERE source = 2))::int,
                   coalesce(sum(close), 0),
                   (count(*) FILTER (WHERE price_sum IS NULL AND tick_count IS NULL AND first_trade_id IS NULL))::int
              FROM candles_1m
             WHERE symbol_id = $1
            """,
            connection);
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = symbolId });
        await using var reader = await command.ExecuteReaderAsync(Ct);
        await reader.ReadAsync(Ct);
        return new CandleStats(
            reader.GetInt32(0),
            reader.IsDBNull(1) ? null : reader.GetDateTime(1),
            reader.IsDBNull(2) ? null : reader.GetDateTime(2),
            reader.GetInt32(3),
            reader.GetDecimal(4),
            reader.GetInt32(5));
    }

    private async Task<decimal> CloseAtAsync(int symbolId, DateTime minute)
    {
        await using var connection = new NpgsqlConnection(db.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT close FROM candles_1m WHERE symbol_id = $1 AND bucket_start = $2", connection);
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = symbolId });
        command.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = minute, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.TimestampTz });
        return (decimal)(await command.ExecuteScalarAsync(Ct))!;
    }

    /// <summary>Throws an unexpected <see cref="InvalidOperationException"/> on the first insert, then delegates.</summary>
    private sealed class ThrowOnceCandleWriter(ICandleWriter inner) : ICandleWriter
    {
        private int _calls;

        public Task<int> InsertBackfillAsync(int symbolId, IReadOnlyList<Kline> klines, CancellationToken ct) =>
            Interlocked.Increment(ref _calls) == 1
                ? throw new InvalidOperationException("Injected unexpected failure.")
                : inner.InsertBackfillAsync(symbolId, klines, ct);
    }

    /// <summary>Count, first and last minute, backfilled count, sum of closes and rows without stream sums/ids.</summary>
    private sealed record CandleStats(int Count, DateTime? First, DateTime? Last, int Backfilled, decimal CloseSum, int WithoutSums);

    /// <summary>One worker with its own container (as one process would have).</summary>
    private sealed class Run(ServiceProvider services) : IAsyncDisposable
    {
        private bool _started;
        private bool _disposed;

        public ServiceProvider Services { get; } = services;

        internal BackfillWorker Worker => (BackfillWorker)Services.GetRequiredService<IBackfillQueue>();

        public IngestionState State => Services.GetRequiredService<IngestionState>();

        public ISymbolRepository Symbols => Services.GetRequiredService<ISymbolRepository>();

        public async Task StartAsync()
        {
            _started = true;
            await Worker.StartAsync(Ct);
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_started)
            {
                await Worker.StopAsync(CancellationToken.None);
            }

            await Services.DisposeAsync();
        }
    }
}

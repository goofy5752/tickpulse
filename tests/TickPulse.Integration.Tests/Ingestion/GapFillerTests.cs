using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TickPulse.Application.Abstractions;
using TickPulse.Application.Ingestion;
using TickPulse.Domain;
using TickPulse.Infrastructure.Binance;
using TickPulse.Infrastructure.Ingestion;
using TickPulse.Infrastructure.Telemetry;

namespace TickPulse.Integration.Tests.Ingestion;

public sealed class GapFillerTests
{
    private const int BtcId = 1;
    private static readonly DateTime T0 = new(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(T0.AddHours(1)));
    private readonly Channel<Tick> _output = Channel.CreateUnbounded<Tick>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Fill_ReachesFirstStreamId_EmitsOnlyTheGap()
    {
        var source = new FakeAggTrades(1, 3500);

        var emitted = await Filler(source).FillAsync(BtcId, "BTCUSDT", 0, () => 2501, _output.Writer, Ct);

        emitted.ShouldBe(new GapFillResult(2500, 2500, Complete: true));
        var ticks = Emitted();
        ticks.Select(t => t.AggTradeId).ShouldBe(Enumerable.Range(1, 2500).Select(i => (long)i));
        source.Requests.ShouldBe([1L, 1001L, 2001L]);
        ticks[0].ShouldBe(new Tick(BtcId, 1, 1m, 0.5m, T0.AddMilliseconds(1), T0.AddHours(1)));
    }

    [Fact]
    public async Task Fill_NoStreamTradeYet_StopsAtTheEndOfTheData()
    {
        var source = new FakeAggTrades(6, 10);

        var emitted = await Filler(source).FillAsync(BtcId, "BTCUSDT", 5, () => null, _output.Writer, Ct);

        emitted.ShouldBe(new GapFillResult(5, 10, Complete: true));
        Emitted().Select(t => t.AggTradeId).ShouldBe([6L, 7L, 8L, 9L, 10L]);
        source.Requests.ShouldBe([6L]); // a short page means there is nothing more yet
    }

    [Fact]
    public async Task Fill_StreamIdLearnedDuringTheFill_StopsThere()
    {
        var source = new FakeAggTrades(1, 5000);
        long? boundary = null;
        source.OnRequest = from => boundary = from > 1 ? 1500 : null;

        var emitted = await Filler(source).FillAsync(BtcId, "BTCUSDT", 0, () => boundary, _output.Writer, Ct);

        emitted.ShouldBe(new GapFillResult(1499, 1499, Complete: true));
        source.Requests.ShouldBe([1L, 1001L]);
    }

    [Fact]
    public async Task Fill_ShortPage_BelowTheStream_IsNotTheEndOfTheData()
    {
        // The stream delivered up to 1000 (its boundary is 1001), but REST lags and ends at 950.
        var source = new FakeAggTrades(500, 950);

        var emitted = await Filler(source).FillAsync(BtcId, "BTCUSDT", 499, () => 1001, _output.Writer, Ct);

        emitted.ShouldBe(new GapFillResult(451, 950, Complete: false)); // 951..1000 are still missing: the gap stays open
        source.Requests.ShouldBe([500L]);
    }

    [Fact]
    public async Task Fill_EmptyPage_BelowTheStream_IsNotTheEndOfTheData()
    {
        var source = new FakeAggTrades(1, 0);

        (await Filler(source).FillAsync(BtcId, "BTCUSDT", 41, () => 50, _output.Writer, Ct)).ShouldBe(new GapFillResult(0, null, Complete: false));
    }

    [Fact]
    public async Task Fill_ShortPage_ReachingTheStream_IsComplete_EvenIfTheStreamMovedOnDuringTheRequest()
    {
        var source = new FakeAggTrades(500, 1000);
        long? boundary = 1001;
        source.OnRequest = _ => boundary = 1200; // the live stream delivered 1001..1199 while REST answered

        var emitted = await Filler(source).FillAsync(BtcId, "BTCUSDT", 499, () => boundary, _output.Writer, Ct);

        emitted.ShouldBe(new GapFillResult(501, 1000, Complete: true)); // 1001.. came from the stream
        source.Requests.ShouldBe([500L]);
    }

    [Theory]
    [InlineData(12, false)] // REST lags behind what the retired link delivered (15): not the end of the data
    [InlineData(15, true)]  // REST reached it: complete
    [InlineData(18, true)]  // REST is ahead: the trades after 15 are emitted too (no stream boundary stops them)
    public async Task Fill_NoStreamTradeYet_ShortPageCompletesOnlyPastWhatARetiredLinkDelivered(int restLast, bool complete)
    {
        var source = new FakeAggTrades(11, restLast);

        var emitted = await Filler(source).FillAsync(BtcId, "BTCUSDT", 10, () => null, () => 15, _output.Writer, Ct);

        emitted.ShouldBe(new GapFillResult(restLast - 10, restLast, complete));
        Emitted().Select(t => t.AggTradeId).ShouldBe(Enumerable.Range(11, restLast - 10).Select(i => (long)i));
        source.Requests.ShouldBe([11L]);
    }

    [Fact]
    public async Task Fill_NothingMissed_EmitsNothing()
    {
        var source = new FakeAggTrades(1, 0);

        (await Filler(source).FillAsync(BtcId, "BTCUSDT", 41, () => null, _output.Writer, Ct)).ShouldBe(new GapFillResult(0, null, Complete: true));

        source.Requests.ShouldBe([42L]);
        Emitted().ShouldBeEmpty();
    }

    [Fact]
    public async Task Fill_PageCap_StopsAfterMaxPages()
    {
        var source = new FakeAggTrades(1, 5000);

        var emitted = await Filler(source, maxPages: 2).FillAsync(BtcId, "BTCUSDT", 0, () => null, _output.Writer, Ct);

        emitted.ShouldBe(new GapFillResult(2000, 2000, Complete: false)); // the gap stays open after the cap
        source.Requests.ShouldBe([1L, 1001L]);
        new GapFiller(source, _clock, NullLogger<GapFiller>.Instance, Metrics()).MaxPages.ShouldBe(GapFiller.DefaultMaxPages);
    }

    [Fact]
    public async Task Fill_TransientFailure_RetriesThePage()
    {
        var source = new FakeAggTrades(1, 3);
        source.Failures.Enqueue(new BinanceApiException("unavailable", HttpStatusCode.ServiceUnavailable, isTransient: true, null));

        var fill = Filler(source).FillAsync(BtcId, "BTCUSDT", 0, () => null, _output.Writer, Ct);
        // Advance until the retry delay (registered some time after the first attempt failed) has passed.
        await IngestionTestHost.WaitForAsync(
            () =>
            {
                _clock.Advance(TimeSpan.FromSeconds(1));
                return source.Requests.Count == 2;
            },
            "the retry");

        (await fill.WaitAsync(IngestionTestHost.WaitTimeout, Ct)).ShouldBe(new GapFillResult(3, 3, Complete: true));
        source.Requests.ShouldBe([1L, 1L]);
    }

    [Fact]
    public async Task Fill_PermanentFailure_GivesUpOnTheSymbol()
    {
        var source = new FakeAggTrades(1, 3);
        source.Failures.Enqueue(new BinanceApiException("bad request", HttpStatusCode.BadRequest, isTransient: false, null));

        (await Filler(source).FillAsync(BtcId, "BTCUSDT", 0, () => null, _output.Writer, Ct)).ShouldBe(new GapFillResult(0, null, Complete: false));

        source.Requests.ShouldBe([1L]);
    }

    [Fact]
    public async Task Fill_Banned_GivesUpOnTheSymbol()
    {
        var source = new FakeAggTrades(1, 3);
        source.Failures.Enqueue(new BinanceBannedException(T0.AddHours(2)));

        (await Filler(source).FillAsync(BtcId, "BTCUSDT", 0, () => null, _output.Writer, Ct)).ShouldBe(new GapFillResult(0, null, Complete: false));
    }

    [Fact]
    public async Task FirstTradeIdFrom_ReturnsTheFirstTradeAtOrAfterTheTime()
    {
        var source = new FakeAggTrades(1, 3000);
        var filler = Filler(source);

        (await filler.FirstTradeIdFromAsync("BTCUSDT", T0.AddMilliseconds(1500), Ct)).ShouldBe((true, 1500L));
        (await filler.FirstTradeIdFromAsync("BTCUSDT", T0.AddTicks(15_005_000), Ct)).ShouldBe((true, 1501L));
        (await filler.FirstTradeIdFromAsync("BTCUSDT", T0.AddSeconds(4), Ct)).ShouldBe((true, (long?)null)); // nothing traded since: done, nothing to fill
        source.TimeRequests.ShouldBe([T0.AddMilliseconds(1500), T0.AddTicks(15_005_000), T0.AddSeconds(4)]);
    }

    [Fact]
    public async Task FirstTradeIdFrom_TransientFailure_Retries_PermanentFailure_ReportsFailure()
    {
        var source = new FakeAggTrades(1, 3);
        source.Failures.Enqueue(new BinanceApiException("unavailable", HttpStatusCode.ServiceUnavailable, isTransient: true, null));
        var lookup = Filler(source).FirstTradeIdFromAsync("BTCUSDT", T0, Ct);
        await IngestionTestHost.WaitForAsync(
            () =>
            {
                _clock.Advance(TimeSpan.FromSeconds(1));
                return source.TimeRequests.Count == 2;
            },
            "the retry");
        (await lookup.WaitAsync(IngestionTestHost.WaitTimeout, Ct)).ShouldBe((true, 1L));

        source.Failures.Enqueue(new BinanceApiException("bad request", HttpStatusCode.BadRequest, isTransient: false, null));
        (await Filler(source).FirstTradeIdFromAsync("BTCUSDT", T0, Ct)).ShouldBe((false, (long?)null)); // failed: worth another try
        source.Failures.Enqueue(new BinanceBannedException(T0.AddHours(2)));
        (await Filler(source).FirstTradeIdFromAsync("BTCUSDT", T0, Ct)).ShouldBe((false, (long?)null));
    }

    private GapFiller Filler(IAggTradeSource source, int maxPages = GapFiller.DefaultMaxPages) =>
        new(source, _clock, NullLogger<GapFiller>.Instance, Metrics(), maxPages);

    private static TickPulseMetrics Metrics() =>
        new(new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>(), new IngestionState(TimeProvider.System), new SymbolIdCache());

    private Tick[] Emitted()
    {
        var ticks = new List<Tick>();
        while (_output.Reader.TryRead(out var tick))
        {
            ticks.Add(tick);
        }

        return [.. ticks];
    }

    /// <summary>Trades <c>first..last</c> (price = id), served like <c>aggTrades?fromId</c>.</summary>
    private sealed class FakeAggTrades(long first, long last) : IAggTradeSource
    {
        private readonly ConcurrentQueue<long> _requests = new();
        private readonly ConcurrentQueue<DateTime> _timeRequests = new();

        public ConcurrentQueue<Exception> Failures { get; } = new();

        public IReadOnlyList<DateTime> TimeRequests => [.. _timeRequests];

        public Action<long>? OnRequest { get; set; }

        public IReadOnlyList<long> Requests => [.. _requests];

        public Task<IReadOnlyList<(long Id, decimal Price, decimal Qty, DateTime TimeUtc)>> GetAggTradesFromAsync(
            SymbolName s, long fromId, int limit, CancellationToken ct)
        {
            s.Value.ShouldBe("BTCUSDT");
            limit.ShouldBe(GapFiller.PageSize);
            _requests.Enqueue(fromId);
            OnRequest?.Invoke(fromId);
            if (Failures.TryDequeue(out var failure))
            {
                throw failure;
            }

            IReadOnlyList<(long, decimal, decimal, DateTime)> page = [.. LongRange(Math.Max(first, fromId), last)
                .Take(limit)
                .Select(id => (id, (decimal)id, 0.5m, T0.AddMilliseconds(id)))];
            return Task.FromResult(page);
        }

        // Trade id n trades at T0 + n ms.
        public Task<IReadOnlyList<(long Id, decimal Price, decimal Qty, DateTime TimeUtc)>> GetAggTradesFromTimeAsync(
            SymbolName s, DateTime startUtc, int limit, CancellationToken ct)
        {
            s.Value.ShouldBe("BTCUSDT");
            _timeRequests.Enqueue(startUtc);
            if (Failures.TryDequeue(out var failure))
            {
                throw failure;
            }

            var firstId = Math.Max(first, (long)Math.Ceiling((startUtc - T0).TotalMilliseconds));
            IReadOnlyList<(long, decimal, decimal, DateTime)> page = [.. LongRange(firstId, last)
                .Take(limit)
                .Select(id => (id, (decimal)id, 0.5m, T0.AddMilliseconds(id)))];
            return Task.FromResult(page);
        }

        private static IEnumerable<long> LongRange(long from, long to)
        {
            for (var id = from; id <= to; id++)
            {
                yield return id;
            }
        }
    }
}

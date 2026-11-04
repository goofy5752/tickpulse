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

        public ConcurrentQueue<Exception> Failures { get; } = new();

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

        private static IEnumerable<long> LongRange(long from, long to)
        {
            for (var id = from; id <= to; id++)
            {
                yield return id;
            }
        }
    }
}

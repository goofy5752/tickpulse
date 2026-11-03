using System.Collections.Concurrent;
using System.Net;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TickPulse.Application.Abstractions;
using TickPulse.Domain;
using TickPulse.Infrastructure.Binance;
using TickPulse.Infrastructure.Ingestion;

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

        emitted.ShouldBe(2500);
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

        emitted.ShouldBe(5);
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

        emitted.ShouldBe(1499);
        source.Requests.ShouldBe([1L, 1001L]);
    }

    [Fact]
    public async Task Fill_NothingMissed_EmitsNothing()
    {
        var source = new FakeAggTrades(1, 0);

        (await Filler(source).FillAsync(BtcId, "BTCUSDT", 41, () => null, _output.Writer, Ct)).ShouldBe(0);

        source.Requests.ShouldBe([42L]);
        Emitted().ShouldBeEmpty();
    }

    [Fact]
    public async Task Fill_PageCap_StopsAfterMaxPages()
    {
        var source = new FakeAggTrades(1, 5000);

        var emitted = await Filler(source, maxPages: 2).FillAsync(BtcId, "BTCUSDT", 0, () => null, _output.Writer, Ct);

        emitted.ShouldBe(2000);
        source.Requests.ShouldBe([1L, 1001L]);
        new GapFiller(source, _clock, NullLogger<GapFiller>.Instance).MaxPages.ShouldBe(GapFiller.DefaultMaxPages);
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

        (await fill.WaitAsync(IngestionTestHost.WaitTimeout, Ct)).ShouldBe(3);
        source.Requests.ShouldBe([1L, 1L]);
    }

    [Fact]
    public async Task Fill_PermanentFailure_GivesUpOnTheSymbol()
    {
        var source = new FakeAggTrades(1, 3);
        source.Failures.Enqueue(new BinanceApiException("bad request", HttpStatusCode.BadRequest, isTransient: false, null));

        (await Filler(source).FillAsync(BtcId, "BTCUSDT", 0, () => null, _output.Writer, Ct)).ShouldBe(0);

        source.Requests.ShouldBe([1L]);
    }

    [Fact]
    public async Task Fill_Banned_GivesUpOnTheSymbol()
    {
        var source = new FakeAggTrades(1, 3);
        source.Failures.Enqueue(new BinanceBannedException(T0.AddHours(2)));

        (await Filler(source).FillAsync(BtcId, "BTCUSDT", 0, () => null, _output.Writer, Ct)).ShouldBe(0);
    }

    private GapFiller Filler(IAggTradeSource source, int maxPages = GapFiller.DefaultMaxPages) =>
        new(source, _clock, NullLogger<GapFiller>.Instance, maxPages);

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

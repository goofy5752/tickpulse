using Microsoft.Extensions.Time.Testing;
using Npgsql;
using TickPulse.Application.Abstractions;
using TickPulse.Domain;
using TickPulse.Infrastructure;
using TickPulse.Infrastructure.Persistence;
using TickPulse.Integration.Tests.Fixtures;

namespace TickPulse.Integration.Tests.Persistence;

public sealed class MarketDataQueriesTests(TimescaleFixture db) : IAsyncLifetime
{
    private const int Btc = 1;
    private const int Ada = 2;
    private const int Eth = 3;

    // Monday 2026-03-02 10:00 UTC. "Now" is a day later, so every tick is well inside the replay window.
    private static readonly DateTime Start = new(2026, 3, 2, 10, 0, 0, DateTimeKind.Utc);

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(Start.AddDays(1)));
    private NpgsqlDataSource _dataSource = null!;
    private NpgsqlDataSource _writerDataSource = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await db.ResetAsync();
        _dataSource = DependencyInjection.BuildDataSource(db.ConnectionString, null);
        _writerDataSource = DependencyInjection.BuildTickWriterDataSource(db.ConnectionString, null);
    }

    public async ValueTask DisposeAsync()
    {
        await _writerDataSource.DisposeAsync();
        await _dataSource.DisposeAsync();
    }

    [Fact]
    public async Task Average_EqualsRawAvg_WithDuplicatesReplayed()
    {
        var from = Start.AddMinutes(7).AddTicks(131_234_560);   // 10:07:13.123456
        var to = Start.AddMinutes(83).AddTicks(416_543_210);    // 11:23:41.654321
        var rng = new Random(20261103);
        var ticks = new List<Tick>();
        for (var i = 1; i <= 2_000; i++)
        {
            // Microsecond-resolution times over 90 minutes, prices and quantities with up to 8 dp.
            var time = Start.AddTicks(rng.NextInt64(0, 90L * 60 * 1_000_000) * 10);
            ticks.Add(NewTick(Btc, i, RandomDecimal(rng, 10_000_000_000_000), RandomDecimal(rng, 1_000_000_000), time));
        }

        // Ticks exactly on the edges of every sub-range: from, to, and the full-minute boundaries next to them.
        DateTime[] boundaries = [from, to, Period.OneMinute.AlignUp(from), Period.OneMinute.AlignDown(to), from.AddTicks(-10), to.AddTicks(10)];
        for (var i = 0; i < boundaries.Length; i++)
        {
            ticks.Add(NewTick(Btc, 10_000 + i, RandomDecimal(rng, 10_000_000_000_000), 1m, boundaries[i]));
        }

        // Another symbol in the same minutes must not leak into the result.
        for (var i = 1; i <= 200; i++)
        {
            ticks.Add(NewTick(Eth, i, RandomDecimal(rng, 1_000_000_000), 1m, Start.AddSeconds(i * 20)));
        }

        var writer = Writer();
        foreach (var batch in ticks.Chunk(137))
        {
            await writer.WriteBatchAsync(batch, Ct);
            await writer.WriteBatchAsync(batch, Ct);
        }

        var replayed = 0;
        foreach (var batch in ticks.Chunk(500))
        {
            replayed += await writer.WriteBatchAsync(batch, Ct);
        }

        replayed.ShouldBe(0);

        var result = await Queries().GetAverageAsync(Btc, from, to, Ct);

        var raw = await RawWindowAsync(Btc, from, to);
        result.ShouldNotBeNull();
        result.Average.ShouldBe(Math.Round(raw.Sum / raw.Count, 8, MidpointRounding.ToEven));
        result.SampleCount.ShouldBe(raw.Count);
        result.SampleCount.ShouldBe(ticks.Count(t => t.SymbolId == Btc && t.TradeTimeUtc >= from && t.TradeTimeUtc <= to));
        result.FirstTickUtc.ShouldBe(raw.First);
        result.LastTickUtc.ShouldBe(raw.Last);
        result.FirstTickUtc.Kind.ShouldBe(DateTimeKind.Utc);
        result.LastTickUtc.Kind.ShouldBe(DateTimeKind.Utc);
    }

    [Fact]
    public async Task Average_FromAndToInSameMinute_UsesRawTicksOnly()
    {
        var minute = Start.AddMinutes(5);
        await Writer().WriteBatchAsync(
        [
            NewTick(Btc, 1, 10m, 1m, minute.AddSeconds(5)),
            NewTick(Btc, 2, 20m, 1m, minute.AddSeconds(15)),
            NewTick(Btc, 3, 31m, 1m, minute.AddSeconds(25)),
            NewTick(Btc, 4, 40m, 1m, minute.AddSeconds(45)),
        ], Ct);

        var result = await Queries().GetAverageAsync(Btc, minute.AddSeconds(10), minute.AddSeconds(30), Ct);

        result.ShouldBe(new AverageWindow(25.5m, minute.AddSeconds(15), minute.AddSeconds(25), 2));
    }

    [Fact]
    public async Task Average_MinuteAlignedBounds_IncludesTickAtTo()
    {
        await Writer().WriteBatchAsync(
        [
            NewTick(Btc, 1, 1m, 1m, Start.AddTicks(-10)),
            NewTick(Btc, 2, 2m, 1m, Start),
            NewTick(Btc, 3, 3m, 1m, Start.AddSeconds(90)),
            NewTick(Btc, 4, 5m, 1m, Start.AddMinutes(2)),
            NewTick(Btc, 5, 100m, 1m, Start.AddMinutes(2).AddTicks(10)),
        ], Ct);

        var result = await Queries().GetAverageAsync(Btc, Start, Start.AddMinutes(2), Ct);

        // 10/3 = 3.333333333… rounds to 8 dp.
        result.ShouldBe(new AverageWindow(3.33333333m, Start, Start.AddMinutes(2), 3));
    }

    [Fact]
    public async Task Average_PointWindow_CountsTickAtThatInstant()
    {
        await Writer().WriteBatchAsync([NewTick(Btc, 1, 7m, 1m, Start), NewTick(Btc, 2, 9m, 1m, Start.AddSeconds(1))], Ct);

        var result = await Queries().GetAverageAsync(Btc, Start, Start, Ct);

        result.ShouldBe(new AverageWindow(7m, Start, Start, 1));
    }

    [Fact]
    public async Task Average_IgnoresBackfillCandles()
    {
        Kline[] klines = [.. Enumerable.Range(0, 5).Select(i => Candle(Start.AddMinutes(i), close: 1_000m))];
        (await CandleWriter().InsertBackfillAsync(Btc, klines, Ct)).ShouldBe(5);
        await Writer().WriteBatchAsync([NewTick(Btc, 1, 10m, 1m, Start.AddMinutes(6)), NewTick(Btc, 2, 20m, 1m, Start.AddMinutes(7))], Ct);

        var queries = Queries();
        var result = await queries.GetAverageAsync(Btc, Start.AddSeconds(-30), Start.AddMinutes(10), Ct);
        var backfillOnly = await queries.GetAverageAsync(Btc, Start, Start.AddMinutes(4).AddSeconds(30), Ct);

        result.ShouldBe(new AverageWindow(15m, Start.AddMinutes(6), Start.AddMinutes(7), 2));
        backfillOnly.ShouldBeNull();
    }

    [Fact]
    public async Task Average_InteriorMinutesFromStreamCandles_EdgeMinutesFromRawTicks()
    {
        // Ticks at :10 and :40 of minutes 10:00..10:04; the window [10:00:30, 10:04:30] holds 8 of them.
        var ticks = new List<Tick>();
        for (var m = 0; m < 5; m++)
        {
            ticks.Add(NewTick(Btc, (m * 2) + 1, 100m + m, 1m, Start.AddMinutes(m).AddSeconds(10)));
            ticks.Add(NewTick(Btc, (m * 2) + 2, 200m + m, 1m, Start.AddMinutes(m).AddSeconds(40)));
        }

        await Writer().WriteBatchAsync(ticks, Ct);
        var from = Start.AddSeconds(30);
        var to = Start.AddMinutes(4).AddSeconds(30);
        var inWindow = ticks.Where(t => t.TradeTimeUtc >= from && t.TradeTimeUtc <= to).ToList();
        inWindow.Count.ShouldBe(8);
        var rawSum = inWindow.Sum(t => t.Price);
        var queries = Queries();

        (await queries.GetAverageAsync(Btc, from, to, Ct))!.Average.ShouldBe(Math.Round(rawSum / 8, 8, MidpointRounding.ToEven));

        // An interior minute is read from its stream candle: inflating its price_sum moves the average.
        await BumpPriceSumAsync(Start.AddMinutes(2), 3m);
        (await queries.GetAverageAsync(Btc, from, to, Ct))!.Average.ShouldBe(Math.Round((rawSum + 3m) / 8, 8, MidpointRounding.ToEven));
        await BumpPriceSumAsync(Start.AddMinutes(2), -3m);

        // The edge minutes are read from the raw ticks: their candles do not matter.
        await BumpPriceSumAsync(Start, 1_000m);
        await BumpPriceSumAsync(Start.AddMinutes(4), 1_000m);
        var result = await queries.GetAverageAsync(Btc, from, to, Ct);
        result.ShouldBe(new AverageWindow(Math.Round(rawSum / 8, 8, MidpointRounding.ToEven), inWindow[0].TradeTimeUtc, inWindow[^1].TradeTimeUtc, 8));
    }

    [Fact]
    public async Task StreamQueries_UsePartialStreamIndex()
    {
        await SeedMixedSourcesAsync();
        var now = Start.AddDays(1);

        var firstStream = await ExplainAsync(MarketDataQueries.FirstStreamMinuteSql, Int(Btc));
        var average = await ExplainAsync(
            MarketDataQueries.AverageSql, Int(Btc), Ts(now.AddDays(-1)), Ts(now.AddDays(-1)), Ts(now), Ts(now));

        // The first stream minute would have to filter its way past every backfill minute on the primary key, so the
        // partial index wins by a wide margin. For the average both indexes give the same bounded range on
        // (symbol_id, bucket_start) and the choice is cost-only: either is fine, a sequential scan is not.
        firstStream.ShouldContain("ix_candles_1m_stream");
        average.ShouldMatch(@"(Index|Index Only|Bitmap Index) Scan( Backward)? using (ix_candles_1m_stream|pk_candles_1m)");
        average.ShouldNotContain("Seq Scan on candles_1m");
    }

    [Fact]
    public async Task CandlesQuery_AnchorsOnceThenRangeScansPrimaryKey()
    {
        await SeedMixedSourcesAsync();

        var plan = await ExplainAsync(
            MarketDataQueries.CandlesSql,
            Int(Btc),
            new NpgsqlParameter<TimeSpan> { TypedValue = Period.OneDay.Length },
            Ts(Period.Origin),
            new NpgsqlParameter<TimeSpan> { TypedValue = Period.OneDay.Length * 299 });

        // The anchor is an InitPlan (evaluated once); the outer scan is a primary-key range scan bounded by it.
        plan.ShouldContain("InitPlan");
        // A plain or a bitmap scan of the primary key are both a range scan bounded by the anchor.
        plan.ShouldMatch(@"(Index|Index Only|Bitmap Index) Scan using pk_candles_1m on candles_1m\b[\s\S]*Index Cond: \(\(symbol_id = [^)]+\) AND \(bucket_start >= \(\(InitPlan \d+\)");
        plan.ShouldNotContain("Seq Scan");
    }

    [Fact]
    public async Task Average_LessThanWindow_ReportsActualCoverage()
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var first = now.AddHours(-2).AddTicks(123_450);
        var last = now.AddHours(-1).AddSeconds(-17);
        await Writer().WriteBatchAsync(
        [
            NewTick(Btc, 1, 100m, 1m, first),
            NewTick(Btc, 2, 101m, 1m, now.AddMinutes(-90)),
            NewTick(Btc, 3, 102m, 1m, last),
        ], Ct);

        var result = await Queries().GetAverageAsync(Btc, now.AddHours(-24), now, Ct);

        result.ShouldBe(new AverageWindow(101m, first, last, 3));
    }

    [Fact]
    public async Task Average_NoTicks_ReturnsNull()
    {
        await Writer().WriteBatchAsync([NewTick(Eth, 1, 5m, 1m, Start.AddMinutes(3))], Ct);

        var result = await Queries().GetAverageAsync(Btc, Start, Start.AddHours(1), Ct);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task Average_ToBeforeFrom_Throws()
    {
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => Queries().GetAverageAsync(Btc, Start, Start.AddTicks(-10), Ct));
    }

    [Fact]
    public async Task Average_NonUtcBounds_Throw()
    {
        var local = DateTime.SpecifyKind(Start, DateTimeKind.Unspecified);
        await Should.ThrowAsync<ArgumentException>(() => Queries().GetAverageAsync(Btc, local, Start.AddHours(1), Ct));
        await Should.ThrowAsync<ArgumentException>(() => Queries().GetAverageAsync(Btc, Start, local.AddHours(1), Ct));
    }

    [Fact]
    public async Task Closes_Week_GroupsMondayBuckets()
    {
        var monday = Start.Date; // 2026-03-02 00:00 UTC, a Monday
        var previousMonday = monday.AddDays(-7);
        (await CandleWriter().InsertBackfillAsync(
            Btc,
            [
                Candle(previousMonday, close: 5m),
                Candle(monday.AddMinutes(-1), close: 10m),  // Sunday 23:59, still the previous week
                Candle(monday, close: 20m),                 // Monday 00:00, the next week
                Candle(monday.AddDays(3), close: 30m),
            ],
            Ct)).ShouldBe(4);

        var closes = await Queries().GetClosesAsync(Btc, Period.OneWeek, new SmaWindow(previousMonday, monday.AddDays(7)), Ct);

        closes.ShouldBe([new PricePoint(previousMonday, 10m), new PricePoint(monday, 30m)]);
        closes.ShouldAllBe(c => c.BucketStart.Kind == DateTimeKind.Utc);
    }

    [Fact]
    public async Task Closes_SkipsEmptyBuckets()
    {
        (await CandleWriter().InsertBackfillAsync(
            Btc,
            [
                Candle(Start.AddMinutes(-1), close: 99m),   // before the window
                Candle(Start, close: 1m),
                Candle(Start.AddMinutes(4), close: 2m),
                Candle(Start.AddMinutes(12), close: 3m),
                Candle(Start.AddMinutes(15), close: 98m),   // at the exclusive end
            ],
            Ct)).ShouldBe(5);
        await CandleWriter().InsertBackfillAsync(Eth, [Candle(Start.AddMinutes(6), close: 97m)], Ct);

        var closes = await Queries().GetClosesAsync(Btc, Period.FiveMinutes, new SmaWindow(Start, Start.AddMinutes(15)), Ct);

        closes.ShouldBe([new PricePoint(Start, 2m), new PricePoint(Start.AddMinutes(10), 3m)]);
    }

    [Fact]
    public async Task Closes_MixesBackfillAndStream()
    {
        (await CandleWriter().InsertBackfillAsync(
            Btc,
            [
                Candle(Start, close: 1m),
                Candle(Start.AddMinutes(1), close: 2m),
                Candle(Start.AddMinutes(6), close: 4m),
                Candle(Start.AddMinutes(8), close: 6m),
            ],
            Ct)).ShouldBe(4);
        await Writer().WriteBatchAsync(
        [
            NewTick(Btc, 1, 3m, 1m, Start.AddMinutes(2).AddSeconds(10)),   // stream minute after the backfill ones
            NewTick(Btc, 2, 5m, 1m, Start.AddMinutes(7).AddSeconds(10)),   // stream minute before a backfill one
            NewTick(Btc, 3, 7m, 1m, Start.AddMinutes(10).AddSeconds(10)),  // stream-only bucket
        ], Ct);

        var closes = await Queries().GetClosesAsync(Btc, Period.FiveMinutes, new SmaWindow(Start, Start.AddMinutes(15)), Ct);

        closes.ShouldBe(
        [
            new PricePoint(Start, 3m),
            new PricePoint(Start.AddMinutes(5), 6m),
            new PricePoint(Start.AddMinutes(10), 7m),
        ]);
    }

    [Fact]
    public async Task Closes_EmptyWindow_ReturnsEmpty()
    {
        await CandleWriter().InsertBackfillAsync(Btc, [Candle(Start, close: 1m)], Ct);

        var closes = await Queries().GetClosesAsync(Btc, Period.OneMinute, new SmaWindow(Start, Start), Ct);

        closes.ShouldBeEmpty();
    }

    [Fact]
    public async Task Candles_ReturnsOldestFirst_LimitRespected()
    {
        // 25 minutes = five 5-minute buckets; minute i has open i, high i+0.5, low i-0.5, close i+0.25, volume 1.
        Kline[] klines = [.. Enumerable.Range(0, 25).Select(i => new Kline(Start.AddMinutes(i), i, i + 0.5m, i - 0.5m, i + 0.25m, 1m, 2m))];
        (await CandleWriter().InsertBackfillAsync(Btc, klines, Ct)).ShouldBe(25);
        await CandleWriter().InsertBackfillAsync(Eth, [Candle(Start.AddMinutes(30), close: 1m)], Ct);

        var candles = await Queries().GetCandlesAsync(Btc, Period.FiveMinutes, 3, Ct);

        candles.ShouldBe(
        [
            new CandleRow(Start.AddMinutes(10), 10m, 14.5m, 9.5m, 14.25m, 5m),
            new CandleRow(Start.AddMinutes(15), 15m, 19.5m, 14.5m, 19.25m, 5m),
            new CandleRow(Start.AddMinutes(20), 20m, 24.5m, 19.5m, 24.25m, 5m),
        ]);
        candles.ShouldAllBe(c => c.BucketStart.Kind == DateTimeKind.Utc);

        // 1w: weeks start Monday 00:00 UTC, so Sunday 23:59 and Monday 00:00 land in different candles.
        var monday = Start.Date; // 2026-03-02
        (await CandleWriter().InsertBackfillAsync(
            Ada,
            [
                Candle(monday.AddDays(-14), close: 1m),                 // two weeks back, outside limit 2
                Candle(monday.AddDays(-7).AddHours(6), close: 2m),
                Candle(monday.AddMinutes(-1), close: 3m),               // Sunday 23:59
                Candle(monday, close: 4m),                              // Monday 00:00
                Candle(monday.AddDays(2).AddHours(12), close: 5m),
            ],
            Ct)).ShouldBe(5);

        var weeks = await Queries().GetCandlesAsync(Ada, Period.OneWeek, 2, Ct);

        weeks.ShouldBe(
        [
            new CandleRow(monday.AddDays(-7), 2m, 4m, 1m, 3m, 2m),
            new CandleRow(monday, 4m, 6m, 3m, 5m, 2m),
        ]);
    }

    [Fact]
    public async Task Candles_StreamMinutes_AggregateTicks()
    {
        await Writer().WriteBatchAsync(
        [
            NewTick(Btc, 1, 10m, 2m, Start.AddSeconds(1)),
            NewTick(Btc, 2, 12m, 1m, Start.AddSeconds(2)),
            NewTick(Btc, 3, 8m, 0.5m, Start.AddMinutes(1)),
            NewTick(Btc, 4, 11m, 0.25m, Start.AddMinutes(1).AddSeconds(1)),
        ], Ct);

        var candles = await Queries().GetCandlesAsync(Btc, Period.OneMinute, 10, Ct);

        candles.ShouldBe(
        [
            new CandleRow(Start, 10m, 12m, 10m, 12m, 3m),
            new CandleRow(Start.AddMinutes(1), 8m, 11m, 8m, 11m, 0.75m),
        ]);
    }

    [Fact]
    public async Task Candles_EmptyPeriods_AreAbsent()
    {
        // The window is the last `limit` periods up to the latest candle; periods without candles are skipped.
        (await CandleWriter().InsertBackfillAsync(
            Btc,
            [Candle(Start, close: 1m), Candle(Start.AddMinutes(10), close: 2m), Candle(Start.AddMinutes(20), close: 3m)],
            Ct)).ShouldBe(3);

        var candles = await Queries().GetCandlesAsync(Btc, Period.FiveMinutes, 4, Ct);

        candles.Select(c => (c.BucketStart, c.Close)).ShouldBe([(Start.AddMinutes(10), 2m), (Start.AddMinutes(20), 3m)]);
    }

    [Fact]
    public async Task Candles_NoData_ReturnsEmpty()
    {
        (await Queries().GetCandlesAsync(Btc, Period.OneDay, 300, Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Candles_LimitOutOfRange_Throws()
    {
        var queries = Queries();
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => queries.GetCandlesAsync(Btc, Period.OneMinute, 0, Ct));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(
            () => queries.GetCandlesAsync(Btc, Period.OneWeek, MarketDataQueries.MaxCandleLimit + 1, Ct));
    }

    [Fact]
    public async Task Candles_MaxLimit_ReachesBackExactlyThatManyPeriods()
    {
        var monday = Start.Date;
        var limit = MarketDataQueries.MaxCandleLimit;
        var oldest = monday.AddDays(-7 * (limit - 1));
        (await CandleWriter().InsertBackfillAsync(
            Btc,
            [Candle(oldest.AddDays(-7), close: 1m), Candle(oldest, close: 2m), Candle(monday, close: 3m)],
            Ct)).ShouldBe(3);

        var weeks = await Queries().GetCandlesAsync(Btc, Period.OneWeek, limit, Ct);

        weeks.Select(c => (c.BucketStart, c.Close)).ShouldBe([(oldest, 2m), (monday, 3m)]);
    }

    [Fact]
    public async Task CloseAtOrBefore_ReturnsLatestNotAfter()
    {
        await CandleWriter().InsertBackfillAsync(Btc, [Candle(Start, close: 1m), Candle(Start.AddMinutes(10), close: 3m)], Ct);
        await CandleWriter().InsertBackfillAsync(Eth, [Candle(Start.AddMinutes(6), close: 50m)], Ct);
        await Writer().WriteBatchAsync([NewTick(Btc, 1, 2m, 1m, Start.AddMinutes(5).AddSeconds(30))], Ct);
        var queries = Queries();

        (await queries.GetCloseAtOrBeforeAsync(Btc, Start.AddMinutes(-1), Ct)).ShouldBeNull();
        (await queries.GetCloseAtOrBeforeAsync(Btc, Start, Ct)).ShouldBe(1m);
        (await queries.GetCloseAtOrBeforeAsync(Btc, Start.AddMinutes(9).AddSeconds(59), Ct)).ShouldBe(2m);
        (await queries.GetCloseAtOrBeforeAsync(Btc, Start.AddMinutes(10), Ct)).ShouldBe(3m);
        (await queries.GetCloseAtOrBeforeAsync(Btc, Start.AddDays(1), Ct)).ShouldBe(3m);
    }

    [Fact]
    public async Task ClosesAtOrBefore_ReturnsTheLatestPerSymbolInOneQuery()
    {
        await CandleWriter().InsertBackfillAsync(Btc, [Candle(Start, close: 1m), Candle(Start.AddMinutes(10), close: 3m)], Ct);
        await CandleWriter().InsertBackfillAsync(Eth, [Candle(Start.AddMinutes(6), close: 50m)], Ct);
        await Writer().WriteBatchAsync([NewTick(Btc, 1, 2m, 1m, Start.AddMinutes(5).AddSeconds(30))], Ct);
        var queries = Queries();

        // ADA has no candle and 999 does not exist: both are absent. A repeated id is answered once.
        var closes = await queries.GetClosesAtOrBeforeAsync([Btc, Ada, Eth, 999, Btc], Start.AddMinutes(9).AddSeconds(59), Ct);
        closes.OrderBy(c => c.Key).Select(c => (c.Key, c.Value))
            .ShouldBe([(Btc, new PricePoint(Start.AddMinutes(5), 2m)), (Eth, new PricePoint(Start.AddMinutes(6), 50m))]);
        closes.Values.ShouldAllBe(p => p.BucketStart.Kind == DateTimeKind.Utc);

        (await queries.GetClosesAtOrBeforeAsync([Btc, Eth], Start.AddMinutes(5), Ct))
            .OrderBy(c => c.Key).Select(c => (c.Key, c.Value.Close)).ShouldBe([(Btc, 2m)]);
        (await queries.GetClosesAtOrBeforeAsync([Btc, Eth], Start.AddMinutes(-1), Ct)).ShouldBeEmpty();
        (await queries.GetClosesAtOrBeforeAsync([], Start.AddDays(1), Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task ClosesAtOrBeforeQuery_ProbesThePrimaryKeyPerSymbol()
    {
        await SeedMixedSourcesAsync();

        var plan = await ExplainAsync(
            MarketDataQueries.ClosesAtOrBeforeSql,
            new NpgsqlParameter<int[]> { TypedValue = [Btc, Ada, Eth] },
            Ts(Start.AddDays(1)));

        // One backward index probe per symbol (LIMIT 1 inside the lateral join), never a scan of the whole table.
        plan.ShouldContain("Limit");
        plan.ShouldMatch(@"Index (Only )?Scan Backward using pk_candles_1m on candles_1m");
        plan.ShouldNotContain("Seq Scan on candles_1m");
    }

    [Fact]
    public async Task ApproximateTickCount_AfterAnalyze_MatchesRows()
    {
        var queries = Queries();
        await ExecuteAsync("ANALYZE price_ticks");
        (await queries.ApproximateTickCountAsync(Ct)).ShouldBe(0);

        Tick[] ticks = [.. Enumerable.Range(1, 1_234).Select(i => NewTick(i % 2 == 0 ? Btc : Eth, i, 1m, 1m, Start.AddSeconds(i)))];
        await Writer().WriteBatchAsync(ticks, Ct);
        await ExecuteAsync("ANALYZE price_ticks");

        (await queries.ApproximateTickCountAsync(Ct)).ShouldBe(1_234);
    }

    [Fact]
    public async Task FirstStreamMinute_IgnoresBackfill()
    {
        var queries = Queries();
        await CandleWriter().InsertBackfillAsync(Btc, [Candle(Start.AddMinutes(-30), close: 1m)], Ct);
        (await queries.FirstStreamMinuteAsync(Btc, Ct)).ShouldBeNull();

        await Writer().WriteBatchAsync(
        [
            NewTick(Btc, 1, 1m, 1m, Start.AddMinutes(5).AddSeconds(42)),
            NewTick(Btc, 2, 1m, 1m, Start.AddMinutes(8)),
            NewTick(Eth, 1, 1m, 1m, Start.AddMinutes(1)),
        ], Ct);

        var first = await queries.FirstStreamMinuteAsync(Btc, Ct);
        first.ShouldBe(Start.AddMinutes(5));
        first!.Value.Kind.ShouldBe(DateTimeKind.Utc);
    }

    [Fact]
    public async Task FirstStreamMinuteAtOrAfter_IgnoresBackfillAndEarlierMinutes()
    {
        var queries = Queries();
        await CandleWriter().InsertBackfillAsync(Btc, [Candle(Start.AddMinutes(20), close: 1m)], Ct);
        await Writer().WriteBatchAsync(
        [
            NewTick(Btc, 1, 1m, 1m, Start.AddMinutes(5).AddSeconds(42)),
            NewTick(Btc, 2, 1m, 1m, Start.AddMinutes(30).AddSeconds(1)),
            NewTick(Eth, 1, 1m, 1m, Start.AddMinutes(10)),
        ], Ct);

        // The minute containing the bound starts before it, so it does not count.
        (await queries.FirstStreamMinuteAtOrAfterAsync(Btc, Start.AddMinutes(5).AddSeconds(1), Ct)).ShouldBe(Start.AddMinutes(30));
        (await queries.FirstStreamMinuteAtOrAfterAsync(Btc, Start.AddMinutes(5), Ct)).ShouldBe(Start.AddMinutes(5));
        var first = await queries.FirstStreamMinuteAtOrAfterAsync(Btc, Start.AddMinutes(6), Ct);
        first.ShouldBe(Start.AddMinutes(30));
        first!.Value.Kind.ShouldBe(DateTimeKind.Utc);
        (await queries.FirstStreamMinuteAtOrAfterAsync(Btc, Start.AddMinutes(31), Ct)).ShouldBeNull();
        (await queries.FirstStreamMinuteAtOrAfterAsync(Ada, Start, Ct)).ShouldBeNull();
        await Should.ThrowAsync<ArgumentException>(() => queries.FirstStreamMinuteAtOrAfterAsync(Btc, DateTime.SpecifyKind(Start, DateTimeKind.Unspecified), Ct));
    }

    [Fact]
    public async Task LastMinuteBefore_CountsEverySource()
    {
        var queries = Queries();
        (await queries.LastMinuteBeforeAsync(Btc, Start.AddHours(1), Ct)).ShouldBeNull();

        await Writer().WriteBatchAsync([NewTick(Btc, 1, 1m, 1m, Start.AddMinutes(5).AddSeconds(42)), NewTick(Eth, 1, 1m, 1m, Start.AddMinutes(40))], Ct);
        await CandleWriter().InsertBackfillAsync(Btc, [Candle(Start.AddMinutes(9), close: 1m), Candle(Start.AddMinutes(50), close: 1m)], Ct);

        (await queries.LastMinuteBeforeAsync(Btc, Start.AddMinutes(9), Ct)).ShouldBe(Start.AddMinutes(5));
        var last = await queries.LastMinuteBeforeAsync(Btc, Start.AddMinutes(50), Ct);
        last.ShouldBe(Start.AddMinutes(9));
        last!.Value.Kind.ShouldBe(DateTimeKind.Utc);
        (await queries.LastMinuteBeforeAsync(Btc, Start.AddMinutes(5), Ct)).ShouldBeNull();
        (await queries.LastMinuteBeforeAsync(Ada, Start.AddHours(1), Ct)).ShouldBeNull();
        await Should.ThrowAsync<ArgumentException>(() => queries.LastMinuteBeforeAsync(Btc, DateTime.SpecifyKind(Start, DateTimeKind.Unspecified), Ct));
    }

    [Fact]
    public async Task BackfillRangeBefore_IgnoresStreamOtherSymbolsAndLaterMinutes()
    {
        var queries = Queries();
        (await queries.BackfillRangeBeforeAsync(Btc, Start.AddHours(1), Ct)).ShouldBeNull();

        await CandleWriter().InsertBackfillAsync(
            Btc, [Candle(Start, close: 1m), Candle(Start.AddMinutes(3), close: 2m), Candle(Start.AddMinutes(9), close: 3m)], Ct);
        await CandleWriter().InsertBackfillAsync(Eth, [Candle(Start.AddMinutes(-7), close: 1m), Candle(Start.AddMinutes(20), close: 1m)], Ct);
        await Writer().WriteBatchAsync([NewTick(Btc, 1, 1m, 1m, Start.AddMinutes(-5).AddSeconds(1))], Ct);

        (await queries.BackfillRangeBeforeAsync(Btc, Start.AddMinutes(9), Ct)).ShouldBe(new MinuteRange(Start, Start.AddMinutes(3)));
        var range = (await queries.BackfillRangeBeforeAsync(Btc, Start.AddHours(1), Ct)).ShouldNotBeNull();
        range.ShouldBe(new MinuteRange(Start, Start.AddMinutes(9)));
        range.First.Kind.ShouldBe(DateTimeKind.Utc);
        range.Last.Kind.ShouldBe(DateTimeKind.Utc);
        (await queries.BackfillRangeBeforeAsync(Btc, Start.AddMinutes(1), Ct)).ShouldBe(new MinuteRange(Start, Start));
        (await queries.BackfillRangeBeforeAsync(Btc, Start, Ct)).ShouldBeNull();
        (await queries.BackfillRangeBeforeAsync(Ada, Start.AddHours(1), Ct)).ShouldBeNull();
        await Should.ThrowAsync<ArgumentException>(() => queries.BackfillRangeBeforeAsync(Btc, DateTime.SpecifyKind(Start, DateTimeKind.Unspecified), Ct));
    }

    private static decimal RandomDecimal(Random rng, long maxUnitsExclusive) => rng.NextInt64(1, maxUnitsExclusive) / 100_000_000m;

    private static Tick NewTick(int symbolId, long id, decimal price, decimal qty, DateTime time) => new(symbolId, id, price, qty, time, time);

    private static Kline Candle(DateTime openTime, decimal close) => new(openTime, close, close + 1m, close - 1m, close, 1m, close);

    private MarketDataQueries Queries() => new(_dataSource);

    private TickWriter Writer() => new(_writerDataSource, _clock);

    private CandleWriter CandleWriter() => new(_dataSource);

    private static NpgsqlParameter<int> Int(int value) => new() { TypedValue = value };

    private static NpgsqlParameter<DateTime> Ts(DateTime value) => new() { TypedValue = value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.TimestampTz };

    private async Task BumpPriceSumAsync(DateTime bucketStart, decimal delta)
    {
        await using var command = _dataSource.CreateCommand(
            "UPDATE candles_1m SET price_sum = price_sum + $3 WHERE symbol_id = $1 AND bucket_start = $2 AND source = 1");
        command.Parameters.Add(Int(Btc));
        command.Parameters.Add(Ts(bucketStart));
        command.Parameters.Add(new NpgsqlParameter<decimal> { TypedValue = delta });
        (await command.ExecuteNonQueryAsync(Ct)).ShouldBe(1);
    }

    /// <summary>
    /// Several days of backfill minutes for two symbols plus a stretch of stream minutes, then a rewritten table and
    /// fresh statistics. Earlier tests insert and delete many more minutes; the rewrite drops their dead heap and index
    /// pages, which would otherwise tip the cost-based choice of scan (partial index or primary key, plain or bitmap).
    /// </summary>
    private async Task SeedMixedSourcesAsync()
    {
        var backfillStart = Start.AddDays(-5);
        Kline[] backfill = [.. Enumerable.Range(0, 3_000).Select(i => Candle(backfillStart.AddMinutes(i), close: 10m + (i % 7)))];
        await CandleWriter().InsertBackfillAsync(Btc, backfill, Ct);
        await CandleWriter().InsertBackfillAsync(Eth, backfill, Ct);
        await Writer().WriteBatchAsync([.. Enumerable.Range(1, 200).Select(i => NewTick(Btc, i, 10m, 1m, Start.AddSeconds(i * 15)))], Ct);
        // VACUUM FULL takes an ACCESS EXCLUSIVE lock on candles_1m; that is fine here because the integration tests
        // run serially (Parallelization(Mode = ParallelMode.None) in TimescaleFixture).
        await ExecuteAsync("VACUUM (FULL, ANALYZE) candles_1m");
    }

    /// <summary>
    /// The plan of <paramref name="sql"/> with sequential scans disabled, so the test data can stay small and the
    /// assertion pins which index a query is able to use rather than a data-size-dependent cost choice.
    /// </summary>
    private async Task<string> ExplainAsync(string sql, params NpgsqlParameter[] parameters)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using (var off = new NpgsqlCommand("SET LOCAL enable_seqscan = off", connection, transaction))
        {
            await off.ExecuteNonQueryAsync(Ct);
        }

        await using var command = new NpgsqlCommand("EXPLAIN " + sql, connection, transaction);
        command.Parameters.AddRange(parameters);
        var lines = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync(Ct))
        {
            while (await reader.ReadAsync(Ct))
            {
                lines.Add(reader.GetString(0));
            }
        }

        await transaction.RollbackAsync(Ct);
        return string.Join('\n', lines);
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var command = _dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private async Task<(decimal Sum, long Count, DateTime First, DateTime Last)> RawWindowAsync(int symbolId, DateTime from, DateTime to)
    {
        await using var command = _dataSource.CreateCommand(
            """
            SELECT sum(price), count(*), min(trade_time), max(trade_time)
              FROM price_ticks
             WHERE symbol_id = $1 AND trade_time BETWEEN $2 AND $3
            """);
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = symbolId });
        command.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = from });
        command.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = to });
        await using var reader = await command.ExecuteReaderAsync(Ct);
        (await reader.ReadAsync(Ct)).ShouldBeTrue();
        return (reader.GetDecimal(0), reader.GetInt64(1), reader.GetDateTime(2), reader.GetDateTime(3));
    }
}

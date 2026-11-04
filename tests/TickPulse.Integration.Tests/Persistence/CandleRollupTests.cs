using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using NpgsqlTypes;
using TickPulse.Application.Abstractions;
using TickPulse.Domain;
using TickPulse.Infrastructure;
using TickPulse.Infrastructure.Persistence;
using TickPulse.Integration.Tests.Fixtures;

namespace TickPulse.Integration.Tests.Persistence;

/// <summary>
/// The 1d/1w read path over the <c>candles_1h</c>/<c>candles_1d</c> rollups must answer exactly what the
/// query-time aggregation of <c>candles_1m</c> answers. The oracle is that original SQL,
/// <see cref="MarketDataQueries.ClosesSql"/> and <see cref="MarketDataQueries.CandlesSql"/>, run with the same
/// parameters.
/// </summary>
public sealed class CandleRollupTests(TimescaleFixture db) : IAsyncLifetime
{
    private const int Btc = 1;
    private const int Ada = 2;
    private const int Eth = 3;

    // Wednesday. Mondays 2026-03-09 and 2026-03-16 fall inside the stream range, several more inside the backfill.
    private static readonly DateTime Now = new(2026, 3, 18, 12, 0, 0, DateTimeKind.Utc);

    // Differences between a rollup and the aggregate of its minutes, computed straight from candles_1m.
    private const string RollupMismatchSql = """
        SELECT (SELECT count(*)
                  FROM candles_1h r
                  FULL JOIN (SELECT symbol_id, time_bucket(INTERVAL '1 hour', bucket_start) AS bucket_start,
                                    first(open, bucket_start) AS o, max(high) AS h, min(low) AS l,
                                    last(close, bucket_start) AS c, sum(volume) AS v
                               FROM candles_1m GROUP BY 1, 2) e USING (symbol_id, bucket_start)
                 WHERE r.open IS DISTINCT FROM e.o OR r.high IS DISTINCT FROM e.h OR r.low IS DISTINCT FROM e.l
                    OR r.close IS DISTINCT FROM e.c OR r.volume IS DISTINCT FROM e.v)
             + (SELECT count(*)
                  FROM candles_1d r
                  FULL JOIN (SELECT symbol_id,
                                    time_bucket(INTERVAL '1 day', bucket_start, TIMESTAMPTZ '2000-01-03 00:00:00+00') AS bucket_start,
                                    first(open, bucket_start) AS o, max(high) AS h, min(low) AS l,
                                    last(close, bucket_start) AS c, sum(volume) AS v
                               FROM candles_1m GROUP BY 1, 2) e USING (symbol_id, bucket_start)
                 WHERE r.open IS DISTINCT FROM e.o OR r.high IS DISTINCT FROM e.h OR r.low IS DISTINCT FROM e.l
                    OR r.close IS DISTINCT FROM e.c OR r.volume IS DISTINCT FROM e.v)
        """;

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(Now));
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

    [Theory]
    [InlineData(20261104)]
    [InlineData(7)]
    [InlineData(424242)]
    public async Task RandomisedMixedWrites_RollupReadsEqualMinuteAggregation(int seed)
    {
        var rng = new Random(seed);
        await WriteRandomHistoryAsync(rng);

        (await RollupMismatchesAsync()).ShouldBe(0, "every rollup row must equal the aggregate of its minutes");
        await AssertReadsMatchOracleAsync(rng);
    }

    [Fact]
    public async Task ConcurrentStreamAndBackfill_InTheSameHour_KeepRollupsExact()
    {
        // Smoke test, not a proof: whether the writers interleave depends on scheduling. Each round, a tick batch
        // and a backfill page write different and overlapping minutes of one hour at the same time, through the real
        // writers. The deterministic interleaving is OverlappingTransactions_InTheSameHour_LaterRefreshSeesTheEarlierCommit.
        const int rounds = 30;
        var rng = new Random(99);
        var nextId = 1L;
        for (var round = 0; round < rounds; round++)
        {
            var hour = Now.AddDays(-3).AddHours(round);
            var ticks = new List<Tick>();
            for (var minute = 0; minute < 60; minute += 2)
            {
                ticks.Add(Tick(Btc, nextId++, Price(rng, 100, 200), hour.AddMinutes(minute).AddSeconds(rng.Next(60))));
            }

            // Odd minutes are backfill-only; every sixth minute collides with a stream minute.
            Kline[] klines = [.. Enumerable.Range(0, 60).Where(m => m % 2 == 1 || m % 6 == 0).Select(m => RandomKline(rng, hour.AddMinutes(m)))];
            var writer = Writer();
            var candles = CandleWriter();

            await Task.WhenAll(
                Task.Run(() => writer.WriteBatchAsync(ticks, Ct), Ct),
                Task.Run(() => candles.InsertBackfillAsync(Btc, klines, Ct), Ct));
        }

        (await RollupMismatchesAsync()).ShouldBe(0, "concurrent writers must not lose each other's minutes in the rollups");
        await AssertReadsMatchOracleAsync(rng);
    }

    [Fact]
    public async Task OverlappingTransactions_InTheSameHour_LaterRefreshSeesTheEarlierCommit()
    {
        // Deterministic interleaving: the first transaction writes a minute and stays open; the second writes
        // another minute of the same hour and must wait. Its recompute has to run in a snapshot taken after the
        // first commits. Recomputing in a snapshot taken before (no day-row lock first) would overwrite the hour
        // with an aggregate that misses the first minute.
        var hour = Now.AddDays(-3);
        await using var first = await _dataSource.OpenConnectionAsync(Ct);
        await using var second = await _dataSource.OpenConnectionAsync(Ct);
        await using var firstTx = await first.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, Ct);
        await InsertMinuteAsync(first, firstTx, hour, close: 10m);

        var secondPid = second.ProcessID;
        var secondWrite = Task.Run(
            async () =>
            {
                await using var secondTx = await second.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, Ct);
                await InsertMinuteAsync(second, secondTx, hour.AddMinutes(1), close: 20m);
                await secondTx.CommitAsync(Ct);
            },
            Ct);

        await WaitUntilWaitingOnLockAsync(secondPid);
        await firstTx.CommitAsync(Ct);
        await secondWrite;

        (await RollupMismatchesAsync()).ShouldBe(0, "the second refresh must include the first transaction's minute");
        (await Queries().GetCandlesAsync(Btc, Period.OneDay, 1, Ct)).ShouldHaveSingleItem()
            .ShouldBe(new CandleRow(hour.Date, 10m, 21m, 9m, 20m, 2m));
    }

    [Fact]
    public async Task StreamReplacingBackfill_LowersDailyHighAndVolume()
    {
        // The backfill minute holds the day's high; the stream candle that replaces it is lower. A max-merge into the
        // rollup would keep the old high, so the rollup must be recomputed, not merged.
        var minute = Now.AddDays(-2);
        (await CandleWriter().InsertBackfillAsync(
            Btc,
            [new Kline(minute, 10m, 500m, 9m, 11m, 40m, 400m), new Kline(minute.AddMinutes(1), 11m, 12m, 10m, 12m, 2m, 24m)],
            Ct)).ShouldBe(2);
        await Writer().WriteBatchAsync([Tick(Btc, 1, 20m, minute.AddSeconds(5), 0.5m), Tick(Btc, 2, 21m, minute.AddSeconds(9), 0.25m)], Ct);

        var day = (await Queries().GetCandlesAsync(Btc, Period.OneDay, 1, Ct)).ShouldHaveSingleItem();

        day.ShouldBe(new CandleRow(minute.Date, 20m, 21m, 10m, 12m, 2.75m));
        (await RollupMismatchesAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task DeletedMinutes_RemoveOrShrinkTheirRollups()
    {
        var day = Now.Date.AddDays(-2);
        (await CandleWriter().InsertBackfillAsync(
            Btc,
            [RandomKline(new Random(1), day), RandomKline(new Random(2), day.AddHours(5)), RandomKline(new Random(3), day.AddDays(1))],
            Ct)).ShouldBe(3);

        await ExecuteAsync("DELETE FROM candles_1m WHERE symbol_id = $1 AND bucket_start >= $2", Int(Btc), Ts(day.AddHours(1)));

        (await RollupMismatchesAsync()).ShouldBe(0);
        (await CountAsync("SELECT count(*) FROM candles_1h")).ShouldBe(1);
        (await CountAsync("SELECT count(*) FROM candles_1d")).ShouldBe(1);
    }

    [Fact]
    public async Task PlainUpdate_ChangingHighAndMovingMinutesAcrossHourAndDay_KeepsRollupsExact()
    {
        // 23:58 and 23:59 of one day, 00:30 of the next. The update lowers a high and moves 23:59 into the next
        // day's 00:00 hour, so both the old and the new hour and day must be recomputed.
        var midnight = Now.Date.AddDays(-2);
        (await CandleWriter().InsertBackfillAsync(
            Btc,
            [
                new Kline(midnight.AddMinutes(-2), 10m, 11m, 9m, 10m, 1m, 10m),
                new Kline(midnight.AddMinutes(-1), 20m, 500m, 19m, 20m, 2m, 40m),
                new Kline(midnight.AddMinutes(30), 30m, 31m, 29m, 30m, 3m, 90m),
            ],
            Ct)).ShouldBe(3);

        await ExecuteAsync(
            "UPDATE candles_1m SET high = 21, bucket_start = $2 WHERE symbol_id = $1 AND bucket_start = $3",
            Int(Btc), Ts(midnight.AddMinutes(5)), Ts(midnight.AddMinutes(-1)));

        (await RollupMismatchesAsync()).ShouldBe(0);
        (await Queries().GetCandlesAsync(Btc, Period.OneDay, 2, Ct)).ShouldBe(
        [
            new CandleRow(midnight.AddDays(-1), 10m, 11m, 9m, 10m, 1m),
            new CandleRow(midnight, 20m, 31m, 19m, 30m, 5m),
        ]);
    }

    [Fact]
    public async Task OneUpsertInsertingAndUpdating_InSharedAndSeparateHours_KeepsRollupsExact()
    {
        // One INSERT ... ON CONFLICT DO UPDATE fires both triggers. Minute :00 is updated, :01 (same hour) and the
        // next hour's :00 are inserted; the second trigger must still recompute the hour the first did not cover.
        var hour = Now.AddDays(-2);
        await CandleWriter().InsertBackfillAsync(Btc, [Candle(hour, 10m)], Ct);

        await ExecuteAsync(
            """
            INSERT INTO candles_1m AS k (symbol_id, bucket_start, open, high, low, close, volume, quote_volume, source)
            SELECT $1, t, 50, 60, 40, 55, 7, 1, 2 FROM unnest($2::timestamptz[]) AS t
            ON CONFLICT (symbol_id, bucket_start) DO UPDATE SET high = 99, close = 98, volume = k.volume + 4
            """,
            Int(Btc),
            new NpgsqlParameter<DateTime[]> { TypedValue = [hour, hour.AddMinutes(1), hour.AddHours(1)], NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.TimestampTz });

        (await RollupMismatchesAsync()).ShouldBe(0);
        (await CountAsync("SELECT count(*) FROM candles_1h")).ShouldBe(2);
    }

    [Fact]
    public async Task TwoStatementsInOneTransaction_SecondIsRecomputedToo()
    {
        // The same transaction writes the same hour twice; the guard that lets the second trigger of one statement
        // skip covered hours must not let a later statement skip them.
        var hour = Now.AddDays(-2);
        await using var connection = await _dataSource.OpenConnectionAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, Ct);
        await InsertMinuteAsync(connection, transaction, hour, close: 10m);
        await using (var update = new NpgsqlCommand(
            "UPDATE candles_1m SET close = 20, high = 30 WHERE symbol_id = $1 AND bucket_start = $2", connection, transaction))
        {
            update.Parameters.Add(Int(Btc));
            update.Parameters.Add(Ts(hour));
            await update.ExecuteNonQueryAsync(Ct);
        }

        await InsertMinuteAsync(connection, transaction, hour.AddMinutes(1), close: 40m);
        await transaction.CommitAsync(Ct);

        (await RollupMismatchesAsync()).ShouldBe(0);
        (await Queries().GetCandlesAsync(Btc, Period.OneDay, 1, Ct)).ShouldHaveSingleItem()
            .ShouldBe(new CandleRow(hour.Date, 10m, 41m, 9m, 40m, 2m));
    }

    [Fact]
    public async Task WholeDayPeriods_ReadTheDailyRollup()
    {
        var monday = new DateTime(2026, 3, 9, 0, 0, 0, DateTimeKind.Utc);
        await CandleWriter().InsertBackfillAsync(Btc, [Candle(monday.AddHours(3), 5m), Candle(monday.AddDays(1).AddHours(3), 6m)], Ct);

        // A marker that exists only in the rollup: the read path must return it.
        await ExecuteAsync("UPDATE candles_1d SET close = 77 WHERE symbol_id = $1 AND bucket_start = $2", Int(Btc), Ts(monday.AddDays(1)));
        var queries = Queries();

        (await queries.GetClosesAsync(Btc, Period.OneDay, new SmaWindow(monday, monday.AddDays(2)), Ct))
            .ShouldBe([new PricePoint(monday, 5m), new PricePoint(monday.AddDays(1), 77m)]);
        (await queries.GetClosesAsync(Btc, Period.OneWeek, new SmaWindow(monday, monday.AddDays(7)), Ct))
            .ShouldBe([new PricePoint(monday, 77m)]);
        (await queries.GetCandlesAsync(Btc, Period.OneDay, 2, Ct)).Select(c => c.Close).ShouldBe([5m, 77m]);
        (await queries.GetCandlesAsync(Btc, Period.OneWeek, 1, Ct)).ShouldHaveSingleItem().Close.ShouldBe(77m);

        // A window that does not start on a day boundary cannot be answered from day rows: it reads the minutes.
        (await queries.GetClosesAsync(Btc, Period.OneDay, new SmaWindow(monday.AddHours(1), monday.AddDays(2)), Ct))
            .ShouldBe([new PricePoint(monday, 5m), new PricePoint(monday.AddDays(1), 6m)]);

        // Sub-day periods never read the rollup.
        (await queries.GetCandlesAsync(Btc, Period.ThirtyMinutes, 1, Ct)).ShouldHaveSingleItem().Close.ShouldBe(6m);
    }

    [Fact]
    public async Task DailyQueries_RangeScanThePrimaryKey()
    {
        var day = Now.Date.AddDays(-30);
        Kline[] klines = [.. Enumerable.Range(0, 30 * 24).Select(h => Candle(day.AddHours(h), 10m))];
        await CandleWriter().InsertBackfillAsync(Btc, klines, Ct);
        await CandleWriter().InsertBackfillAsync(Eth, klines, Ct);
        await ExecuteAsync("ANALYZE candles_1d");

        var candles = await ExplainAsync(
            MarketDataQueries.DailyCandlesSql, Int(Btc), Interval(Period.OneWeek.Length), Ts(Period.Origin), Interval(Period.OneWeek.Length * 299));
        var closes = await ExplainAsync(
            MarketDataQueries.DailyClosesSql, Int(Btc), Interval(Period.OneWeek.Length), Ts(Period.Origin), Ts(day), Ts(Now.Date));

        candles.ShouldContain("InitPlan");
        candles.ShouldMatch(@"(Index|Index Only|Bitmap Index) Scan using pk_candles_1d on candles_1d\b[\s\S]*Index Cond: \(\(symbol_id = [^)]+\) AND \(bucket_start >= \(\(InitPlan \d+\)");
        candles.ShouldNotContain("candles_1m");
        closes.ShouldMatch(@"(Index|Index Only|Bitmap Index) Scan using pk_candles_1d on candles_1d");
        closes.ShouldNotContain("candles_1m");
    }

    [Fact]
    public async Task Migration_BackfillsRollupsForExistingMinutes()
    {
        var options = new DbContextOptionsBuilder<TickPulseDbContext>().UseNpgsql(db.ConnectionString).Options;
        await using var context = new TickPulseDbContext(options);
        var migrator = context.GetService<IMigrator>();
        try
        {
            await migrator.MigrateAsync("20261103222000_Initial", Ct);
            (await CountAsync("SELECT count(*) FROM pg_tables WHERE tablename IN ('candles_1h', 'candles_1d')")).ShouldBe(0);

            // Minutes written before the rollups existed: both sources, three symbols, several weeks with gaps.
            var rng = new Random(5);
            foreach (var symbolId in new[] { Btc, Ada, Eth })
            {
                Kline[] klines = [.. RandomBackfill(rng, Now.AddDays(-30), Now, density: 0.05)];
                await CandleWriter().InsertBackfillAsync(symbolId, klines, Ct);
            }

            await Writer().WriteBatchAsync(RandomTicks(rng, Btc, 1, Now.AddDays(-4), Now, 500), Ct);

            await migrator.MigrateAsync(null, Ct);
        }
        finally
        {
            await migrator.MigrateAsync(null, Ct);
        }

        (await CountAsync("SELECT count(*) FROM candles_1d")).ShouldBeGreaterThan(0);
        (await RollupMismatchesAsync()).ShouldBe(0, "the migration must roll up the minutes that already exist");
        await AssertReadsMatchOracleAsync(new Random(6));
    }

    /// <summary>
    /// Backfill pages (shuffled, newest-first like the backfill worker, with whole days and one whole week missing),
    /// stream batches (shuffled within the batch, some replayed) and backfill pages arriving after the stream, all
    /// interleaved at random. Stream minutes replace overlapping backfill minutes; later backfill never overwrites.
    /// </summary>
    private async Task WriteRandomHistoryAsync(Random rng)
    {
        var actions = new List<Func<Task>>();
        var nextId = 1L;
        foreach (var symbolId in new[] { Btc, Eth })
        {
            var klines = RandomBackfill(rng, Now.AddDays(-45), Now.AddDays(-4), density: 0.08)
                .Where(k => symbolId != Btc || k.OpenTimeUtc < new DateTime(2026, 2, 23, 0, 0, 0, DateTimeKind.Utc)
                    || k.OpenTimeUtc >= new DateTime(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc))
                .ToList();
            foreach (var page in klines.Chunk(1_000))
            {
                actions.Add(() => CandleWriter().InsertBackfillAsync(symbolId, page, Ct));
            }

            var ticks = RandomTicks(rng, symbolId, nextId, Now.AddDays(-11), Now, 4_000);
            nextId += ticks.Length;
            foreach (var batch in ticks.Chunk(250))
            {
                Tick[] shuffled = [.. batch.OrderBy(_ => rng.Next())];
                actions.Add(() => Writer().WriteBatchAsync(shuffled, Ct));
                if (rng.NextDouble() < 0.2)
                {
                    actions.Add(() => Writer().WriteBatchAsync(shuffled, Ct));
                }
            }
        }

        foreach (var action in actions.OrderBy(_ => rng.Next()))
        {
            await action();
        }
    }

    private async Task AssertReadsMatchOracleAsync(Random rng)
    {
        var queries = Queries();
        int[] symbols = [Btc, Ada, Eth];
        foreach (var period in new[] { Period.OneDay, Period.OneWeek })
        {
            foreach (var symbolId in symbols)
            {
                foreach (var limit in new[] { 1, 2, 3, 5, 8, 20, 60, 300, IMarketDataQueries.MaxCandleLimit })
                {
                    var actual = await queries.GetCandlesAsync(symbolId, period, limit, Ct);
                    var expected = await OracleCandlesAsync(symbolId, period, limit);
                    actual.Select(Format).ShouldBe(expected, $"candles {symbolId} {period.Code} limit {limit}");
                }

                for (var i = 0; i < 25; i++)
                {
                    var n = rng.Next(1, period == Period.OneDay ? 70 : 12);
                    DateTime? start = rng.NextDouble() < 0.3 ? null : Now.AddMinutes(-rng.Next(0, 60 * 24 * 60));
                    var window = SmaWindowCalculator.Compute(period, n, start, Now);
                    var actual = await queries.GetClosesAsync(symbolId, period, window, Ct);
                    var expected = await OracleClosesAsync(symbolId, period, window);
                    actual.Select(Format).ShouldBe(expected, $"closes {symbolId} {period.Code} {window}");
                }
            }
        }
    }

    private async Task<List<string>> OracleClosesAsync(int symbolId, Period period, SmaWindow window)
    {
        await using var command = _dataSource.CreateCommand(MarketDataQueries.ClosesSql);
        command.Parameters.Add(Int(symbolId));
        command.Parameters.Add(Interval(period.Length));
        command.Parameters.Add(Ts(Period.Origin));
        command.Parameters.Add(Ts(window.Start));
        command.Parameters.Add(Ts(window.EndExclusive));
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(Format(new PricePoint(reader.GetDateTime(0), reader.GetDecimal(1))));
        }

        return rows;
    }

    private async Task<List<string>> OracleCandlesAsync(int symbolId, Period period, int limit)
    {
        await using var command = _dataSource.CreateCommand(MarketDataQueries.CandlesSql);
        command.Parameters.Add(Int(symbolId));
        command.Parameters.Add(Interval(period.Length));
        command.Parameters.Add(Ts(Period.Origin));
        command.Parameters.Add(Interval(period.Length * (limit - 1)));
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(Format(new CandleRow(
                reader.GetDateTime(0), reader.GetDecimal(1), reader.GetDecimal(2), reader.GetDecimal(3), reader.GetDecimal(4), reader.GetDecimal(5))));
        }

        return rows;
    }

    // Invariant text, so that a difference in numeric scale (1.5 vs 1.50000000) also counts as a mismatch.
    private static string Format(PricePoint p) =>
        string.Create(CultureInfo.InvariantCulture, $"{p.BucketStart:O}|{p.Close}");

    private static string Format(CandleRow c) =>
        string.Create(CultureInfo.InvariantCulture, $"{c.BucketStart:O}|{c.Open}|{c.High}|{c.Low}|{c.Close}|{c.Volume}");

    /// <summary>1-minute klines in <c>[from, to)</c>: each day is skipped with p = 0.15, each minute kept with <paramref name="density"/>.</summary>
    private static List<Kline> RandomBackfill(Random rng, DateTime from, DateTime to, double density)
    {
        var klines = new List<Kline>();
        for (var day = from.Date; day < to; day = day.AddDays(1))
        {
            if (rng.NextDouble() < 0.15)
            {
                continue;
            }

            for (var minute = day; minute < day.AddDays(1) && minute < to; minute = minute.AddMinutes(1))
            {
                if (minute >= from && rng.NextDouble() < density)
                {
                    klines.Add(RandomKline(rng, minute));
                }
            }
        }

        // The backfill worker pages backwards from the newest minute.
        klines.Reverse();
        return klines;
    }

    private static Kline RandomKline(Random rng, DateTime minute)
    {
        // Wider than the stream prices, so a replacing stream candle often lowers the high or raises the low.
        var low = Price(rng, 50, 150);
        var high = low + Price(rng, 0, 150);
        var open = low + ((high - low) * rng.Next(0, 101) / 100m);
        var close = low + ((high - low) * rng.Next(0, 101) / 100m);
        var volume = Price(rng, 0, 1_000);
        return new Kline(minute, Math.Round(open, 8), high, low, Math.Round(close, 8), volume, Math.Round(volume * close, 8));
    }

    /// <summary><paramref name="count"/> ticks at random microsecond times in <c>[from, to)</c>, trade ids in time order.</summary>
    private static Tick[] RandomTicks(Random rng, int symbolId, long firstId, DateTime from, DateTime to, int count)
    {
        var span = (to - from).Ticks / 10;
        return [.. Enumerable.Range(0, count)
            .Select(_ => from.AddTicks(rng.NextInt64(0, span) * 10))
            .Order()
            .Select((time, i) => Tick(symbolId, firstId + i, Price(rng, 100, 200), time, Price(rng, 0, 5)))];
    }

    private static decimal Price(Random rng, int min, int max) => min + (rng.NextInt64(1, (max - min) * 100_000_000L) / 100_000_000m);

    private static Tick Tick(int symbolId, long id, decimal price, DateTime time, decimal qty = 1m) => new(symbolId, id, price, qty, time, time);

    private static Kline Candle(DateTime openTime, decimal close) => new(openTime, close, close + 1m, close - 1m, close, 1m, close);

    private MarketDataQueries Queries() => new(_dataSource);

    private TickWriter Writer() => new(_writerDataSource, _clock);

    private CandleWriter CandleWriter() => new(_dataSource);

    private static NpgsqlParameter<int> Int(int value) => new() { TypedValue = value, NpgsqlDbType = NpgsqlDbType.Integer };

    private static NpgsqlParameter<DateTime> Ts(DateTime value) => new() { TypedValue = value, NpgsqlDbType = NpgsqlDbType.TimestampTz };

    private static NpgsqlParameter<TimeSpan> Interval(TimeSpan value) => new() { TypedValue = value, NpgsqlDbType = NpgsqlDbType.Interval };

    private async Task<long> RollupMismatchesAsync() => await CountAsync(RollupMismatchSql);

    private static async Task InsertMinuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, DateTime minute, decimal close)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO candles_1m (symbol_id, bucket_start, open, high, low, close, volume, quote_volume, source)
            VALUES ($1, $2, $3, $3 + 1, $3 - 1, $3, 1, $3, 2)
            """,
            connection,
            transaction);
        command.Parameters.Add(Int(Btc));
        command.Parameters.Add(Ts(minute));
        command.Parameters.Add(new NpgsqlParameter<decimal> { TypedValue = close, NpgsqlDbType = NpgsqlDbType.Numeric });
        await command.ExecuteNonQueryAsync(Ct);
    }

    private async Task WaitUntilWaitingOnLockAsync(int pid)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        await using var command = _dataSource.CreateCommand("SELECT count(*) FROM pg_stat_activity WHERE pid = $1 AND wait_event_type = 'Lock'");
        command.Parameters.Add(Int(pid));
        while (Convert.ToInt64(await command.ExecuteScalarAsync(deadline.Token), CultureInfo.InvariantCulture) == 0)
        {
            await Task.Delay(20, deadline.Token);
        }
    }

    private async Task<long> CountAsync(string sql)
    {
        await using var command = _dataSource.CreateCommand(sql);
        return Convert.ToInt64(await command.ExecuteScalarAsync(Ct), CultureInfo.InvariantCulture);
    }

    private async Task ExecuteAsync(string sql, params NpgsqlParameter[] parameters)
    {
        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync(Ct);
    }

    /// <summary>The plan with sequential scans disabled, so small test data still shows which index a query can use.</summary>
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
}

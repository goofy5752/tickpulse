using Microsoft.Extensions.Time.Testing;
using Npgsql;
using TickPulse.Domain;
using TickPulse.Infrastructure;
using TickPulse.Infrastructure.Persistence;
using TickPulse.Integration.Tests.Fixtures;

namespace TickPulse.Integration.Tests.Persistence;

public sealed class TickWriterTests(TimescaleFixture db) : IAsyncLifetime
{
    private const int Btc = 1;
    private const int Eth = 3;

    private static readonly DateTime Minute = new(2026, 3, 2, 10, 15, 0, DateTimeKind.Utc);

    // Trade times in these tests sit an hour before "now", well inside the replay window.
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(Minute.AddHours(1)));
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
    public async Task WriteBatch_InsertsTicksAndBuildsCandle()
    {
        // Delivered out of trade-id order: open/close must follow the trade id, not the arrival order.
        Tick[] ticks =
        [
            T(Btc, 3, 101.25m, 0.5m, 30),
            T(Btc, 1, 100.10m, 1.0m, 1),
            T(Btc, 4, 99.75m, 2.0m, 59),
            T(Btc, 2, 102.00000001m, 0.25m, 15),
        ];

        var inserted = await Writer().WriteBatchAsync(ticks, Ct);

        inserted.ShouldBe(4);
        (await CountTicksAsync()).ShouldBe(4);
        var candle = (await ReadCandlesAsync()).ShouldHaveSingleItem();
        candle.ShouldBe(new CandleState(
            Btc, Minute,
            Open: 100.10m, High: 102.00000001m, Low: 99.75m, Close: 99.75m,
            FirstTradeId: 1, LastTradeId: 4,
            PriceSum: 403.10000001m, TickCount: 4,
            // 100.10 + 25.5000000025 + 50.625 + 199.5, kept exactly (quote_volume is unconstrained numeric).
            Volume: 3.75m, QuoteVolume: 375.7250000025m,
            Source: 1));
    }

    [Fact]
    public async Task WriteBatch_Twice_LeavesCandlesUnchanged()
    {
        Tick[] ticks = [T(Btc, 10, 50m, 1m, 5), T(Btc, 11, 52m, 2m, 6), T(Eth, 10, 7m, 3m, 7)];
        var writer = Writer();

        (await writer.WriteBatchAsync(ticks, Ct)).ShouldBe(3);
        var before = await ReadCandlesAsync();

        var replayed = await writer.WriteBatchAsync(ticks, Ct);

        replayed.ShouldBe(0);
        (await CountTicksAsync()).ShouldBe(3);
        (await ReadCandlesAsync()).ShouldBe(before);
        (await StreamAverageAsync(Btc)).ShouldBe(await RawAverageAsync(Btc));
    }

    [Fact]
    public async Task WriteBatch_PartialReplay_CountsOnlyNewTicks()
    {
        var writer = Writer();
        await writer.WriteBatchAsync([T(Btc, 1, 10m, 1m, 1), T(Btc, 2, 20m, 1m, 2)], Ct);

        var inserted = await writer.WriteBatchAsync([T(Btc, 2, 20m, 1m, 2), T(Btc, 3, 30m, 1m, 3)], Ct);

        inserted.ShouldBe(1);
        var candle = (await ReadCandlesAsync()).ShouldHaveSingleItem();
        candle.PriceSum.ShouldBe(60m);
        candle.TickCount.ShouldBe(3);
        candle.Volume.ShouldBe(3m);
        candle.Close.ShouldBe(30m);
    }

    [Fact]
    public async Task WriteBatch_DuplicateTickWithinBatch_CountsOnce()
    {
        var tick = T(Btc, 7, 42m, 1m, 10);

        var inserted = await Writer().WriteBatchAsync([tick, tick, T(Btc, 8, 44m, 1m, 11)], Ct);

        inserted.ShouldBe(2);
        var candle = (await ReadCandlesAsync()).ShouldHaveSingleItem();
        candle.TickCount.ShouldBe(2);
        candle.PriceSum.ShouldBe(86m);
    }

    [Fact]
    public async Task WriteBatch_SplitAcrossBatches_MergesOpenCloseByTradeId()
    {
        Tick[] batchA = [T(Btc, 1, 100m, 1m, 1), T(Btc, 2, 90m, 1m, 2)];
        Tick[] batchB = [T(Btc, 3, 120m, 1m, 3), T(Btc, 4, 110m, 1m, 4)];
        var writer = Writer();

        (await writer.WriteBatchAsync(batchB, Ct)).ShouldBe(2);
        (await writer.WriteBatchAsync(batchA, Ct)).ShouldBe(2);

        var candle = (await ReadCandlesAsync()).ShouldHaveSingleItem();
        candle.Open.ShouldBe(100m);
        candle.Close.ShouldBe(110m);
        candle.High.ShouldBe(120m);
        candle.Low.ShouldBe(90m);
        candle.FirstTradeId.ShouldBe(1);
        candle.LastTradeId.ShouldBe(4);
        candle.PriceSum.ShouldBe(420m);
        candle.TickCount.ShouldBe(4);
        candle.Volume.ShouldBe(4m);
        candle.Source.ShouldBe((short)1);
    }

    [Fact]
    public async Task WriteBatch_TicksAcrossMinutesAndSymbols_BuildsOneCandlePerBucket()
    {
        Tick[] ticks =
        [
            T(Btc, 1, 10m, 1m, 0),
            new Tick(Btc, 2, 11m, 1m, Minute.AddMinutes(1).AddTicks(-10), Minute),
            new Tick(Btc, 3, 12m, 1m, Minute.AddMinutes(1), Minute),
            T(Eth, 1, 5m, 1m, 30),
        ];

        await Writer().WriteBatchAsync(ticks, Ct);

        var candles = await ReadCandlesAsync();
        candles.Select(c => (c.SymbolId, c.BucketStart, c.TickCount)).ShouldBe(
        [
            (Btc, Minute, 2L),
            (Btc, Minute.AddMinutes(1), 1L),
            (Eth, Minute, 1L),
        ]);
        candles[0].Open.ShouldBe(10m);
        candles[0].Close.ShouldBe(11m);
    }

    [Fact]
    public async Task WriteBatch_Empty_ReturnsZero()
    {
        var inserted = await Writer().WriteBatchAsync([], Ct);

        inserted.ShouldBe(0);
        (await ReadCandlesAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task StreamTick_ReplacesBackfillCandle()
    {
        (await CandleWriter().InsertBackfillAsync(Btc, [new Kline(Minute, 1m, 9m, 0.5m, 2m, 100m, 150m)], Ct)).ShouldBe(1);

        await Writer().WriteBatchAsync([T(Btc, 5, 3m, 2m, 10), T(Btc, 6, 4m, 1m, 20)], Ct);

        var candle = (await ReadCandlesAsync()).ShouldHaveSingleItem();
        candle.ShouldBe(new CandleState(
            Btc, Minute,
            Open: 3m, High: 4m, Low: 3m, Close: 4m,
            FirstTradeId: 5, LastTradeId: 6,
            PriceSum: 7m, TickCount: 2,
            Volume: 3m, QuoteVolume: 10m,
            Source: 1));
    }

    [Fact]
    public async Task Backfill_DoesNotOverwriteStreamCandle()
    {
        await Writer().WriteBatchAsync([T(Btc, 5, 3m, 2m, 10)], Ct);
        var before = await ReadCandlesAsync();

        var inserted = await CandleWriter().InsertBackfillAsync(
            Btc,
            [new Kline(Minute, 1m, 9m, 0.5m, 2m, 100m, 150m), new Kline(Minute.AddMinutes(-1), 1m, 1m, 1m, 1m, 1m, 1m)],
            Ct);

        inserted.ShouldBe(1);
        var after = await ReadCandlesAsync();
        after.ShouldContain(before.ShouldHaveSingleItem());
        after.Count.ShouldBe(2);
    }

    [Fact]
    public async Task InsertBackfill_NewMinutes_StoresSourceTwoWithoutSums()
    {
        Kline[] klines = [new Kline(Minute, 1m, 3m, 0.5m, 2m, 10m, 15m), new Kline(Minute.AddMinutes(1), 2m, 2m, 2m, 2m, 1m, 2m)];
        var writer = CandleWriter();

        (await writer.InsertBackfillAsync(Eth, klines, Ct)).ShouldBe(2);
        (await writer.InsertBackfillAsync(Eth, klines, Ct)).ShouldBe(0);

        var candles = await ReadCandlesAsync();
        candles[0].ShouldBe(new CandleState(
            Eth, Minute,
            Open: 1m, High: 3m, Low: 0.5m, Close: 2m,
            FirstTradeId: null, LastTradeId: null,
            PriceSum: null, TickCount: null,
            Volume: 10m, QuoteVolume: 15m,
            Source: 2));
        candles.Count.ShouldBe(2);
        (await CountTicksAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task InsertBackfill_Empty_ReturnsZero()
    {
        (await CandleWriter().InsertBackfillAsync(Btc, [], Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task InsertBackfill_UnalignedOpenTime_Throws()
    {
        await Should.ThrowAsync<ArgumentException>(() => CandleWriter().InsertBackfillAsync(
            Btc, [new Kline(Minute.AddSeconds(1), 1m, 1m, 1m, 1m, 1m, 1m)], Ct));
    }

    [Fact]
    public async Task ConcurrentWriters_DoNotSeeEachOthersStagedRows()
    {
        // Smoke test, not a proof: whether the two transactions really interleave depends on scheduling.
        // Two writers on separate connections, each with its own session temp table, write overlapping
        // id ranges at the same time. Every tick must be counted exactly once.
        const int rounds = 5;
        for (var round = 0; round < rounds; round++)
        {
            var baseId = round * 1_000L;
            var first = Range(baseId + 1, baseId + 600);
            var second = Range(baseId + 401, baseId + 1_000);
            var writerA = Writer();
            var writerB = Writer();

            var counts = await Task.WhenAll(
                Task.Run(() => writerA.WriteBatchAsync(first, Ct), Ct),
                Task.Run(() => writerB.WriteBatchAsync(second, Ct), Ct));

            counts.Sum().ShouldBe(1_000);
            counts[0].ShouldBeInRange(400, 600);
            counts[1].ShouldBeInRange(400, 600);
        }

        (await CountTicksAsync()).ShouldBe(rounds * 1_000);
        foreach (var symbolId in new[] { Btc, Eth })
        {
            (await StreamAverageAsync(symbolId)).ShouldBe(await RawAverageAsync(symbolId));
        }

        await using var connection = await _dataSource.OpenConnectionAsync(Ct);
        await using var command = new NpgsqlCommand(
            """
            SELECT count(*)
              FROM candles_1m k
              FULL JOIN (SELECT symbol_id, time_bucket(INTERVAL '1 minute', trade_time) AS bucket_start,
                                first(price, agg_trade_id) AS o, max(price) AS h, min(price) AS l,
                                last(price, agg_trade_id) AS c, sum(price) AS ps, count(*) AS tc, sum(quantity) AS v,
                                min(agg_trade_id) AS fid, max(agg_trade_id) AS lid, sum(price * quantity) AS qv
                           FROM price_ticks GROUP BY 1, 2) r
                     USING (symbol_id, bucket_start)
             WHERE k.open IS DISTINCT FROM r.o OR k.high IS DISTINCT FROM r.h OR k.low IS DISTINCT FROM r.l
                OR k.close IS DISTINCT FROM r.c OR k.price_sum IS DISTINCT FROM r.ps
                OR k.tick_count IS DISTINCT FROM r.tc OR k.volume IS DISTINCT FROM r.v
                OR k.first_trade_id IS DISTINCT FROM r.fid OR k.last_trade_id IS DISTINCT FROM r.lid
                OR k.quote_volume IS DISTINCT FROM r.qv
            """,
            connection);
        ((long)(await command.ExecuteScalarAsync(Ct))!).ShouldBe(0, "every candle must equal the raw aggregate of its ticks");
    }

    [Fact]
    public async Task BackfillThenOutOfOrderStream_MergesCorrectly()
    {
        (await CandleWriter().InsertBackfillAsync(Btc, [new Kline(Minute, 1m, 9m, 0.5m, 2m, 100m, 150m)], Ct)).ShouldBe(1);
        var writer = Writer();

        (await writer.WriteBatchAsync([T(Btc, 5, 30m, 1m, 40), T(Btc, 6, 25m, 2m, 50)], Ct)).ShouldBe(2);
        (await writer.WriteBatchAsync([T(Btc, 3, 20m, 1m, 10), T(Btc, 4, 35m, 3m, 20)], Ct)).ShouldBe(2);

        var candle = (await ReadCandlesAsync()).ShouldHaveSingleItem();
        candle.ShouldBe(new CandleState(
            Btc, Minute,
            Open: 20m, High: 35m, Low: 20m, Close: 25m,
            FirstTradeId: 3, LastTradeId: 6,
            PriceSum: 110m, TickCount: 4,
            Volume: 7m, QuoteVolume: 30m + 50m + 20m + 105m,
            Source: 1));
    }

    [Fact]
    public async Task WriteBatch_TickOlderThanReplayWindow_IsIgnored()
    {
        var floor = _clock.GetUtcNow().UtcDateTime - TickWriter.ReplayWindow;
        Tick[] ticks =
        [
            new Tick(Btc, 1, 10m, 1m, floor.AddDays(-2), floor),
            new Tick(Btc, 2, 11m, 1m, floor, floor),
            new Tick(Btc, 3, 12m, 1m, floor.AddTicks(10), floor),
        ];

        var inserted = await Writer().WriteBatchAsync(ticks, Ct);

        // Only the tick strictly inside the window (1 µs after the floor) is stored and counted.
        inserted.ShouldBe(1);
        (await CountTicksAsync()).ShouldBe(1);
        var candle = (await ReadCandlesAsync()).ShouldHaveSingleItem();
        candle.FirstTradeId.ShouldBe(3);
        candle.TickCount.ShouldBe(1);
    }

    [Fact]
    public async Task WriteBatch_ReplayAfterRetentionDroppedTick_ReturnsZeroAndLeavesCandle()
    {
        Tick[] ticks = [T(Btc, 1, 10m, 1m, 1), T(Btc, 2, 20m, 1m, 2)];
        var writer = Writer();
        (await writer.WriteBatchAsync(ticks, Ct)).ShouldBe(2);
        var before = await ReadCandlesAsync();

        // Retention has dropped the raw ticks; the candle (kept forever) still counts them.
        _clock.Advance(TickWriter.RawRetention + TimeSpan.FromDays(1));
        await ExecuteAsync("DELETE FROM price_ticks");

        var replayed = await writer.WriteBatchAsync(ticks, Ct);

        replayed.ShouldBe(0);
        (await CountTicksAsync()).ShouldBe(0);
        (await ReadCandlesAsync()).ShouldBe(before);
    }

    [Fact]
    public async Task ReplayWindow_IsInsideRetentionPolicy()
    {
        await using var connection = await _dataSource.OpenConnectionAsync(Ct);
        await using var command = new NpgsqlCommand(
            """
            SELECT (config->>'drop_after')::interval
              FROM timescaledb_information.jobs
             WHERE hypertable_name = 'price_ticks' AND proc_name = 'policy_retention'
            """,
            connection);
        var dropAfter = (TimeSpan)(await command.ExecuteScalarAsync(Ct))!;

        dropAfter.ShouldBe(TickWriter.RawRetention);
        TickWriter.ReplayWindow.ShouldBeLessThan(TickWriter.RawRetention);
    }

    [Fact]
    public async Task TickWriterDataSource_KeepsEmptyStagingTableBetweenBatches()
    {
        await Writer().WriteBatchAsync([T(Btc, 1, 10m, 1m, 1)], Ct);

        // The writer's pool has one idle connection now; it comes back without DISCARD ALL.
        await using var connection = await _writerDataSource.OpenConnectionAsync(Ct);
        await using var command = new NpgsqlCommand(
            "SELECT to_regclass('pg_temp.tick_staging') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM tick_staging)",
            connection);

        ((bool)(await command.ExecuteScalarAsync(Ct))!).ShouldBeTrue();
    }

    [Fact]
    public async Task WriteBatch_AfterFailedBatch_StartsWithEmptyStaging()
    {
        var writer = Writer();
        (await writer.WriteBatchAsync([T(Btc, 1, 10m, 1m, 1)], Ct)).ShouldBe(1);

        // Symbol 999 does not exist: COPY stages the row, then the insert fails on the foreign key.
        await Should.ThrowAsync<PostgresException>(() => writer.WriteBatchAsync([T(999, 2, 10m, 1m, 2)], Ct));

        // Same pooled connection and temp table. A leftover staged row would fail this batch again.
        (await writer.WriteBatchAsync([T(Btc, 3, 30m, 1m, 3)], Ct)).ShouldBe(1);
        (await CountTicksAsync()).ShouldBe(2);
        (await ReadCandlesAsync()).ShouldHaveSingleItem().PriceSum.ShouldBe(40m);
    }

    private static Tick[] Range(long fromId, long toId)
    {
        var ticks = new List<Tick>();
        for (var id = fromId; id <= toId; id++)
        {
            // Spread over two symbols and several minutes; the price depends only on the id, so both
            // writers send identical rows for the overlapping ids.
            var symbolId = id % 2 == 0 ? Btc : Eth;
            var time = Minute.AddSeconds(id % 600).AddMilliseconds(id % 7);
            ticks.Add(new Tick(symbolId, id, 100m + (id % 97 * 0.01234567m), 0.001m * (id % 13 + 1), time, time));
        }

        // Shuffle deterministically so the arrival order differs from the trade-id order.
        return [.. ticks.OrderBy(t => (t.AggTradeId * 7919) % 1_009)];
    }

    private static Tick T(int symbolId, long id, decimal price, decimal qty, int second) =>
        new(symbolId, id, price, qty, Minute.AddSeconds(second), Minute.AddSeconds(second).AddMilliseconds(5));

    private TickWriter Writer() => new(_writerDataSource, _clock);

    private CandleWriter CandleWriter() => new(_dataSource);

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private async Task<long> CountTicksAsync()
    {
        await using var connection = await _dataSource.OpenConnectionAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT count(*) FROM price_ticks", connection);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    private async Task<decimal> StreamAverageAsync(int symbolId)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(Ct);
        await using var command = new NpgsqlCommand(
            "SELECT round(sum(price_sum) / sum(tick_count), 8) FROM candles_1m WHERE symbol_id = $1 AND source = 1",
            connection)
        { Parameters = { new() { Value = symbolId } } };
        return (decimal)(await command.ExecuteScalarAsync(Ct))!;
    }

    private async Task<decimal> RawAverageAsync(int symbolId)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(Ct);
        await using var command = new NpgsqlCommand(
            "SELECT round(avg(price), 8) FROM price_ticks WHERE symbol_id = $1",
            connection)
        { Parameters = { new() { Value = symbolId } } };
        return (decimal)(await command.ExecuteScalarAsync(Ct))!;
    }

    private async Task<List<CandleState>> ReadCandlesAsync()
    {
        await using var connection = await _dataSource.OpenConnectionAsync(Ct);
        await using var command = new NpgsqlCommand(
            """
            SELECT symbol_id, bucket_start, open, high, low, close, first_trade_id, last_trade_id,
                   price_sum, tick_count, volume, quote_volume, source
              FROM candles_1m
             ORDER BY symbol_id, bucket_start
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var candles = new List<CandleState>();
        while (await reader.ReadAsync(Ct))
        {
            candles.Add(new CandleState(
                reader.GetInt32(0),
                reader.GetDateTime(1),
                reader.GetDecimal(2),
                reader.GetDecimal(3),
                reader.GetDecimal(4),
                reader.GetDecimal(5),
                reader.IsDBNull(6) ? null : reader.GetInt64(6),
                reader.IsDBNull(7) ? null : reader.GetInt64(7),
                reader.IsDBNull(8) ? null : reader.GetDecimal(8),
                reader.IsDBNull(9) ? null : reader.GetInt64(9),
                reader.GetDecimal(10),
                reader.GetDecimal(11),
                reader.GetInt16(12)));
        }

        return candles;
    }

    private sealed record CandleState(
        int SymbolId,
        DateTime BucketStart,
        decimal Open,
        decimal High,
        decimal Low,
        decimal Close,
        long? FirstTradeId,
        long? LastTradeId,
        decimal? PriceSum,
        long? TickCount,
        decimal Volume,
        decimal QuoteVolume,
        short Source);
}

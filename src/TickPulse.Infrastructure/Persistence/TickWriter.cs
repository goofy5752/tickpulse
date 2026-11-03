using System.Data;
using Npgsql;
using NpgsqlTypes;
using TickPulse.Application.Abstractions;
using TickPulse.Domain;

namespace TickPulse.Infrastructure.Persistence;

/// <summary>
/// Writes a batch of ticks and folds the ticks that were actually inserted into <c>candles_1m</c>, all in one
/// transaction. Replayed ticks hit the <c>price_ticks</c> primary key, are skipped by
/// <c>ON CONFLICT DO NOTHING</c>, never reach <c>RETURNING</c> and so never touch a candle. That keeps
/// <c>price_sum</c>/<c>tick_count</c> exact under reconnect replays, rotation overlap and gap-fill.
/// </summary>
public sealed class TickWriter(NpgsqlDataSource dataSource, TimeProvider clock) : ITickWriter
{
    /// <summary>The raw-tick retention policy on <c>price_ticks</c> (see the Initial migration).</summary>
    public static readonly TimeSpan RawRetention = TimeSpan.FromDays(14);

    /// <summary>
    /// Ticks at or before <c>now - ReplayWindow</c> are never inserted. Once retention has dropped a tick's chunk, a
    /// replay of it would no longer hit the primary key and would be counted into its candle a second time. The
    /// window is one day narrower than <see cref="RawRetention"/> as headroom for skew between this clock and the
    /// database clock that the retention job uses; any tick inside it is guaranteed to still be stored.
    /// </summary>
    public static readonly TimeSpan ReplayWindow = RawRetention - TimeSpan.FromDays(1);

    // A session temp table, private to this connection, so concurrent writers never see each other's rows.
    // ON COMMIT DELETE ROWS empties it at the end of every batch transaction. LIKE copies the column types
    // (and their numeric scales) but no primary key, so a batch may hold duplicates. On the writer's own
    // data source (NoResetOnClose, see DependencyInjection.BuildTickWriterDataSource) the table survives
    // between batches, so IF NOT EXISTS is a no-op after the first batch on each connection.
    private const string CreateStagingSql =
        "CREATE TEMP TABLE IF NOT EXISTS tick_staging (LIKE price_ticks INCLUDING DEFAULTS) ON COMMIT DELETE ROWS";

    private const string CopySql =
        "COPY tick_staging (symbol_id, agg_trade_id, price, quantity, trade_time, ingested_at) FROM STDIN (FORMAT BINARY)";

    // One statement: the candle aggregate reads only the rows that the tick insert returned.
    // Both inserts run in key order so that concurrent writers take row locks in the same order.
    // Stream rows (source 1) merge: open/close by trade id, high/low by GREATEST/LEAST, sums add.
    // A backfill row (source 2) is replaced outright by the stream aggregate.
    private const string UpsertSql = """
        WITH ins AS (
            INSERT INTO price_ticks (symbol_id, agg_trade_id, price, quantity, trade_time, ingested_at)
            SELECT symbol_id, agg_trade_id, price, quantity, trade_time, ingested_at
              FROM tick_staging
             WHERE trade_time > $1
             ORDER BY symbol_id, agg_trade_id, trade_time
            ON CONFLICT DO NOTHING
            RETURNING symbol_id, agg_trade_id, price, quantity, trade_time),
        agg AS (
            SELECT symbol_id,
                   time_bucket(INTERVAL '1 minute', trade_time) AS bucket_start,
                   first(price, agg_trade_id) AS o,
                   max(price) AS h,
                   min(price) AS l,
                   last(price, agg_trade_id) AS c,
                   min(agg_trade_id) AS fid,
                   max(agg_trade_id) AS lid,
                   sum(price) AS ps,
                   count(*) AS tc,
                   sum(quantity) AS v,
                   sum(price * quantity) AS qv
              FROM ins
             GROUP BY 1, 2),
        upsert AS (
            INSERT INTO candles_1m AS k (symbol_id, bucket_start, open, high, low, close, first_trade_id,
                                         last_trade_id, price_sum, tick_count, volume, quote_volume, source)
            SELECT symbol_id, bucket_start, o, h, l, c, fid, lid, ps, tc, v, qv, 1
              FROM agg
             ORDER BY symbol_id, bucket_start
            ON CONFLICT (symbol_id, bucket_start) DO UPDATE SET
                open           = CASE WHEN k.source = 2 OR excluded.first_trade_id < k.first_trade_id THEN excluded.open ELSE k.open END,
                close          = CASE WHEN k.source = 2 OR excluded.last_trade_id > k.last_trade_id THEN excluded.close ELSE k.close END,
                high           = CASE WHEN k.source = 2 THEN excluded.high ELSE GREATEST(k.high, excluded.high) END,
                low            = CASE WHEN k.source = 2 THEN excluded.low ELSE LEAST(k.low, excluded.low) END,
                first_trade_id = CASE WHEN k.source = 2 THEN excluded.first_trade_id ELSE LEAST(k.first_trade_id, excluded.first_trade_id) END,
                last_trade_id  = CASE WHEN k.source = 2 THEN excluded.last_trade_id ELSE GREATEST(k.last_trade_id, excluded.last_trade_id) END,
                price_sum      = CASE WHEN k.source = 2 THEN excluded.price_sum ELSE k.price_sum + excluded.price_sum END,
                tick_count     = CASE WHEN k.source = 2 THEN excluded.tick_count ELSE k.tick_count + excluded.tick_count END,
                volume         = CASE WHEN k.source = 2 THEN excluded.volume ELSE k.volume + excluded.volume END,
                quote_volume   = CASE WHEN k.source = 2 THEN excluded.quote_volume ELSE k.quote_volume + excluded.quote_volume END,
                source         = 1)
        SELECT count(*)::integer FROM ins
        """;

    /// <inheritdoc />
    public async Task<int> WriteBatchAsync(IReadOnlyList<Tick> ticks, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ticks);
        if (ticks.Count == 0)
        {
            return 0;
        }

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        // READ COMMITTED is required for the exact merge: a tick that a concurrent batch is inserting makes ON
        // CONFLICT wait for that batch and then skip the tick, and the candle upsert then re-reads the latest
        // committed candle. Under REPEATABLE READ or SERIALIZABLE those cases fail with serialization errors.
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        await using (var create = new NpgsqlCommand(CreateStagingSql, connection, transaction))
        {
            await create.ExecuteNonQueryAsync(ct);
        }

        await using (var importer = await connection.BeginBinaryImportAsync(CopySql, ct))
        {
            foreach (var tick in ticks)
            {
                await importer.StartRowAsync(ct);
                await importer.WriteAsync(tick.SymbolId, NpgsqlDbType.Integer, ct);
                await importer.WriteAsync(tick.AggTradeId, NpgsqlDbType.Bigint, ct);
                await importer.WriteAsync(tick.Price, NpgsqlDbType.Numeric, ct);
                await importer.WriteAsync(tick.Quantity, NpgsqlDbType.Numeric, ct);
                await importer.WriteAsync(tick.TradeTimeUtc, NpgsqlDbType.TimestampTz, ct);
                await importer.WriteAsync(tick.ReceivedAtUtc, NpgsqlDbType.TimestampTz, ct);
            }

            await importer.CompleteAsync(ct);
        }

        int inserted;
        await using (var upsert = new NpgsqlCommand(UpsertSql, connection, transaction))
        {
            upsert.Parameters.Add(new NpgsqlParameter<DateTime>
            {
                TypedValue = clock.GetUtcNow().UtcDateTime - ReplayWindow,
                NpgsqlDbType = NpgsqlDbType.TimestampTz,
            });
            inserted = (int)(await upsert.ExecuteScalarAsync(ct))!;
        }

        await transaction.CommitAsync(ct);
        return inserted;
    }
}

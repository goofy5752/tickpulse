using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TickPulse.Infrastructure.Persistence.Migrations;

/// <summary>
/// Hourly and daily rollups of <c>candles_1m</c>, so that 1d and 1w analytics read at most a few hundred rows
/// instead of aggregating up to several hundred thousand 1-minute candles per request.
/// </summary>
/// <remarks>
/// <para>
/// The rollups are kept exact by statement-level triggers on <c>candles_1m</c>: after every statement that
/// inserts, updates or deletes minutes, the affected hours are recomputed from <c>candles_1m</c> and the affected
/// days from <c>candles_1h</c>, inside the writing transaction. Every writer is covered (the tick upsert, the
/// backfill insert, manual repairs and any older build of the service that knows nothing about the rollups), and
/// a reader always sees rollups that match the minutes committed with them.
/// </para>
/// <para>
/// Concurrency: a statement first locks the <c>candles_1d</c> rows of its affected days, in key order, and only
/// then recomputes. In READ COMMITTED each statement of the refresh function takes a fresh snapshot, so the
/// recompute sees every transaction that held those day locks before it. A transaction that changed minutes in
/// the same day but has not reached its own refresh yet recomputes after this one commits.
/// </para>
/// </remarks>
public partial class CandleRollups : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Writers wait until the migration commits, so no minute escapes both the backfill below and the
        // triggers. Readers are not blocked.
        migrationBuilder.Sql("LOCK TABLE candles_1m IN SHARE MODE;");

        // Same OHLC types as candles_1m. volume sums at most 10,080 numeric(28,8) minutes per week, scale 8.
        migrationBuilder.Sql("""
            CREATE TABLE candles_1h (
                symbol_id    integer        NOT NULL,
                bucket_start timestamptz    NOT NULL,
                open         numeric(24, 8) NOT NULL,
                high         numeric(24, 8) NOT NULL,
                low          numeric(24, 8) NOT NULL,
                close        numeric(24, 8) NOT NULL,
                volume       numeric(38, 8) NOT NULL,
                CONSTRAINT pk_candles_1h PRIMARY KEY (symbol_id, bucket_start),
                CONSTRAINT fk_candles_1h_symbols_symbol_id FOREIGN KEY (symbol_id) REFERENCES symbols (id)
            );

            CREATE TABLE candles_1d (
                symbol_id    integer        NOT NULL,
                bucket_start timestamptz    NOT NULL,
                open         numeric(24, 8) NOT NULL,
                high         numeric(24, 8) NOT NULL,
                low          numeric(24, 8) NOT NULL,
                close        numeric(24, 8) NOT NULL,
                volume       numeric(38, 8) NOT NULL,
                CONSTRAINT pk_candles_1d PRIMARY KEY (symbol_id, bucket_start),
                CONSTRAINT fk_candles_1d_symbols_symbol_id FOREIGN KEY (symbol_id) REFERENCES symbols (id)
            );
            """);

        // Rollups for the minutes that already exist. Both sources count, exactly as in the read queries.
        migrationBuilder.Sql("""
            INSERT INTO candles_1h (symbol_id, bucket_start, open, high, low, close, volume)
            SELECT symbol_id, time_bucket(INTERVAL '1 hour', bucket_start),
                   first(open, bucket_start), max(high), min(low), last(close, bucket_start), sum(volume)
              FROM candles_1m
             GROUP BY 1, 2;

            INSERT INTO candles_1d (symbol_id, bucket_start, open, high, low, close, volume)
            SELECT symbol_id, time_bucket(INTERVAL '1 day', bucket_start, TIMESTAMPTZ '2000-01-03 00:00:00+00'),
                   first(open, bucket_start), max(high), min(low), last(close, bucket_start), sum(volume)
              FROM candles_1h
             GROUP BY 1, 2;
            """);

        // Recomputes the given (symbol, hour) pairs and their days. Day boundaries use the Monday origin of the
        // read queries; a day is 24 hours in UTC (INTERVAL '24 hours', not '1 day', which would follow the
        // session time zone across DST changes).
        migrationBuilder.Sql("""
            CREATE FUNCTION candles_refresh_rollups(symbol_ids integer[], hours timestamptz[]) RETURNS void
            LANGUAGE plpgsql AS $$
            BEGIN
                -- 1. Lock the day rows in key order (a new day gets a placeholder that step 3 overwrites).
                INSERT INTO candles_1d AS d (symbol_id, bucket_start, open, high, low, close, volume)
                SELECT DISTINCT k.symbol_id, time_bucket(INTERVAL '1 day', k.hour, TIMESTAMPTZ '2000-01-03 00:00:00+00'),
                       0, 0, 0, 0, 0
                  FROM unnest(symbol_ids, hours) AS k(symbol_id, hour)
                 ORDER BY 1, 2
                ON CONFLICT (symbol_id, bucket_start) DO UPDATE SET volume = d.volume;

                -- 2. Hours from minutes, in a snapshot taken after the locks were granted.
                INSERT INTO candles_1h AS h (symbol_id, bucket_start, open, high, low, close, volume)
                SELECT k.symbol_id, k.hour,
                       first(m.open, m.bucket_start), max(m.high), min(m.low), last(m.close, m.bucket_start), sum(m.volume)
                  FROM (SELECT DISTINCT * FROM unnest(symbol_ids, hours) AS u(symbol_id, hour)) AS k
                  JOIN candles_1m m
                    ON m.symbol_id = k.symbol_id AND m.bucket_start >= k.hour AND m.bucket_start < k.hour + INTERVAL '1 hour'
                 GROUP BY 1, 2
                 ORDER BY 1, 2
                ON CONFLICT (symbol_id, bucket_start) DO UPDATE SET
                    open = excluded.open, high = excluded.high, low = excluded.low, close = excluded.close,
                    volume = excluded.volume;

                DELETE FROM candles_1h h
                 USING unnest(symbol_ids, hours) AS k(symbol_id, hour)
                 WHERE h.symbol_id = k.symbol_id AND h.bucket_start = k.hour
                   AND NOT EXISTS (SELECT 1 FROM candles_1m m
                                    WHERE m.symbol_id = k.symbol_id
                                      AND m.bucket_start >= k.hour AND m.bucket_start < k.hour + INTERVAL '1 hour');

                -- 3. Days from hours.
                INSERT INTO candles_1d AS d (symbol_id, bucket_start, open, high, low, close, volume)
                SELECT k.symbol_id, k.day,
                       first(h.open, h.bucket_start), max(h.high), min(h.low), last(h.close, h.bucket_start), sum(h.volume)
                  FROM (SELECT DISTINCT u.symbol_id,
                               time_bucket(INTERVAL '1 day', u.hour, TIMESTAMPTZ '2000-01-03 00:00:00+00') AS day
                          FROM unnest(symbol_ids, hours) AS u(symbol_id, hour)) AS k
                  JOIN candles_1h h
                    ON h.symbol_id = k.symbol_id AND h.bucket_start >= k.day AND h.bucket_start < k.day + INTERVAL '24 hours'
                 GROUP BY 1, 2
                 ORDER BY 1, 2
                ON CONFLICT (symbol_id, bucket_start) DO UPDATE SET
                    open = excluded.open, high = excluded.high, low = excluded.low, close = excluded.close,
                    volume = excluded.volume;

                DELETE FROM candles_1d d
                 USING unnest(symbol_ids, hours) AS k(symbol_id, hour)
                 WHERE d.symbol_id = k.symbol_id
                   AND d.bucket_start = time_bucket(INTERVAL '1 day', k.hour, TIMESTAMPTZ '2000-01-03 00:00:00+00')
                   AND NOT EXISTS (SELECT 1 FROM candles_1h h
                                    WHERE h.symbol_id = d.symbol_id
                                      AND h.bucket_start >= d.bucket_start AND h.bucket_start < d.bucket_start + INTERVAL '24 hours');
            END
            $$;

            CREATE FUNCTION candles_1m_refresh_rollups() RETURNS trigger
            LANGUAGE plpgsql AS $$
            DECLARE
                symbol_ids integer[];
                hours timestamptz[];
            BEGIN
                IF TG_OP = 'INSERT' THEN
                    SELECT array_agg(symbol_id), array_agg(hour) INTO symbol_ids, hours
                      FROM (SELECT DISTINCT symbol_id, time_bucket(INTERVAL '1 hour', bucket_start) AS hour FROM new_rows) AS k;
                ELSIF TG_OP = 'UPDATE' THEN
                    SELECT array_agg(symbol_id), array_agg(hour) INTO symbol_ids, hours
                      FROM (SELECT symbol_id, time_bucket(INTERVAL '1 hour', bucket_start) AS hour FROM new_rows
                            UNION
                            SELECT symbol_id, time_bucket(INTERVAL '1 hour', bucket_start) FROM old_rows) AS k;
                ELSE
                    SELECT array_agg(symbol_id), array_agg(hour) INTO symbol_ids, hours
                      FROM (SELECT DISTINCT symbol_id, time_bucket(INTERVAL '1 hour', bucket_start) AS hour FROM old_rows) AS k;
                END IF;

                IF symbol_ids IS NOT NULL THEN
                    PERFORM candles_refresh_rollups(symbol_ids, hours);
                END IF;

                RETURN NULL;
            END
            $$;

            -- An INSERT ... ON CONFLICT DO UPDATE fires both the insert and the update trigger, each with its own rows.
            CREATE TRIGGER candles_1m_rollups_insert AFTER INSERT ON candles_1m
                REFERENCING NEW TABLE AS new_rows
                FOR EACH STATEMENT EXECUTE FUNCTION candles_1m_refresh_rollups();

            CREATE TRIGGER candles_1m_rollups_update AFTER UPDATE ON candles_1m
                REFERENCING OLD TABLE AS old_rows NEW TABLE AS new_rows
                FOR EACH STATEMENT EXECUTE FUNCTION candles_1m_refresh_rollups();

            CREATE TRIGGER candles_1m_rollups_delete AFTER DELETE ON candles_1m
                REFERENCING OLD TABLE AS old_rows
                FOR EACH STATEMENT EXECUTE FUNCTION candles_1m_refresh_rollups();
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP TRIGGER IF EXISTS candles_1m_rollups_delete ON candles_1m;
            DROP TRIGGER IF EXISTS candles_1m_rollups_update ON candles_1m;
            DROP TRIGGER IF EXISTS candles_1m_rollups_insert ON candles_1m;
            DROP FUNCTION IF EXISTS candles_1m_refresh_rollups();
            DROP FUNCTION IF EXISTS candles_refresh_rollups(integer[], timestamptz[]);
            DROP TABLE IF EXISTS candles_1d;
            DROP TABLE IF EXISTS candles_1h;
            """);
    }
}

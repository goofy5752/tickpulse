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
/// <para>
/// Deadlock freedom rests on three rules, which TickWriter and CandleWriter both follow:
/// (1) a transaction runs at most one statement that writes <c>candles_1m</c>;
/// (2) that statement takes its minute row locks before the trigger takes any day lock, because statement-level
/// triggers fire after the statement's rows are written;
/// (3) day locks are taken in <c>(symbol_id, bucket_start)</c> order, and <c>candles_1h</c> rows are written
/// only while their day is locked.
/// A transaction that writes minutes again after its refresh would hold day locks while waiting for minute locks,
/// and could deadlock with a writer that holds those minutes and waits for the same day (PostgreSQL then aborts
/// one of them). Such a transaction is still exact, just not deadlock-free.
/// </para>
/// <para>
/// Write cost: an existing day is locked with <c>SELECT … FOR UPDATE</c>, which writes no row version. Only a
/// new day gets a placeholder row (<c>INSERT … ON CONFLICT DO NOTHING</c>). <c>INSERT … ON CONFLICT DO UPDATE</c>
/// fires both the insert and the update trigger. The second one in the same statement recomputes only the hours
/// the first one has not covered; the first ran after all of the statement's rows were written, and its days are
/// still locked, so its hours are current. A statement is identified by its transaction id and the command id
/// (<c>cmin</c>) of a row it wrote, which is unique per statement within a transaction.
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

        // Same OHLC types as candles_1m. A rollup volume sums at most 1,440 numeric(28,8) minutes (one day), so
        // numeric(38, 8) cannot overflow and keeps scale 8; week sums are taken at query time, as before.
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
                -- 1. Lock the day rows in key order. A new day first gets a placeholder that step 3 overwrites;
                --    DO NOTHING writes nothing for an existing day and waits for a day another transaction is
                --    still inserting. FOR UPDATE then locks every day row (sorted before locking) without writing
                --    a new row version.
                INSERT INTO candles_1d (symbol_id, bucket_start, open, high, low, close, volume)
                SELECT DISTINCT k.symbol_id, time_bucket(INTERVAL '1 day', k.hour, TIMESTAMPTZ '2000-01-03 00:00:00+00'),
                       0, 0, 0, 0, 0
                  FROM unnest(symbol_ids, hours) AS k(symbol_id, hour)
                 ORDER BY 1, 2
                ON CONFLICT (symbol_id, bucket_start) DO NOTHING;

                PERFORM 1
                   FROM candles_1d d
                   JOIN (SELECT DISTINCT u.symbol_id,
                                time_bucket(INTERVAL '1 day', u.hour, TIMESTAMPTZ '2000-01-03 00:00:00+00') AS day
                           FROM unnest(symbol_ids, hours) AS u(symbol_id, hour)) AS k
                     ON d.symbol_id = k.symbol_id AND d.bucket_start = k.day
                  ORDER BY d.symbol_id, d.bucket_start
                    FOR UPDATE OF d;

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
            -- Deadlock freedom assumes one candles_1m statement per transaction: minute locks first (the statement),
            -- then day locks in key order (this trigger). See the CandleRollups migration remarks.
            DECLARE
                symbol_ids integer[];
                hours timestamptz[];
                statement_id text;
                covered text[] := '{}';
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

                IF symbol_ids IS NULL THEN
                    RETURN NULL;
                END IF;

                -- The statement that fired this trigger: the transaction plus the command id of a row it wrote.
                -- INSERT ... ON CONFLICT DO UPDATE fires the update and the insert trigger after all of its rows are
                -- written; the second one skips the hours the first one already recomputed (their days are still
                -- locked, so they are current). A later statement has another command id and skips nothing.
                IF TG_OP <> 'DELETE' THEN
                    SELECT pg_current_xact_id()::text || ':' || m.cmin::text INTO statement_id
                      FROM (SELECT symbol_id, bucket_start FROM new_rows LIMIT 1) AS n
                      JOIN candles_1m m USING (symbol_id, bucket_start);
                END IF;

                IF statement_id IS NOT NULL AND statement_id = current_setting('tickpulse.rollup_statement', true) THEN
                    covered := coalesce(nullif(current_setting('tickpulse.rollup_hours', true), ''), '{}')::text[];
                    SELECT array_agg(k.symbol_id), array_agg(k.hour) INTO symbol_ids, hours
                      FROM unnest(symbol_ids, hours) AS k(symbol_id, hour)
                     WHERE format('%s/%s', k.symbol_id, extract(epoch FROM k.hour)) <> ALL (covered);
                END IF;

                IF symbol_ids IS NOT NULL THEN
                    PERFORM candles_refresh_rollups(symbol_ids, hours);
                END IF;

                IF statement_id IS NOT NULL THEN
                    PERFORM set_config('tickpulse.rollup_statement', statement_id, true);
                    PERFORM set_config(
                        'tickpulse.rollup_hours',
                        (covered || ARRAY(SELECT format('%s/%s', k.symbol_id, extract(epoch FROM k.hour))
                                            FROM unnest(symbol_ids, hours) AS k(symbol_id, hour)))::text,
                        true);
                END IF;

                RETURN NULL;
            END
            $$;

            -- An INSERT ... ON CONFLICT DO UPDATE fires both the insert and the update trigger, each with its own rows.
            -- They cannot be one trigger: a trigger with transition tables may have only one event.
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

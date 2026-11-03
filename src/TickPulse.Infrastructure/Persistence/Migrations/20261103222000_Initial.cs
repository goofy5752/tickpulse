using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TickPulse.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class Initial : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS timescaledb;");

        migrationBuilder.CreateTable(
            name: "symbols",
            columns: table => new
            {
                id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.SerialColumn),
                name = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                is_active = table.Column<bool>(type: "boolean", nullable: false),
                created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                deactivated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                backfill_status = table.Column<short>(type: "smallint", nullable: false),
                backfilled_from = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_symbols", x => x.id);
            });

        migrationBuilder.CreateIndex(
            name: "ix_symbols_name",
            table: "symbols",
            column: "name",
            unique: true);

        // Initial symbols. Inserted without ids so the serial sequence stays in step.
        migrationBuilder.Sql("""
            INSERT INTO symbols (name, is_active, created_at, updated_at, deactivated_at, backfill_status, backfilled_from)
            VALUES ('BTCUSDT', true, now(), now(), NULL, 0, NULL),
                   ('ADAUSDT', true, now(), now(), NULL, 0, NULL),
                   ('ETHUSDT', true, now(), now(), NULL, 0, NULL);
            """);

        // Raw trades: a hypertable with 1-day chunks. The primary key makes replayed trades harmless
        // (ON CONFLICT DO NOTHING); it must contain the partitioning column.
        migrationBuilder.Sql("""
            CREATE TABLE price_ticks (
                symbol_id    integer        NOT NULL,
                agg_trade_id bigint         NOT NULL,
                price        numeric(24, 8) NOT NULL,
                quantity     numeric(28, 8) NOT NULL,
                trade_time   timestamptz    NOT NULL,
                ingested_at  timestamptz    NOT NULL,
                CONSTRAINT pk_price_ticks PRIMARY KEY (symbol_id, agg_trade_id, trade_time),
                CONSTRAINT fk_price_ticks_symbols_symbol_id FOREIGN KEY (symbol_id) REFERENCES symbols (id)
            );

            SELECT create_hypertable(
                'price_ticks',
                by_range('trade_time', INTERVAL '1 day'),
                create_default_indexes => false);

            CREATE INDEX ix_price_ticks_symbol_id_trade_time ON price_ticks (symbol_id, trade_time DESC);
            """);

        // Community-licensed policies. Queries never depend on them. Every primary-key column is in
        // segmentby or orderby, so ON CONFLICT checks against compressed chunks stay narrow.
        migrationBuilder.Sql("""
            ALTER TABLE price_ticks SET (
                timescaledb.compress,
                timescaledb.compress_segmentby = 'symbol_id',
                timescaledb.compress_orderby = 'trade_time, agg_trade_id');

            SELECT add_compression_policy('price_ticks', INTERVAL '2 days');
            SELECT add_retention_policy('price_ticks', INTERVAL '14 days');
            """);

        // 1-minute candles, kept forever. source: 1 = stream, 2 = backfill. Backfill rows carry no
        // trade ids or sums and never feed the 24 h average. quote_volume (sum of price * quantity, up to
        // 16 dp) is unconstrained numeric so that adding batch after batch never rounds.
        migrationBuilder.Sql("""
            CREATE TABLE candles_1m (
                symbol_id      integer        NOT NULL,
                bucket_start   timestamptz    NOT NULL,
                open           numeric(24, 8) NOT NULL,
                high           numeric(24, 8) NOT NULL,
                low            numeric(24, 8) NOT NULL,
                close          numeric(24, 8) NOT NULL,
                first_trade_id bigint         NULL,
                last_trade_id  bigint         NULL,
                price_sum      numeric(38, 8) NULL,
                tick_count     bigint         NULL,
                volume         numeric(28, 8) NOT NULL,
                quote_volume   numeric        NOT NULL,
                source         smallint       NOT NULL,
                CONSTRAINT pk_candles_1m PRIMARY KEY (symbol_id, bucket_start),
                CONSTRAINT fk_candles_1m_symbols_symbol_id FOREIGN KEY (symbol_id) REFERENCES symbols (id),
                CONSTRAINT ck_candles_1m_source CHECK (source IN (1, 2))
            );
            """);

        // Stream minutes only: the exact 24 h average sums and the first stream minute read just these rows,
        // without filtering out the (much more numerous) backfill minutes.
        migrationBuilder.Sql("""
            CREATE INDEX ix_candles_1m_stream ON candles_1m (symbol_id, bucket_start) WHERE source = 1;
            """);

        // No permanent staging table: each tick batch stages into a session temp table inside its
        // own transaction, so concurrent writers never see each other's rows.
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Dropping the hypertable also removes its compression and retention jobs.
        migrationBuilder.Sql("DROP INDEX IF EXISTS ix_candles_1m_stream;");
        migrationBuilder.DropTable(name: "candles_1m");
        migrationBuilder.DropTable(name: "price_ticks");
        migrationBuilder.DropTable(name: "symbols");
    }
}

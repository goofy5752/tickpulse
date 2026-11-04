using Npgsql;
using TickPulse.Application.Abstractions;
using TickPulse.Integration.Tests.Fixtures;

namespace TickPulse.Integration.Tests.Persistence;

public sealed class SchemaTests(TimescaleFixture db)
{
    [Fact]
    public async Task Migrations_CreateHypertable()
    {
        var hypertables = await QueryStringsAsync(
            "SELECT hypertable_name FROM timescaledb_information.hypertables");

        hypertables.ShouldContain("price_ticks");
    }

    [Fact]
    public void Migrations_SeedInitialSymbols()
    {
        // Captured by the fixture straight after MigrateAsync, before any test or reset touched the table.
        var seeded = db.InitialSymbols;

        seeded.Select(s => s.Name).Order(StringComparer.Ordinal).ShouldBe(["ADAUSDT", "BTCUSDT", "ETHUSDT"]);
        seeded.ShouldAllBe(s => s.IsActive && s.DeactivatedAt == null && s.BackfillStatus == BackfillStatus.NotRequested);
    }

    [Fact]
    public async Task Migrations_RegisterCompressionAndRetentionJobs()
    {
        var jobs = await QueryStringsAsync(
            "SELECT proc_name FROM timescaledb_information.jobs WHERE hypertable_name = 'price_ticks'");

        jobs.ShouldContain("policy_compression");
        jobs.ShouldContain("policy_retention");
    }

    [Fact]
    public async Task Migrations_CompressionSettings_CoverEveryPrimaryKeyColumn()
    {
        // attname:segmentby_index:orderby_index
        var settings = await QueryStringsAsync(
            """
            SELECT concat_ws(':', attname, coalesce(segmentby_column_index::text, ''), coalesce(orderby_column_index::text, ''))
              FROM timescaledb_information.compression_settings
             WHERE hypertable_name = 'price_ticks'
            """);

        settings.Order(StringComparer.Ordinal).ShouldBe(["agg_trade_id::2", "symbol_id:1:", "trade_time::1"]);
    }

    [Fact]
    public async Task Migrations_NoPermanentStagingTable()
    {
        var tables = await QueryStringsAsync(
            "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' AND table_type = 'BASE TABLE'");

        tables.Order(StringComparer.Ordinal).ShouldBe(["__EFMigrationsHistory", "candles_1d", "candles_1h", "candles_1m", "price_ticks", "symbols"]);
    }

    [Fact]
    public async Task Migrations_MaintainRollupsWithStatementTriggers()
    {
        var triggers = await QueryStringsAsync(
            """
            SELECT tgname || ':' || (pg_get_triggerdef(oid) LIKE '%FOR EACH STATEMENT%')::text
              FROM pg_trigger
             WHERE tgrelid = 'candles_1m'::regclass AND NOT tgisinternal
            """);

        triggers.Order(StringComparer.Ordinal).ShouldBe(
            ["candles_1m_rollups_delete:true", "candles_1m_rollups_insert:true", "candles_1m_rollups_update:true"]);
    }

    [Fact]
    public async Task Migrations_CreatePartialStreamCandleIndex()
    {
        var definitions = await QueryStringsAsync(
            "SELECT indexdef FROM pg_indexes WHERE schemaname = 'public' AND indexname = 'ix_candles_1m_stream'");

        definitions.ShouldHaveSingleItem().ShouldBe(
            "CREATE INDEX ix_candles_1m_stream ON public.candles_1m USING btree (symbol_id, bucket_start) WHERE (source = 1)");
    }

    [Fact]
    public async Task ResetAsync_AfterWrites_RestoresSeedAndClearsMarketData()
    {
        await db.ResetAsync();
        await using (var connection = await OpenAsync())
        await using (var command = new NpgsqlCommand(
            """
            INSERT INTO symbols (name, is_active, created_at, updated_at, backfill_status)
            VALUES ('SOLUSDT', true, now(), now(), 0);
            UPDATE symbols SET is_active = false, deactivated_at = now(), backfill_status = 3 WHERE name = 'BTCUSDT';
            INSERT INTO price_ticks VALUES (1, 10, 100.5, 0.1, '2026-01-01T00:00:00Z', now());
            INSERT INTO candles_1m (symbol_id, bucket_start, open, high, low, close, volume, quote_volume, source)
            VALUES (1, '2026-01-01T00:00:00Z', 1, 1, 1, 1, 1, 1, 2);
            """,
            connection))
        {
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await db.ResetAsync();

        var state = await QueryStringsAsync(
            """
            SELECT concat_ws(':', id, name, is_active, deactivated_at IS NULL, backfill_status) FROM symbols
            UNION ALL SELECT 'ticks:' || count(*) FROM price_ticks
            UNION ALL SELECT 'candles:' || count(*) FROM candles_1m
            UNION ALL SELECT 'rollups:' || (SELECT count(*) FROM candles_1h) + (SELECT count(*) FROM candles_1d)
            """);
        state.Order(StringComparer.Ordinal).ShouldBe(
        [
            "1:BTCUSDT:t:t:0",
            "2:ADAUSDT:t:t:0",
            "3:ETHUSDT:t:t:0",
            "candles:0",
            "rollups:0",
            "ticks:0",
        ]);
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(db.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        return connection;
    }

    private async Task<List<string>> QueryStringsAsync(string sql)
    {
        var values = new List<string>();
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }
}

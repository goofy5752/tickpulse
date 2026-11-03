using Npgsql;
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
    public async Task Migrations_SeedInitialSymbols()
    {
        await db.ResetAsync();

        var symbols = new List<(string Name, bool IsActive)>();
        await using (var connection = await OpenAsync())
        await using (var command = new NpgsqlCommand("SELECT name, is_active FROM symbols ORDER BY name", connection))
        await using (var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken))
        {
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            {
                symbols.Add((reader.GetString(0), reader.GetBoolean(1)));
            }
        }

        symbols.Select(s => s.Name).ShouldBe(["ADAUSDT", "BTCUSDT", "ETHUSDT"]);
        symbols.ShouldAllBe(s => s.IsActive);
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
            INSERT INTO tick_staging VALUES (1, 11, 100.5, 0.1, '2026-01-01T00:00:00Z', now());
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
            UNION ALL SELECT 'staging:' || count(*) FROM tick_staging
            """);
        state.Order(StringComparer.Ordinal).ShouldBe(
        [
            "1:BTCUSDT:t:t:0",
            "2:ADAUSDT:t:t:0",
            "3:ETHUSDT:t:t:0",
            "candles:0",
            "staging:0",
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

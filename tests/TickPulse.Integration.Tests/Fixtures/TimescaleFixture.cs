using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using TickPulse.Infrastructure.Persistence;
using TickPulse.Integration.Tests.Fixtures;
using Xunit.Sdk;
using Xunit.v3;

[assembly: AssemblyFixture(typeof(TimescaleFixture))]

// Every test shares one database and resets it with ResetAsync(), so tests must not run concurrently.
[assembly: Parallelization(Mode = ParallelMode.None)]

namespace TickPulse.Integration.Tests.Fixtures;

/// <summary>
/// One TimescaleDB container for the whole test assembly, with the real migrations applied once.
/// Tests that write call <see cref="ResetAsync"/> first.
/// </summary>
public sealed class TimescaleFixture : IAsyncLifetime
{
    /// <summary>The single pinned image tag. The docker-compose files must use exactly the same tag.</summary>
    public const string TimescaleImage = "timescale/timescaledb:2.31.0-pg17";

    private static readonly string[] SeedSymbols = ["BTCUSDT", "ADAUSDT", "ETHUSDT"];

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder(TimescaleImage)
        .WithDatabase("tickpulse")
        .WithUsername("tickpulse")
        .WithPassword("tickpulse")
        .Build();

    /// <summary>
    /// The database with <see cref="NpgsqlConnectionPolicy"/> applied (GSS encryption off, the default pool size), like
    /// every connection the app opens. Use it for helper connections and for hosts under test.
    /// </summary>
    public string ConnectionString { get; private set; } = string.Empty;

    /// <summary>
    /// The connection string exactly as the container reports it, with no policy applied. Only for tests of
    /// <see cref="NpgsqlConnectionPolicy"/> itself, which need to see what an unset keyword turns into.
    /// </summary>
    public string RawConnectionString { get; private set; } = string.Empty;

    /// <summary>The <c>symbols</c> rows exactly as the migrations left them, read before any test ran.</summary>
    public IReadOnlyList<SymbolEntity> InitialSymbols { get; private set; } = [];

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        RawConnectionString = _container.GetConnectionString();
        ConnectionString = NpgsqlConnectionPolicy.Apply(RawConnectionString, NpgsqlPolicySettings.Default);

        var options = new DbContextOptionsBuilder<TickPulseDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;
        await using (var context = new TickPulseDbContext(options))
        {
            await context.Database.MigrateAsync();
            InitialSymbols = await context.Symbols.AsNoTracking().OrderBy(s => s.Id).ToListAsync();
        }

        // Keep the policy jobs registered but stop the background scheduler from compressing or
        // dropping chunks under a running test (test data often has old trade times).
        await using var connection = await OpenAsync();
        await using var pause = new NpgsqlCommand(
            """
            SELECT alter_job(job_id, scheduled => false)
              FROM timescaledb_information.jobs
             WHERE hypertable_name = 'price_ticks'
            """,
            connection);
        await pause.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Clears ticks and candles, removes symbols added by tests, and puts the three seeded symbols
    /// back to their initial state (active, no backfill). Seeded ids are preserved.
    /// </summary>
    public async Task ResetAsync()
    {
        await using var connection = await OpenAsync();
        await using var batch = new NpgsqlBatch(connection)
        {
            BatchCommands =
            {
                new NpgsqlBatchCommand("DELETE FROM price_ticks"),
                new NpgsqlBatchCommand("DELETE FROM candles_1m"),
                new NpgsqlBatchCommand("DELETE FROM symbols WHERE NOT (name = ANY($1))") { Parameters = { new() { Value = SeedSymbols } } },
                new NpgsqlBatchCommand(
                    """
                    UPDATE symbols
                       SET is_active = true,
                           updated_at = now(),
                           deactivated_at = NULL,
                           reactivated_at = NULL,
                           backfill_status = 0,
                           backfilled_from = NULL
                     WHERE name = ANY($1)
                    """)
                { Parameters = { new() { Value = SeedSymbols } } },
            },
        };
        await batch.ExecuteNonQueryAsync();

        var restored = batch.BatchCommands[^1].RecordsAffected;
        if (restored != SeedSymbols.Length)
        {
            throw new InvalidOperationException(
                $"Expected the {SeedSymbols.Length} seeded symbols, found {restored}; the migration seed is missing.");
        }
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        return connection;
    }
}

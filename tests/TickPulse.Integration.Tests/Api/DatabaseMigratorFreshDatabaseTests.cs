using Microsoft.Extensions.Logging;
using Npgsql;
using TickPulse.Integration.Tests.Fixtures;
using TickPulse.Integration.Tests.Ingestion;

namespace TickPulse.Integration.Tests.Api;

/// <summary>The first start on an empty database: the schema is created and nothing is logged as an error.</summary>
public sealed class DatabaseMigratorFreshDatabaseTests(TimescaleFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FirstStart_OnAnEmptyDatabase_MigratesWithoutLoggingErrors()
    {
        var name = "fresh_" + Guid.NewGuid().ToString("N");
        await ExecuteAsync($"CREATE DATABASE {name}");
        try
        {
            var logs = new IngestionTestHost.LogCapture();
            var fresh = new NpgsqlConnectionStringBuilder(db.ConnectionString) { Database = name, Pooling = false }.ConnectionString;
            await using var host = new ApiFactory(db).WithSetting("ConnectionStrings:TickPulse", fresh).WithLogging(logs);
            await host.InitializeAsync();
            _ = host.Services; // starts the host, and with it the migrator

            logs.Entries.ShouldContain(e => e.EventName == "LogMigrated" && !e.Message.Contains("; 0 migration", StringComparison.Ordinal));
            logs.Entries.Where(e => e.Level >= LogLevel.Error).Select(e => $"{e.Category}: {e.Message}").ShouldBeEmpty();
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await ExecuteAsync($"DROP DATABASE IF EXISTS {name} WITH (FORCE)");
        }
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(db.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }
}

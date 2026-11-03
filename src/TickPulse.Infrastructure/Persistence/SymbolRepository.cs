using System.Data.Common;
using Npgsql;
using NpgsqlTypes;
using TickPulse.Application.Abstractions;
using TickPulse.Domain;

namespace TickPulse.Infrastructure.Persistence;

/// <summary>
/// The <c>symbols</c> table through raw Npgsql. The add path checks the active-symbol limit and writes in
/// one transaction under a transaction-scoped advisory lock, so concurrent adds cannot overshoot the limit.
/// </summary>
public sealed class SymbolRepository(NpgsqlDataSource dataSource, TimeProvider clock) : ISymbolRepository
{
    /// <summary>Key of the transaction-scoped advisory lock that serialises symbol adds.</summary>
    public const long AddLockKey = 0x5450_5359_4D41_4444; // "TPSYMADD"

    private const string Columns =
        "id, name, is_active, created_at, deactivated_at, backfill_status, backfilled_from";

    /// <inheritdoc />
    public Task<IReadOnlyList<MonitoredSymbol>> ListAsync(CancellationToken ct) =>
        QueryAsync($"SELECT {Columns} FROM symbols ORDER BY id", ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<MonitoredSymbol>> ListActiveAsync(CancellationToken ct) =>
        QueryAsync($"SELECT {Columns} FROM symbols WHERE is_active ORDER BY id", ct);

    /// <inheritdoc />
    public async Task<MonitoredSymbol?> FindAsync(SymbolName name, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand($"SELECT {Columns} FROM symbols WHERE name = $1");
        command.Parameters.Add(NameParameter(name));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    /// <inheritdoc />
    public async Task<SymbolAddOutcome> AddOrReactivateAsync(SymbolName name, int maxActiveSymbols, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxActiveSymbols);

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock($1)", connection, transaction))
        {
            lockCommand.Parameters.Add(new NpgsqlParameter<long> { TypedValue = AddLockKey });
            await lockCommand.ExecuteNonQueryAsync(ct);
        }

        MonitoredSymbol? existing;
        await using (var find = new NpgsqlCommand($"SELECT {Columns} FROM symbols WHERE name = $1", connection, transaction))
        {
            find.Parameters.Add(NameParameter(name));
            await using var reader = await find.ExecuteReaderAsync(ct);
            existing = await reader.ReadAsync(ct) ? Read(reader) : null;
        }

        if (existing is { IsActive: true })
        {
            await transaction.CommitAsync(ct);
            return new SymbolAddOutcome(existing, SymbolAddStatus.AlreadyActive);
        }

        int activeCount;
        await using (var count = new NpgsqlCommand("SELECT count(*)::integer FROM symbols WHERE is_active", connection, transaction))
        {
            activeCount = (int)(await count.ExecuteScalarAsync(ct))!;
        }

        if (activeCount >= maxActiveSymbols)
        {
            await transaction.CommitAsync(ct);
            return new SymbolAddOutcome(null, SymbolAddStatus.LimitReached);
        }

        var sql = existing is null
            ? $"""
              INSERT INTO symbols (name, is_active, created_at, updated_at, deactivated_at, backfill_status, backfilled_from)
              VALUES ($1, true, $2, $2, NULL, 0, NULL)
              RETURNING {Columns}
              """
            : $"""
              UPDATE symbols SET is_active = true, deactivated_at = NULL, updated_at = $2
               WHERE name = $1
              RETURNING {Columns}
              """;

        MonitoredSymbol stored;
        await using (var write = new NpgsqlCommand(sql, connection, transaction))
        {
            write.Parameters.Add(NameParameter(name));
            write.Parameters.Add(NowParameter());
            await using var reader = await write.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                throw new InvalidOperationException($"Writing symbol {name} returned no row.");
            }

            stored = Read(reader);
        }

        await transaction.CommitAsync(ct);
        return new SymbolAddOutcome(stored, existing is null ? SymbolAddStatus.Created : SymbolAddStatus.Reactivated);
    }

    /// <inheritdoc />
    public async Task<bool> DeactivateAsync(SymbolName name, CancellationToken ct)
    {
        // An already inactive symbol keeps its original deactivation time.
        await using var command = dataSource.CreateCommand(
            """
            UPDATE symbols
               SET is_active = false,
                   deactivated_at = CASE WHEN is_active THEN $2 ELSE deactivated_at END,
                   updated_at = $2
             WHERE name = $1
            """);
        command.Parameters.Add(NameParameter(name));
        command.Parameters.Add(NowParameter());
        return await command.ExecuteNonQueryAsync(ct) > 0;
    }

    /// <inheritdoc />
    public async Task<int> CountActiveAsync(CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("SELECT count(*)::integer FROM symbols WHERE is_active");
        return (int)(await command.ExecuteScalarAsync(ct))!;
    }

    /// <inheritdoc />
    public async Task SetBackfillStatusAsync(int symbolId, BackfillStatus status, DateTime? fromUtc, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(
            "UPDATE symbols SET backfill_status = $2, backfilled_from = $3, updated_at = $4 WHERE id = $1");
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = symbolId });
        command.Parameters.Add(new NpgsqlParameter<short> { TypedValue = (short)status });
        command.Parameters.Add(new NpgsqlParameter { Value = fromUtc ?? (object)DBNull.Value, NpgsqlDbType = NpgsqlDbType.TimestampTz });
        command.Parameters.Add(NowParameter());
        await command.ExecuteNonQueryAsync(ct);
    }

    private static NpgsqlParameter<string> NameParameter(SymbolName name) =>
        new() { TypedValue = name.Value, NpgsqlDbType = NpgsqlDbType.Varchar };

    private static MonitoredSymbol Read(DbDataReader reader) => new(
        reader.GetInt32(0),
        reader.GetString(1),
        reader.GetBoolean(2),
        reader.GetDateTime(3),
        reader.IsDBNull(4) ? null : reader.GetDateTime(4),
        (BackfillStatus)reader.GetInt16(5),
        reader.IsDBNull(6) ? null : reader.GetDateTime(6));

    private NpgsqlParameter<DateTime> NowParameter() =>
        new() { TypedValue = clock.GetUtcNow().UtcDateTime, NpgsqlDbType = NpgsqlDbType.TimestampTz };

    private async Task<IReadOnlyList<MonitoredSymbol>> QueryAsync(string sql, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(sql);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var symbols = new List<MonitoredSymbol>();
        while (await reader.ReadAsync(ct))
        {
            symbols.Add(Read(reader));
        }

        return symbols;
    }
}

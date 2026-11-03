using Npgsql;
using NpgsqlTypes;
using TickPulse.Application.Abstractions;
using TickPulse.Domain;

namespace TickPulse.Infrastructure.Persistence;

/// <summary>
/// Inserts historical exchange candles as <c>source = 2</c>. They carry no trade ids and no sums, so they
/// never feed the 24 h average, and <c>ON CONFLICT DO NOTHING</c> means they never overwrite a minute that
/// already exists (streamed or backfilled earlier). A later stream write replaces them.
/// </summary>
public sealed class CandleWriter(NpgsqlDataSource dataSource) : ICandleWriter
{
    private const string InsertSql = """
        INSERT INTO candles_1m (symbol_id, bucket_start, open, high, low, close, volume, quote_volume, source)
        SELECT $1, t.bucket_start, t.open, t.high, t.low, t.close, t.volume, t.quote_volume, 2
          FROM unnest($2, $3, $4, $5, $6, $7, $8) AS t(bucket_start, open, high, low, close, volume, quote_volume)
         ORDER BY t.bucket_start
        ON CONFLICT (symbol_id, bucket_start) DO NOTHING
        """;

    /// <inheritdoc />
    public async Task<int> InsertBackfillAsync(int symbolId, IReadOnlyList<Kline> klines, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(klines);
        if (klines.Count == 0)
        {
            return 0;
        }

        var openTimes = new DateTime[klines.Count];
        for (var i = 0; i < klines.Count; i++)
        {
            var openTime = klines[i].OpenTimeUtc;
            if (openTime.Kind != DateTimeKind.Utc || openTime.Ticks % TimeSpan.TicksPerMinute != 0)
            {
                // An unaligned start would create a bucket that no stream minute ever matches.
                throw new ArgumentException($"Kline open time {openTime:O} is not a UTC minute boundary.", nameof(klines));
            }

            openTimes[i] = openTime;
        }

        await using var command = dataSource.CreateCommand(InsertSql);
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = symbolId, NpgsqlDbType = NpgsqlDbType.Integer });
        command.Parameters.Add(new NpgsqlParameter<DateTime[]> { TypedValue = openTimes, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.TimestampTz });
        AddNumericArray(command, klines, k => k.Open);
        AddNumericArray(command, klines, k => k.High);
        AddNumericArray(command, klines, k => k.Low);
        AddNumericArray(command, klines, k => k.Close);
        AddNumericArray(command, klines, k => k.Volume);
        AddNumericArray(command, klines, k => k.QuoteVolume);

        return await command.ExecuteNonQueryAsync(ct);
    }

    private static void AddNumericArray(NpgsqlCommand command, IReadOnlyList<Kline> klines, Func<Kline, decimal> selector)
    {
        var values = new decimal[klines.Count];
        for (var i = 0; i < klines.Count; i++)
        {
            values[i] = selector(klines[i]);
        }

        command.Parameters.Add(new NpgsqlParameter<decimal[]> { TypedValue = values, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Numeric });
    }
}

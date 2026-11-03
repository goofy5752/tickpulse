using Npgsql;
using NpgsqlTypes;
using TickPulse.Application.Abstractions;
using TickPulse.Domain;

namespace TickPulse.Infrastructure.Persistence;

/// <summary>
/// Read-side queries over <c>price_ticks</c> and <c>candles_1m</c> through raw, parameterised Npgsql SQL on the
/// shared (unkeyed) data source. Higher periods are aggregated from the 1-minute candles with
/// <c>time_bucket(width, bucket_start, origin)</c> and the Monday origin <see cref="Period.Origin"/>.
/// </summary>
public sealed class MarketDataQueries(NpgsqlDataSource dataSource) : IMarketDataQueries
{
    // $1 symbol, $2 from, $3 full-minute start, $4 full-minute end (exclusive), $5 to (inclusive).
    // Whole minutes come from the stream candles, whose price_sum/tick_count are exact (see TickWriter);
    // backfill candles (source 2) carry no sums and are excluded. The partial minutes at either edge come from
    // the raw ticks. The sub-ranges [$2,$3), [$3,$4) and [$4,$5] are disjoint and together cover [$2,$5].
    // First/last tick times always come from the raw ticks of the whole window.
    private const string AverageSql = """
        SELECT (SELECT sum(price_sum) FROM candles_1m
                 WHERE symbol_id = $1 AND source = 1 AND bucket_start >= $3 AND bucket_start < $4),
               (SELECT sum(tick_count)::bigint FROM candles_1m
                 WHERE symbol_id = $1 AND source = 1 AND bucket_start >= $3 AND bucket_start < $4),
               lead.s, lead.c, trail.s, trail.c,
               (SELECT min(trade_time) FROM price_ticks WHERE symbol_id = $1 AND trade_time >= $2 AND trade_time <= $5),
               (SELECT max(trade_time) FROM price_ticks WHERE symbol_id = $1 AND trade_time >= $2 AND trade_time <= $5)
          FROM (SELECT sum(price) AS s, count(*) AS c FROM price_ticks
                 WHERE symbol_id = $1 AND trade_time >= $2 AND trade_time < $3) AS lead,
               (SELECT sum(price) AS s, count(*) AS c FROM price_ticks
                 WHERE symbol_id = $1 AND trade_time >= $4 AND trade_time <= $5) AS trail
        """;

    // $1 symbol, $2 bucket width, $3 origin, $4 window start, $5 window end (exclusive). Both sources count;
    // the close of a bucket is the close of its latest minute. Buckets without candles produce no row.
    private const string ClosesSql = """
        SELECT time_bucket($2, bucket_start, $3) AS b, last(close, bucket_start)
          FROM candles_1m
         WHERE symbol_id = $1 AND bucket_start >= $4 AND bucket_start < $5
         GROUP BY b
         ORDER BY b
        """;

    // $1 symbol, $2 bucket width, $3 origin, $4 limit. The window is the $4 periods that end with the period of
    // the latest candle, so the scan stays bounded however much history is kept; periods without candles are
    // absent. Both sources count.
    private const string CandlesSql = """
        WITH latest AS (
            SELECT time_bucket($2, max(bucket_start), $3) AS anchor FROM candles_1m WHERE symbol_id = $1)
        SELECT time_bucket($2, k.bucket_start, $3) AS b,
               first(k.open, k.bucket_start), max(k.high), min(k.low), last(k.close, k.bucket_start), sum(k.volume)
          FROM candles_1m k, latest
         WHERE k.symbol_id = $1 AND k.bucket_start >= latest.anchor - $2 * ($4 - 1)
         GROUP BY b
         ORDER BY b
        """;

    private const string CloseAtOrBeforeSql = """
        SELECT close FROM candles_1m
         WHERE symbol_id = $1 AND bucket_start <= $2
         ORDER BY bucket_start DESC
         LIMIT 1
        """;

    private const string ApproximateTickCountSql = "SELECT approximate_row_count('price_ticks')";

    private const string FirstStreamMinuteSql = "SELECT min(bucket_start) FROM candles_1m WHERE symbol_id = $1 AND source = 1";

    /// <inheritdoc />
    /// <remarks>
    /// Exact over <c>[fromUtc, toUtc]</c> (both inclusive): whole minutes are read from the stream candles and the
    /// partial edge minutes from the raw ticks; when both bounds fall into the same minute only raw ticks are
    /// read. Returns null when the window holds no raw tick. The window must lie inside the raw-tick retention.
    /// </remarks>
    public async Task<AverageWindow?> GetAverageAsync(int symbolId, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        RequireUtc(fromUtc, nameof(fromUtc));
        RequireUtc(toUtc, nameof(toUtc));
        ArgumentOutOfRangeException.ThrowIfLessThan(toUtc, fromUtc);

        var fullStart = Period.OneMinute.AlignUp(fromUtc);
        var fullEnd = Period.OneMinute.AlignDown(toUtc);
        if (fullStart >= fullEnd)
        {
            // No whole minute inside the window: collapse the candle range so that the edges cover [from, to].
            fullStart = fullEnd = toUtc;
        }

        await using var command = dataSource.CreateCommand(AverageSql);
        command.Parameters.Add(Integer(symbolId));
        command.Parameters.Add(TimestampTz(fromUtc));
        command.Parameters.Add(TimestampTz(fullStart));
        command.Parameters.Add(TimestampTz(fullEnd));
        command.Parameters.Add(TimestampTz(toUtc));

        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        if (reader.IsDBNull(6))
        {
            return null;
        }

        PriceSumPart[] parts =
        [
            new(reader.IsDBNull(0) ? 0m : reader.GetDecimal(0), reader.IsDBNull(1) ? 0L : reader.GetInt64(1)),
            new(reader.IsDBNull(2) ? 0m : reader.GetDecimal(2), reader.GetInt64(3)),
            new(reader.IsDBNull(4) ? 0m : reader.GetDecimal(4), reader.GetInt64(5)),
        ];
        var average = AverageCalculator.Combine(parts);
        return average is { } value
            ? new AverageWindow(value, reader.GetDateTime(6), reader.GetDateTime(7), parts.Sum(p => p.Count))
            : null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PricePoint>> GetClosesAsync(int symbolId, Period p, SmaWindow w, CancellationToken ct)
    {
        RequirePeriod(p);
        RequireUtc(w.Start, nameof(w));
        RequireUtc(w.EndExclusive, nameof(w));
        if (w.IsEmpty)
        {
            return [];
        }

        await using var command = dataSource.CreateCommand(ClosesSql);
        command.Parameters.Add(Integer(symbolId));
        command.Parameters.Add(Interval(p.Length));
        command.Parameters.Add(TimestampTz(Period.Origin));
        command.Parameters.Add(TimestampTz(w.Start));
        command.Parameters.Add(TimestampTz(w.EndExclusive));

        var closes = new List<PricePoint>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            closes.Add(new PricePoint(reader.GetDateTime(0), reader.GetDecimal(1)));
        }

        return closes;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Covers the <paramref name="limit"/> periods that end with the period of the latest candle (the current,
    /// possibly incomplete one included), oldest first. Periods without candles are absent, so fewer rows may
    /// come back.
    /// </remarks>
    public async Task<IReadOnlyList<CandleRow>> GetCandlesAsync(int symbolId, Period p, int limit, CancellationToken ct)
    {
        RequirePeriod(p);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        await using var command = dataSource.CreateCommand(CandlesSql);
        command.Parameters.Add(Integer(symbolId));
        command.Parameters.Add(Interval(p.Length));
        command.Parameters.Add(TimestampTz(Period.Origin));
        command.Parameters.Add(Integer(limit));

        var candles = new List<CandleRow>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            candles.Add(new CandleRow(
                reader.GetDateTime(0),
                reader.GetDecimal(1),
                reader.GetDecimal(2),
                reader.GetDecimal(3),
                reader.GetDecimal(4),
                reader.GetDecimal(5)));
        }

        return candles;
    }

    /// <inheritdoc />
    /// <remarks>The close of the latest 1-minute candle (any source) whose bucket starts at or before <paramref name="utc"/>.</remarks>
    public async Task<decimal?> GetCloseAtOrBeforeAsync(int symbolId, DateTime utc, CancellationToken ct)
    {
        RequireUtc(utc, nameof(utc));

        await using var command = dataSource.CreateCommand(CloseAtOrBeforeSql);
        command.Parameters.Add(Integer(symbolId));
        command.Parameters.Add(TimestampTz(utc));
        return await command.ExecuteScalarAsync(ct) is decimal close ? close : null;
    }

    /// <inheritdoc />
    /// <remarks>An estimate from the planner statistics of the <c>price_ticks</c> chunks; cheap at any size.</remarks>
    public async Task<long> ApproximateTickCountAsync(CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(ApproximateTickCountSql);
        var count = await command.ExecuteScalarAsync(ct) is long value ? value : 0L;
        return Math.Max(count, 0L);
    }

    /// <inheritdoc />
    /// <remarks>The earliest minute that holds streamed ticks; backfill stops there.</remarks>
    public async Task<DateTime?> FirstStreamMinuteAsync(int symbolId, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(FirstStreamMinuteSql);
        command.Parameters.Add(Integer(symbolId));
        return await command.ExecuteScalarAsync(ct) is DateTime minute ? minute : null;
    }

    private static void RequireUtc(DateTime value, string paramName)
    {
        if (value.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Timestamp must have Kind=Utc.", paramName);
        }
    }

    private static void RequirePeriod(Period p)
    {
        if (p.Length <= TimeSpan.Zero)
        {
            throw new ArgumentException("Uninitialised Period.", nameof(p));
        }
    }

    private static NpgsqlParameter<int> Integer(int value) => new() { TypedValue = value, NpgsqlDbType = NpgsqlDbType.Integer };

    private static NpgsqlParameter<DateTime> TimestampTz(DateTime value) =>
        new() { TypedValue = value, NpgsqlDbType = NpgsqlDbType.TimestampTz };

    private static NpgsqlParameter<TimeSpan> Interval(TimeSpan value) => new() { TypedValue = value, NpgsqlDbType = NpgsqlDbType.Interval };
}

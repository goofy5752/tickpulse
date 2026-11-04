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
    /// <summary>The largest <c>limit</c> that <see cref="GetCandlesAsync"/> accepts; it bounds the scanned time range.</summary>
    public const int MaxCandleLimit = 1000;

    // $1 symbol, $2 from, $3 full-minute start, $4 full-minute end (exclusive), $5 to (inclusive).
    // Whole minutes come from the stream candles, whose price_sum/tick_count are exact (see TickWriter);
    // backfill candles (source 2) carry no sums and are excluded, and the partial index ix_candles_1m_stream
    // covers exactly the stream rows. The partial minutes at either edge come from the raw ticks. The sub-ranges
    // [$2,$3), [$3,$4) and [$4,$5] are disjoint and together cover [$2,$5]. First/last tick times always come
    // from the raw ticks of the whole window.
    internal const string AverageSql = """
        SELECT whole.s, whole.c, lead.s, lead.c, trail.s, trail.c,
               (SELECT min(trade_time) FROM price_ticks WHERE symbol_id = $1 AND trade_time >= $2 AND trade_time <= $5),
               (SELECT max(trade_time) FROM price_ticks WHERE symbol_id = $1 AND trade_time >= $2 AND trade_time <= $5)
          FROM (SELECT sum(price_sum) AS s, sum(tick_count)::bigint AS c FROM candles_1m
                 WHERE symbol_id = $1 AND source = 1 AND bucket_start >= $3 AND bucket_start < $4) AS whole,
               (SELECT sum(price) AS s, count(*) AS c FROM price_ticks
                 WHERE symbol_id = $1 AND trade_time >= $2 AND trade_time < $3) AS lead,
               (SELECT sum(price) AS s, count(*) AS c FROM price_ticks
                 WHERE symbol_id = $1 AND trade_time >= $4 AND trade_time <= $5) AS trail
        """;

    // $1 symbol, $2 bucket width, $3 origin, $4 window start, $5 window end (exclusive). Both sources count;
    // the close of a bucket is the close of its latest minute. Buckets without candles produce no row.
    internal const string ClosesSql = """
        SELECT time_bucket($2, bucket_start, $3) AS b, last(close, bucket_start)
          FROM candles_1m
         WHERE symbol_id = $1 AND bucket_start >= $4 AND bucket_start < $5
         GROUP BY b
         ORDER BY b
        """;

    // $1 symbol, $2 bucket width, $3 origin, $4 lookback (width * (limit - 1)). The anchor, the start of the
    // period holding the latest candle, is an uncorrelated scalar subquery: it runs once as an InitPlan, so the
    // outer scan is a plain primary-key range scan from anchor - lookback. With limit capped at MaxCandleLimit
    // the range is bounded for every period. Periods without candles are absent. Both sources count.
    internal const string CandlesSql = """
        SELECT time_bucket($2, bucket_start, $3) AS b,
               first(open, bucket_start), max(high), min(low), last(close, bucket_start), sum(volume)
          FROM candles_1m
         WHERE symbol_id = $1
           AND bucket_start >= (SELECT time_bucket($2, max(bucket_start), $3) FROM candles_1m WHERE symbol_id = $1) - $4
         GROUP BY b
         ORDER BY b
        """;

    internal const string CloseAtOrBeforeSql = """
        SELECT close FROM candles_1m
         WHERE symbol_id = $1 AND bucket_start <= $2
         ORDER BY bucket_start DESC
         LIMIT 1
        """;

    // $1 symbol ids, $2 instant. One backward primary-key probe per id (LIMIT 1 inside the lateral join); ids
    // without a candle at or before $2 produce no row. DISTINCT answers a repeated id once.
    internal const string ClosesAtOrBeforeSql = """
        SELECT s.id, c.bucket_start, c.close
          FROM (SELECT DISTINCT unnest($1::int[]) AS id) AS s
         CROSS JOIN LATERAL (
               SELECT bucket_start, close FROM candles_1m
                WHERE symbol_id = s.id AND bucket_start <= $2
                ORDER BY bucket_start DESC
                LIMIT 1) AS c
        """;

    internal const string ApproximateTickCountSql = "SELECT approximate_row_count('price_ticks')";

    // Reads the partial index ix_candles_1m_stream.
    internal const string FirstStreamMinuteSql = "SELECT min(bucket_start) FROM candles_1m WHERE symbol_id = $1 AND source = 1";

    internal const string BackfillRangeSql =
        "SELECT min(bucket_start), max(bucket_start) FROM candles_1m WHERE symbol_id = $1 AND source = 2 AND bucket_start < $2";

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
    /// come back. <paramref name="limit"/> must be in <c>[1, MaxCandleLimit]</c>, so the scanned range never
    /// exceeds <c>MaxCandleLimit</c> periods.
    /// </remarks>
    public async Task<IReadOnlyList<CandleRow>> GetCandlesAsync(int symbolId, Period p, int limit, CancellationToken ct)
    {
        RequirePeriod(p);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, MaxCandleLimit);

        await using var command = dataSource.CreateCommand(CandlesSql);
        command.Parameters.Add(Integer(symbolId));
        command.Parameters.Add(Interval(p.Length));
        command.Parameters.Add(TimestampTz(Period.Origin));
        command.Parameters.Add(Interval(p.Length * (limit - 1)));

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
    /// <remarks>Any source counts, as in <see cref="GetCloseAtOrBeforeAsync"/>. An empty <paramref name="symbolIds"/> runs no query.</remarks>
    public async Task<IReadOnlyDictionary<int, PricePoint>> GetClosesAtOrBeforeAsync(IReadOnlyCollection<int> symbolIds, DateTime utc, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(symbolIds);
        RequireUtc(utc, nameof(utc));
        var closes = new Dictionary<int, PricePoint>(symbolIds.Count);
        if (symbolIds.Count == 0)
        {
            return closes;
        }

        await using var command = dataSource.CreateCommand(ClosesAtOrBeforeSql);
        command.Parameters.Add(new NpgsqlParameter<int[]> { TypedValue = [.. symbolIds], NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Integer });
        command.Parameters.Add(TimestampTz(utc));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            closes[reader.GetInt32(0)] = new PricePoint(reader.GetDateTime(1), reader.GetDecimal(2));
        }

        return closes;
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

    /// <inheritdoc />
    /// <remarks>Two primary-key endpoint scans (min forwards, max backwards from <paramref name="beforeUtc"/>).</remarks>
    public async Task<MinuteRange?> BackfillRangeBeforeAsync(int symbolId, DateTime beforeUtc, CancellationToken ct)
    {
        RequireUtc(beforeUtc, nameof(beforeUtc));
        await using var command = dataSource.CreateCommand(BackfillRangeSql);
        command.Parameters.Add(Integer(symbolId));
        command.Parameters.Add(TimestampTz(beforeUtc));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) && !reader.IsDBNull(0)
            ? new MinuteRange(reader.GetDateTime(0), reader.GetDateTime(1))
            : null;
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

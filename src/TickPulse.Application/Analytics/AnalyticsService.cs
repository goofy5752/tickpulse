using TickPulse.Application.Abstractions;
using TickPulse.Application.Ingestion;
using TickPulse.Application.Results;
using TickPulse.Domain;

namespace TickPulse.Application.Analytics;

/// <summary>
/// The read-side use cases behind the public API: the 24 h average, the simple moving average, the symbol list and
/// the candles. Parameters are validated before the symbol is looked up, so a bad request is a <c>Validation</c>
/// error whatever the symbol. Inactive symbols keep their history and are served like active ones; a symbol that was
/// never monitored (or is malformed) is <c>UnknownSymbol</c>.
/// </summary>
public sealed class AnalyticsService(
    IMarketDataQueries queries,
    ISymbolRepository symbols,
    IngestionState ingestion,
    TimeProvider clock)
{
    public const int AverageWindowHours = 24;
    public const int MinDataPoints = 1;
    public const int MaxDataPoints = 1000;
    public const int MinCandleLimit = 1;
    public const int DefaultCandleLimit = 300;

    /// <summary>The largest candle <c>limit</c>; it matches the infrastructure query's bound.</summary>
    public const int MaxCandleLimit = 1000;

    /// <summary>
    /// How far before the minute of <c>now − 24 h</c> the reference close of the 24 h change may lie while the
    /// symbol's backfill is not <see cref="BackfillStatus.Completed"/>. Until then, an older reference can be the
    /// frontier of a backfill that has not reached <c>now − 24 h</c> yet (also after a failure, or while a
    /// reactivation gap is being filled), so the change is null instead. Once the backfill has completed, an older
    /// close is the last real price before the window (a thin pair without trades), as in a rolling 24 h ticker.
    /// </summary>
    public static readonly TimeSpan DayAgoCloseTolerance = TimeSpan.FromMinutes(15);

    private static readonly TimeSpan AverageWindow = TimeSpan.FromHours(AverageWindowHours);
    /// <summary>The detail of the validation failure for an <c>s</c> after the current time.</summary>
    public const string FutureStartDetail = "s must not be in the future.";

    private static readonly string InvalidPeriodDetail = $"p must be one of {string.Join(", ", Period.All.Reverse().Select(p => p.Code))}.";

    /// <summary>The mean of the trades collected in <c>[now − 24 h, now]</c>.</summary>
    public async Task<AppResult<AveragePriceResult>> GetAverageAsync(string symbol, CancellationToken ct)
    {
        var found = await FindAsync(symbol, ct);
        if (found is null)
        {
            return UnknownSymbol<AveragePriceResult>(symbol);
        }

        var now = Now();
        var window = await queries.GetAverageAsync(found.Id, now - AverageWindow, now, ct);
        if (window is null)
        {
            return AppResult<AveragePriceResult>.Fail(
                AppError.NoData,
                $"No trades were collected for '{found.Name}' in the last {AverageWindowHours} hours.");
        }

        var covered = (window.LastTickUtc - window.FirstTickUtc).Ticks / TimeSpan.TicksPerSecond;
        return AppResult<AveragePriceResult>.Ok(new AveragePriceResult(
            found.Name,
            window.Average,
            window.FirstTickUtc,
            window.LastTickUtc,
            covered,
            window.SampleCount,
            AverageWindowHours));
    }

    /// <summary>
    /// The mean of the closes of up to <paramref name="n"/> complete <paramref name="p"/> buckets: the last ones
    /// before now, or, with <paramref name="sUtc"/>, the first ones starting at or after it.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="sUtc"/> is not <see cref="DateTimeKind.Utc"/>.</exception>
    public async Task<AppResult<SmaResult>> GetSmaAsync(string symbol, int? n, string? p, DateTime? sUtc, CancellationToken ct)
    {
        if (sUtc is { Kind: not DateTimeKind.Utc })
        {
            throw new ArgumentException("Timestamp must have Kind=Utc.", nameof(sUtc));
        }

        if (n is not { } count)
        {
            return AppResult<SmaResult>.Invalid(nameof(n), "n is required.");
        }

        if (count is < MinDataPoints or > MaxDataPoints)
        {
            return AppResult<SmaResult>.Invalid(nameof(n), $"n must be between {MinDataPoints} and {MaxDataPoints}.");
        }

        if (ParsePeriod<SmaResult>(p, out var period) is { } invalidPeriod)
        {
            return invalidPeriod;
        }

        var now = Now();
        if (sUtc > now)
        {
            return AppResult<SmaResult>.Invalid("s", FutureStartDetail);
        }

        var found = await FindAsync(symbol, ct);
        if (found is null)
        {
            return UnknownSymbol<SmaResult>(symbol);
        }

        var window = SmaWindowCalculator.Compute(period, count, sUtc, now);
        IReadOnlyList<PricePoint> closes = window.IsEmpty ? [] : await queries.GetClosesAsync(found.Id, period, window, ct);
        if (SmaCalculator.Average(closes) is not { } value)
        {
            return AppResult<SmaResult>.Fail(
                AppError.NoData,
                $"No complete {period.Code} bucket of '{found.Name}' falls inside the requested window.");
        }

        return AppResult<SmaResult>.Ok(new SmaResult(found.Name, period, count, value, window, now, sUtc, closes));
    }

    /// <summary>
    /// Every symbol TickPulse monitors or used to monitor. The latest price is the live one, or the last stored
    /// close; the 24 h change compares it with the close at or before <c>now − 24 h</c>. The change is null when the
    /// latest price is itself that old (it would be compared with itself), and, until the symbol's backfill has
    /// completed, when that close lies more than <see cref="DayAgoCloseTolerance"/> before the minute of
    /// <c>now − 24 h</c> (it may be a backfill frontier, far more than 24 h back). Three queries in all, whatever the
    /// number of symbols.
    /// </summary>
    public async Task<AppResult<IReadOnlyList<SymbolSummary>>> ListSymbolsAsync(CancellationToken ct)
    {
        var all = await symbols.ListAsync(ct);
        if (all.Count == 0)
        {
            return AppResult<IReadOnlyList<SymbolSummary>>.Ok([]);
        }

        var now = Now();
        var dayAgoUtc = now - AverageWindow;
        var oldestReferenceUtc = Period.OneMinute.AlignDown(dayAgoUtc) - DayAgoCloseTolerance;
        var live = all.ToDictionary(
            s => s.Id,
            s => ingestion.Latest.TryGetValue(s.Name, out var price) ? new PricePoint(price.TradeTimeUtc, price.Price) : (PricePoint?)null);
        int[] withoutLive = [.. all.Where(s => live[s.Id] is null).Select(s => s.Id)];

        var dayAgo = await queries.GetClosesAtOrBeforeAsync([.. all.Select(s => s.Id)], dayAgoUtc, ct);
        var fallback = withoutLive.Length == 0
            ? new Dictionary<int, PricePoint>()
            : await queries.GetClosesAtOrBeforeAsync(withoutLive, now, ct);

        var summaries = new List<SymbolSummary>(all.Count);
        foreach (var s in all)
        {
            var latest = live[s.Id] ?? (fallback.TryGetValue(s.Id, out var close) ? close : null);
            var boundApplies = s.BackfillStatus != BackfillStatus.Completed;
            decimal? then = dayAgo.TryGetValue(s.Id, out var old) && (!boundApplies || old.BucketStart >= oldestReferenceUtc)
                ? old.Close
                : null;
            var change = latest is { } current && current.BucketStart > dayAgoUtc ? ChangePercent(current.Close, then) : null;
            summaries.Add(new SymbolSummary(s.Name, s.IsActive, latest?.Close, change, s.BackfillStatus));
        }

        return AppResult<IReadOnlyList<SymbolSummary>>.Ok(summaries);
    }

    /// <summary>The OHLCV candles of the last <paramref name="limit"/> <paramref name="p"/> periods, oldest first.</summary>
    public async Task<AppResult<IReadOnlyList<CandleRow>>> GetCandlesAsync(string symbol, string? p, int limit, CancellationToken ct)
    {
        if (ParsePeriod<IReadOnlyList<CandleRow>>(p, out var period) is { } invalidPeriod)
        {
            return invalidPeriod;
        }

        if (limit is < MinCandleLimit or > MaxCandleLimit)
        {
            return AppResult<IReadOnlyList<CandleRow>>.Invalid(nameof(limit), $"limit must be between {MinCandleLimit} and {MaxCandleLimit}.");
        }

        var found = await FindAsync(symbol, ct);
        if (found is null)
        {
            return UnknownSymbol<IReadOnlyList<CandleRow>>(symbol);
        }

        var candles = await queries.GetCandlesAsync(found.Id, period, limit, ct);
        return candles.Count == 0
            ? AppResult<IReadOnlyList<CandleRow>>.Fail(AppError.NoData, $"No candles are stored for '{found.Name}'.")
            : AppResult<IReadOnlyList<CandleRow>>.Ok(candles);
    }

    /// <summary><c>(latest − dayAgo) / dayAgo × 100</c> to 2 dp (banker's rounding), or null when it is undefined.</summary>
    private static decimal? ChangePercent(decimal latest, decimal? dayAgo) =>
        dayAgo is { } then && then != 0m
            ? Math.Round((latest - then) / then * 100m, 2, MidpointRounding.ToEven)
            : null;

    /// <summary>Parses <paramref name="p"/>; returns the validation failure, or null when it is a known period.</summary>
    private static AppResult<T>? ParsePeriod<T>(string? p, out Period period)
    {
        period = default;
        if (string.IsNullOrWhiteSpace(p))
        {
            return AppResult<T>.Invalid(nameof(p), "p is required.");
        }

        return Period.TryParse(p, out period) ? null : AppResult<T>.Invalid(nameof(p), InvalidPeriodDetail);
    }

    private static AppResult<T> UnknownSymbol<T>(string? raw) =>
        AppResult<T>.Fail(
            AppError.UnknownSymbol,
            $"Symbol '{(SymbolName.TryCreate(raw, out var name) ? name.Value : raw?.Trim())}' is not monitored.");

    private async Task<MonitoredSymbol?> FindAsync(string? raw, CancellationToken ct) =>
        SymbolName.TryCreate(raw, out var name) ? await symbols.FindAsync(name, ct) : null;

    private DateTime Now() => clock.GetUtcNow().UtcDateTime;
}

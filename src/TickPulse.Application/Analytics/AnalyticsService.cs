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

    private static readonly TimeSpan AverageWindow = TimeSpan.FromHours(AverageWindowHours);
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
    public async Task<AppResult<SmaResult>> GetSmaAsync(string symbol, int n, string p, DateTime? sUtc, CancellationToken ct)
    {
        if (sUtc is { Kind: not DateTimeKind.Utc })
        {
            throw new ArgumentException("Timestamp must have Kind=Utc.", nameof(sUtc));
        }

        if (n is < MinDataPoints or > MaxDataPoints)
        {
            return Invalid<SmaResult>($"n must be between {MinDataPoints} and {MaxDataPoints}.");
        }

        if (!Period.TryParse(p, out var period))
        {
            return Invalid<SmaResult>(InvalidPeriodDetail);
        }

        var now = Now();
        if (sUtc > now)
        {
            return Invalid<SmaResult>("s must not be in the future.");
        }

        var found = await FindAsync(symbol, ct);
        if (found is null)
        {
            return UnknownSymbol<SmaResult>(symbol);
        }

        var window = SmaWindowCalculator.Compute(period, n, sUtc, now);
        IReadOnlyList<PricePoint> closes = window.IsEmpty ? [] : await queries.GetClosesAsync(found.Id, period, window, ct);
        if (SmaCalculator.Average(closes) is not { } value)
        {
            return AppResult<SmaResult>.Fail(
                AppError.NoData,
                $"No complete {period.Code} bucket of '{found.Name}' falls inside the requested window.");
        }

        return AppResult<SmaResult>.Ok(new SmaResult(found.Name, period, n, value, window, now, sUtc, closes));
    }

    /// <summary>
    /// Every symbol TickPulse monitors or used to monitor. The latest price is the live one, or the last stored
    /// close; the 24 h change compares it with the close at or before <c>now − 24 h</c>.
    /// </summary>
    public async Task<AppResult<IReadOnlyList<SymbolSummary>>> ListSymbolsAsync(CancellationToken ct)
    {
        var now = Now();
        var all = await symbols.ListAsync(ct);
        var summaries = new List<SymbolSummary>(all.Count);
        foreach (var s in all)
        {
            var latest = ingestion.Latest.TryGetValue(s.Name, out var live)
                ? live.Price
                : await queries.GetCloseAtOrBeforeAsync(s.Id, now, ct);
            var dayAgo = await queries.GetCloseAtOrBeforeAsync(s.Id, now - AverageWindow, ct);
            summaries.Add(new SymbolSummary(s.Name, s.IsActive, latest, ChangePercent(latest, dayAgo), s.BackfillStatus));
        }

        return AppResult<IReadOnlyList<SymbolSummary>>.Ok(summaries);
    }

    /// <summary>The OHLCV candles of the last <paramref name="limit"/> <paramref name="p"/> periods, oldest first.</summary>
    public async Task<AppResult<IReadOnlyList<CandleRow>>> GetCandlesAsync(string symbol, string p, int limit, CancellationToken ct)
    {
        if (!Period.TryParse(p, out var period))
        {
            return Invalid<IReadOnlyList<CandleRow>>(InvalidPeriodDetail);
        }

        if (limit is < MinCandleLimit or > MaxCandleLimit)
        {
            return Invalid<IReadOnlyList<CandleRow>>($"limit must be between {MinCandleLimit} and {MaxCandleLimit}.");
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
    private static decimal? ChangePercent(decimal? latest, decimal? dayAgo) =>
        latest is { } now && dayAgo is { } then && then != 0m
            ? Math.Round((now - then) / then * 100m, 2, MidpointRounding.ToEven)
            : null;

    private static AppResult<T> Invalid<T>(string detail) => AppResult<T>.Fail(AppError.Validation, detail);

    private static AppResult<T> UnknownSymbol<T>(string? raw) =>
        AppResult<T>.Fail(
            AppError.UnknownSymbol,
            $"Symbol '{(SymbolName.TryCreate(raw, out var name) ? name.Value : raw?.Trim())}' is not monitored.");

    private async Task<MonitoredSymbol?> FindAsync(string? raw, CancellationToken ct) =>
        SymbolName.TryCreate(raw, out var name) ? await symbols.FindAsync(name, ct) : null;

    private DateTime Now() => clock.GetUtcNow().UtcDateTime;
}

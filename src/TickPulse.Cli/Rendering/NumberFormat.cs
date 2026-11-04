using System.Globalization;

namespace TickPulse.Cli.Rendering;

/// <summary>
/// Display formatting with invariant culture. Prices are shown exactly: every significant decimal the API sent and at
/// least two, so <c>0.00001234</c> stays <c>0.00001234</c> and <c>1.5</c> reads <c>1.50</c>. Nothing is rounded.
/// </summary>
internal static class NumberFormat
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>Exact, grouped, with at least two decimals: <c>67,234.51234567</c>, <c>0.00001234</c>, <c>100.00</c>.</summary>
    public static string Price(decimal value)
    {
        var decimals = Math.Max(2, SignificantDecimals(value));
        return value.ToString("#,0." + new string('0', decimals), Invariant);
    }

    /// <summary>A percentage that is already in percent units, with a sign: <c>+1.25%</c>, <c>-2.50%</c>, <c>0.00%</c>.</summary>
    public static string SignedPercent(decimal percent) =>
        percent.ToString("+#,0.00;-#,0.00;0.00", Invariant) + "%";

    /// <summary>A grouped whole number: <c>1,234,567</c>.</summary>
    public static string Count(long value) => value.ToString("#,0", Invariant);

    /// <summary>
    /// A compact duration: <c>45s</c>, <c>2m 5s</c>, <c>23h 59m</c> (up to a full day, so a 24 h window reads
    /// <c>24h 0m</c>), then <c>2d 2h</c>.
    /// </summary>
    public static string Duration(long seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return seconds switch
        {
            < 60 => $"{span.Seconds}s",
            < 3_600 => $"{span.Minutes}m {span.Seconds}s",
            <= 86_400 => $"{(int)span.TotalHours}h {span.Minutes}m",
            _ => $"{span.Days}d {span.Hours}h",
        };
    }

    /// <summary>A UTC timestamp: <c>2026-11-04 09:59:58 UTC</c>.</summary>
    public static string Timestamp(DateTime utc) =>
        utc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss", Invariant) + " UTC";

    /// <summary>A bucket start, as precise as its period needs: a date for <c>1d</c>/<c>1w</c>, else minutes.</summary>
    public static string Bucket(DateTime utc, string period) =>
        utc.ToUniversalTime().ToString(period is "1d" or "1w" ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm", Invariant);

    /// <summary>How a period reads in a sentence: <c>daily</c>, <c>weekly</c>, <c>5-minute</c>.</summary>
    public static string PeriodAdjective(string period) => period switch
    {
        "1m" => "1-minute",
        "5m" => "5-minute",
        "30m" => "30-minute",
        "1d" => "daily",
        "1w" => "weekly",
        _ => period,
    };

    /// <summary>The number of decimals left after trailing zeros are dropped (<c>1.50000000</c> → 1).</summary>
    private static int SignificantDecimals(decimal value)
    {
        var text = value.ToString(Invariant);
        var dot = text.IndexOf('.', StringComparison.Ordinal);
        return dot < 0 ? 0 : text.TrimEnd('0').Length - dot - 1;
    }
}

using System.Globalization;
using System.Text.RegularExpressions;

namespace TickPulse.Application.Analytics;

/// <summary>Parses the <c>s</c> (start) parameter of the simple moving average.</summary>
public static partial class SmaStartParameter
{
    /// <summary>The largest UTC offset ISO-8601 timestamps use in practice (UTC+14:00).</summary>
    private static readonly TimeSpan MaxOffset = TimeSpan.FromHours(14);

    // The local part: an ISO-8601 date, or date and time with optional seconds and fraction. The offset is split off
    // first and applied by hand, so a timestamp whose UTC equivalent leaves the DateTime range is still recognised.
    private static readonly string[] LocalFormats =
    [
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF",
        "yyyy-MM-dd'T'HH:mm:ss",
        "yyyy-MM-dd'T'HH:mm",
        "yyyy-MM-dd",
    ];

    /// <summary>
    /// Parses an ISO-8601 timestamp (upper or lower case <c>T</c>/<c>Z</c>) into UTC. A timestamp without an offset is
    /// UTC. A query string that carried an unencoded <c>+hh:mm</c> offset arrives with a space instead of the
    /// <c>+</c> (form decoding), so a space before a trailing <c>hh:mm</c> is read as <c>+</c>. Missing or blank
    /// input succeeds with <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// A timestamp whose UTC equivalent is after <see cref="DateTime.MaxValue"/> is clamped to it, which is always in
    /// the future; one before <see cref="DateTime.MinValue"/> fails.
    /// </remarks>
    public static bool TryParse(string? raw, out DateTime? utc)
    {
        utc = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        var value = DecodedPlusOffset().Replace(raw.Trim().ToUpperInvariant(), "${time}+${offset}");
        if (!TrySplitOffset(value, out var local, out var offset)
            || !DateTime.TryParseExact(local, LocalFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return false;
        }

        var ticks = parsed.Ticks - offset.Ticks;
        if (ticks < DateTime.MinValue.Ticks)
        {
            return false;
        }

        utc = new DateTime(Math.Min(ticks, DateTime.MaxValue.Ticks), DateTimeKind.Utc);
        return true;
    }

    private static bool TrySplitOffset(string value, out string local, out TimeSpan offset)
    {
        local = value;
        offset = TimeSpan.Zero;
        if (value.EndsWith('Z'))
        {
            local = value[..^1];
            return true;
        }

        var match = TrailingOffset().Match(value);
        if (!match.Success)
        {
            return true;
        }

        var hours = int.Parse(match.Groups["hh"].ValueSpan, CultureInfo.InvariantCulture);
        var minutes = int.Parse(match.Groups["mm"].ValueSpan, CultureInfo.InvariantCulture);
        offset = new TimeSpan(hours, minutes, 0);
        if (minutes >= 60 || offset > MaxOffset)
        {
            return false;
        }

        if (match.Groups["sign"].Value == "-")
        {
            offset = -offset;
        }

        local = value[..match.Index];
        return true;
    }

    [GeneratedRegex(@"^(?<time>\d{4}-\d{2}-\d{2}T[0-9:.]+) (?<offset>\d{2}:\d{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex DecodedPlusOffset();

    // Only after a time, so the '-' separators of a bare date are never taken for an offset.
    [GeneratedRegex(@"(?<=T[0-9:.]+)(?<sign>[+-])(?<hh>\d{2}):(?<mm>\d{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingOffset();
}

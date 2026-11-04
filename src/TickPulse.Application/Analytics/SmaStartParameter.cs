using System.Globalization;
using System.Text.RegularExpressions;

namespace TickPulse.Application.Analytics;

/// <summary>Parses the <c>s</c> (start) parameter of the simple moving average.</summary>
public static partial class SmaStartParameter
{
    // ISO-8601 date, or date and time with optional seconds, fraction and offset ('K': Z, ±hh:mm or nothing).
    private static readonly string[] Formats =
    [
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK",
        "yyyy-MM-dd'T'HH:mm:ssK",
        "yyyy-MM-dd'T'HH:mmK",
        "yyyy-MM-dd",
    ];

    /// <summary>
    /// Parses an ISO-8601 timestamp into UTC. A timestamp without an offset is UTC. A query string that carried an
    /// unencoded <c>+hh:mm</c> offset arrives with a space instead of the <c>+</c> (form decoding), so a space before
    /// a trailing <c>hh:mm</c> is read as <c>+</c>. Missing or blank input succeeds with <see langword="null"/>.
    /// </summary>
    public static bool TryParse(string? raw, out DateTime? utc)
    {
        utc = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        var value = DecodedPlusOffset().Replace(raw.Trim(), "${time}+${offset}");
        if (!DateTime.TryParseExact(
                value,
                Formats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return false;
        }

        utc = parsed;
        return true;
    }

    [GeneratedRegex(@"^(?<time>\d{4}-\d{2}-\d{2}T[0-9:.]+) (?<offset>\d{2}:\d{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex DecodedPlusOffset();
}

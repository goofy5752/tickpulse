using System.Globalization;

namespace TickPulse.Domain.Tests;

internal static class TestData
{
    public static DateTime Utc(string iso) =>
        DateTime.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    public static PricePoint P(decimal price) => new(Utc("2026-01-01T00:00:00"), price);
}

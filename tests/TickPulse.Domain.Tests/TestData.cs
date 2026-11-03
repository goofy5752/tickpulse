using System.Globalization;
using TickPulse.Domain;

namespace TickPulse.Domain.Tests;

internal static class TestData
{
    public static DateTime Utc(string iso) =>
        DateTime.SpecifyKind(DateTime.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal), DateTimeKind.Utc);

}

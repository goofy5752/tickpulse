using TickPulse.Application.Analytics;

namespace TickPulse.Application.Tests;

public sealed class SmaStartParameterTests
{
    [Theory]
    [InlineData("2026-10-01T00:00:00Z", "2026-10-01T00:00:00.0000000Z")]
    [InlineData("2026-10-01T00:00:00", "2026-10-01T00:00:00.0000000Z")]          // no offset: UTC
    [InlineData("2026-10-01T03:00:00+03:00", "2026-10-01T00:00:00.0000000Z")]
    [InlineData("2026-09-30T21:00:00-03:00", "2026-10-01T00:00:00.0000000Z")]
    [InlineData("2026-10-01T03:00:00 03:00", "2026-10-01T00:00:00.0000000Z")]    // an unencoded '+' decodes to a space
    [InlineData("2026-10-01T03:00 03:00", "2026-10-01T00:00:00.0000000Z")]
    [InlineData("2026-10-01T03:00:00.123 03:00", "2026-10-01T00:00:00.1230000Z")]
    [InlineData("2026-10-01T12:34:56.7890123Z", "2026-10-01T12:34:56.7890123Z")]
    [InlineData("2026-10-01T12:34", "2026-10-01T12:34:00.0000000Z")]
    [InlineData("2026-10-01", "2026-10-01T00:00:00.0000000Z")]
    [InlineData(" 2026-10-01T00:00:00Z ", "2026-10-01T00:00:00.0000000Z")]
    public void TryParse_Iso8601_ReturnsUtc(string raw, string expected)
    {
        SmaStartParameter.TryParse(raw, out var utc).ShouldBeTrue();

        var value = utc.ShouldNotBeNull();
        value.Kind.ShouldBe(DateTimeKind.Utc);
        value.ToString("O", System.Globalization.CultureInfo.InvariantCulture).ShouldBe(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryParse_Missing_IsNull(string? raw)
    {
        SmaStartParameter.TryParse(raw, out var utc).ShouldBeTrue();

        utc.ShouldBeNull();
    }

    [Theory]
    [InlineData("yesterday")]
    [InlineData("10/01/2026")]
    [InlineData("Oct 1 2026")]
    [InlineData("2026-13-01T00:00:00Z")]
    [InlineData("2026-10-01T25:00:00Z")]
    [InlineData("2026-10-01T03:00:00 3")]
    [InlineData("2026-10-01 03:00:00")]
    [InlineData("1759276800")]
    public void TryParse_Garbage_Fails(string raw) =>
        SmaStartParameter.TryParse(raw, out _).ShouldBeFalse();
}

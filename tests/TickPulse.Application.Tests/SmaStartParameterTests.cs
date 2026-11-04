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
    [InlineData("2026-10-01t00:00:00z", "2026-10-01T00:00:00.0000000Z")]       // RFC 3339 allows lower case
    [InlineData("2026-10-01T03:00:00+14:00", "2026-09-30T13:00:00.0000000Z")]
    [InlineData("0001-01-01T00:00:00", "0001-01-01T00:00:00.0000000Z")]
    [InlineData("0001-01-01T00:00:00-03:00", "0001-01-01T03:00:00.0000000Z")]
    [InlineData("9999-12-31T23:59:59.9999999Z", "9999-12-31T23:59:59.9999999Z")]
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
    [InlineData("0001-01-01T00:00:00+03:00")]   // before the earliest representable instant
    [InlineData("2026-10-01T00:00:00+15:00")]
    [InlineData("2026-10-01T00:00:00+03:60")]
    [InlineData("2026-10-01T00:00:00+0300")]
    public void TryParse_Garbage_Fails(string raw) =>
        SmaStartParameter.TryParse(raw, out _).ShouldBeFalse();

    [Fact]
    public void TryParse_BeyondTheLatestRepresentableInstant_IsClampedToMaxValue()
    {
        // 9999-12-31T23:59:59-01:00 is 10000-01-01T00:59:59Z; clamping keeps it "in the future" rather than unparseable.
        SmaStartParameter.TryParse("9999-12-31T23:59:59-01:00", out var utc).ShouldBeTrue();

        utc.ShouldBe(DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc));
        utc!.Value.Kind.ShouldBe(DateTimeKind.Utc);
    }
}

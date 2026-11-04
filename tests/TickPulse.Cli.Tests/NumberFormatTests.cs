using TickPulse.Cli.Rendering;

namespace TickPulse.Cli.Tests;

public sealed class NumberFormatTests
{
    [Theory]
    [InlineData("0.00001234", "0.00001234")]
    [InlineData("0.00000001", "0.00000001")]
    [InlineData("-0.00000001", "-0.00000001")]
    [InlineData("67234.51234567", "67,234.51234567")]
    [InlineData("1234567.1", "1,234,567.10")]
    [InlineData("1.50000000", "1.50")]
    [InlineData("100", "100.00")]
    [InlineData("0", "0.00")]
    public void Price_IsExact_WithAtLeastTwoDecimals(string value, string expected) =>
        NumberFormat.Price(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture)).ShouldBe(expected);

    [Theory]
    [InlineData("1.25", "+1.25%")]
    [InlineData("-2.5", "-2.50%")]
    [InlineData("0", "0.00%")]
    public void SignedPercent_ShowsSign(string value, string expected) =>
        NumberFormat.SignedPercent(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture)).ShouldBe(expected);

    [Theory]
    [InlineData(45, "45s")]
    [InlineData(86_393, "23h 59m")]
    [InlineData(86_400, "24h 0m")]
    [InlineData(3_600 * 50, "2d 2h")]
    [InlineData(125, "2m 5s")]
    public void Duration_IsCompact(long seconds, string expected) => NumberFormat.Duration(seconds).ShouldBe(expected);
}

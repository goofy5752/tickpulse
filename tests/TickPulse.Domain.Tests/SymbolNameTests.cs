namespace TickPulse.Domain.Tests;

public sealed class SymbolNameTests
{
    [Theory]
    [InlineData("btcusdt", "BTCUSDT")]
    [InlineData("  ETHUSDT ", "ETHUSDT")]
    public void TryCreate_Normalises(string raw, string expected)
    {
        SymbolName.TryCreate(raw, out var s).ShouldBeTrue();
        s.Value.ShouldBe(expected);
    }

    [Theory]
    [InlineData("BTC-USDT")]
    [InlineData("BTC")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("ABCDEFGHIJKLMNOPQRSTU")]
    public void TryCreate_Invalid_ReturnsFalse(string? raw) => SymbolName.TryCreate(raw, out _).ShouldBeFalse();
}

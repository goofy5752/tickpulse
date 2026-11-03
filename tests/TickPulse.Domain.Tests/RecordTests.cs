using static TickPulse.Domain.Tests.TestData;

namespace TickPulse.Domain.Tests;

public sealed class RecordTests
{
    [Fact]
    public void Tick_WithSameValues_IsEqual()
    {
        var at = Utc("2026-11-03T10:00:00");
        var a = new Tick(1, 42, 100.5m, 0.25m, at, at.AddMilliseconds(5));
        var b = new Tick(1, 42, 100.5m, 0.25m, at, at.AddMilliseconds(5));

        a.ShouldBe(b);
        a.Price.ShouldBe(100.5m);
        a.AggTradeId.ShouldBe(42);
    }

    [Fact]
    public void Kline_WithSameValues_IsEqual()
    {
        var open = Utc("2026-11-03T10:00:00");
        var a = new Kline(open, 1m, 4m, 0.5m, 3m, 10m, 25m);
        var b = new Kline(open, 1m, 4m, 0.5m, 3m, 10m, 25m);

        a.ShouldBe(b);
        a.High.ShouldBe(4m);
        a.QuoteVolume.ShouldBe(25m);
    }

    [Fact]
    public void SymbolName_ToString_ReturnsValue()
    {
        SymbolName.TryCreate("ethusdt", out var name).ShouldBeTrue();
        name.ToString().ShouldBe("ETHUSDT");
    }
}

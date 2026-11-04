using TickPulse.Application.Abstractions;
using TickPulse.Infrastructure.Ingestion;

namespace TickPulse.Integration.Tests.Ingestion;

public sealed class GapFillWatermarksTests
{
    private static readonly DateTime T0 = new(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);

    private readonly SymbolIdCache _symbols = new();
    private readonly GapFillWatermarks _marks;

    public GapFillWatermarksTests()
    {
        Activate("BTCUSDT", "ETHUSDT");
        _marks = new GapFillWatermarks(_symbols);
    }

    [Fact]
    public void Merge_CleanEnd_RaisesLastIdWithoutAGap()
    {
        _marks.Merge([new("BTCUSDT", 10)], gap: false);
        _marks.Merge([new("BTCUSDT", 25), new("ETHUSDT", 3)], gap: false);
        _marks.Merge([new("BTCUSDT", 20)], gap: false);

        Start("BTCUSDT", pendingOnly: false).ShouldBe(25);
        Start("ETHUSDT", pendingOnly: false).ShouldBe(3);
        Start("BTCUSDT", pendingOnly: true).ShouldBeNull();
    }

    [Fact]
    public void Merge_Gap_OpensAGapForEveryKnownSymbol()
    {
        _marks.Merge([new("BTCUSDT", 10), new("ETHUSDT", 3)], gap: false);

        _marks.Merge([new("BTCUSDT", 12)], gap: true); // ETH delivered nothing on the connection that broke

        Start("BTCUSDT", pendingOnly: true).ShouldBe(12);
        Start("ETHUSDT", pendingOnly: true).ShouldBe(3);
    }

    [Fact]
    public void Merge_WhileAGapIsOpen_NeverRaisesTheLastId()
    {
        _marks.Merge([new("BTCUSDT", 10)], gap: true);

        // A later connection (whose SUBSCRIBE was never acked) delivered newer trades and ended.
        _marks.Merge([new("BTCUSDT", 500)], gap: false);
        _marks.Merge([new("BTCUSDT", 900)], gap: true);

        Start("BTCUSDT", pendingOnly: true).ShouldBe(10);
    }

    [Fact]
    public void Filled_ClosesTheGap_ThenMergesRaiseAgain()
    {
        _marks.Merge([new("BTCUSDT", 10)], gap: true);
        _marks.TryGetStart("BTCUSDT", pendingOnly: true, out _, out var version).ShouldBeTrue();

        _marks.Filled("BTCUSDT", version);
        _marks.Merge([new("BTCUSDT", 40)], gap: false);

        Start("BTCUSDT", pendingOnly: true).ShouldBeNull();
        Start("BTCUSDT", pendingOnly: false).ShouldBe(40);
    }

    [Fact]
    public void Filled_AfterAChangeDuringTheFill_KeepsTheGapOpen()
    {
        _marks.Merge([new("BTCUSDT", 10)], gap: true);
        _marks.TryGetStart("BTCUSDT", pendingOnly: true, out _, out var version).ShouldBeTrue();

        _marks.Lower("BTCUSDT", 8); // the writer dropped trades 8.. while the fill ran
        _marks.Filled("BTCUSDT", version);

        Start("BTCUSDT", pendingOnly: true).ShouldBe(7);
    }

    [Fact]
    public void Lower_MovesBelowTheDroppedTradesAndOpensAGap()
    {
        _marks.Merge([new("BTCUSDT", 100)], gap: false);

        _marks.Lower("BTCUSDT", 60);
        _marks.Lower("BTCUSDT", 80); // a later drop never moves it up again
        _marks.Lower("ETHUSDT", 5);  // first trades of a symbol dropped: a gap from before them

        Start("BTCUSDT", pendingOnly: true).ShouldBe(59);
        Start("ETHUSDT", pendingOnly: true).ShouldBe(4);
    }

    [Fact]
    public void Advance_PartialFill_KeepsTheGapOpenFromTheRecoveredId()
    {
        _marks.Merge([new("BTCUSDT", 10)], gap: true);
        _marks.TryGetStart("BTCUSDT", pendingOnly: true, out _, out var version).ShouldBeTrue();

        _marks.Advance("BTCUSDT", 50_010, version);

        Start("BTCUSDT", pendingOnly: true).ShouldBe(50_010);
    }

    [Fact]
    public void InactiveSymbols_AreNeitherMergedNorLowered()
    {
        Activate("BTCUSDT"); // ETH removed

        _marks.Merge([new("ETHUSDT", 3)], gap: true);
        _marks.Lower("ETHUSDT", 2);

        Start("ETHUSDT", pendingOnly: false).ShouldBeNull();
    }

    [Fact]
    public void Forget_DropsTheSymbol()
    {
        _marks.Merge([new("BTCUSDT", 10)], gap: true);

        _marks.Forget("BTCUSDT");

        Start("BTCUSDT", pendingOnly: false).ShouldBeNull();
    }

    private long? Start(string symbol, bool pendingOnly) =>
        _marks.TryGetStart(symbol, pendingOnly, out var lastId, out _) ? lastId : null;

    private void Activate(params string[] names) =>
        _symbols.Update(names.Select((n, i) => new MonitoredSymbol(i + 1, n, true, T0, null, BackfillStatus.NotRequested, null)));
}

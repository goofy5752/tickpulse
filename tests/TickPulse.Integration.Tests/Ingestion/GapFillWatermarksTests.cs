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

    [Fact]
    public void Seed_OpensAGapAfterTheStoredId_ForSymbolsNotKnownYet()
    {
        _marks.Contains("BTCUSDT").ShouldBeFalse();

        _marks.Seed([new("BTCUSDT", 120), new("ETHUSDT", 7)]);

        Start("BTCUSDT", pendingOnly: true).ShouldBe(120);
        Start("ETHUSDT", pendingOnly: true).ShouldBe(7);
        _marks.Contains("BTCUSDT").ShouldBeTrue();

        // A fresh process seeds before its first reconcile: the symbol cache is still empty, but the database
        // listed the symbols as active.
        var fresh = new GapFillWatermarks(new SymbolIdCache());
        fresh.Seed([new("BTCUSDT", 1)]);
        fresh.TryGetStart("BTCUSDT", pendingOnly: true, out var lastId, out _).ShouldBeTrue();
        lastId.ShouldBe(1);
    }

    [Fact]
    public void Seed_RaisesAKnownEntryToTheStoredId_UnlessItHasAHole()
    {
        // This process led until id 100; another instance led since and stored up to 150.
        Activate("BTCUSDT", "ETHUSDT", "ADAUSDT");
        _marks.Merge([new("BTCUSDT", 100), new("ADAUSDT", 70)], gap: true);
        _marks.Lower("ETHUSDT", 40); // a dropped batch: trades from 40 are missing although later ones may be stored
        _marks.TryGetStart("BTCUSDT", pendingOnly: true, out _, out var before).ShouldBeTrue();

        _marks.Seed([new("BTCUSDT", 150), new("ETHUSDT", 90), new("ADAUSDT", 60)]);

        Start("BTCUSDT", pendingOnly: true).ShouldBe(150);
        Start("ETHUSDT", pendingOnly: true).ShouldBe(39);
        Start("ADAUSDT", pendingOnly: true).ShouldBe(70); // never lowered by the seed

        // The raise invalidates a fill that started from the old id.
        _marks.Filled("BTCUSDT", before);
        Start("BTCUSDT", pendingOnly: true).ShouldBe(150);
    }

    [Fact]
    public void Filled_ClearsTheHole_SoALaterSeedRaisesAgain()
    {
        _marks.Lower("BTCUSDT", 40);
        _marks.TryGetStart("BTCUSDT", pendingOnly: true, out _, out var version).ShouldBeTrue();
        _marks.Filled("BTCUSDT", version);

        _marks.Seed([new("BTCUSDT", 90)]);

        Start("BTCUSDT", pendingOnly: true).ShouldBe(90);
    }

    [Fact]
    public void Merge_Gap_InvalidatesAFillThatIsRunning()
    {
        _marks.Seed([new("BTCUSDT", 10)]);
        _marks.TryGetStart("BTCUSDT", pendingOnly: true, out _, out var version).ShouldBeTrue();

        // The connection the fill is bounded by breaks while it runs.
        _marks.Merge([new("BTCUSDT", 25)], gap: true);
        _marks.Filled("BTCUSDT", version);
        _marks.Advance("BTCUSDT", 20, version);

        Start("BTCUSDT", pendingOnly: true).ShouldBe(10); // still open, from where it was
    }

    [Fact]
    public void Seed_GapClosesOnlyWhenFilled()
    {
        _marks.Seed([new("BTCUSDT", 120)]);
        _marks.Merge([new("BTCUSDT", 500)], gap: false); // a connection ended before the fill: never skip the gap
        Start("BTCUSDT", pendingOnly: true).ShouldBe(120);

        _marks.TryGetStart("BTCUSDT", pendingOnly: true, out _, out var version).ShouldBeTrue();
        _marks.Filled("BTCUSDT", version);
        Start("BTCUSDT", pendingOnly: true).ShouldBeNull();
    }

    private long? Start(string symbol, bool pendingOnly) =>
        _marks.TryGetStart(symbol, pendingOnly, out var lastId, out _) ? lastId : null;

    private void Activate(params string[] names) =>
        _symbols.Update(names.Select((n, i) => new MonitoredSymbol(i + 1, n, true, T0, null, BackfillStatus.NotRequested, null)));
}

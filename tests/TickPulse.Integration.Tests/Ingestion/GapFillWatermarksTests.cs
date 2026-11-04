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
        _marks.Merge([Span("BTCUSDT", 1, 10)], gap: false);
        _marks.Merge([Span("BTCUSDT", 11, 25), Span("ETHUSDT", 1, 3)], gap: false);
        _marks.Merge([Span("BTCUSDT", 11, 20)], gap: false);

        Start("BTCUSDT", pendingOnly: false).ShouldBe(25);
        Start("ETHUSDT", pendingOnly: false).ShouldBe(3);
        Start("BTCUSDT", pendingOnly: true).ShouldBeNull();
    }

    [Fact]
    public void Merge_Gap_OpensAGapForEveryKnownSymbol()
    {
        _marks.Merge([Span("BTCUSDT", 1, 10), Span("ETHUSDT", 1, 3)], gap: false);

        _marks.Merge([Span("BTCUSDT", 11, 12)], gap: true); // ETH delivered nothing on the connection that broke

        Start("BTCUSDT", pendingOnly: true).ShouldBe(12);
        Start("ETHUSDT", pendingOnly: true).ShouldBe(3);
    }

    [Fact]
    public void Merge_WhileAGapIsOpen_NeverRaisesTheLastId()
    {
        _marks.Merge([Span("BTCUSDT", 1, 10)], gap: true);

        // Later connections delivered newer trades and ended.
        _marks.Merge([Span("BTCUSDT", 11, 500)], gap: false);
        _marks.Merge([Span("BTCUSDT", 501, 900)], gap: true);

        Start("BTCUSDT", pendingOnly: true).ShouldBe(10);
    }

    [Fact]
    public void Merge_Replaced_StreamThatStartedAboveTheLastId_OpensAGapInsteadOfRaising()
    {
        _marks.Merge([Span("BTCUSDT", 1, 14)], gap: false);   // the old connection, replaced by a rotation, had stalled at 14

        _marks.Merge([Span("BTCUSDT", 17, 20)], gap: false);  // the new one streamed from 17 and was replaced in turn

        Start("BTCUSDT", pendingOnly: true).ShouldBe(14);      // 15 and 16 are fetched
    }

    [Fact]
    public void Verify_LiveStreamThatStartedAboveTheLastId_OpensAGap_AContiguousOneDoesNot()
    {
        _marks.Merge([Span("BTCUSDT", 1, 14), Span("ETHUSDT", 1, 14)], gap: false);

        _marks.Verify([Span("BTCUSDT", 17, 20), Span("ETHUSDT", 15, 20), Span("ADAUSDT", 5, 9)]);

        Start("BTCUSDT", pendingOnly: true).ShouldBe(14);
        Start("ETHUSDT", pendingOnly: true).ShouldBeNull();
        Start("ETHUSDT", pendingOnly: false).ShouldBe(14); // never raised: only the connection's end does that
        _marks.Contains("ADAUSDT").ShouldBeFalse();        // never created: an unknown symbol still gets its lead-in
    }

    [Fact]
    public void Filled_ClosesTheGap_ThenMergesRaiseAgain()
    {
        _marks.Merge([Span("BTCUSDT", 1, 10)], gap: true);
        _marks.TryGetStart("BTCUSDT", pendingOnly: true, out _, out var version).ShouldBeTrue();

        _marks.Filled("BTCUSDT", version, 12);                // the fill reached the live stream's first id, 13
        _marks.Merge([Span("BTCUSDT", 13, 40)], gap: false);

        Start("BTCUSDT", pendingOnly: true).ShouldBeNull();
        Start("BTCUSDT", pendingOnly: false).ShouldBe(40);
    }

    [Fact]
    public void Filled_RaisesTheLastIdToWhatItEmitted()
    {
        _marks.Merge([Span("BTCUSDT", 1, 10), Span("ETHUSDT", 1, 10)], gap: true);
        _marks.TryGetStart("BTCUSDT", pendingOnly: true, out _, out var btc).ShouldBeTrue();
        _marks.TryGetStart("ETHUSDT", pendingOnly: true, out _, out var eth).ShouldBeTrue();

        _marks.Filled("BTCUSDT", btc, 16);
        _marks.Filled("ETHUSDT", eth, null); // nothing to recover: the stream started right after 10

        Start("BTCUSDT", pendingOnly: false).ShouldBe(16);
        Start("ETHUSDT", pendingOnly: false).ShouldBe(10);
        _marks.IsPending("BTCUSDT").ShouldBeFalse();
        _marks.IsPending("ETHUSDT").ShouldBeFalse();
    }

    [Fact]
    public void Filled_AfterAChangeDuringTheFill_KeepsTheGapOpen()
    {
        _marks.Merge([Span("BTCUSDT", 1, 10)], gap: true);
        _marks.TryGetStart("BTCUSDT", pendingOnly: true, out _, out var version).ShouldBeTrue();

        _marks.Lower("BTCUSDT", 8); // the writer dropped trades 8.. while the fill ran
        _marks.Filled("BTCUSDT", version, 12);

        Start("BTCUSDT", pendingOnly: true).ShouldBe(7);
    }

    [Fact]
    public void Lower_MovesBelowTheDroppedTradesAndOpensAGap()
    {
        _marks.Merge([Span("BTCUSDT", 1, 100)], gap: false);

        _marks.Lower("BTCUSDT", 60);
        _marks.Lower("BTCUSDT", 80); // a later drop never moves it up again
        _marks.Lower("ETHUSDT", 5);  // first trades of a symbol dropped: a gap from before them

        Start("BTCUSDT", pendingOnly: true).ShouldBe(59);
        Start("ETHUSDT", pendingOnly: true).ShouldBe(4);
    }

    [Fact]
    public void Advance_PartialFill_KeepsTheGapOpenFromTheRecoveredId()
    {
        _marks.Merge([Span("BTCUSDT", 1, 10)], gap: true);
        _marks.TryGetStart("BTCUSDT", pendingOnly: true, out _, out var version).ShouldBeTrue();

        _marks.Advance("BTCUSDT", 50_010, version);

        Start("BTCUSDT", pendingOnly: true).ShouldBe(50_010);
    }

    [Fact]
    public void InactiveSymbols_AreNeitherMergedNorLowered()
    {
        Activate("BTCUSDT"); // ETH removed

        _marks.Merge([Span("ETHUSDT", 1, 3)], gap: true);
        _marks.Lower("ETHUSDT", 2);

        Start("ETHUSDT", pendingOnly: false).ShouldBeNull();
    }

    [Fact]
    public void Forget_DropsTheSymbol()
    {
        _marks.Merge([Span("BTCUSDT", 1, 10)], gap: true);

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
    public void Seed_ForASymbolWithoutAnEntry_StartsAtTheStoredId_WhateverADiscardedLinkDelivered()
    {
        _marks.NoteStored([Span("BTCUSDT", 500, 600)]); // delivered and stored; nothing of it was ever dropped

        _marks.Seed([new("BTCUSDT", 600)]);

        Start("BTCUSDT", pendingOnly: true).ShouldBe(600);
    }

    [Fact]
    public void Seed_AlwaysOpensAGap_TheStreamWasDownBetweenSessions()
    {
        _marks.Merge([Span("BTCUSDT", 1, 10)], gap: false); // closed

        _marks.Seed([new("BTCUSDT", 10)]);

        Start("BTCUSDT", pendingOnly: true).ShouldBe(10);
    }

    [Fact]
    public void Seed_RaisesAKnownEntryToTheStoredId_UnlessThisProcessDeliveredAboveIt()
    {
        // This process led until id 100; another instance led since and stored up to 150.
        Activate("BTCUSDT", "ETHUSDT", "ADAUSDT");
        _marks.Merge([Span("BTCUSDT", 1, 100), Span("ADAUSDT", 1, 70)], gap: true);
        _marks.Lower("ETHUSDT", 40); // a dropped batch: trades from 40 are missing although later ones may be stored
        _marks.TryGetStart("BTCUSDT", pendingOnly: true, out _, out var before).ShouldBeTrue();

        _marks.Seed([new("BTCUSDT", 150), new("ETHUSDT", 90), new("ADAUSDT", 60)]);

        Start("BTCUSDT", pendingOnly: true).ShouldBe(150);
        Start("ETHUSDT", pendingOnly: true).ShouldBe(39);
        Start("ADAUSDT", pendingOnly: true).ShouldBe(70); // never lowered by the seed

        // The raise invalidates a fill that started from the old id.
        _marks.Filled("BTCUSDT", before, 120);
        Start("BTCUSDT", pendingOnly: true).ShouldBe(150);
    }

    [Theory]
    [InlineData(true)]  // the next connection broke too, or the session ended
    [InlineData(false)] // a rotation replaced it
    public void Seed_PendingWithStreamDataStoredAbove_IsNotRaised(bool gap)
    {
        _marks.Merge([Span("BTCUSDT", 1, 5)], gap: true);        // a reconnect: trades from 6 on are missing
        _marks.Merge([Span("BTCUSDT", 11, 1000)], gap: gap);     // the next connection streamed 11..1000; its fill never closed the gap

        _marks.Seed([new("BTCUSDT", 1000)]);                     // 1000 is this process's own stream, above the gap

        Start("BTCUSDT", pendingOnly: true).ShouldBe(5);
    }

    [Fact]
    public void Seed_AfterAFillStoppedAtThePageCap_IsNotRaisedPastThisProcessesOwnStream()
    {
        _marks.Merge([Span("BTCUSDT", 1, 5), Span("ETHUSDT", 1, 5)], gap: true);
        _marks.TryGetStart("BTCUSDT", pendingOnly: true, out _, out var btc).ShouldBeTrue();
        _marks.TryGetStart("ETHUSDT", pendingOnly: true, out _, out var eth).ShouldBeTrue();
        _marks.Advance("BTCUSDT", 50_005, btc);                           // 50 pages recovered, more trades above
        _marks.Advance("ETHUSDT", 50_005, eth);
        _marks.Merge([Span("BTCUSDT", 60_000, 90_000)], gap: true);      // BTC streamed on above the cap until the session ended; ETH did not

        _marks.Seed([new("BTCUSDT", 90_000), new("ETHUSDT", 90_000)]);

        Start("BTCUSDT", pendingOnly: true).ShouldBe(50_005);            // 50 006..59 999 are still fetched
        Start("ETHUSDT", pendingOnly: true).ShouldBe(90_000);            // stored by another instance, which filled from 50 006 itself
    }

    [Fact]
    public void NoteStored_ADiscardedLinksTrades_KeepTheSeedBelowThem_OnlyWhenTheyLieAboveTheGap()
    {
        _marks.Merge([Span("BTCUSDT", 1, 5), Span("ETHUSDT", 1, 50)], gap: true);

        _marks.NoteStored([Span("BTCUSDT", 11, 1000)]); // the discarded link had delivered up to 1000
        _marks.NoteStored([Span("ETHUSDT", 40, 50)]);   // nothing delivered past the gap

        _marks.Seed([new("BTCUSDT", 1000), new("ETHUSDT", 80)]);
        Start("BTCUSDT", pendingOnly: true).ShouldBe(5);
        Start("ETHUSDT", pendingOnly: true).ShouldBe(80); // stored by another instance since: raised (I1)
    }

    [Theory]
    [InlineData(true)]  // the discarded rotation link retired before the broken old one
    [InlineData(false)] // or after it
    public void NoteStored_FromADiscardedLink_KeepsTheSeedBelowItsTrades_InEitherOrder(bool discardedFirst)
    {
        // The old link broke at 400; the discarded rotation link had delivered (and stored) 500..600.
        if (discardedFirst)
        {
            _marks.NoteStored([Span("BTCUSDT", 500, 600)]); // no entry exists yet
            _marks.Merge([Span("BTCUSDT", 1, 400)], gap: true);
        }
        else
        {
            _marks.Merge([Span("BTCUSDT", 1, 400)], gap: true);
            _marks.NoteStored([Span("BTCUSDT", 500, 600)]);
        }

        _marks.Seed([new("BTCUSDT", 600)]);

        Start("BTCUSDT", pendingOnly: true).ShouldBe(400); // 401..499 are still filled
    }

    [Fact]
    public void NoteStored_NeverOpensAGap_TheConnectionStillFollowedDecides()
    {
        _marks.Merge([Span("BTCUSDT", 1, 5)], gap: false);

        _marks.NoteStored([Span("BTCUSDT", 11, 15)]); // the old connection may deliver 6.. itself, or break and open the gap at 5

        Start("BTCUSDT", pendingOnly: true).ShouldBeNull();
        Start("BTCUSDT", pendingOnly: false).ShouldBe(5);
    }

    [Fact]
    public void Seed_AfterAFillClosedTheGapWhileADiscardedLinkStoredAbove_StaysBelowThoseTrades()
    {
        _marks.Merge([Span("BTCUSDT", 1, 5)], gap: true);                                       // the old connection broke at 5
        _marks.TryGetStart("BTCUSDT", pendingOnly: true, out _, out var version).ShouldBeTrue(); // the live one's fill (from 6 up to its first id, 7) starts
        _marks.NoteStored([Span("BTCUSDT", 17, 20)]);                                           // a discarded rotation link stored 17..20 while the live one stalled at 14
        _marks.Filled("BTCUSDT", version, 6);                                                   // the fill reached 7
        _marks.Merge([Span("BTCUSDT", 7, 14)], gap: true);                                      // the stalled connection is found dead at 14

        _marks.Seed([new("BTCUSDT", 20)]);                                                      // the next session sees the discarded link's maximum

        Start("BTCUSDT", pendingOnly: true).ShouldBe(14);                                       // 15 and 16 are still fetched
    }

    [Fact]
    public void Seed_RaisesAgain_OnceAContiguousStreamPassedWhatADiscardedLinkStored()
    {
        _marks.Merge([Span("BTCUSDT", 1, 5), Span("ETHUSDT", 1, 5)], gap: true);
        _marks.NoteStored([Span("BTCUSDT", 11, 600), Span("ETHUSDT", 11, 600)]);
        _marks.TryGetStart("BTCUSDT", pendingOnly: true, out _, out var version).ShouldBeTrue();
        _marks.Filled("BTCUSDT", version, 10);                   // 6..10 recovered up to the live stream's first id
        _marks.Merge([Span("BTCUSDT", 11, 700)], gap: true);     // which ran on to 700, past the discarded link's 600, until the session ended

        _marks.Forget("ETHUSDT");                                // removed, then re-added: starts fresh
        Activate("BTCUSDT", "ETHUSDT");
        _marks.Seed([new("BTCUSDT", 900), new("ETHUSDT", 650)]);

        Start("BTCUSDT", pendingOnly: true).ShouldBe(900);
        Start("ETHUSDT", pendingOnly: true).ShouldBe(650);
    }

    [Fact]
    public void Seed_AfterTheGapWasFilledAndTheStreamMergedPastIt_RaisesAgain()
    {
        _marks.Merge([Span("BTCUSDT", 1, 5)], gap: true);
        _marks.Merge([Span("BTCUSDT", 11, 1000)], gap: true);    // a connection streamed above the open gap and broke
        _marks.TryGetStart("BTCUSDT", pendingOnly: true, out _, out var version).ShouldBeTrue();
        _marks.Filled("BTCUSDT", version, 1000);                 // the next connection's fill walked 6..1000 up to its first id
        _marks.Merge([Span("BTCUSDT", 1001, 1500)], gap: true);  // then the session ended cleanly at 1500

        _marks.Seed([new("BTCUSDT", 2000)]);                     // another instance stored up to 2000 since

        Start("BTCUSDT", pendingOnly: true).ShouldBe(2000);
    }

    [Fact]
    public void Seed_AfterALoweredGapWasFilled_RaisesAgain()
    {
        _marks.Lower("BTCUSDT", 40);
        _marks.TryGetStart("BTCUSDT", pendingOnly: true, out _, out var version).ShouldBeTrue();
        _marks.Filled("BTCUSDT", version, 49); // 40..49 recovered up to the live stream's first id

        _marks.Seed([new("BTCUSDT", 90)]);

        Start("BTCUSDT", pendingOnly: true).ShouldBe(90);
    }

    [Fact]
    public void Merge_Gap_InvalidatesAFillThatIsRunning()
    {
        _marks.Seed([new("BTCUSDT", 10)]);
        _marks.TryGetStart("BTCUSDT", pendingOnly: true, out _, out var version).ShouldBeTrue();

        // The connection the fill is bounded by breaks while it runs.
        _marks.Merge([Span("BTCUSDT", 21, 25)], gap: true);
        _marks.Filled("BTCUSDT", version, 20);
        _marks.Advance("BTCUSDT", 20, version);

        Start("BTCUSDT", pendingOnly: true).ShouldBe(10); // still open, from where it was
    }

    [Fact]
    public void Seed_GapClosesOnlyWhenFilled()
    {
        _marks.Seed([new("BTCUSDT", 120)]);
        _marks.Merge([Span("BTCUSDT", 121, 500)], gap: false); // a connection ended before the fill: never skip the gap
        Start("BTCUSDT", pendingOnly: true).ShouldBe(120);

        _marks.TryGetStart("BTCUSDT", pendingOnly: true, out _, out var version).ShouldBeTrue();
        _marks.Filled("BTCUSDT", version, null);
        Start("BTCUSDT", pendingOnly: true).ShouldBeNull();
    }

    private static KeyValuePair<string, IdSpan> Span(string symbol, long first, long last) => new(symbol, new IdSpan(first, last));

    private long? Start(string symbol, bool pendingOnly) =>
        _marks.TryGetStart(symbol, pendingOnly, out var lastId, out _) ? lastId : null;

    private void Activate(params string[] names) =>
        _symbols.Update(names.Select((n, i) => new MonitoredSymbol(i + 1, n, true, T0, null, BackfillStatus.NotRequested, null)));
}

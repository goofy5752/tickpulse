using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using TickPulse.Application.Abstractions;
using TickPulse.Application.Analytics;
using TickPulse.Application.Ingestion;
using TickPulse.Application.Results;
using TickPulse.Domain;

namespace TickPulse.Application.Tests;

public sealed class AnalyticsServiceTests
{
    // Wednesday 2026-10-07 12:34:56.789 UTC.
    private static readonly DateTime Now = new DateTime(2026, 10, 7, 12, 34, 56, DateTimeKind.Utc).AddMilliseconds(789);

    private static readonly MonitoredSymbol Btc = Symbol(1, "BTCUSDT", isActive: true, BackfillStatus.Completed);
    private static readonly MonitoredSymbol Ada = Symbol(2, "ADAUSDT", isActive: false, BackfillStatus.NotRequested);
    private static readonly MonitoredSymbol Eth = Symbol(3, "ETHUSDT", isActive: true, BackfillStatus.Running);

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(Now));
    private readonly IMarketDataQueries _queries = Substitute.For<IMarketDataQueries>();
    private readonly ISymbolRepository _symbols = Substitute.For<ISymbolRepository>();
    private readonly IngestionState _ingestion;
    private readonly AnalyticsService _service;

    public AnalyticsServiceTests()
    {
        _ingestion = new IngestionState(_clock);
        _service = new AnalyticsService(_queries, _symbols, _ingestion, _clock);
        _symbols.FindAsync(Name("BTCUSDT"), Arg.Any<CancellationToken>()).Returns(Btc);
        _symbols.FindAsync(Name("ADAUSDT"), Arg.Any<CancellationToken>()).Returns(Ada);
        _symbols.FindAsync(Name("ETHUSDT"), Arg.Any<CancellationToken>()).Returns(Eth);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- 24h average ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Average_QueriesTheLast24Hours_AndReportsTheCoveredSpan()
    {
        var first = Now.AddHours(-20);
        var last = Now.AddMinutes(-1).AddMilliseconds(-500);
        _queries.GetAverageAsync(Btc.Id, Now.AddHours(-24), Now, Arg.Any<CancellationToken>())
            .Returns(new AverageWindow(64_000.12345678m, first, last, 1_234));

        var result = await _service.GetAverageAsync("BTCUSDT", Ct);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(new AveragePriceResult(
            "BTCUSDT",
            64_000.12345678m,
            first,
            last,
            CoveredSeconds: (19 * 3600) + (58 * 60) + 59,   // 19:59:59.5 truncated to whole seconds
            SampleCount: 1_234,
            WindowHours: 24));
    }

    [Theory]
    [InlineData("btcusdt")]
    [InlineData(" BtcUsdt ")]
    public async Task Average_NormalisesTheSymbol(string raw)
    {
        _queries.GetAverageAsync(Btc.Id, Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new AverageWindow(1m, Now, Now, 1));

        var result = await _service.GetAverageAsync(raw, Ct);

        result.Value.ShouldNotBeNull().Symbol.ShouldBe("BTCUSDT");
    }

    [Theory]
    [InlineData("BTC-USDT")]
    [InlineData("ADMIN")]
    [InlineData("")]
    [InlineData("ABC")]
    public async Task Average_UnknownOrMalformedSymbol_ReturnsUnknownSymbol(string raw)
    {
        var result = await _service.GetAverageAsync(raw, Ct);

        result.Error.ShouldBe(AppError.UnknownSymbol);
        result.Detail.ShouldBe($"Symbol '{raw.Trim()}' is not monitored.");
        await _queries.DidNotReceiveWithAnyArgs().GetAverageAsync(default, default, default, Ct);
    }

    [Fact]
    public async Task Average_InactiveSymbolWithTicks_Succeeds()
    {
        _queries.GetAverageAsync(Ada.Id, Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new AverageWindow(0.25m, Now.AddHours(-1), Now.AddHours(-1), 1));

        var result = await _service.GetAverageAsync("ADAUSDT", Ct);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.AveragePrice.ShouldBe(0.25m);
        result.Value.CoveredSeconds.ShouldBe(0);
    }

    [Fact]
    public async Task Average_NoTicksInWindow_ReturnsNoData()
    {
        var result = await _service.GetAverageAsync("BTCUSDT", Ct);

        result.Error.ShouldBe(AppError.NoData);
        result.Detail.ShouldBe("No trades were collected for 'BTCUSDT' in the last 24 hours.");
    }

    // ---- SMA --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Sma_NoStart_AveragesTheLastNCompleteBuckets()
    {
        // 5m buckets: the last complete one ends at 12:30, so n=3 covers [12:15, 12:30).
        var window = new SmaWindow(At(12, 15), At(12, 30));
        _queries.GetClosesAsync(Btc.Id, Period.FiveMinutes, window, Arg.Any<CancellationToken>())
            .Returns([new PricePoint(At(12, 15), 10m), new PricePoint(At(12, 20), 20m), new PricePoint(At(12, 25), 31m)]);

        var result = await _service.GetSmaAsync("BTCUSDT", 3, "5m", null, Ct);

        result.IsSuccess.ShouldBeTrue();
        var sma = result.Value.ShouldNotBeNull();
        sma.Symbol.ShouldBe("BTCUSDT");
        sma.Period.ShouldBe(Period.FiveMinutes);
        sma.RequestedDataPoints.ShouldBe(3);
        sma.ActualDataPoints.ShouldBe(3);
        sma.Value.ShouldBe(20.33333333m);   // 61 / 3 = 20.333333333… → 8 dp
        sma.Window.ShouldBe(window);
        sma.ReferenceTimeUtc.ShouldBe(Now);
        sma.StartParameterUtc.ShouldBeNull();
        sma.DataPoints.Select(p => p.Close).ShouldBe([10m, 20m, 31m]);
    }

    [Fact]
    public async Task Sma_WithStart_StartsAtTheFirstBoundaryAtOrAfterS()
    {
        var s = new DateTime(2026, 10, 1, 0, 0, 1, DateTimeKind.Utc);
        var window = new SmaWindow(new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 4, 0, 0, 0, DateTimeKind.Utc));
        _queries.GetClosesAsync(Btc.Id, Period.OneDay, window, Arg.Any<CancellationToken>())
            .Returns([new PricePoint(window.Start, 100m), new PricePoint(window.Start.AddDays(1), 101m)]);

        var result = await _service.GetSmaAsync("BTCUSDT", 2, "1d", s, Ct);

        var sma = result.Value.ShouldNotBeNull();
        sma.Window.ShouldBe(window);
        sma.StartParameterUtc.ShouldBe(s);
        sma.Value.ShouldBe(100.5m);
    }

    [Fact]
    public async Task Sma_FewerPointsThanRequested_ReportsTheActualCount()
    {
        _queries.GetClosesAsync(Btc.Id, Period.OneMinute, Arg.Any<SmaWindow>(), Arg.Any<CancellationToken>())
            .Returns([new PricePoint(At(12, 30), 7m), new PricePoint(At(12, 33), 8m)]);

        var result = await _service.GetSmaAsync("BTCUSDT", 10, "1m", null, Ct);

        var sma = result.Value.ShouldNotBeNull();
        sma.RequestedDataPoints.ShouldBe(10);
        sma.ActualDataPoints.ShouldBe(2);
        sma.Value.ShouldBe(7.5m);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1001)]
    public async Task Sma_NOutOfRange_ReturnsValidation(int n)
    {
        var result = await _service.GetSmaAsync("BTCUSDT", n, "1m", null, Ct);

        result.Error.ShouldBe(AppError.Validation);
        result.Parameter.ShouldBe("n");
        result.Detail.ShouldBe("n must be between 1 and 1000.");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1000)]
    public async Task Sma_NAtBounds_IsAccepted(int n)
    {
        _queries.GetClosesAsync(Btc.Id, Period.OneWeek, Arg.Any<SmaWindow>(), Arg.Any<CancellationToken>())
            .Returns([new PricePoint(new DateTime(2026, 9, 28, 0, 0, 0, DateTimeKind.Utc), 1m)]);

        var result = await _service.GetSmaAsync("BTCUSDT", n, "1w", null, Ct);

        result.IsSuccess.ShouldBeTrue();
    }

    [Theory]
    [InlineData("1h")]
    [InlineData("1W")]
    [InlineData("5 m")]
    [InlineData("60m")]
    public async Task Sma_UnknownPeriod_ReturnsValidation(string p)
    {
        var result = await _service.GetSmaAsync("BTCUSDT", 5, p, null, Ct);

        result.Error.ShouldBe(AppError.Validation);
        result.Parameter.ShouldBe("p");
        result.Detail.ShouldBe("p must be one of 1w, 1d, 30m, 5m, 1m.");
    }

    [Fact]
    public async Task Sma_MissingN_ReturnsValidation()
    {
        var result = await _service.GetSmaAsync("BTCUSDT", null, "1m", null, Ct);

        result.Error.ShouldBe(AppError.Validation);
        result.Parameter.ShouldBe("n");
        result.Detail.ShouldBe("n is required.");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task Sma_MissingP_ReturnsValidation(string? p)
    {
        var result = await _service.GetSmaAsync("BTCUSDT", 5, p, null, Ct);

        result.Error.ShouldBe(AppError.Validation);
        result.Parameter.ShouldBe("p");
        result.Detail.ShouldBe("p is required.");
    }

    [Fact]
    public async Task Sma_FutureStart_ReturnsValidation()
    {
        var result = await _service.GetSmaAsync("BTCUSDT", 5, "1m", Now.AddTicks(1), Ct);

        result.Error.ShouldBe(AppError.Validation);
        result.Parameter.ShouldBe("s");
        result.Detail.ShouldBe("s must not be in the future.");
    }

    [Fact]
    public async Task Sma_ValidationComesBeforeTheSymbolLookup()
    {
        var result = await _service.GetSmaAsync("NOPEUSDT", 0, "1m", null, Ct);

        result.Error.ShouldBe(AppError.Validation);
        await _symbols.DidNotReceiveWithAnyArgs().FindAsync(default, Ct);
    }

    [Fact]
    public async Task Sma_EarliestRepresentableStart_IsAligned()
    {
        // 0001-01-01 is a Monday, so it is a bucket boundary of every period; nothing overflows.
        var s = DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc);

        var result = await _service.GetSmaAsync("BTCUSDT", 5, "1w", s, Ct);

        result.Error.ShouldBe(AppError.NoData);
        await _queries.Received(1).GetClosesAsync(Btc.Id, Period.OneWeek, new SmaWindow(s, s.AddDays(35)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Sma_StartInTheCurrentBucket_ReturnsNoDataWithoutQuerying()
    {
        // No complete 1d bucket starts at or after today 00:00:01.
        var result = await _service.GetSmaAsync("BTCUSDT", 5, "1d", Now.Date.AddSeconds(1), Ct);

        result.Error.ShouldBe(AppError.NoData);
        result.Detail.ShouldBe("No complete 1d bucket of 'BTCUSDT' falls inside the requested window.");
        await _queries.DidNotReceiveWithAnyArgs().GetClosesAsync(default, default, default, Ct);
    }

    [Fact]
    public async Task Sma_NoCloses_ReturnsNoData()
    {
        _queries.GetClosesAsync(Btc.Id, Period.OneMinute, Arg.Any<SmaWindow>(), Arg.Any<CancellationToken>()).Returns([]);

        var result = await _service.GetSmaAsync("BTCUSDT", 5, "1m", null, Ct);

        result.Error.ShouldBe(AppError.NoData);
        result.Detail.ShouldBe("No complete 1m bucket of 'BTCUSDT' falls inside the requested window.");
    }

    [Fact]
    public async Task Sma_UnknownSymbol_ReturnsUnknownSymbol()
    {
        var result = await _service.GetSmaAsync("NOPEUSDT", 5, "1m", null, Ct);

        result.Error.ShouldBe(AppError.UnknownSymbol);
        result.Detail.ShouldBe("Symbol 'NOPEUSDT' is not monitored.");
    }

    [Fact]
    public async Task Sma_LocalStart_Throws() =>
        await Should.ThrowAsync<ArgumentException>(() =>
            _service.GetSmaAsync("BTCUSDT", 5, "1m", DateTime.SpecifyKind(Now.AddDays(-1), DateTimeKind.Local), Ct));

    // ---- symbols ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task ListSymbols_ComputesTheChangeFromTheLivePriceAndTheCloseADayAgo()
    {
        _symbols.ListAsync(Arg.Any<CancellationToken>()).Returns([Btc]);
        _ingestion.RecordBatch(1, 0, [new LivePrice("BTCUSDT", 66_000m, Now.AddSeconds(-1))]);
        DayAgoCloses((Btc.Id, Now.AddHours(-24).AddMinutes(-4), 64_000m));

        var result = await _service.ListSymbolsAsync(Ct);

        // (66000 - 64000) / 64000 × 100 = 3.125 → 3.12 (banker's rounding to 2 dp).
        result.Value.ShouldNotBeNull().ShouldBe([new SymbolSummary("BTCUSDT", true, 66_000m, 3.12m, BackfillStatus.Completed)]);

        // Every symbol has a live price, so no fallback query runs.
        await _queries.DidNotReceive().GetClosesAtOrBeforeAsync(Arg.Any<IReadOnlyCollection<int>>(), Now, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListSymbols_WithoutALivePrice_FallsBackToTheLastCandleClose()
    {
        _symbols.ListAsync(Arg.Any<CancellationToken>()).Returns([Ada]);
        LatestCloses((Ada.Id, Now.AddHours(-2), 0.2475m));
        DayAgoCloses((Ada.Id, Now.AddHours(-24).AddMinutes(-3), 0.25m));

        var result = await _service.ListSymbolsAsync(Ct);

        // (0.2475 - 0.25) / 0.25 × 100 = -1.
        result.Value.ShouldNotBeNull().ShouldBe([new SymbolSummary("ADAUSDT", false, 0.2475m, -1.00m, BackfillStatus.NotRequested)]);
    }

    [Fact]
    public async Task ListSymbols_StaleFallback_GivesNullChange()
    {
        // The last candle is 30 h old, so it is also "the close a day ago": comparing it with itself would claim 0 %.
        _symbols.ListAsync(Arg.Any<CancellationToken>()).Returns([Ada]);
        LatestCloses((Ada.Id, Now.AddHours(-30), 0.3m));
        DayAgoCloses((Ada.Id, Now.AddHours(-30), 0.3m));

        var result = await _service.ListSymbolsAsync(Ct);

        result.Value.ShouldNotBeNull().ShouldBe([new SymbolSummary("ADAUSDT", false, 0.3m, null, BackfillStatus.NotRequested)]);
    }

    [Fact]
    public async Task ListSymbols_DayAgoCloseFarBeforeTheWindow_GivesNullChange()
    {
        // A backfill runs oldest first: until it reaches now − 24 h, the newest close at or before now − 24 h is its
        // frontier, weeks back. Comparing with it would report a change over weeks as the 24 h change.
        _symbols.ListAsync(Arg.Any<CancellationToken>()).Returns([Eth]);
        _ingestion.RecordBatch(1, 0, [new LivePrice("ETHUSDT", 2_840m, Now.AddSeconds(-1))]);
        DayAgoCloses((Eth.Id, Now.AddDays(-40), 2_000m));

        var result = await _service.ListSymbolsAsync(Ct);

        result.Value.ShouldNotBeNull().ShouldBe([new SymbolSummary("ETHUSDT", true, 2_840m, null, BackfillStatus.Running)]);
    }

    [Theory]
    [InlineData(BackfillStatus.Running, 0, true)]
    [InlineData(BackfillStatus.Running, 14, true)]
    [InlineData(BackfillStatus.Running, 15, true)]
    [InlineData(BackfillStatus.Running, 16, false)]
    [InlineData(BackfillStatus.Running, 60, false)]
    [InlineData(BackfillStatus.NotRequested, 16, false)]
    [InlineData(BackfillStatus.Pending, 16, false)]
    [InlineData(BackfillStatus.Failed, 16, false)]
    public async Task ListSymbols_BeforeTheBackfillCompletes_DayAgoCloseMustLieWithinTheTolerance(
        BackfillStatus backfill, int minutesBeforeTheWindow, bool hasChange)
    {
        _symbols.ListAsync(Arg.Any<CancellationToken>()).Returns([Btc with { BackfillStatus = backfill }]);
        _ingestion.RecordBatch(1, 0, [new LivePrice("BTCUSDT", 66_000m, Now.AddSeconds(-1))]);
        DayAgoCloses((Btc.Id, Period.OneMinute.AlignDown(Now.AddHours(-24)).AddMinutes(-minutesBeforeTheWindow), 64_000m));

        var result = await _service.ListSymbolsAsync(Ct);

        result.Value.ShouldNotBeNull().Single().Change24hPercent.ShouldBe(hasChange ? 3.12m : null);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(120)]
    public async Task ListSymbols_BackfillCompleted_AcceptsAnOlderDayAgoClose(int minutesBeforeTheWindow)
    {
        // A thin pair can go hours without a trade. Once the history is complete, an older close is the last real
        // price before the window, as in a rolling 24 h ticker, not a backfill frontier.
        _symbols.ListAsync(Arg.Any<CancellationToken>()).Returns([Btc]);
        _ingestion.RecordBatch(1, 0, [new LivePrice("BTCUSDT", 66_000m, Now.AddSeconds(-1))]);
        DayAgoCloses((Btc.Id, Period.OneMinute.AlignDown(Now.AddHours(-24)).AddMinutes(-minutesBeforeTheWindow), 64_000m));

        var result = await _service.ListSymbolsAsync(Ct);

        result.Value.ShouldNotBeNull().ShouldBe([new SymbolSummary("BTCUSDT", true, 66_000m, 3.12m, BackfillStatus.Completed)]);
    }

    [Fact]
    public async Task ListSymbols_LivePriceOlderThanADay_GivesNullChange()
    {
        _symbols.ListAsync(Arg.Any<CancellationToken>()).Returns([Btc]);
        _ingestion.RecordBatch(1, 0, [new LivePrice("BTCUSDT", 66_000m, Now.AddHours(-24))]);
        DayAgoCloses((Btc.Id, Now.AddHours(-24), 66_000m));

        var result = await _service.ListSymbolsAsync(Ct);

        result.Value.ShouldNotBeNull().Single().Change24hPercent.ShouldBeNull();
    }

    [Fact]
    public async Task ListSymbols_BatchesTheCloseQueries()
    {
        _symbols.ListAsync(Arg.Any<CancellationToken>()).Returns([Btc, Ada, Eth]);
        _ingestion.RecordBatch(1, 0, [new LivePrice("BTCUSDT", 66_000m, Now)]);

        await _service.ListSymbolsAsync(Ct);

        // One query for the closes a day ago (every symbol), one for the fallback prices (symbols without a live one).
        await _queries.Received(1).GetClosesAtOrBeforeAsync(
            Arg.Is<IReadOnlyCollection<int>>(ids => ids.SequenceEqual(new[] { Btc.Id, Ada.Id, Eth.Id })),
            Now.AddHours(-24),
            Arg.Any<CancellationToken>());
        await _queries.Received(1).GetClosesAtOrBeforeAsync(
            Arg.Is<IReadOnlyCollection<int>>(ids => ids.SequenceEqual(new[] { Ada.Id, Eth.Id })),
            Now,
            Arg.Any<CancellationToken>());
        _queries.ReceivedCalls().Count().ShouldBe(2);
    }

    [Fact]
    public async Task ListSymbols_MissingPrices_GiveNullValues()
    {
        _symbols.ListAsync(Arg.Any<CancellationToken>()).Returns([Btc, Eth]);
        _ingestion.RecordBatch(1, 0, [new LivePrice("BTCUSDT", 66_000m, Now)]);
        LatestCloses();
        DayAgoCloses();

        var result = await _service.ListSymbolsAsync(Ct);

        result.Value.ShouldNotBeNull().ShouldBe(
        [
            new SymbolSummary("BTCUSDT", true, 66_000m, null, BackfillStatus.Completed),
            new SymbolSummary("ETHUSDT", true, null, null, BackfillStatus.Running),
        ]);
    }

    [Fact]
    public async Task ListSymbols_ZeroCloseADayAgo_GivesNullChange()
    {
        _symbols.ListAsync(Arg.Any<CancellationToken>()).Returns([Btc]);
        _ingestion.RecordBatch(1, 0, [new LivePrice("BTCUSDT", 1m, Now)]);
        DayAgoCloses((Btc.Id, Now.AddHours(-24).AddMinutes(-3), 0m));

        var result = await _service.ListSymbolsAsync(Ct);

        result.Value.ShouldNotBeNull().Single().Change24hPercent.ShouldBeNull();
    }

    [Fact]
    public async Task ListSymbols_NoSymbols_ReturnsAnEmptyListWithoutQueries()
    {
        _symbols.ListAsync(Arg.Any<CancellationToken>()).Returns([]);

        var result = await _service.ListSymbolsAsync(Ct);

        result.Value.ShouldNotBeNull().ShouldBeEmpty();
        _queries.ReceivedCalls().ShouldBeEmpty();
    }

    // ---- candles ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Candles_ReturnsTheRowsOfTheQuery()
    {
        CandleRow[] rows = [new(At(12, 0), 1m, 3m, 0.5m, 2m, 10m), new(At(12, 30), 2m, 2.5m, 1.5m, 2.25m, 4m)];
        _queries.GetCandlesAsync(Btc.Id, Period.ThirtyMinutes, 2, Arg.Any<CancellationToken>()).Returns(rows);

        var result = await _service.GetCandlesAsync("btcusdt", "30m", 2, Ct);

        result.Value.ShouldBe(rows);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1001)]
    public async Task Candles_LimitOutOfRange_ReturnsValidation(int limit)
    {
        var result = await _service.GetCandlesAsync("BTCUSDT", "1m", limit, Ct);

        result.Error.ShouldBe(AppError.Validation);
        result.Parameter.ShouldBe("limit");
        result.Detail.ShouldBe("limit must be between 1 and 1000.");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Candles_MissingP_ReturnsValidation(string? p)
    {
        var result = await _service.GetCandlesAsync("BTCUSDT", p, 10, Ct);

        result.Error.ShouldBe(AppError.Validation);
        result.Parameter.ShouldBe("p");
        result.Detail.ShouldBe("p is required.");
    }

    [Fact]
    public async Task Candles_UnknownPeriod_ReturnsValidation()
    {
        var result = await _service.GetCandlesAsync("BTCUSDT", "2h", 10, Ct);

        result.Error.ShouldBe(AppError.Validation);
        result.Parameter.ShouldBe("p");
        result.Detail.ShouldBe("p must be one of 1w, 1d, 30m, 5m, 1m.");
    }

    [Fact]
    public async Task Candles_UnknownSymbol_ReturnsUnknownSymbol()
    {
        var result = await _service.GetCandlesAsync("NOPEUSDT", "1m", 10, Ct);

        result.Error.ShouldBe(AppError.UnknownSymbol);
    }

    [Fact]
    public async Task Candles_NoRows_ReturnsNoData()
    {
        _queries.GetCandlesAsync(Btc.Id, Period.OneMinute, 10, Arg.Any<CancellationToken>()).Returns([]);

        var result = await _service.GetCandlesAsync("BTCUSDT", "1m", 10, Ct);

        result.Error.ShouldBe(AppError.NoData);
        result.Detail.ShouldBe("No candles are stored for 'BTCUSDT'.");
    }

    private void LatestCloses(params (int Id, DateTime At, decimal Close)[] closes) => Closes(Now, closes);

    private void DayAgoCloses(params (int Id, DateTime At, decimal Close)[] closes) => Closes(Now.AddHours(-24), closes);

    private void Closes(DateTime utc, (int Id, DateTime At, decimal Close)[] closes) =>
        _queries.GetClosesAtOrBeforeAsync(Arg.Any<IReadOnlyCollection<int>>(), utc, Arg.Any<CancellationToken>())
            .Returns(closes.ToDictionary(c => c.Id, c => new PricePoint(c.At, c.Close)));

    private static DateTime At(int hour, int minute) => new(2026, 10, 7, hour, minute, 0, DateTimeKind.Utc);

    private static SymbolName Name(string value) => SymbolName.TryCreate(value, out var name) ? name : throw new ArgumentException(value);

    private static MonitoredSymbol Symbol(int id, string name, bool isActive, BackfillStatus backfill) =>
        new(id, name, isActive, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), isActive ? null : new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), backfill, null);
}

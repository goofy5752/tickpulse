using NSubstitute;
using NSubstitute.ExceptionExtensions;
using TickPulse.Application.Abstractions;
using TickPulse.Application.Results;
using TickPulse.Application.Symbols;
using TickPulse.Domain;

namespace TickPulse.Application.Tests;

public sealed class SymbolAdminServiceTests
{
    private const int Limit = 20;

    private static readonly DateTime Created = new(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc);
    private static readonly MonitoredSymbol Btc = Symbol(1, "BTCUSDT", isActive: true);
    private static readonly MonitoredSymbol Ada = Symbol(2, "ADAUSDT", isActive: false);
    private static readonly MonitoredSymbol Bnb = Symbol(7, "BNBUSDT", isActive: true);

    private readonly ISymbolRepository _symbols = Substitute.For<ISymbolRepository>();
    private readonly IExchangeMetadata _exchange = Substitute.For<IExchangeMetadata>();
    private readonly ISymbolChangeSignal _changes = Substitute.For<ISymbolChangeSignal>();
    private readonly IBackfillQueue _backfill = Substitute.For<IBackfillQueue>();

    public SymbolAdminServiceTests()
    {
        _symbols.FindAsync(Name("BTCUSDT"), Arg.Any<CancellationToken>()).Returns(Btc);
        _symbols.FindAsync(Name("ADAUSDT"), Arg.Any<CancellationToken>()).Returns(Ada);
        _exchange.IsTradingSymbolAsync(Arg.Any<SymbolName>(), Arg.Any<CancellationToken>()).Returns(true);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- add --------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("BTC-USDT")]
    [InlineData("ABC")]
    [InlineData("ABCDEFGHIJKLMNOPQRSTU")]
    public async Task Add_MalformedSymbol_ReturnsInvalidSymbol_WithoutTouchingPorts(string? raw)
    {
        var result = await Service().AddAsync(raw, Ct);

        result.Error.ShouldBe(AppError.InvalidSymbol);
        result.Detail.ShouldNotBeNullOrWhiteSpace();
        _symbols.ReceivedCalls().ShouldBeEmpty();
        _exchange.ReceivedCalls().ShouldBeEmpty();
        _changes.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("BTCUSDT")]
    [InlineData(" btcusdt ")]
    public async Task Add_AlreadyActive_ReturnsNotCreated_WithoutExchangeCallOrWrite(string raw)
    {
        var result = await Service().AddAsync(raw, Ct);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(new SymbolAddResult(Btc, Created: false));
        await _exchange.DidNotReceiveWithAnyArgs().IsTradingSymbolAsync(default, Ct);
        await _symbols.DidNotReceiveWithAnyArgs().AddOrReactivateAsync(default, default, Ct);
        _changes.DidNotReceive().Notify();
        _backfill.DidNotReceiveWithAnyArgs().Enqueue(default);
    }

    [Fact]
    public async Task Add_NotTradingOnExchange_ReturnsInvalidSymbol_AndWritesNothing()
    {
        _exchange.IsTradingSymbolAsync(Name("NOPEUSDT"), Arg.Any<CancellationToken>()).Returns(false);

        var result = await Service().AddAsync("nopeusdt", Ct);

        result.Error.ShouldBe(AppError.InvalidSymbol);
        result.Detail.ShouldNotBeNull().ShouldContain("NOPEUSDT");
        await _symbols.DidNotReceiveWithAnyArgs().AddOrReactivateAsync(default, default, Ct);
        _changes.DidNotReceive().Notify();
    }

    [Fact]
    public async Task Add_ExchangeUnavailable_ReturnsUpstreamUnavailable_AndWritesNothing()
    {
        _exchange.IsTradingSymbolAsync(Name("BNBUSDT"), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ExchangeUnavailableException("Binance is unreachable.", new HttpRequestException("refused")));

        var result = await Service().AddAsync("BNBUSDT", Ct);

        result.Error.ShouldBe(AppError.UpstreamUnavailable);
        result.Detail.ShouldNotBeNullOrWhiteSpace();
        await _symbols.DidNotReceiveWithAnyArgs().AddOrReactivateAsync(default, default, Ct);
        _changes.DidNotReceive().Notify();
    }

    [Fact]
    public async Task Add_OtherExchangeFailure_Propagates()
    {
        _exchange.IsTradingSymbolAsync(Name("BNBUSDT"), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("malformed"));

        await Should.ThrowAsync<InvalidOperationException>(() => Service().AddAsync("BNBUSDT", Ct));
    }

    [Fact]
    public async Task Add_NewSymbol_CreatesWithLimit_NotifiesAndReturnsCreated()
    {
        _symbols.AddOrReactivateAsync(Name("BNBUSDT"), Limit, Arg.Any<CancellationToken>())
            .Returns(new SymbolAddOutcome(Bnb, SymbolAddStatus.Created));

        var result = await Service().AddAsync(" bnbusdt ", Ct);

        result.Value.ShouldBe(new SymbolAddResult(Bnb, Created: true));
        await _exchange.Received(1).IsTradingSymbolAsync(Name("BNBUSDT"), Arg.Any<CancellationToken>());
        _changes.Received(1).Notify();

        // Backfill is off: the status stays NotRequested and nothing is queued.
        await _symbols.DidNotReceiveWithAnyArgs().SetBackfillStatusAsync(default, default, default, Ct);
        _backfill.DidNotReceiveWithAnyArgs().Enqueue(default);
    }

    [Fact]
    public async Task Add_InactiveSymbol_ChecksExchange_AndReactivates()
    {
        var reactivated = Ada with { IsActive = true, DeactivatedAtUtc = null };
        _symbols.AddOrReactivateAsync(Name("ADAUSDT"), Limit, Arg.Any<CancellationToken>())
            .Returns(new SymbolAddOutcome(reactivated, SymbolAddStatus.Reactivated));

        var result = await Service().AddAsync("ADAUSDT", Ct);

        result.Value.ShouldBe(new SymbolAddResult(reactivated, Created: true));
        await _exchange.Received(1).IsTradingSymbolAsync(Name("ADAUSDT"), Arg.Any<CancellationToken>());
        _changes.Received(1).Notify();
    }

    [Fact]
    public async Task Add_ActivatedConcurrently_ReturnsNotCreated_WithoutNotify()
    {
        _symbols.AddOrReactivateAsync(Name("BNBUSDT"), Limit, Arg.Any<CancellationToken>())
            .Returns(new SymbolAddOutcome(Bnb, SymbolAddStatus.AlreadyActive));

        var result = await Service().AddAsync("BNBUSDT", Ct);

        result.Value.ShouldBe(new SymbolAddResult(Bnb, Created: false));
        _changes.DidNotReceive().Notify();
        _backfill.DidNotReceiveWithAnyArgs().Enqueue(default);
    }

    [Fact]
    public async Task Add_LimitReached_ReturnsSymbolLimit_WithoutNotify()
    {
        _symbols.AddOrReactivateAsync(Name("BNBUSDT"), 3, Arg.Any<CancellationToken>())
            .Returns(new SymbolAddOutcome(null, SymbolAddStatus.LimitReached));

        var result = await Service(maxActiveSymbols: 3).AddAsync("BNBUSDT", Ct);

        result.Error.ShouldBe(AppError.SymbolLimit);
        result.Detail.ShouldNotBeNull().ShouldContain("3");
        _changes.DidNotReceive().Notify();
        _backfill.DidNotReceiveWithAnyArgs().Enqueue(default);
    }

    [Theory]
    [InlineData(SymbolAddStatus.Created)]
    [InlineData(SymbolAddStatus.Reactivated)]
    public async Task Add_BackfillEnabled_NotifiesThenMarksPendingThenEnqueues(SymbolAddStatus status)
    {
        var from = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        var stored = Bnb with { BackfillStatus = BackfillStatus.Completed, BackfilledFromUtc = from };
        _symbols.AddOrReactivateAsync(Name("BNBUSDT"), Limit, Arg.Any<CancellationToken>())
            .Returns(new SymbolAddOutcome(stored, status));

        var result = await Service(backfillEnabled: true).AddAsync("BNBUSDT", Ct);

        result.Value.ShouldBe(new SymbolAddResult(stored with { BackfillStatus = BackfillStatus.Pending }, Created: true));
        Received.InOrder(() =>
        {
            _changes.Notify();
            _symbols.SetBackfillStatusAsync(Bnb.Id, BackfillStatus.Pending, from, Arg.Any<CancellationToken>());
            _backfill.Enqueue(Bnb.Id);
        });
    }

    // ---- remove -----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("BTCUSDT")]
    [InlineData(" btcusdt ")]
    public async Task Remove_Active_DeactivatesAndNotifies(string raw)
    {
        _symbols.DeactivateAsync(Name("BTCUSDT"), Arg.Any<CancellationToken>()).Returns(true);

        var result = await Service().RemoveAsync(raw, Ct);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe("BTCUSDT");
        Received.InOrder(() =>
        {
            _symbols.DeactivateAsync(Name("BTCUSDT"), Arg.Any<CancellationToken>());
            _changes.Notify();
        });
    }

    [Theory]
    [InlineData("NOPEUSDT")]
    [InlineData("ADAUSDT")]     // already inactive
    [InlineData("BTC-USDT")]    // malformed: never monitored
    [InlineData(null)]
    public async Task Remove_UnknownOrInactive_ReturnsUnknownSymbol_WithoutWrite(string? raw)
    {
        var result = await Service().RemoveAsync(raw, Ct);

        result.Error.ShouldBe(AppError.UnknownSymbol);
        result.Detail.ShouldNotBeNullOrWhiteSpace();
        await _symbols.DidNotReceiveWithAnyArgs().DeactivateAsync(default, Ct);
        _changes.DidNotReceive().Notify();
    }

    [Fact]
    public async Task Remove_DeletedConcurrently_ReturnsUnknownSymbol_WithoutNotify()
    {
        _symbols.DeactivateAsync(Name("BTCUSDT"), Arg.Any<CancellationToken>()).Returns(false);

        var result = await Service().RemoveAsync("BTCUSDT", Ct);

        result.Error.ShouldBe(AppError.UnknownSymbol);
        _changes.DidNotReceive().Notify();
    }

    // ---- list -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task List_ReturnsEverySymbol_IncludingInactive()
    {
        _symbols.ListAsync(Arg.Any<CancellationToken>()).Returns([Btc, Ada]);

        var result = await Service().ListAsync(Ct);

        result.Value.ShouldBe([Btc, Ada]);
    }

    // ---- settings ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_NonPositiveLimit_Throws(int limit) =>
        Should.Throw<ArgumentOutOfRangeException>(() => Service(maxActiveSymbols: limit));

    private SymbolAdminService Service(int maxActiveSymbols = Limit, bool backfillEnabled = false) =>
        new(_symbols, _exchange, _changes, _backfill, new SymbolAdminSettings(maxActiveSymbols, backfillEnabled));

    private static MonitoredSymbol Symbol(int id, string name, bool isActive) =>
        new(id, name, isActive, Created, isActive ? null : Created.AddDays(1), BackfillStatus.NotRequested, null);

    private static SymbolName Name(string raw) =>
        SymbolName.TryCreate(raw, out var name) ? name : throw new ArgumentException(raw, nameof(raw));
}

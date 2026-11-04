using Microsoft.Extensions.Time.Testing;
using TickPulse.Application.Ingestion;

namespace TickPulse.Application.Tests;

public sealed class IngestionStateTests
{
    private static readonly DateTime T0 = new(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(T0));

    [Fact]
    public void New_IsStandbyWithNothingKnown()
    {
        var state = new IngestionState(_clock);

        state.Status.ShouldBe(WsState.Standby);
        state.IsLeader.ShouldBeFalse();
        state.ConnectedSinceUtc.ShouldBeNull();
        state.NextRotationUtc.ShouldBeNull();
        state.LastReconnectUtc.ShouldBeNull();
        state.TicksPerSecond.ShouldBe(0);
        state.LastLagMs.ShouldBe(0);
        state.Latest.ShouldBeEmpty();
    }

    [Fact]
    public void Transitions_TrackLeadershipAndConnection()
    {
        var state = new IngestionState(_clock);

        state.SetConnecting();
        state.IsLeader.ShouldBeTrue();
        state.Status.ShouldBe(WsState.Connecting);

        state.SetConnected(T0, T0.AddHours(23));
        state.Status.ShouldBe(WsState.Connected);
        state.ConnectedSinceUtc.ShouldBe(T0);
        state.NextRotationUtc.ShouldBe(T0.AddHours(23));

        state.SetDisconnected();
        state.Status.ShouldBe(WsState.Disconnected);
        state.IsLeader.ShouldBeTrue();
        state.ConnectedSinceUtc.ShouldBeNull();
        state.NextRotationUtc.ShouldBeNull();

        state.MarkReconnected(T0.AddMinutes(1));
        state.LastReconnectUtc.ShouldBe(T0.AddMinutes(1));

        state.SetStandby();
        state.Status.ShouldBe(WsState.Standby);
        state.IsLeader.ShouldBeFalse();
        state.LastReconnectUtc.ShouldBe(T0.AddMinutes(1));
    }

    [Fact]
    public void LeaderSinceUtc_MarksTheStartOfEachLeadershipSession()
    {
        var state = new IngestionState(_clock);
        state.LeaderSinceUtc.ShouldBeNull();

        state.SetConnecting();
        state.LeaderSinceUtc.ShouldBe(T0);
        state.LeaderSinceUtc!.Value.Kind.ShouldBe(DateTimeKind.Utc);

        // Connection changes within the session keep it.
        _clock.Advance(TimeSpan.FromMinutes(1));
        state.SetConnected(T0, T0.AddHours(23));
        state.SetDisconnected();
        state.SetConnecting();
        state.LeaderSinceUtc.ShouldBe(T0);

        // A new session after standing by starts a new one.
        state.SetStandby();
        state.LeaderSinceUtc.ShouldBeNull();
        _clock.Advance(TimeSpan.FromMinutes(1));
        state.SetConnecting();
        state.LeaderSinceUtc.ShouldBe(T0.AddMinutes(2));
    }

    [Fact]
    public void TicksPerSecond_SteadyRate_ConvergesThenDecays()
    {
        var state = new IngestionState(_clock);

        // 50 ticks every 500 ms is 100 ticks/s. After six time constants the estimate has converged; read just
        // after a batch it sits above the true rate by at most that batch's share, 1 - e^(-0.5 s / 10 s).
        for (var i = 0; i < 120; i++)
        {
            _clock.Advance(TimeSpan.FromMilliseconds(500));
            state.RecordBatch(50, 10, []);
        }

        state.TicksPerSecond.ShouldBe(100, tolerance: 0.3 + (100 * (1 - Math.Exp(-0.05))));

        // With nothing stored the rate decays by e every time constant.
        var before = state.TicksPerSecond;
        _clock.Advance(IngestionState.RateWindow);
        state.TicksPerSecond.ShouldBe(before / Math.E, tolerance: 1e-9);
    }

    [Fact]
    public void RecordBatch_KeepsTheNewestPricePerSymbol()
    {
        var state = new IngestionState(_clock);
        var raised = new List<LivePrice>();
        state.PriceUpdated += raised.Add;

        state.RecordBatch(2, 120.5, [new("BTCUSDT", 100m, T0), new("ETHUSDT", 3000m, T0)]);
        state.RecordBatch(1, 99, [new("BTCUSDT", 90m, T0.AddSeconds(-5))]); // a gap-filled, older trade
        state.RecordBatch(1, 98, [new("BTCUSDT", 101m, T0.AddSeconds(1))]);
        state.RecordBatch(1, 97, [new("BTCUSDT", 101m, T0.AddSeconds(1))]); // the same again

        state.Latest["BTCUSDT"].ShouldBe(new LivePrice("BTCUSDT", 101m, T0.AddSeconds(1)));
        state.Latest["ETHUSDT"].Price.ShouldBe(3000m);
        state.LastLagMs.ShouldBe(97);
        raised.Select(p => p.Price).ShouldBe([100m, 3000m, 101m]);
    }

    [Fact]
    public void RecordBatch_WithoutALagSample_KeepsTheLastLag()
    {
        var state = new IngestionState(_clock);

        state.RecordBatch(1, 120.5, []);
        state.RecordBatch(3, null, [new("BTCUSDT", 100m, T0)]); // a recovered batch: its age is not ingest lag

        state.LastLagMs.ShouldBe(120.5);
        state.TicksPerSecond.ShouldBe(4 / IngestionState.RateWindow.TotalSeconds, 1e-9);
        state.Latest["BTCUSDT"].Price.ShouldBe(100m);
    }

    [Fact]
    public void RecordBatch_FailingHandler_StillCallsTheOthers()
    {
        var state = new IngestionState(_clock);
        var raised = new List<LivePrice>();
        state.PriceUpdated += _ => throw new InvalidOperationException("broken");
        state.PriceUpdated += raised.Add;

        var failure = Should.Throw<AggregateException>(() => state.RecordBatch(1, 1, [new("BTCUSDT", 1m, T0)]));

        failure.InnerExceptions.ShouldHaveSingleItem().ShouldBeOfType<InvalidOperationException>();
        raised.ShouldHaveSingleItem().Price.ShouldBe(1m);
        state.Latest["BTCUSDT"].Price.ShouldBe(1m);
    }

    [Fact]
    public void RecordBatch_NegativeCount_Throws() =>
        Should.Throw<ArgumentOutOfRangeException>(() => new IngestionState(_clock).RecordBatch(-1, 0, []));
}

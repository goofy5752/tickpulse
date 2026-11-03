using TickPulse.Infrastructure.Ingestion;

namespace TickPulse.Integration.Tests.Binance;

public sealed class ReconnectBackoffTests
{
    [Fact]
    public void Backoff_DoublesUntilCap_AndResets()
    {
        var backoff = new ReconnectBackoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), new FixedRandom(0.5));

        var delays = Enumerable.Range(0, 8).Select(_ => backoff.Next().TotalSeconds).ToArray();

        // Full jitter: delay = random * min(max, initial * 2^attempt); the fixed random halves each ceiling.
        delays.ShouldBe([0.5, 1, 2, 4, 8, 15, 15, 15]);

        backoff.Reset();

        backoff.Next().ShouldBe(TimeSpan.FromSeconds(0.5));
    }

    [Fact]
    public void Next_FullJitter_StaysWithinCeiling()
    {
        var backoff = new ReconnectBackoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), new Random(42));

        for (var attempt = 0; attempt < 200; attempt++)
        {
            var ceiling = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(attempt, 10))));
            var delay = backoff.Next();

            delay.ShouldBeGreaterThanOrEqualTo(TimeSpan.Zero);
            delay.ShouldBeLessThanOrEqualTo(ceiling);
        }
    }

    [Fact]
    public void Ctor_Defaults_AreOneSecondAndThirtySeconds()
    {
        var backoff = new ReconnectBackoff(random: new FixedRandom(0.5));

        backoff.Next().ShouldBe(TimeSpan.FromSeconds(0.5));
        for (var i = 0; i < 10; i++)
        {
            backoff.Next();
        }

        backoff.Next().ShouldBe(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void Ctor_InvalidDelays_Throws()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new ReconnectBackoff(TimeSpan.Zero, TimeSpan.FromSeconds(30)));
        Should.Throw<ArgumentOutOfRangeException>(() => new ReconnectBackoff(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5)));
    }

    private sealed class FixedRandom(double value) : Random
    {
        public override double NextDouble() => value;
    }
}

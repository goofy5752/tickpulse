using Microsoft.Extensions.Time.Testing;
using TickPulse.Infrastructure.Binance;

namespace TickPulse.Integration.Tests.Binance;

public sealed class SubscribeThrottleTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Throttle_LimitsToFourPerSecond()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero));
        var throttle = new SubscribeThrottle(4, clock);

        var first = Enumerable.Range(0, 4).Select(_ => throttle.WaitAsync(ct).AsTask()).ToArray();
        first.ShouldAllBe(t => t.IsCompletedSuccessfully);

        var fifth = throttle.WaitAsync(ct).AsTask();
        fifth.IsCompleted.ShouldBeFalse();

        clock.Advance(TimeSpan.FromMilliseconds(999));
        fifth.IsCompleted.ShouldBeFalse();

        clock.Advance(TimeSpan.FromMilliseconds(1));
        await fifth.WaitAsync(Timeout, ct);

        // The window slides: three more fit in the second that started at t+1s, the next one waits.
        var more = Enumerable.Range(0, 3).Select(_ => throttle.WaitAsync(ct).AsTask()).ToArray();
        await Task.WhenAll(more).WaitAsync(Timeout, ct);
        var ninth = throttle.WaitAsync(ct).AsTask();
        ninth.IsCompleted.ShouldBeFalse();

        clock.Advance(TimeSpan.FromSeconds(1));
        await ninth.WaitAsync(Timeout, ct);
    }

    [Fact]
    public async Task WaitAsync_Canceled_Throws()
    {
        var clock = new FakeTimeProvider();
        var throttle = new SubscribeThrottle(1, clock);
        await throttle.WaitAsync(TestContext.Current.CancellationToken);
        using var cts = new CancellationTokenSource();

        var waiting = throttle.WaitAsync(cts.Token).AsTask();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => waiting);
    }

    [Fact]
    public void Ctor_NonPositiveRate_Throws() =>
        Should.Throw<ArgumentOutOfRangeException>(() => new SubscribeThrottle(0, new FakeTimeProvider()));
}

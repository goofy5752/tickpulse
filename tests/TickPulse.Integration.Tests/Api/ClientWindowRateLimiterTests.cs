using System.Threading.RateLimiting;
using Microsoft.Extensions.Time.Testing;
using TickPulse.Api.Hosting;

namespace TickPulse.Integration.Tests.Api;

public sealed class ClientWindowRateLimiterTests
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Rejection_ReportsTimeLeft_AndCountsOnlyAcquireAsync()
    {
        using var limiter = new ClientWindowRateLimiter(2, Window, _clock);
        limiter.AttemptAcquire().IsAcquired.ShouldBeTrue();
        _clock.Advance(TimeSpan.FromSeconds(10));
        (await limiter.AcquireAsync(1, Ct)).IsAcquired.ShouldBeTrue();
        _clock.Advance(TimeSpan.FromSeconds(15));

        // What the middleware does for a rejected request: a probe, then the lease it hands to OnRejected.
        using var probe = limiter.AttemptAcquire();
        using var first = await limiter.AcquireAsync(1, Ct);
        using var second = await limiter.AcquireAsync(1, Ct);

        probe.IsAcquired.ShouldBeFalse();
        Rejections(probe).ShouldBe(1);
        first.IsAcquired.ShouldBeFalse();
        Rejections(first).ShouldBe(1);
        Rejections(second).ShouldBe(2);
        first.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter).ShouldBeTrue();
        retryAfter.ShouldBe(TimeSpan.FromSeconds(35));
        first.MetadataNames.ShouldBe([MetadataName.RetryAfter.Name, ClientWindowRateLimiter.RejectionsInWindow.Name]);
        first.TryGetMetadata("other", out _).ShouldBeFalse();

        _clock.Advance(TimeSpan.FromSeconds(35));
        using var renewed = limiter.AttemptAcquire();
        renewed.IsAcquired.ShouldBeTrue();
        renewed.MetadataNames.ShouldBeEmpty();
        renewed.TryGetMetadata(MetadataName.RetryAfter.Name, out _).ShouldBeFalse();
        (await limiter.AcquireAsync(1, Ct)).IsAcquired.ShouldBeTrue();
        Rejections(await limiter.AcquireAsync(1, Ct)).ShouldBe(1, "a new window counts from 1 again");
    }

    [Fact]
    public void IdleDuration_IsNullWhileTheWindowHoldsPermits()
    {
        using var limiter = new ClientWindowRateLimiter(1, Window, _clock);
        _clock.Advance(TimeSpan.FromSeconds(3));
        limiter.IdleDuration.ShouldBe(TimeSpan.FromSeconds(3));

        limiter.AttemptAcquire().IsAcquired.ShouldBeTrue();
        limiter.IdleDuration.ShouldBeNull();
        _clock.Advance(TimeSpan.FromSeconds(59));
        limiter.IdleDuration.ShouldBeNull();

        _clock.Advance(TimeSpan.FromSeconds(6));
        limiter.IdleDuration.ShouldBe(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void ZeroPermits_ChecksAvailabilityWithoutUsingAny()
    {
        using var limiter = new ClientWindowRateLimiter(1, Window, _clock);

        limiter.AttemptAcquire(0).IsAcquired.ShouldBeTrue();
        limiter.IdleDuration.ShouldBe(TimeSpan.Zero, "a zero-permit probe uses nothing");
        limiter.AttemptAcquire(1).IsAcquired.ShouldBeTrue();
        limiter.AttemptAcquire(0).IsAcquired.ShouldBeFalse();
        Should.Throw<ArgumentOutOfRangeException>(() => limiter.AttemptAcquire(2));
    }

    [Fact]
    public async Task Statistics_CountLeasesAndFreePermits_OneFailurePerRejection()
    {
        using var limiter = new ClientWindowRateLimiter(3, Window, _clock);
        limiter.AttemptAcquire(2).IsAcquired.ShouldBeTrue();

        // One rejected request, as the middleware asks for it: a probe, then AcquireAsync.
        limiter.AttemptAcquire(2).IsAcquired.ShouldBeFalse();
        limiter.GetStatistics().ShouldNotBeNull().TotalFailedLeases.ShouldBe(0, "a probe alone counts nothing");
        (await limiter.AcquireAsync(2, Ct)).IsAcquired.ShouldBeFalse();

        var stats = limiter.GetStatistics().ShouldNotBeNull();
        stats.CurrentAvailablePermits.ShouldBe(1);
        stats.CurrentQueuedCount.ShouldBe(0);
        stats.TotalSuccessfulLeases.ShouldBe(1);
        stats.TotalFailedLeases.ShouldBe(1);

        _clock.Advance(Window);
        limiter.GetStatistics().ShouldNotBeNull().CurrentAvailablePermits.ShouldBe(3);
    }

    [Fact]
    public void InvalidSettings_Throw()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new ClientWindowRateLimiter(0, Window, _clock));
        Should.Throw<ArgumentOutOfRangeException>(() => new ClientWindowRateLimiter(1, TimeSpan.Zero, _clock));
        Should.Throw<ArgumentNullException>(() => new ClientWindowRateLimiter(1, Window, null!));
    }

    private static int Rejections(RateLimitLease lease) =>
        lease.TryGetMetadata(ClientWindowRateLimiter.RejectionsInWindow, out var count) ? count : -1;
}

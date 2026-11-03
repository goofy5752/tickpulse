using static TickPulse.Domain.Tests.TestData;

namespace TickPulse.Domain.Tests;

public sealed class PeriodTests
{
    [Theory]
    [InlineData("1m", 60)]
    [InlineData("5m", 300)]
    [InlineData("30m", 1800)]
    [InlineData("1d", 86400)]
    [InlineData("1w", 604800)]
    public void TryParse_ValidCode_ReturnsLength(string code, int seconds)
    {
        Period.TryParse(code, out var p).ShouldBeTrue();
        p.Length.TotalSeconds.ShouldBe(seconds);
        p.Code.ShouldBe(code);
    }

    [Theory]
    [InlineData("1h")]
    [InlineData("1M")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData(" 1d")]
    public void TryParse_InvalidCode_ReturnsFalse(string? code) => Period.TryParse(code, out _).ShouldBeFalse();

    [Fact]
    public void All_ListsEveryPeriod() =>
        Period.All.Select(p => p.Code).ShouldBe(["1m", "5m", "30m", "1d", "1w"]);

    [Fact]
    public void AlignDown_Week_IsMondayMidnightUtc() =>
        Period.OneWeek.AlignDown(Utc("2026-11-05T13:45:00")).ShouldBe(Utc("2026-11-02T00:00:00"));

    [Fact]
    public void AlignDown_BeforeOrigin_FloorsTowardsThePast() =>
        Period.OneDay.AlignDown(Utc("1999-12-31T12:00:00")).ShouldBe(Utc("1999-12-31T00:00:00"));

    [Fact]
    public void AlignDown_OnBoundary_IsIdentity() =>
        Period.FiveMinutes.AlignDown(Utc("2026-11-03T10:05:00")).ShouldBe(Utc("2026-11-03T10:05:00"));

    [Fact]
    public void AlignUp_OnBoundary_IsIdentity() =>
        Period.OneDay.AlignUp(Utc("2026-11-03T00:00:00")).ShouldBe(Utc("2026-11-03T00:00:00"));

    [Fact]
    public void AlignUp_OffBoundary_GoesToNext() =>
        Period.ThirtyMinutes.AlignUp(Utc("2026-11-03T10:00:01")).ShouldBe(Utc("2026-11-03T10:30:00"));

    [Fact]
    public void Align_NonUtcKind_Throws()
    {
        var local = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Local);
        Should.Throw<ArgumentException>(() => Period.OneMinute.AlignDown(local));
        Should.Throw<ArgumentException>(() => Period.OneMinute.AlignUp(local));
    }

    [Fact]
    public void Align_UnspecifiedKind_Throws() =>
        Should.Throw<ArgumentException>(() => Period.OneMinute.AlignDown(new DateTime(2026, 1, 1)));

    [Fact]
    public void AlignDown_WeekBeforeOrigin_LandsOnMonday() =>
        Period.OneWeek.AlignDown(Utc("1999-12-31T12:00:00")).ShouldBe(Utc("1999-12-27T00:00:00"));

    [Fact]
    public void Align_UninitialisedPeriod_Throws()
    {
        Should.Throw<InvalidOperationException>(() => default(Period).AlignDown(Utc("2026-01-01T00:00:00")));
        Should.Throw<InvalidOperationException>(() => default(Period).AlignUp(Utc("2026-01-01T00:00:00")));
    }
}

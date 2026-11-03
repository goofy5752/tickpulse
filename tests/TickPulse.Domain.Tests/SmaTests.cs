using static TickPulse.Domain.Tests.TestData;

namespace TickPulse.Domain.Tests;

public sealed class SmaTests
{
    [Fact]
    public void Window_NoS_EndsAtLastCompleteBucket()
    {
        var w = SmaWindowCalculator.Compute(Period.OneDay, 3, null, Utc("2026-11-03T15:00:00"));
        w.ShouldBe(new SmaWindow(Utc("2026-10-31T00:00:00"), Utc("2026-11-03T00:00:00")));
    }

    [Fact]
    public void Window_WithS_StartsAtFirstBucketAtOrAfterS()
    {
        var w = SmaWindowCalculator.Compute(Period.OneDay, 2, Utc("2026-10-01T06:00:00"), Utc("2026-11-03T15:00:00"));
        w.ShouldBe(new SmaWindow(Utc("2026-10-02T00:00:00"), Utc("2026-10-04T00:00:00")));
    }

    [Fact]
    public void Window_WithSOnBoundary_StartsAtS() =>
        SmaWindowCalculator.Compute(Period.OneDay, 1, Utc("2026-10-01T00:00:00"), Utc("2026-11-03T00:00:00"))
            .Start.ShouldBe(Utc("2026-10-01T00:00:00"));

    [Fact]
    public void Window_WithS_ClippedAtNow() =>
        SmaWindowCalculator.Compute(Period.OneDay, 10, Utc("2026-11-01T00:00:00"), Utc("2026-11-03T15:00:00"))
            .EndExclusive.ShouldBe(Utc("2026-11-03T00:00:00"));

    [Fact]
    public void Window_SInCurrentBucket_IsEmpty() =>
        SmaWindowCalculator.Compute(Period.OneDay, 5, Utc("2026-11-03T01:00:00"), Utc("2026-11-03T15:00:00"))
            .IsEmpty.ShouldBeTrue();

    [Fact]
    public void Window_Week_UsesMondayBuckets() =>
        SmaWindowCalculator.Compute(Period.OneWeek, 2, null, Utc("2026-11-05T00:00:00"))
            .ShouldBe(new SmaWindow(Utc("2026-10-19T00:00:00"), Utc("2026-11-02T00:00:00")));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Window_NonPositiveN_Throws(int n) =>
        Should.Throw<ArgumentOutOfRangeException>(
            () => SmaWindowCalculator.Compute(Period.OneDay, n, null, Utc("2026-11-03T15:00:00")));

    [Fact]
    public void Window_NonUtcNow_Throws() =>
        Should.Throw<ArgumentException>(
            () => SmaWindowCalculator.Compute(Period.OneDay, 1, null, new DateTime(2026, 11, 3, 0, 0, 0, DateTimeKind.Local)));

    [Fact]
    public void Window_NonUtcStart_Throws() =>
        Should.Throw<ArgumentException>(
            () => SmaWindowCalculator.Compute(Period.OneDay, 1, new DateTime(2026, 11, 1), Utc("2026-11-03T15:00:00")));

    [Fact]
    public void Average_Empty_IsNull() => SmaCalculator.Average([]).ShouldBeNull();

    [Fact]
    public void Average_RoundsTo8dpBankers() =>
        SmaCalculator.Average([P(0.000000015m), P(0.000000015m)]).ShouldBe(0.00000002m);

    [Fact]
    public void Average_TinyPrices_NoPrecisionLoss() =>
        SmaCalculator.Average([P(0.00001234m), P(0.00001236m)]).ShouldBe(0.00001235m);

    [Fact]
    public void Window_HugeNWithS_ClipsToLastCompleteBucket() =>
        SmaWindowCalculator.Compute(Period.OneWeek, int.MaxValue, Utc("2026-10-01T00:00:00"), Utc("2026-11-03T15:00:00"))
            .ShouldBe(new SmaWindow(Utc("2026-10-05T00:00:00"), Utc("2026-11-02T00:00:00")));

    [Fact]
    public void Window_HugeNWithoutS_Throws() =>
        Should.Throw<ArgumentOutOfRangeException>(
            () => SmaWindowCalculator.Compute(Period.OneWeek, int.MaxValue, null, Utc("2026-11-03T15:00:00")))
            .ParamName.ShouldBe("n");

    [Fact]
    public void Window_SAfterNow_IsEmpty() =>
        SmaWindowCalculator.Compute(Period.OneDay, 3, Utc("2027-01-01T00:00:00"), Utc("2026-11-03T15:00:00"))
            .IsEmpty.ShouldBeTrue();

    [Fact]
    public void Window_WeekWithSMidWeek_StartsNextMonday() =>
        SmaWindowCalculator.Compute(Period.OneWeek, 1, Utc("2026-10-07T12:00:00"), Utc("2026-11-03T15:00:00"))
            .ShouldBe(new SmaWindow(Utc("2026-10-12T00:00:00"), Utc("2026-10-19T00:00:00")));

    [Fact]
    public void Window_UninitialisedPeriod_Throws() =>
        Should.Throw<InvalidOperationException>(
            () => SmaWindowCalculator.Compute(default, 1, null, Utc("2026-11-03T15:00:00")));

    [Fact]
    public void Average_HalfwayCase_RoundsToEven() =>
        SmaCalculator.Average([P(0.000000025m)]).ShouldBe(0.00000002m);
}

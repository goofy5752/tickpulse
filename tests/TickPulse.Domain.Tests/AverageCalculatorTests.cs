namespace TickPulse.Domain.Tests;

public sealed class AverageCalculatorTests
{
    [Fact]
    public void Combine_WeightsByCount() =>
        AverageCalculator.Combine([new(300m, 3), new(10m, 1)]).ShouldBe(77.5m);

    [Fact]
    public void Combine_NoSamples_IsNull() => AverageCalculator.Combine([new(0m, 0)]).ShouldBeNull();

    [Fact]
    public void Combine_NoParts_IsNull() => AverageCalculator.Combine([]).ShouldBeNull();

    [Fact]
    public void Combine_RoundsTo8dpBankers() =>
        AverageCalculator.Combine([new(0.000000045m, 3)]).ShouldBe(0.00000002m);

    [Fact]
    public void Combine_HalfwayCase_RoundsToEven() =>
        AverageCalculator.Combine([new(0.000000025m, 1)]).ShouldBe(0.00000002m);
}

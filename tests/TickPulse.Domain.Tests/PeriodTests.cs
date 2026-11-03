using TickPulse.Domain;

namespace TickPulse.Domain.Tests;

public sealed class PeriodTests
{
    [Fact]
    public void OneMinute_Code_Is1m() => Period.OneMinute.Code.ShouldBe("1m");
}

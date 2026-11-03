using System.Reflection;

namespace TickPulse.Cli.Tests;

public sealed class EntryPointTests
{
    [Fact]
    public void Main_NoArguments_ReturnsZero()
    {
        var entryPoint = Assembly.Load("TickPulse.Cli").EntryPoint.ShouldNotBeNull();

        var exitCode = entryPoint.Invoke(null, [Array.Empty<string>()]);

        exitCode.ShouldBe(0);
    }
}

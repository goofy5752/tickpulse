using System.Reflection;
using NetArchTest.Rules;

namespace TickPulse.Architecture.Tests;

public sealed class LayeringTests
{
    private static readonly Assembly DomainAssembly = typeof(TickPulse.Domain.Period).Assembly;
    private static readonly Assembly ApplicationAssembly = Assembly.Load("TickPulse.Application");
    private static readonly Assembly CliAssembly = Assembly.Load("TickPulse.Cli");

    [Fact]
    public void Domain_HasNoDependencies() =>
        Types.InAssembly(DomainAssembly)
            .ShouldNot().HaveDependencyOnAny(
                "TickPulse.Application",
                "TickPulse.Infrastructure",
                "TickPulse.Contracts",
                "TickPulse.Api",
                "TickPulse.Cli",
                "Microsoft.EntityFrameworkCore",
                "Npgsql")
            .GetResult().IsSuccessful.ShouldBeTrue();

    [Fact]
    public void Application_DoesNotDependOnInfrastructureOrApi() =>
        Types.InAssembly(ApplicationAssembly)
            .ShouldNot().HaveDependencyOnAny(
                "TickPulse.Infrastructure",
                "TickPulse.Api",
                "TickPulse.Cli",
                "Microsoft.EntityFrameworkCore",
                "Npgsql")
            .GetResult().IsSuccessful.ShouldBeTrue();

    [Fact]
    public void Cli_DependsOnlyOnContracts() =>
        Types.InAssembly(CliAssembly)
            .ShouldNot().HaveDependencyOnAny(
                "TickPulse.Domain",
                "TickPulse.Application",
                "TickPulse.Infrastructure",
                "TickPulse.Api")
            .GetResult().IsSuccessful.ShouldBeTrue();

    [Theory]
    [InlineData("TickPulse.Domain")]
    [InlineData("TickPulse.Application", "TickPulse.Domain")]
    [InlineData("TickPulse.Cli", "TickPulse.Contracts")]
    [InlineData("TickPulse.Contracts")]
    public void ProjectReferences_StayWithinAllowedLayers(string assemblyName, params string[] allowed)
    {
        var referenced = Assembly.Load(assemblyName)
            .GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(n => n.StartsWith("TickPulse.", StringComparison.Ordinal));

        referenced.ShouldAllBe(n => allowed.Contains(n));
    }
}

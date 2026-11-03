using System.Reflection;
using System.Xml.Linq;
using NetArchTest.Rules;

namespace TickPulse.Architecture.Tests;

public sealed class LayeringTests
{
    private static readonly Assembly DomainAssembly = typeof(TickPulse.Domain.Period).Assembly;
    private static readonly Assembly ApplicationAssembly = Assembly.Load("TickPulse.Application");
    private static readonly Assembly InfrastructureAssembly = Assembly.Load("TickPulse.Infrastructure");
    private static readonly Assembly ContractsAssembly = Assembly.Load("TickPulse.Contracts");
    private static readonly Assembly ApiAssembly = Assembly.Load("TickPulse.Api");
    private static readonly Assembly CliAssembly = Assembly.Load("TickPulse.Cli");

    [Fact]
    public void Domain_HasNoDependencies() =>
        AssertNoDependency(
            DomainAssembly,
            "TickPulse.Application",
            "TickPulse.Infrastructure",
            "TickPulse.Contracts",
            "TickPulse.Api",
            "TickPulse.Cli",
            "Microsoft.EntityFrameworkCore",
            "Npgsql",
            "Microsoft.AspNetCore",
            "System.Net.Http");

    [Fact]
    public void Application_DoesNotDependOnInfrastructureOrApi() =>
        AssertNoDependency(
            ApplicationAssembly,
            "TickPulse.Infrastructure",
            "TickPulse.Contracts",
            "TickPulse.Api",
            "TickPulse.Cli",
            "Microsoft.EntityFrameworkCore",
            "Npgsql");

    [Fact]
    public void Infrastructure_DoesNotDependOnApiOrCli() =>
        AssertNoDependency(InfrastructureAssembly, "TickPulse.Api", "TickPulse.Cli");

    [Fact]
    public void Contracts_HasNoTickPulseDependencies() =>
        AssertNoDependency(
            ContractsAssembly,
            "TickPulse.Domain",
            "TickPulse.Application",
            "TickPulse.Infrastructure",
            "TickPulse.Api",
            "TickPulse.Cli");

    [Fact]
    public void Api_DoesNotDependOnCli() => AssertNoDependency(ApiAssembly, "TickPulse.Cli");

    [Fact]
    public void Cli_DependsOnlyOnContracts() =>
        AssertNoDependency(
            CliAssembly,
            "TickPulse.Domain",
            "TickPulse.Application",
            "TickPulse.Infrastructure",
            "TickPulse.Api");

    // Checks the csproj files themselves, so an unused (but forbidden) ProjectReference is caught too.
    [Theory]
    [InlineData("TickPulse.Domain")]
    [InlineData("TickPulse.Application", "TickPulse.Domain")]
    [InlineData("TickPulse.Infrastructure", "TickPulse.Application", "TickPulse.Domain")]
    [InlineData("TickPulse.Contracts")]
    [InlineData("TickPulse.Api", "TickPulse.Infrastructure", "TickPulse.Application", "TickPulse.Domain", "TickPulse.Contracts")]
    [InlineData("TickPulse.Cli", "TickPulse.Contracts")]
    public void ProjectFile_ProjectReferences_AreOnlyAllowedLayers(string project, params string[] allowed)
    {
        var csproj = Path.Combine(RepositoryRoot(), "src", project, project + ".csproj");

        var referenced = XDocument.Load(csproj)
            .Descendants("ProjectReference")
            .Select(e => Path.GetFileNameWithoutExtension(e.Attribute("Include")!.Value.Replace('\\', '/')))
            .ToList();

        referenced.ShouldAllBe(name => allowed.Contains(name));
    }

    [Fact]
    public void ProjectFile_EverySourceProject_IsCoveredByLayerRules()
    {
        var projects = Directory.GetDirectories(Path.Combine(RepositoryRoot(), "src"))
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToList();

        projects.ShouldBe(
        [
            "TickPulse.Api",
            "TickPulse.Application",
            "TickPulse.Cli",
            "TickPulse.Contracts",
            "TickPulse.Domain",
            "TickPulse.Infrastructure",
        ]);
    }

    private static void AssertNoDependency(Assembly assembly, params string[] forbidden)
    {
        var result = Types.InAssembly(assembly).ShouldNot().HaveDependencyOnAny(forbidden).GetResult();

        result.IsSuccessful.ShouldBeTrue(
            $"{assembly.GetName().Name} depends on a forbidden namespace: "
            + string.Join(", ", result.FailingTypeNames ?? []));
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TickPulse.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("TickPulse.slnx not found above the test output.");
    }
}

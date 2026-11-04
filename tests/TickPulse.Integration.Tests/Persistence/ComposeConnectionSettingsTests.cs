using System.Text.RegularExpressions;
using Npgsql;
using TickPulse.Infrastructure;
using TickPulse.Infrastructure.Persistence;

namespace TickPulse.Integration.Tests.Persistence;

/// <summary>
/// The compose files and the code agree on the connection budget: both stacks run a pool of the default size, the API's
/// connections fit the production database's tuned <c>max_connections</c> with headroom, and both give the API (and
/// the database) a stop grace period, without which Compose kills a container one second after SIGTERM.
/// </summary>
public sealed partial class ComposeConnectionSettingsTests
{
    private static readonly string Root = FindRoot();

    [Theory]
    [InlineData("docker-compose.yml")]
    [InlineData("deploy/docker-compose.prod.yml")]
    public void ApiConnectionString_SetsTheDefaultPoolSize(string file)
    {
        var connectionString = ApiConnectionString(file);

        // Compose placeholders (${VAR:-x}, ${VAR:?msg}) are not part of the pool setting.
        new NpgsqlConnectionStringBuilder(Placeholder().Replace(connectionString, "x")).MaxPoolSize
            .ShouldBe(NpgsqlConnectionPolicy.DefaultMaxPoolSize);
    }

    [Fact]
    public void ConnectionBudget_FitsTheProductionDatabase()
    {
        var maxConnections = int.Parse(
            TsTuneMaxConns().Match(Read("deploy/docker-compose.prod.yml")).Groups[1].Value,
            System.Globalization.CultureInfo.InvariantCulture);
        const int superuserReserved = 3; // PostgreSQL's default superuser_reserved_connections

        var budget = NpgsqlConnectionPolicy.ConnectionBudget(NpgsqlConnectionPolicy.DefaultMaxPoolSize, DependencyInjection.TickWriterMaxPoolSize);

        budget.ShouldBeLessThanOrEqualTo(maxConnections - superuserReserved - NpgsqlConnectionPolicy.ConnectionHeadroom);
    }

    [Theory]
    [InlineData("docker-compose.yml", "api")]
    [InlineData("docker-compose.yml", "db")]
    [InlineData("deploy/docker-compose.prod.yml", "api")]
    [InlineData("deploy/docker-compose.prod.yml", "db")]
    public void Service_HasAStopGracePeriodOf30Seconds(string file, string service)
    {
        ServiceBlock(file, service).ShouldContain("stop_grace_period: 30s");
    }

    private static string ApiConnectionString(string file)
    {
        var match = ConnectionStringLine().Match(ServiceBlock(file, "api"));
        match.Success.ShouldBeTrue($"{file}: api has no ConnectionStrings__TickPulse");
        return match.Groups[1].Value;
    }

    // The lines from "  <service>:" to the next key at the same or a lower indentation.
    private static string ServiceBlock(string file, string service)
    {
        var lines = Read(file).Split('\n');
        var start = Array.FindIndex(lines, l => l.TrimEnd() == $"  {service}:");
        start.ShouldBeGreaterThanOrEqualTo(0, $"{file}: no service {service}");
        var end = Array.FindIndex(lines, start + 1, l =>
            l.TrimEnd().Length > 0 && !l.TrimStart().StartsWith('#') && l.Length - l.TrimStart().Length <= 2);
        return string.Join('\n', lines[start..(end < 0 ? lines.Length : end)]);
    }

    private static string Read(string file) => File.ReadAllText(Path.Combine(Root, file));

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "TickPulse.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("TickPulse.slnx not found above the test output directory.");
    }

    [GeneratedRegex("""ConnectionStrings__TickPulse:\s*"([^"]+)"\s*$""", RegexOptions.Multiline)]
    private static partial Regex ConnectionStringLine();

    [GeneratedRegex("""TS_TUNE_MAX_CONNS:\s*"(\d+)"\s*$""", RegexOptions.Multiline)]
    private static partial Regex TsTuneMaxConns();

    [GeneratedRegex(@"\$\{[^}]*\}")]
    private static partial Regex Placeholder();
}

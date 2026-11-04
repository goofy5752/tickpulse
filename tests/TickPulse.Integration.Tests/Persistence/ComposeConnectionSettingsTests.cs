using System.Globalization;
using System.Text.RegularExpressions;
using Npgsql;
using TickPulse.Infrastructure;
using TickPulse.Infrastructure.Persistence;
using TickPulse.Integration.Tests.Fixtures;

namespace TickPulse.Integration.Tests.Persistence;

/// <summary>
/// The compose files (parsed as YAML) and the code agree on the connection budget: both stacks run a pool of the
/// default size, the API's connections fit the production database's tuned <c>max_connections</c> with headroom, and
/// the api and the database get a stop grace period, without which Compose kills a container one second after SIGTERM.
/// </summary>
public sealed partial class ComposeConnectionSettingsTests
{
    private const int SuperuserReserved = 3; // PostgreSQL's default superuser_reserved_connections

    public static TheoryData<string> Files => [ComposeFile.Local, ComposeFile.Production];

    [Theory]
    [MemberData(nameof(Files))]
    public void ApiConnectionString_SetsTheDefaultPoolSize(string file)
    {
        var connectionString = ComposeFile.Load(file).Environment("api", "ConnectionStrings__TickPulse");

        connectionString.ShouldNotBeNull($"{file}: api has no ConnectionStrings__TickPulse");
        // Compose placeholders (${VAR:-x}, ${VAR:?msg}) are not part of the pool setting.
        new NpgsqlConnectionStringBuilder(Placeholder().Replace(connectionString, "x")).MaxPoolSize
            .ShouldBe(NpgsqlConnectionPolicy.DefaultMaxPoolSize);
    }

    [Fact]
    public void ConnectionBudget_FitsTheProductionDatabase()
    {
        var maxConnections = int.Parse(
            ComposeFile.Load(ComposeFile.Production).Environment("db", "TS_TUNE_MAX_CONNS")!,
            CultureInfo.InvariantCulture);

        var budget = NpgsqlConnectionPolicy.ConnectionBudget(NpgsqlConnectionPolicy.DefaultMaxPoolSize, DependencyInjection.TickWriterMaxPoolSize);

        budget.ShouldBeLessThanOrEqualTo(maxConnections - SuperuserReserved - NpgsqlConnectionPolicy.ConnectionHeadroom);
    }

    [Theory]
    [InlineData(ComposeFile.Local, "api")]
    [InlineData(ComposeFile.Local, "db")]
    [InlineData(ComposeFile.Production, "api")]
    [InlineData(ComposeFile.Production, "db")]
    public void Service_HasAStopGracePeriodOf30Seconds(string file, string service)
    {
        ComposeFile.Load(file).StopGracePeriod(service).ShouldBe(TimeSpan.FromSeconds(30));
    }

    [Theory]
    [InlineData("30s", 30_000)]
    [InlineData("1m30s", 90_000)]
    [InlineData("500ms", 500)]
    [InlineData("1h", 3_600_000)]
    public void ParseDuration_ReadsComposeDurations(string value, int milliseconds)
    {
        ComposeFile.ParseDuration(value).ShouldBe(TimeSpan.FromMilliseconds(milliseconds));
    }

    [Theory]
    [InlineData("30")]
    [InlineData("30 s")]
    [InlineData("soon")]
    public void ParseDuration_RejectsOtherText(string value)
    {
        Should.Throw<FormatException>(() => ComposeFile.ParseDuration(value));
    }

    [GeneratedRegex(@"\$\{[^}]*\}")]
    private static partial Regex Placeholder();
}

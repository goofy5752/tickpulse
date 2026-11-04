using System.Globalization;
using System.Text.RegularExpressions;
using TickPulse.Api.Hosting;
using TickPulse.Infrastructure.Persistence;
using TickPulse.Integration.Tests.Fixtures;

namespace TickPulse.Integration.Tests.Api;

/// <summary>
/// <c>deploy/deploy.sh</c> waits <c>READY_TIMEOUT</c> seconds for <c>/health/ready</c> after starting a tag, and the
/// api applies its migrations before it serves anything, retrying for up to <see cref="MigrationRetryOptions.Timeout"/>.
/// A start that is still within its migration budget must not be declared failed and rolled back.
/// </summary>
public sealed partial class DeployReadyTimeoutTests
{
    // Besides the migrations: the container and process start, the connection-budget check, and one probe interval
    // (3 s sleep plus a 5 s curl) before the first 200 is seen.
    private static readonly TimeSpan StartupMargin = TimeSpan.FromSeconds(30) + NpgsqlConnectionPolicy.BudgetCheckTimeout;

    [Fact]
    public void ReadyTimeout_ExceedsTheMigrationTimeout()
    {
        var script = File.ReadAllText(Path.Combine(ComposeFile.Root, "deploy", "deploy.sh"));
        var match = ReadyTimeoutDefault().Match(script);
        match.Success.ShouldBeTrue("deploy/deploy.sh: no READY_TIMEOUT default (readonly READY_TIMEOUT=\"${READY_TIMEOUT:-<seconds>}\")");

        var ready = TimeSpan.FromSeconds(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));

        ready.ShouldBeGreaterThan(MigrationRetryOptions.Default.Timeout + StartupMargin);
    }

    [GeneratedRegex("""^readonly READY_TIMEOUT="\$\{READY_TIMEOUT:-(\d+)\}"$""", RegexOptions.Multiline)]
    private static partial Regex ReadyTimeoutDefault();
}

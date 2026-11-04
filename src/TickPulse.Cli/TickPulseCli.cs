using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using Spectre.Console;
using TickPulse.Cli.Commands;
using TickPulse.Cli.Rendering;

namespace TickPulse.Cli;

/// <summary>The <c>tickpulse</c> command tree and its invocation.</summary>
internal static class TickPulseCli
{
    /// <summary>
    /// The command tree. The root is a plain <see cref="Command"/> named <c>tickpulse</c> (a <see cref="RootCommand"/>
    /// takes its name from the executable, which is <c>TickPulse.Cli</c> under <c>dotnet run</c> and the tool shim),
    /// with the help and version options a root command would have.
    /// </summary>
    public static Command Build(CliEnvironment environment)
    {
        var options = new GlobalOptions(environment);
        var avg = AnalyticsCommands.Average(environment, options);
        var sma = AnalyticsCommands.SimpleMovingAverage(environment, options);
        var watch = WatchCommand.Create(environment, options);
        var symbols = SymbolsCommand.Create(environment, options);
        var help = new HelpOption { Recursive = true };
        var root = new Command(
            CliOptions.RootName,
            "TickPulse console client: 24h average prices, simple moving averages, live prices and symbol "
            + "administration, from the TickPulse HTTP API.")
        {
            options.ApiUrl,
            options.Format,
            help,
            new VersionOption(),
            avg,
            sma,
            watch,
            symbols,
        };

        Command Sub(string name) => symbols.Subcommands.Single(c => c.Name == name);
        var examples = new Dictionary<Command, string[]>
        {
            [root] =
            [
                "tickpulse avg BTCUSDT",
                "tickpulse sma BTCUSDT -n 20 -p 1d",
                "tickpulse watch ETHUSDT -n 30 -p 1m",
                "tickpulse symbols add BNBUSDT --api-key <key>",
                "tickpulse avg BTCUSDT --format json --api-url http://localhost:8080",
            ],
            [avg] = ["tickpulse avg BTCUSDT", "tickpulse avg ethusdt --format xml"],
            [sma] =
            [
                "tickpulse sma BTCUSDT -n 20 -p 1d",
                "tickpulse sma ETHUSDT -n 12 -p 5m -s 2026-10-01T09:00:00Z",
                "tickpulse sma ADAUSDT --points 4 --period 1w --format json",
            ],
            [watch] = ["tickpulse watch BTCUSDT", "tickpulse watch ETHUSDT -n 30 -p 1m --interval 5s"],
            [symbols] = ["tickpulse symbols", "tickpulse symbols list --api-key <key>"],
            [Sub("list")] = ["tickpulse symbols list", "TICKPULSE_API_KEY=<key> tickpulse symbols ls"],
            [Sub("add")] = ["tickpulse symbols add BNBUSDT --api-key <key>"],
            [Sub("remove")] = ["tickpulse symbols remove BNBUSDT --api-key <key>"],
        };
        help.Action = new ExamplesHelpAction((HelpAction)help.Action!, examples);

        root.SetAction(parse =>
        {
            environment.Out.Write(WelcomeView.Render(CliOptions.TryParseApiUrl(parse.GetValue(options.ApiUrl))!, environment.Out.Profile.Width));
            environment.Out.WriteLine();
            return ExitCodes.Ok;
        });
        return root;
    }

    /// <summary>Parses and runs <paramref name="args"/>. Invalid command lines print their errors and return 64.</summary>
    public static async Task<int> RunAsync(string[] args, CliEnvironment environment, CancellationToken ct)
    {
        var parse = Build(environment).Parse(args);
        if (parse.Action is ParseErrorAction)
        {
            ProblemView.RenderUsage(environment.Error, parse.Errors.Select(e => e.Message).Distinct(StringComparer.Ordinal), CommandPath(parse.CommandResult));
            return ExitCodes.Usage;
        }

        var invocation = new InvocationConfiguration
        {
            Output = environment.HelpOutput,
            Error = environment.HelpOutput,
            EnableDefaultExceptionHandler = false,
        };
        return await parse.InvokeAsync(invocation, ct).ConfigureAwait(false);
    }

    /// <summary><c>tickpulse symbols add</c> for the command that failed to parse.</summary>
    private static string CommandPath(CommandResult result)
    {
        var names = new List<string>();
        for (SymbolResult? current = result; current is CommandResult command; current = current.Parent)
        {
            names.Add(command.Parent is null ? CliOptions.RootName : command.Command.Name);
        }

        names.Reverse();
        return string.Join(' ', names);
    }
}

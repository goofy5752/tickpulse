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
            + "administration, from the TickPulse HTTP API.\n\n"
            + "Exit codes:\n"
            + "  0    success\n"
            + "  1    the API rejected the request (4xx: unknown symbol, validation, bad API key, ...)\n"
            + "  2    the API is unreachable, timed out, failed (5xx) or sent an unreadable body\n"
            + "  64   invalid command line; nothing was sent\n"
            + "  70   internal error in tickpulse itself\n"
            + "  130  interrupted with Ctrl+C (watch exits with 0 instead)")
        {
            options.ApiUrl,
            options.Format,
            options.ApiKey,
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
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(environment);

        var parse = Build(environment).Parse(args);
        var secrets = SecretValues(args, environment, parse);
        if (parse.Action is ParseErrorAction)
        {
            ProblemView.RenderUsage(
                environment.Error,
                parse.Errors.Select(e => Redact(e.Message, secrets)).Distinct(StringComparer.Ordinal),
                CommandPath(parse.CommandResult));
            return ExitCodes.Usage;
        }

        // The default may come from TICKPULSE_API_URL, which no parser validator sees: check the value in effect.
        // --help and --version need no API, so a bad URL must not stop them.
        if (!IsInformational(parse)
            && CliOptions.ValidateApiUrl(parse.GetValue<string>("--api-url"), out _) is { } urlError)
        {
            ProblemView.RenderUsage(environment.Error, [Redact(urlError, secrets)], CommandPath(parse.CommandResult));
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

    private static bool IsInformational(ParseResult parse) =>
        parse.Action is ExamplesHelpAction
        || parse.RootCommandResult.Children.OfType<OptionResult>().Any(r => r.Option is VersionOption);

    /// <summary>
    /// Everything on the command line that may be an API key, so no usage error prints one, even when the parser
    /// quotes a token back ("Unrecognized argument '…'", "Cannot parse argument '…'"). Deliberately broad:
    /// <list type="bullet">
    /// <item>the token after any option-like token (<c>-</c> or <c>/</c>) whose name contains <c>key</c> in any case,
    /// such as <c>--api-key</c>, <c>--API-KEY</c> or <c>--apikey</c>, and the token after <c>-k</c>;</item>
    /// <item>such a token with an attached value (<c>--api-key=…</c>, <c>-k:…</c>), and any <c>-k…</c> token, whole
    /// and without its prefix;</item>
    /// <item>as a final net, the key in effect: the parsed <c>--api-key</c> and <c>TICKPULSE_API_KEY</c>.</item>
    /// </list>
    /// Longest first, so a secret that contains another is replaced whole.
    /// </summary>
    private static List<string> SecretValues(string[] args, CliEnvironment environment, ParseResult parse)
    {
        var secrets = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            var isKeyOption = IsKeyOption(arg, out var attached);
            if (arg == "-k" || (isKeyOption && attached is null))
            {
                if (i + 1 < args.Length)
                {
                    secrets.Add(args[i + 1]);
                }
            }
            else if (arg.StartsWith("-k", StringComparison.Ordinal))
            {
                secrets.Add(arg);
                secrets.Add(arg[2..].TrimStart('=', ':'));
            }
            else if (isKeyOption)
            {
                secrets.Add(arg);
                secrets.Add(attached!);
            }
        }

        secrets.Add(environment.GetEnvironmentVariable(CliEnvironment.ApiKeyVariable) ?? string.Empty);
        try
        {
            secrets.Add(parse.GetValue<string>("--api-key") ?? string.Empty);
        }
        catch (InvalidOperationException)
        {
            // The option itself failed to parse; its tokens are covered above.
        }

        return [.. secrets.Where(s => s.Trim().Length > 0).Distinct(StringComparer.Ordinal).OrderByDescending(s => s.Length)];
    }

    /// <summary>
    /// Whether <paramref name="arg"/> looks like an option (<c>-</c>, <c>--</c> or <c>/</c>) whose name contains
    /// <c>key</c>; <paramref name="attached"/> is the value after <c>=</c> or <c>:</c>, if any.
    /// </summary>
    private static bool IsKeyOption(string arg, out string? attached)
    {
        attached = null;
        if (arg.Length < 2 || (arg[0] != '-' && arg[0] != '/'))
        {
            return false;
        }

        var body = arg.TrimStart('-', '/');
        var separator = body.IndexOfAny(['=', ':']);
        var name = separator < 0 ? body : body[..separator];
        if (!name.Contains("key", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        attached = separator < 0 ? null : body[(separator + 1)..];
        return true;
    }

    private static string Redact(string message, List<string> secrets) =>
        secrets.Aggregate(message, (text, secret) => text.Replace(secret, "***", StringComparison.Ordinal));

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

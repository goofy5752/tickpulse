using System.CommandLine;
using Spectre.Console;
using TickPulse.Cli.Rendering;

namespace TickPulse.Cli.Commands;

/// <summary><c>symbols list|add|remove</c>: the public list, and symbol management with the admin API key.</summary>
internal static class SymbolsCommand
{
    private const string Path = CliOptions.RootName + " symbols";

    public static Command Create(CliEnvironment environment, GlobalOptions options)
    {
        var apiKey = CliOptions.ApiKey();
        var list = List(environment, options, apiKey);
        var command = new Command(
            "symbols",
            "List the monitored symbols (the default), or add and remove them with the admin API key.")
        {
            apiKey,
            list,
            Add(environment, options, apiKey),
            Remove(environment, options, apiKey),
        };
        command.Action = list.Action;
        return command;
    }

    private static Command List(CliEnvironment environment, GlobalOptions options, Option<string> apiKey)
    {
        var command = new Command(
            "list",
            "List the symbols with their latest price and 24h change. With an admin key, the admin view: "
            + "when each was added or deactivated, and its history backfill.");
        command.Aliases.Add("ls");
        command.SetAction((parse, ct) => CommandSession.RunAsync(
            environment,
            parse,
            options,
            async (session, token) => ResolveKey(environment, parse, apiKey) is { } key
                ? session.Complete(await session.Client.ListAdminSymbolsAsync(key, session.Format, token).ConfigureAwait(false), SymbolViews.AdminList)
                : session.Complete(await session.Client.ListSymbolsAsync(session.Format, token).ConfigureAwait(false), SymbolViews.PublicList),
            ct));
        return command;
    }

    private static Command Add(CliEnvironment environment, GlobalOptions options, Option<string> apiKey)
    {
        var symbol = CliOptions.Symbol("The Binance spot symbol to start monitoring, e.g. BNBUSDT.");
        var command = new Command(
            "add",
            "Start monitoring a symbol (admin). It must trade on the Binance spot market; a removed symbol is reactivated "
            + "with its history.")
        {
            symbol,
        };
        command.SetAction((parse, ct) => CommandSession.RunAsync(
            environment,
            parse,
            options,
            async (session, token) =>
            {
                if (ResolveKey(environment, parse, apiKey) is not { } key)
                {
                    return session.Usage(MissingKey("add"), Path + " add");
                }

                var response = await session.Client.AddSymbolAsync(parse.GetValue(symbol)!, key, session.Format, token).ConfigureAwait(false);
                return session.Complete(response, added => SymbolViews.Added(added, created: response.Status == 201));
            },
            ct));
        return command;
    }

    private static Command Remove(CliEnvironment environment, GlobalOptions options, Option<string> apiKey)
    {
        var symbol = CliOptions.Symbol("The symbol to stop monitoring; its history is kept.");
        var command = new Command("remove", "Stop monitoring a symbol (admin). Its stored history stays queryable.")
        {
            symbol,
        };
        command.Aliases.Add("rm");
        command.SetAction((parse, ct) => CommandSession.RunAsync(
            environment,
            parse,
            options,
            async (session, token) =>
            {
                if (ResolveKey(environment, parse, apiKey) is not { } key)
                {
                    return session.Usage(MissingKey("remove"), Path + " remove");
                }

                var name = parse.GetValue(symbol)!;
                var response = await session.Client.RemoveSymbolAsync(name, key, session.Format, token).ConfigureAwait(false);
                return session.CompleteNoContent(response, SymbolViews.Removed(name));
            },
            ct));
        return command;
    }

    /// <summary><c>--api-key</c>, else <c>TICKPULSE_API_KEY</c>; blank counts as none.</summary>
    private static string? ResolveKey(CliEnvironment environment, ParseResult parse, Option<string> apiKey)
    {
        var key = parse.GetValue(apiKey);
        if (string.IsNullOrWhiteSpace(key))
        {
            key = environment.GetEnvironmentVariable(CliEnvironment.ApiKeyVariable);
        }

        return string.IsNullOrWhiteSpace(key) ? null : key.Trim();
    }

    private static string MissingKey(string verb) =>
        $"'symbols {verb}' needs the admin API key: pass --api-key <key> or set {CliEnvironment.ApiKeyVariable}.";
}

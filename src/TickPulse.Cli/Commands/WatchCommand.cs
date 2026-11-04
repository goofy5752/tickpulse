using System.CommandLine;
using System.Globalization;
using Spectre.Console;
using TickPulse.Cli.Rendering;
using TickPulse.Contracts;

namespace TickPulse.Cli.Commands;

/// <summary>
/// <c>watch</c>: polls <c>GET /api/symbols</c> (and, with <c>-n</c>/<c>-p</c>, the SMA) every <c>--interval</c> and
/// redraws a live panel. A failed poll is shown and retried; Ctrl+C ends it with exit code 0.
/// </summary>
internal static class WatchCommand
{
    private const string Path = CliOptions.RootName + " watch";
    private const int LogLineWidth = 1_000;

    public static Command Create(CliEnvironment environment, GlobalOptions options)
    {
        var symbol = CliOptions.Symbol();
        var interval = CliOptions.Interval();
        var points = CliOptions.Points(required: false);
        var period = CliOptions.Period(required: false);
        var command = new Command(
            "watch",
            "Live price, direction, 24h change and sparkline of a symbol, optionally with an SMA (-n, -p). Ctrl+C to exit.")
        {
            symbol,
            interval,
            points,
            period,
        };
        command.Validators.Add(result =>
        {
            if ((result.GetResult(points) is null) != (result.GetResult(period) is null))
            {
                result.AddError("To watch an SMA, give both --points and --period.");
            }
        });

        command.SetAction((parse, ct) => CommandSession.RunAsync(
            environment,
            parse,
            options,
            (session, token) =>
            {
                if (!session.IsTable)
                {
                    return Task.FromResult(session.Usage("watch draws a live view; --format json|xml is not available for it.", Path));
                }

                var sma = parse.GetResult(points) is null ? null : new SmaSpec(parse.GetValue(points), parse.GetValue(period)!);
                return WatchAsync(session, parse.GetValue(symbol)!, parse.GetValue(interval), sma, token);
            },
            ct));
        return command;
    }

    private static async Task<int> WatchAsync(CommandSession session, string symbol, TimeSpan interval, SmaSpec? sma, CancellationToken ct)
    {
        var environment = session.Environment;
        var state = new WatchState(symbol, interval, sma?.Label);
        try
        {
            // The first poll decides whether there is anything to watch: a problem or an unknown symbol ends here.
            var first = await session.Client.ListSymbolsAsync(OutputFormat.Table, ct).ConfigureAwait(false);
            if (first.Problem is { } problem)
            {
                ProblemView.Render(environment.Error, problem, compact: false);
                return ExitCodes.ForStatus(problem.Status);
            }

            if (Find(first.Body, symbol) is not { } row)
            {
                ProblemView.Render(environment.Error, NotListed(symbol), compact: false);
                return ExitCodes.ApiError;
            }

            state.Update(row, environment.Time.GetUtcNow());
            await PollSmaAsync(session, state, symbol, sma, ct).ConfigureAwait(false);

            if (environment.IsOutputRedirected)
            {
                // A file or pipe cannot be redrawn (and has no cursor to hide): log one line per poll instead,
                // unwrapped, since a redirected console otherwise wraps at 80 columns.
                environment.Out.Profile.Width = LogLineWidth;
                while (true)
                {
                    environment.Out.MarkupLine(WatchView.Line(state));
                    await Task.Delay(interval, environment.Time, ct).ConfigureAwait(false);
                    await PollAsync(session, state, symbol, sma, ct).ConfigureAwait(false);
                }
            }

            await environment.Out.Live(WatchView.Render(state))
                .AutoClear(false)
                .Overflow(VerticalOverflow.Ellipsis)
                .StartAsync(async live =>
                {
                    while (true)
                    {
                        await Task.Delay(interval, environment.Time, ct).ConfigureAwait(false);
                        await PollAsync(session, state, symbol, sma, ct).ConfigureAwait(false);
                        live.UpdateTarget(WatchView.Render(state));
                        live.Refresh();
                    }
                }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Ctrl+C is how a watch ends.
        }

        environment.Error.MarkupLine($"[{Theme.Muted}]Stopped watching {Markup.Escape(symbol)}.[/]");
        return ExitCodes.Ok;
    }

    private static async Task PollAsync(CommandSession session, WatchState state, string symbol, SmaSpec? sma, CancellationToken ct)
    {
        try
        {
            var response = await session.Client.ListSymbolsAsync(OutputFormat.Table, ct).ConfigureAwait(false);
            if (response.Problem is { } problem)
            {
                state.Error = Describe(problem);
                return;
            }

            if (Find(response.Body, symbol) is not { } row)
            {
                state.Error = $"{symbol} is no longer listed by the API.";
                return;
            }

            state.Update(row, session.Environment.Time.GetUtcNow());
            await PollSmaAsync(session, state, symbol, sma, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (IsTransient(e, ct))
        {
            state.Error = $"Connection lost ({e.Message}); retrying.";
        }
    }

    private static async Task PollSmaAsync(CommandSession session, WatchState state, string symbol, SmaSpec? sma, CancellationToken ct)
    {
        if (sma is null)
        {
            return;
        }

        try
        {
            var response = await session.Client.GetSmaAsync(symbol, sma.Points, sma.Period, s: null, OutputFormat.Table, ct).ConfigureAwait(false);
            if (response.Problem is { } problem)
            {
                state.Error = $"SMA: {Describe(problem)}";
                return;
            }

            state.Sma = response.Body;
        }
        catch (Exception e) when (IsTransient(e, ct))
        {
            state.Error = $"SMA unavailable ({e.Message}); retrying.";
        }
    }

    private static bool IsTransient(Exception e, CancellationToken ct) =>
        e is HttpRequestException or UnexpectedResponseException
        || (e is TaskCanceledException && !ct.IsCancellationRequested);

    private static SymbolResponse? Find(SymbolListResponse? symbols, string symbol) =>
        symbols?.FirstOrDefault(s => string.Equals(s.Symbol, symbol, StringComparison.OrdinalIgnoreCase));

    private static string Describe(ApiProblem problem) =>
        string.IsNullOrWhiteSpace(problem.Detail) ? $"{problem.Title} ({problem.Status})" : $"{problem.Title}: {problem.Detail}";

    private static ApiProblem NotListed(string symbol) => new(
        ApiProblem.TypeBase + "unknown-symbol",
        "Unknown symbol",
        404,
        $"{symbol} is not monitored by this TickPulse server.",
        TraceId: null,
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal));

    private sealed record SmaSpec(int Points, string Period)
    {
        public string Label => $"SMA({Points.ToString(CultureInfo.InvariantCulture)} × {Period})";
    }
}

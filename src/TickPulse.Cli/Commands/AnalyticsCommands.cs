using System.CommandLine;
using TickPulse.Cli.Rendering;

namespace TickPulse.Cli.Commands;

/// <summary><c>avg</c> and <c>sma</c>: the two public analytics.</summary>
internal static class AnalyticsCommands
{
    public static Command Average(CliEnvironment environment, GlobalOptions options)
    {
        var symbol = CliOptions.Symbol();
        var command = new Command(
            "avg",
            "Mean price of the trades collected for a symbol in the last 24 hours, and how much of the window they cover.")
        {
            symbol,
        };
        command.Aliases.Add("average");

        command.SetAction((parse, ct) => CommandSession.RunAsync(
            environment,
            parse,
            options,
            async (session, token) =>
            {
                var response = await session.Client.GetAverageAsync(parse.GetValue(symbol)!, session.Format, token).ConfigureAwait(false);
                return session.Complete(response, AnalyticsViews.Average);
            },
            ct));
        return command;
    }

    public static Command SimpleMovingAverage(CliEnvironment environment, GlobalOptions options)
    {
        var symbol = CliOptions.Symbol();
        var points = CliOptions.Points(required: true);
        var period = CliOptions.Period(required: true);
        var start = CliOptions.Start();
        var command = new Command(
            "sma",
            "Simple moving average of the closes of N complete buckets (the latest, or the first from --start), as a table and a chart.")
        {
            symbol,
            points,
            period,
            start,
        };

        command.SetAction((parse, ct) => CommandSession.RunAsync(
            environment,
            parse,
            options,
            async (session, token) =>
            {
                var response = await session.Client.GetSmaAsync(
                    parse.GetValue(symbol)!,
                    parse.GetValue(points),
                    parse.GetValue(period)!,
                    parse.GetValue(start),
                    session.Format,
                    token).ConfigureAwait(false);
                return session.Complete(response, AnalyticsViews.Sma);
            },
            ct));
        return command;
    }
}

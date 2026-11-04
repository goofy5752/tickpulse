using Spectre.Console;
using Spectre.Console.Rendering;

namespace TickPulse.Cli.Rendering;

/// <summary>What <c>tickpulse</c> prints without a command: what it does and the commands worth trying first.</summary>
internal static class WelcomeView
{
    /// <summary>Below this width the examples are stacked (command, then description) instead of side by side.</summary>
    private const int TwoColumnWidth = 100;

    private static readonly (string Command, string What)[] Examples =
    [
        ("tickpulse avg BTCUSDT", "mean trade price of the last 24 hours"),
        ("tickpulse sma BTCUSDT -n 20 -p 1d", "simple moving average of the last 20 daily closes"),
        ("tickpulse sma ETHUSDT -n 12 -p 5m -s 2026-10-01", "… of the first 12 five-minute buckets from a start"),
        ("tickpulse watch BTCUSDT -n 30 -p 1m", "live price, 24h change and SMA, refreshed every 2s"),
        ("tickpulse symbols", "the monitored symbols with latest prices"),
        ("tickpulse symbols add BNBUSDT --api-key <key>", "start monitoring a symbol (admin)"),
        ("tickpulse avg BTCUSDT --format json", "the API's raw JSON (or xml) instead of tables"),
    ];

    public static IRenderable Render(Uri apiUrl, int width)
    {
        IRenderable examples;
        if (width >= TwoColumnWidth)
        {
            var grid = new Grid().AddColumn(new GridColumn().NoWrap().PadRight(4)).AddColumn(new GridColumn());
            foreach (var (command, what) in Examples)
            {
                grid.AddRow(new Markup($"  [bold]{Markup.Escape(command)}[/]"), new Markup($"[{Theme.Muted}]{Markup.Escape(what)}[/]"));
            }

            examples = grid;
        }
        else
        {
            examples = new Rows(Examples.Select(e =>
                new Markup($"  [bold]{Markup.Escape(e.Command)}[/]\n    [{Theme.Muted}]{Markup.Escape(e.What)}[/]")));
        }

        return new Rows(
            new Rule($"[bold {Theme.Accent}]TickPulse[/] [{Theme.Muted}]{Markup.Escape(CliEnvironment.Version)}[/]").LeftJustified().RuleStyle(Theme.Muted),
            new Markup("Binance spot-trade analytics in your terminal: 24h averages, moving averages and live prices."),
            Text.Empty,
            examples,
            Text.Empty,
            new Markup($"[{Theme.Muted}]API[/]   {Markup.Escape(apiUrl.ToString().TrimEnd('/'))} [{Theme.Muted}](--api-url or {CliEnvironment.ApiUrlVariable})[/]"),
            new Markup($"[{Theme.Muted}]Help[/]  tickpulse <command> --help"));
    }
}

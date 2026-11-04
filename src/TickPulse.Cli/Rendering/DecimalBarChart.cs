using System.Text;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace TickPulse.Cli.Rendering;

/// <summary>
/// A horizontal bar chart computed in <see cref="decimal"/> with eighth-block resolution. Spectre's own
/// <c>BarChart</c> takes <see cref="double"/> values and starts every bar at zero, so closes of 67,100 and 67,300 would
/// look identical; here the axis starts just below the lowest close and the exact close is printed next to each bar.
/// </summary>
internal static class DecimalBarChart
{
    private const int BarWidth = 40;
    private static readonly string[] Partials = ["", "▏", "▎", "▍", "▌", "▋", "▊", "▉"];

    /// <param name="bars">Label, value and bar colour of each row, top to bottom.</param>
    /// <param name="axisStart">The value at the left edge; see <see cref="AxisStart"/>.</param>
    public static IRenderable Render(IReadOnlyList<(string Label, decimal Value, string Color)> bars, decimal axisStart)
    {
        var max = bars.Count == 0 ? 0m : bars.Max(b => b.Value);
        var grid = new Grid()
            .AddColumn(new GridColumn().NoWrap().PadRight(1))
            .AddColumn(new GridColumn().NoWrap().PadRight(1))
            .AddColumn(new GridColumn().NoWrap().RightAligned());

        foreach (var (label, value, color) in bars)
        {
            grid.AddRow(
                new Markup($"[{Theme.Muted}]{Markup.Escape(label)}[/]"),
                new Markup($"[{color}]{Bar(value, axisStart, max)}[/]"),
                new Markup(NumberFormat.Price(value)));
        }

        return grid;
    }

    /// <summary>
    /// Just below the lowest value (by a tenth of the range), so the shortest bar is still visible and differences
    /// are not flattened by a zero baseline. Never below zero for non-negative data.
    /// </summary>
    public static decimal AxisStart(IEnumerable<decimal> values)
    {
        var list = values.ToList();
        if (list.Count == 0)
        {
            return 0m;
        }

        var (min, max) = (list.Min(), list.Max());
        var start = min == max ? min - (Math.Abs(min) / 2m) : min - ((max - min) / 10m);
        return min >= 0m ? Math.Max(0m, start) : start;
    }

    private static string Bar(decimal value, decimal axisStart, decimal max)
    {
        var span = max - axisStart;
        var fraction = span <= 0m ? 1m : Math.Clamp((value - axisStart) / span, 0m, 1m);
        var eighths = (int)Math.Round(fraction * BarWidth * 8m, MidpointRounding.AwayFromZero);
        var bar = new StringBuilder(BarWidth + 1).Append('█', eighths / 8).Append(Partials[eighths % 8]);
        return bar.Length == 0 ? "▏" : bar.ToString();
    }
}

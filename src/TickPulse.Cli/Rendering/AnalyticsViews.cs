using System.Globalization;
using Spectre.Console;
using Spectre.Console.Rendering;
using TickPulse.Contracts;

namespace TickPulse.Cli.Rendering;

/// <summary>Table output of the two analytics endpoints.</summary>
internal static class AnalyticsViews
{
    /// <summary>The most bars the SMA chart draws; longer series show their most recent buckets.</summary>
    public const int MaxChartBars = 60;

    private const int CoverageCells = 24;

    /// <summary>The 24 h average: the price, the trades behind it, and how much of the window they cover.</summary>
    public static IRenderable Average(AveragePriceResponse r)
    {
        var windowSeconds = r.WindowHours * 3_600L;
        var coverage = windowSeconds <= 0 ? 0m : Math.Clamp((decimal)r.CoveredSeconds / windowSeconds, 0m, 1m);

        var facts = Theme.Facts()
            .AddFact("Trades", NumberFormat.Count(r.SampleCount))
            .AddFact("First", Markup.Escape(NumberFormat.Timestamp(r.From)))
            .AddFact("Last", Markup.Escape(NumberFormat.Timestamp(r.To)))
            .AddFact(
                "Coverage",
                $"{CoverageBar(coverage)} {NumberFormat.Duration(r.CoveredSeconds)} of {r.WindowHours.ToString(CultureInfo.InvariantCulture)}h "
                + $"[{Theme.Muted}]({FloorPercent(coverage)})[/]");

        var rows = new List<IRenderable>
        {
            new Markup($"[bold {Theme.Accent}]{NumberFormat.Price(r.AveragePrice)}[/]  [{Theme.Muted}]mean trade price[/]"),
            Text.Empty,
            facts,
        };
        if (coverage < 0.99m)
        {
            rows.Add(Text.Empty);
            rows.Add(new Markup($"[{Theme.Muted}]Trades have been collected for part of the window only; the mean covers what is stored.[/]"));
        }

        return Theme.Panel(new Rows(rows), $"{Markup.Escape(r.Symbol)} [{Theme.Muted}]·[/] {r.WindowHours.ToString(CultureInfo.InvariantCulture)}h average");
    }

    /// <summary>The SMA: its value and window, the closes behind it, and a chart of those closes against it.</summary>
    public static IRenderable Sma(SmaResponse r)
    {
        var skipped = r.RequestedDataPoints - r.ActualDataPoints;
        var buckets = $"{r.ActualDataPoints.ToString(CultureInfo.InvariantCulture)} of {r.RequestedDataPoints.ToString(CultureInfo.InvariantCulture)}"
            + (skipped > 0 ? $" [{Theme.Warn}]({skipped.ToString(CultureInfo.InvariantCulture)} without trades, skipped)[/]" : string.Empty);

        var facts = Theme.Facts()
            .AddFact("Buckets", buckets)
            .AddFact("Window", Markup.Escape($"{NumberFormat.Timestamp(r.WindowStart)} → {NumberFormat.Timestamp(r.WindowEnd)}"))
            .AddFact("Start", r.StartParameter is { } s ? Markup.Escape(NumberFormat.Timestamp(s)) : $"[{Theme.Muted}]latest complete buckets[/]")
            .AddFact("Computed", Markup.Escape(NumberFormat.Timestamp(r.ReferenceTime)));

        var header = Theme.Panel(
            new Rows(
                new Markup($"[bold {Theme.Accent}]{NumberFormat.Price(r.Value)}[/]  [{Theme.Muted}]mean of {r.ActualDataPoints.ToString(CultureInfo.InvariantCulture)} {NumberFormat.PeriodAdjective(r.Period)} closes[/]"),
                Text.Empty,
                facts),
            $"{Markup.Escape(r.Symbol)} [{Theme.Muted}]·[/] SMA({r.RequestedDataPoints.ToString(CultureInfo.InvariantCulture)}) [{Theme.Muted}]·[/] {Markup.Escape(r.Period)}");

        if (r.DataPoints.Count == 0)
        {
            return new Rows(header, new Markup($"[{Theme.Muted}]No complete bucket in the window had trades.[/]"));
        }

        return new Rows(header, Points(r), Chart(r));
    }

    private static Table Points(SmaResponse r)
    {
        var table = Theme.Table()
            .Title($"[bold]Closes[/] [{Theme.Muted}]({NumberFormat.PeriodAdjective(r.Period)} buckets, oldest first)[/]")
            .AddColumn(new TableColumn($"[{Theme.Muted}]#[/]").RightAligned())
            .AddColumn(new TableColumn("Bucket start (UTC)"))
            .AddColumn(new TableColumn("Close").RightAligned())
            .AddColumn(new TableColumn("vs SMA").RightAligned());

        for (var i = 0; i < r.DataPoints.Count; i++)
        {
            var point = r.DataPoints[i];
            table.AddRow(
                new Markup($"[{Theme.Muted}]{(i + 1).ToString(CultureInfo.InvariantCulture)}[/]"),
                new Text(NumberFormat.Bucket(point.BucketStart, r.Period)),
                new Text(NumberFormat.Price(point.Close)),
                new Markup(VersusSma(point.Close, r.Value)));
        }

        return table;
    }

    private static Panel Chart(SmaResponse r)
    {
        var points = r.DataPoints.Skip(Math.Max(0, r.DataPoints.Count - MaxChartBars)).ToList();
        var axisStart = DecimalBarChart.AxisStart(points.Select(p => p.Close).Append(r.Value));
        var bars = points
            .Select(p => (NumberFormat.Bucket(p.BucketStart, r.Period), p.Close, p.Close >= r.Value ? Theme.Up : Theme.Down))
            .ToList();

        var legend = $"[{Theme.Up}]█[/] at or above SMA   [{Theme.Down}]█[/] below SMA   "
            + $"[{Theme.Muted}]axis starts at {NumberFormat.Price(axisStart)}[/]";
        if (points.Count < r.DataPoints.Count)
        {
            legend += $"\n[{Theme.Muted}]showing the last {points.Count.ToString(CultureInfo.InvariantCulture)} of {r.DataPoints.Count.ToString(CultureInfo.InvariantCulture)} buckets[/]";
        }

        return Theme.Panel(
            new Rows(DecimalBarChart.Render(bars, axisStart), Text.Empty, new Markup(legend)),
            $"Closes vs SMA {NumberFormat.Price(r.Value)}");
    }

    private static string VersusSma(decimal close, decimal sma)
    {
        if (sma == 0m)
        {
            return $"[{Theme.Muted}]–[/]";
        }

        var percent = (close - sma) / sma * 100m;
        var color = percent switch { > 0 => Theme.Up, < 0 => Theme.Down, _ => Theme.Muted };
        return $"[{color}]{NumberFormat.SignedPercent(percent)}[/]";
    }

    private static string CoverageBar(decimal fraction)
    {
        var filled = (int)Math.Floor(fraction * CoverageCells);
        return $"[{Theme.Accent}]{new string('█', filled)}[/][{Theme.Muted}]{new string('░', CoverageCells - filled)}[/]";
    }

    // Floored, so the coverage only reads 100.00% when the window is really complete.
    private static string FloorPercent(decimal fraction) =>
        (Math.Floor(fraction * 10_000m) / 100m).ToString("0.00", CultureInfo.InvariantCulture) + "%";
}

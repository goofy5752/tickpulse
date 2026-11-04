using System.Globalization;
using Spectre.Console;
using Spectre.Console.Rendering;
using TickPulse.Contracts;

namespace TickPulse.Cli.Rendering;

/// <summary>Table output of the symbol lists and the admin results.</summary>
internal static class SymbolViews
{
    public static IRenderable PublicList(IReadOnlyList<SymbolResponse> symbols)
    {
        if (symbols.Count == 0)
        {
            return new Markup($"[{Theme.Muted}]No symbols are monitored yet.[/]");
        }

        var table = Theme.Table()
            .Title("[bold]Symbols[/]")
            .AddColumn("Symbol")
            .AddColumn("Status")
            .AddColumn(new TableColumn("Latest price").RightAligned())
            .AddColumn(new TableColumn("24h").RightAligned())
            .AddColumn("Backfill");

        foreach (var s in symbols.OrderByDescending(s => s.IsActive).ThenBy(s => s.Symbol, StringComparer.Ordinal))
        {
            table.AddRow(
                new Markup($"[bold]{Markup.Escape(s.Symbol)}[/]"),
                new Markup(Theme.ActiveBadge(s.IsActive)),
                new Markup(s.LatestPrice is { } price ? NumberFormat.Price(price) : $"[{Theme.Muted}]no data[/]"),
                new Markup(Theme.Change(s.Change24hPercent)),
                new Markup(Theme.Backfill(s.BackfillStatus)));
        }

        var active = symbols.Count(s => s.IsActive);
        table.Caption($"[{Theme.Muted}]{active.ToString(CultureInfo.InvariantCulture)} active, {(symbols.Count - active).ToString(CultureInfo.InvariantCulture)} inactive · inactive symbols keep their history[/]");
        return table;
    }

    public static IRenderable AdminList(IReadOnlyList<AdminSymbolResponse> symbols)
    {
        if (symbols.Count == 0)
        {
            return new Markup($"[{Theme.Muted}]No symbols have been added yet.[/]");
        }

        var table = Theme.Table()
            .Title("[bold]Symbols[/] [grey](admin)[/]")
            .AddColumn("Symbol")
            .AddColumn("Status")
            .AddColumn("Added")
            .AddColumn("Deactivated")
            .AddColumn("Backfill")
            .AddColumn("History from");

        foreach (var s in symbols.OrderByDescending(s => s.IsActive).ThenBy(s => s.Symbol, StringComparer.Ordinal))
        {
            table.AddRow(
                new Markup($"[bold]{Markup.Escape(s.Symbol)}[/]"),
                new Markup(Theme.ActiveBadge(s.IsActive)),
                new Text(NumberFormat.Timestamp(s.CreatedAt)),
                Optional(s.DeactivatedAt),
                new Markup(Theme.Backfill(s.BackfillStatus)),
                Optional(s.BackfilledFrom));
        }

        var active = symbols.Count(s => s.IsActive);
        table.Caption($"[{Theme.Muted}]{active.ToString(CultureInfo.InvariantCulture)} active of {symbols.Count.ToString(CultureInfo.InvariantCulture)}[/]");
        return table;
    }

    /// <summary>The outcome of <c>symbols add</c>: 201 added or reactivated, 200 already active.</summary>
    public static IRenderable Added(AdminSymbolResponse s, bool created)
    {
        var symbol = Markup.Escape(s.Symbol);
        var headline = created
            ? $"[{Theme.Up}]✓[/] [bold]{symbol} added[/] — trades are being collected now."
            : $"[{Theme.Accent}]•[/] [bold]{symbol}[/] is already active; nothing changed.";

        var lines = new List<string> { headline };
        lines.Add(s.BackfillStatus switch
        {
            "Pending" or "Running" => $"  [{Theme.Muted}]History backfill is {s.BackfillStatus.ToLowerInvariant()}; analytics fill in as it completes.[/]",
            "Completed" when s.BackfilledFrom is { } from => $"  [{Theme.Muted}]History is available from {Markup.Escape(NumberFormat.Timestamp(from))}.[/]",
            "Failed" => $"  [{Theme.Warn}]The history backfill failed; live trades are still collected.[/]",
            _ => $"  [{Theme.Muted}]Analytics cover trades collected from now on.[/]",
        });
        return new Markup(string.Join('\n', lines) + '\n');
    }

    public static IRenderable Removed(string symbol) =>
        new Markup($"[{Theme.Up}]✓[/] [bold]{Markup.Escape(symbol)} removed[/] — no longer collected. Its history is kept and stays queryable.");

    private static Markup Optional(DateTime? value) =>
        value is { } v ? new Markup(Markup.Escape(NumberFormat.Timestamp(v))) : new Markup($"[{Theme.Muted}]–[/]");
}

using Spectre.Console;

namespace TickPulse.Cli.Rendering;

/// <summary>The palette and glyphs every view shares, so the CLI reads as one product.</summary>
internal static class Theme
{
    public const string Accent = "deepskyblue1";
    public const string Up = "green";
    public const string Down = "red";
    public const string Muted = "grey";
    public const string Warn = "yellow";
    public const string Fail = "red";

    public static readonly Color AccentColor = Color.DeepSkyBlue1;

    /// <summary>A rounded panel with the accent border and a bold header.</summary>
    public static Panel Panel(Spectre.Console.Rendering.IRenderable content, string header, Color? border = null) =>
        new Panel(content)
            .Header($" [bold]{header}[/] ")
            .Border(BoxBorder.Rounded)
            .BorderColor(border ?? AccentColor)
            .Padding(1, 0, 1, 0);

    /// <summary>A table in the house style.</summary>
    public static Table Table() => new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey);

    /// <summary>A two-column label/value grid for panel bodies.</summary>
    public static Grid Facts() =>
        new Grid().AddColumn(new GridColumn().NoWrap().PadRight(3)).AddColumn(new GridColumn());

    public static Grid AddFact(this Grid grid, string label, string valueMarkup) =>
        grid.AddRow(new Markup($"[{Muted}]{Markup.Escape(label)}[/]"), new Markup(valueMarkup));

    /// <summary><c>▲ +1.25%</c> in green, <c>▼ -2.50%</c> in red, <c>0.00%</c> plain; <c>–</c> when unknown.</summary>
    public static string Change(decimal? percent) => percent switch
    {
        null => $"[{Muted}]–[/]",
        > 0 => $"[{Up}]▲ {NumberFormat.SignedPercent(percent.Value)}[/]",
        < 0 => $"[{Down}]▼ {NumberFormat.SignedPercent(percent.Value)}[/]",
        _ => NumberFormat.SignedPercent(0),
    };

    public static string ActiveBadge(bool isActive) =>
        isActive ? $"[{Up}]● active[/]" : $"[{Muted}]○ inactive[/]";

    public static string Backfill(string status) => status switch
    {
        "Completed" => $"[{Up}]Completed[/]",
        "Running" => $"[{Warn}]Running[/]",
        "Pending" => $"[{Warn}]Pending[/]",
        "Failed" => $"[{Fail}]Failed[/]",
        "NotRequested" => $"[{Muted}]–[/]",
        _ => Markup.Escape(status),
    };
}

using System.Globalization;
using System.Text;
using Spectre.Console;
using Spectre.Console.Rendering;
using TickPulse.Contracts;

namespace TickPulse.Cli.Rendering;

/// <summary>What <c>watch</c> knows after its latest poll.</summary>
internal sealed class WatchState(string symbol, TimeSpan interval, string? smaLabel)
{
    /// <summary>How many polled prices the sparkline keeps.</summary>
    public const int HistoryLength = 48;

    private readonly Queue<decimal> _history = new();

    public string Symbol { get; } = symbol;

    public TimeSpan Interval { get; } = interval;

    /// <summary><c>SMA(20 × 1d)</c> when the SMA is watched too, else <see langword="null"/>.</summary>
    public string? SmaLabel { get; } = smaLabel;

    public SymbolResponse? Latest { get; private set; }

    public decimal? PreviousPrice { get; private set; }

    public SmaResponse? Sma { get; set; }

    public DateTimeOffset? UpdatedAt { get; private set; }

    /// <summary>Why the latest poll failed; cleared by the next good one.</summary>
    public string? Error { get; set; }

    public IReadOnlyCollection<decimal> History => _history;

    public void Update(SymbolResponse latest, DateTimeOffset now)
    {
        PreviousPrice = Latest?.LatestPrice;
        Latest = latest;
        UpdatedAt = now;
        Error = null;
        if (latest.LatestPrice is { } price)
        {
            _history.Enqueue(price);
            while (_history.Count > HistoryLength)
            {
                _history.Dequeue();
            }
        }
    }
}

/// <summary>The live panel of <c>watch</c>.</summary>
internal static class WatchView
{
    private static readonly char[] Ticks = ['▁', '▂', '▃', '▄', '▅', '▆', '▇', '█'];

    public static IRenderable Render(WatchState state)
    {
        var rows = new List<IRenderable>();
        var latest = state.Latest;
        if (latest?.LatestPrice is { } price)
        {
            rows.Add(new Markup(
                $"[bold {Theme.Accent}]{NumberFormat.Price(price)}[/] {Direction(price, state.PreviousPrice)}   "
                + $"{Theme.Change(latest.Change24hPercent)} [{Theme.Muted}]24h[/]"));
        }
        else
        {
            rows.Add(new Markup($"[{Theme.Muted}]No price yet: no trades are stored for this symbol.[/]"));
        }

        if (state.History.Count >= 2)
        {
            rows.Add(new Markup($"[{Theme.Accent}]{Sparkline(state.History)}[/] [{Theme.Muted}]last {state.History.Count.ToString(CultureInfo.InvariantCulture)} polls[/]"));
        }

        if (state.SmaLabel is { } label)
        {
            rows.Add(new Markup(SmaLine(label, state.Sma, latest?.LatestPrice)));
        }

        if (latest is { IsActive: false })
        {
            rows.Add(new Markup($"[{Theme.Warn}]○ Not being collected; the price is the last stored close.[/]"));
        }

        rows.Add(Text.Empty);
        rows.Add(new Markup(StatusLine(state)));

        var header = $"{Markup.Escape(state.Symbol)} [{Theme.Muted}]·[/] " + (state.Error is null ? $"[{Theme.Up}]● live[/]" : $"[{Theme.Warn}]● reconnecting[/]");
        return Theme.Panel(new Rows(rows), header);
    }

    /// <summary>
    /// One log line per poll, for when stdout is not a terminal (a file, a pipe, a supervisor), where a live panel
    /// cannot redraw: <c>2026-11-04 10:00:02 UTC  BTCUSDT  67,234.50 ▲  ▲ +1.25% 24h  SMA(20 × 1d) 66,100.00</c>.
    /// </summary>
    public static string Line(WatchState state)
    {
        var at = state.UpdatedAt is { } updated ? NumberFormat.Timestamp(updated.UtcDateTime) : "–";
        var line = $"[{Theme.Muted}]{Markup.Escape(at)}[/]  [bold]{Markup.Escape(state.Symbol)}[/]  ";
        if (state.Error is { } error)
        {
            return line + $"[{Theme.Warn}]⚠ {Markup.Escape(error)}[/]";
        }

        var latest = state.Latest;
        line += latest?.LatestPrice is { } price
            ? $"{NumberFormat.Price(price)} {Direction(price, state.PreviousPrice)}  {Theme.Change(latest.Change24hPercent)} 24h"
            : $"[{Theme.Muted}]no price yet[/]";
        if (state.SmaLabel is { } label)
        {
            line += "  " + SmaLine(label, state.Sma, latest?.LatestPrice);
        }

        return line;
    }

    /// <summary>One block per price, scaled between the lowest and highest of them.</summary>
    internal static string Sparkline(IEnumerable<decimal> prices)
    {
        var values = prices.ToList();
        var (min, max) = (values.Min(), values.Max());
        var builder = new StringBuilder(values.Count);
        foreach (var value in values)
        {
            var index = max == min ? Ticks.Length / 2 : (int)Math.Round((value - min) / (max - min) * (Ticks.Length - 1), MidpointRounding.AwayFromZero);
            builder.Append(Ticks[index]);
        }

        return builder.ToString();
    }

    private static string Direction(decimal price, decimal? previous) => previous switch
    {
        null => string.Empty,
        _ when price > previous => $"[{Theme.Up}]▲[/]",
        _ when price < previous => $"[{Theme.Down}]▼[/]",
        _ => $"[{Theme.Muted}]=[/]",
    };

    private static string SmaLine(string label, SmaResponse? sma, decimal? price)
    {
        if (sma is null)
        {
            return $"[{Theme.Muted}]{Markup.Escape(label)}  waiting for data…[/]";
        }

        var line = $"[{Theme.Muted}]{Markup.Escape(label)}[/]  {NumberFormat.Price(sma.Value)}";
        if (price is { } p && sma.Value != 0m)
        {
            var percent = (p - sma.Value) / sma.Value * 100m;
            var (color, word) = percent >= 0 ? (Theme.Up, "above") : (Theme.Down, "below");
            line += $"   [{color}]{NumberFormat.SignedPercent(percent)}[/] [{Theme.Muted}]price {word} SMA[/]";
        }

        return line;
    }

    private static string StatusLine(WatchState state)
    {
        var every = state.Interval.TotalSeconds >= 1
            ? state.Interval.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture) + "s"
            : state.Interval.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture) + "ms";
        var updated = state.UpdatedAt is { } at ? $"updated {at.UtcDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture)} UTC · " : string.Empty;
        var status = $"[{Theme.Muted}]{updated}every {every} · Ctrl+C to exit[/]";
        return state.Error is { } error ? $"[{Theme.Warn}]⚠ {Markup.Escape(error)}[/]\n{status}" : status;
    }
}

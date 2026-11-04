using Spectre.Console;

namespace TickPulse.Cli.Rendering;

/// <summary>Renders failures on stderr: API problems, unreachable servers and command-line mistakes.</summary>
internal static class ProblemView
{
    /// <summary>What to do next for each TickPulse problem type.</summary>
    public static string? HintFor(ApiProblem problem) => problem.Slug switch
    {
        "unknown-symbol" => "Run 'tickpulse symbols list' to see the monitored symbols.",
        "no-data" => "No trades are stored for that window yet; try a shorter period or a later start.",
        "unauthorized" => "The admin API key was rejected or missing. Check --api-key or TICKPULSE_API_KEY.",
        "admin-disabled" => "The admin API is disabled on this server: it has no admin API key configured.",
        "symbol-limit" => "The active symbol limit is reached. Free a slot with 'tickpulse symbols remove <symbol>'.",
        "invalid-symbol" => "Binance does not list that symbol on its spot market. Symbols look like BTCUSDT.",
        "upstream-unavailable" => "Binance could not be reached to verify the symbol. Try again in a moment.",
        "rate-limited" => "Too many requests from this address; wait a minute and try again.",
        "internal" => "The server hit an unexpected error. Quote the trace id if you report it.",
        _ => HintForStatus(problem.Status),
    };

    /// <summary>
    /// For failures without a TickPulse problem type: a proxy or load balancer in front of the API, or a bodiless
    /// response. Phrased by status alone, matching the TickPulse problem that status usually means.
    /// </summary>
    private static string? HintForStatus(int status) => status switch
    {
        401 => "The admin API key was rejected or missing. Check --api-key or TICKPULSE_API_KEY.",
        403 => "Access was refused; the admin API may be disabled on this server.",
        404 => "Nothing at that address. Check --api-url, and run 'tickpulse symbols list' for the monitored symbols.",
        409 => "The request conflicts with the server state, e.g. the active symbol limit is reached.",
        422 => "The symbol was refused; it must be listed on the Binance spot market, e.g. BTCUSDT.",
        429 => "Too many requests from this address; wait a minute and try again.",
        502 or 504 => "A proxy in front of the API could not get an answer from it. Try again in a moment.",
        503 => "The service is unavailable right now. Try again in a moment.",
        >= 500 => "The server failed to handle the request. Try again in a moment.",
        _ => null,
    };

    /// <summary>
    /// The full problem panel for table output, or a compact summary when the raw body was already printed to
    /// stdout (<paramref name="compact"/>).
    /// </summary>
    public static void Render(IAnsiConsole console, ApiProblem problem, bool compact)
    {
        var title = Markup.Escape(problem.Title ?? "Request failed");
        var lines = new List<string>();

        // A one-parameter validation problem repeats its message as the detail; show it once, next to the parameter.
        var detailIsAnError = problem.Errors.Values.Any(messages => messages.Contains(problem.Detail, StringComparer.Ordinal));
        if (!string.IsNullOrWhiteSpace(problem.Detail) && !detailIsAnError)
        {
            lines.Add(Markup.Escape(problem.Detail));
        }

        foreach (var (parameter, messages) in problem.Errors)
        {
            lines.AddRange(messages.Select(m => $"[{Theme.Warn}]•[/] [bold]{Markup.Escape(parameter)}[/]: {Markup.Escape(m)}"));
        }

        if (HintFor(problem) is { } hint)
        {
            lines.Add($"[{Theme.Accent}]→[/] {Markup.Escape(hint)}");
        }

        if (!string.IsNullOrWhiteSpace(problem.TraceId))
        {
            lines.Add($"[{Theme.Muted}]trace {Markup.Escape(problem.TraceId)}[/]");
        }

        if (compact)
        {
            console.MarkupLine($"[{Theme.Fail}]✗[/] [bold]{title}[/] [{Theme.Muted}]({problem.Status})[/]");
            foreach (var line in lines)
            {
                console.MarkupLine("  " + line);
            }

            return;
        }

        var body = new Markup(lines.Count == 0 ? $"[{Theme.Muted}]The server sent no details.[/]" : string.Join('\n', lines));
        console.Write(Theme.Panel(body, $"[{Theme.Fail}]✗[/] {title} [{Theme.Muted}]· {problem.Status}[/]", Color.Red));
    }

    /// <summary>The server could not be reached, timed out, or answered something unreadable.</summary>
    public static void RenderUnavailable(IAnsiConsole console, Uri? apiUrl, string reason)
    {
        var target = apiUrl is null ? "the API" : apiUrl.ToString().TrimEnd('/');
        var body = new Markup(string.Join(
            '\n',
            Markup.Escape(reason),
            $"[{Theme.Accent}]→[/] Check --api-url (or {CliEnvironment.ApiUrlVariable}) and your network connection."));
        console.Write(Theme.Panel(body, $"[{Theme.Fail}]✗[/] Could not reach {Markup.Escape(target)}", Color.Red));
    }

    /// <summary>A command-line mistake: the messages, then where to find the usage.</summary>
    public static void RenderUsage(IAnsiConsole console, IEnumerable<string> messages, string commandPath)
    {
        foreach (var message in messages)
        {
            // System.CommandLine lists allowed values on tab-indented lines; keep them on one line.
            var oneLine = message.Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n\t", " ", StringComparison.Ordinal);
            console.MarkupLine($"[{Theme.Fail}]✗[/] {Markup.Escape(oneLine)}");
        }

        console.MarkupLine($"[{Theme.Muted}]Run '{Markup.Escape(commandPath)} --help' for usage.[/]");
    }
}

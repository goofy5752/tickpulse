using System.CommandLine;
using Spectre.Console;
using Spectre.Console.Rendering;
using TickPulse.Cli.Rendering;

namespace TickPulse.Cli.Commands;

/// <summary>
/// One command invocation: the resolved API URL and output format, the API client, and the mapping of every outcome
/// (success, problem, unreachable server, Ctrl+C) onto output and an exit code.
/// </summary>
internal sealed class CommandSession
{
    private CommandSession(CliEnvironment environment, Uri apiUrl, OutputFormat format, TickPulseApiClient client)
    {
        Environment = environment;
        ApiUrl = apiUrl;
        Format = format;
        Client = client;
    }

    public CliEnvironment Environment { get; }

    public Uri ApiUrl { get; }

    public OutputFormat Format { get; }

    public TickPulseApiClient Client { get; }

    public bool IsTable => Format == OutputFormat.Table;

    /// <summary>Runs <paramref name="body"/> with a client for the chosen API, turning transport failures into exit codes.</summary>
    public static async Task<int> RunAsync(
        CliEnvironment environment,
        ParseResult parse,
        GlobalOptions options,
        Func<CommandSession, CancellationToken, Task<int>> body,
        CancellationToken ct)
    {
        // Both were validated by the parser, so they are well-formed here.
        var apiUrl = CliOptions.TryParseApiUrl(parse.GetValue(options.ApiUrl))!;
        var format = OutputFormats.Parse(parse.GetValue(options.Format));

        using var http = environment.CreateHttpClient(apiUrl);
        var session = new CommandSession(environment, apiUrl, format, new TickPulseApiClient(http));
        try
        {
            return await body(session, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            environment.Error.MarkupLine($"[{Theme.Muted}]Interrupted.[/]");
            return ExitCodes.Interrupted;
        }
        catch (TaskCanceledException)
        {
            // Not our token: the HttpClient timeout.
            ProblemView.RenderUnavailable(environment.Error, apiUrl, $"No answer within {CliEnvironment.RequestTimeout.TotalSeconds:0} seconds.");
            return ExitCodes.Unavailable;
        }
        catch (HttpRequestException e)
        {
            ProblemView.RenderUnavailable(environment.Error, apiUrl, e.Message);
            return ExitCodes.Unavailable;
        }
        catch (UnexpectedResponseException e)
        {
            ProblemView.RenderUnavailable(environment.Error, apiUrl, e.Message);
            return ExitCodes.Unavailable;
        }
    }

    /// <summary>
    /// Prints a response the standard way: the raw body for json/xml, then either the problem (stderr) or the table
    /// produced by <paramref name="table"/>. Returns the exit code.
    /// </summary>
    public int Complete<T>(ApiResponse<T> response, Func<T, IRenderable> table)
        where T : class
    {
        if (!IsTable)
        {
            RawBodyView.Write(Environment, response.Raw, response.ContentType);
        }

        if (response.Problem is { } problem)
        {
            ProblemView.Render(Environment.Error, problem, compact: !IsTable);
            return ExitCodes.ForStatus(problem.Status);
        }

        if (IsTable)
        {
            var body = response.Body ?? throw new UnexpectedResponseException("The server answered with an empty body.");
            Environment.Out.Write(table(body));
        }

        return ExitCodes.Ok;
    }

    /// <summary>
    /// Like <see cref="Complete{T}"/> for a success without a body (204): <paramref name="success"/> goes to stdout in
    /// table mode, and to stderr with json/xml so stdout stays exactly the (empty) body.
    /// </summary>
    public int CompleteNoContent(ApiResponse<object> response, IRenderable success)
    {
        if (!IsTable)
        {
            RawBodyView.Write(Environment, response.Raw, response.ContentType);
        }

        if (response.Problem is { } problem)
        {
            ProblemView.Render(Environment.Error, problem, compact: !IsTable);
            return ExitCodes.ForStatus(problem.Status);
        }

        var console = IsTable ? Environment.Out : Environment.Error;
        console.Write(success);
        console.WriteLine();
        return ExitCodes.Ok;
    }

    /// <summary>A mistake only noticed after parsing (e.g. no API key): reported like a parse error, exit 64.</summary>
    public int Usage(string message, string commandPath)
    {
        ProblemView.RenderUsage(Environment.Error, [message], commandPath);
        return ExitCodes.Usage;
    }
}

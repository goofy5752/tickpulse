using System.Reflection;
using System.Text;
using Spectre.Console;

namespace TickPulse.Cli;

/// <summary>Everything the CLI touches outside itself: consoles, environment variables, HTTP and the clock.</summary>
internal sealed class CliEnvironment
{
    public const string ApiUrlVariable = "TICKPULSE_API_URL";
    public const string ApiKeyVariable = "TICKPULSE_API_KEY";
    public const string DefaultApiUrl = "https://tickpulse.dev";

    /// <summary>How long one request may take before it counts as a network failure.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Results: tables, or raw bodies with <c>--format json|xml</c>.</summary>
    public required IAnsiConsole Out { get; init; }

    /// <summary>Problems, usage errors and status messages that must not pollute piped output.</summary>
    public required IAnsiConsole Error { get; init; }

    /// <summary>Where System.CommandLine writes <c>--help</c> and <c>--version</c>.</summary>
    public required TextWriter HelpOutput { get; init; }

    public required Func<string, string?> GetEnvironmentVariable { get; init; }

    /// <summary>Creates the client for one invocation; the base address always ends with <c>/</c>.</summary>
    public required Func<Uri, HttpClient> CreateHttpClient { get; init; }

    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>When stdout is a pipe or file, raw bodies are written verbatim (no colours, no re-indentation).</summary>
    public bool IsOutputRedirected { get; init; }

    public static string Version { get; } =
        typeof(CliEnvironment).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";

    /// <summary>The real process: <see cref="Console"/> streams, process environment, a fresh <see cref="HttpClient"/>.</summary>
    public static CliEnvironment CreateSystem()
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch (IOException)
        {
            // Some hosts do not let the encoding change; Spectre then falls back to ASCII glyphs.
        }

        return new CliEnvironment
        {
            Out = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(Console.Out) }),
            Error = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(Console.Error) }),
            HelpOutput = Console.Out,
            GetEnvironmentVariable = Environment.GetEnvironmentVariable,
            CreateHttpClient = CreateHttpClientCore,
            IsOutputRedirected = Console.IsOutputRedirected,
        };
    }

    private static HttpClient CreateHttpClientCore(Uri baseAddress)
    {
        // Pooled connections are recycled, so a long watch follows DNS changes of the API host.
        var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
        var client = new HttpClient(handler, disposeHandler: true) { BaseAddress = baseAddress, Timeout = RequestTimeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"tickpulse-cli/{Version}");
        return client;
    }
}

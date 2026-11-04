using Microsoft.Extensions.Time.Testing;
using Spectre.Console.Testing;

namespace TickPulse.Cli.Tests.Support;

/// <summary>Runs the CLI in-process against a <see cref="StubHttpHandler"/>, capturing stdout and stderr, on a fake clock.</summary>
internal sealed class CliHarness : IDisposable
{
    public const string ApiUrl = "https://api.test";

    public CliHarness(StubHttpHandler http, bool outputRedirected = false)
    {
        Http = http;
        Environment = new CliEnvironment
        {
            Out = Out,
            Error = Error,
            HelpOutput = Help,
            GetEnvironmentVariable = name => Variables.GetValueOrDefault(name),
            CreateHttpClient = baseAddress => new HttpClient(Http, disposeHandler: false) { BaseAddress = baseAddress },
            Time = Time,
            IsOutputRedirected = outputRedirected,
        };
        Variables[CliEnvironment.ApiUrlVariable] = ApiUrl;
    }

    public StubHttpHandler Http { get; }

    public TestConsole Out { get; } = new TestConsole().Width(160);

    public TestConsole Error { get; } = new TestConsole().Width(160);

    public StringWriter Help { get; } = new();

    /// <summary>Never moves on its own, so a <c>watch</c> interval only elapses when a test advances it.</summary>
    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 4, 10, 0, 0, TimeSpan.Zero));

    public Dictionary<string, string> Variables { get; } = new(StringComparer.Ordinal);

    public CliEnvironment Environment { get; }

    /// <summary>
    /// Lets a <c>watch</c> run without waiting: every response moves the clock past any polling interval (so the next
    /// poll is due at once), and the <paramref name="requests"/>th response cancels the run, like Ctrl+C.
    /// </summary>
    public void StopAfter(int requests, CancellationTokenSource cts) =>
        Http.AfterResponse = count =>
        {
            Time.Advance(TimeSpan.FromHours(2));
            if (count == requests)
            {
                cts.Cancel();
            }
        };

    public Task<int> RunAsync(params string[] args) => RunAsync(TestContext.Current.CancellationToken, args);

    public Task<int> RunAsync(CancellationToken ct, params string[] args) => TickPulseCli.RunAsync(args, Environment, ct);

    public void Dispose()
    {
        Out.Dispose();
        Error.Dispose();
        Help.Dispose();
        Http.Dispose();
    }
}

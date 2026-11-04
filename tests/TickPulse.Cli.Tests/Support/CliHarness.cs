using Spectre.Console.Testing;

namespace TickPulse.Cli.Tests.Support;

/// <summary>Runs the CLI in-process against a <see cref="StubHttpHandler"/>, capturing stdout and stderr.</summary>
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
            IsOutputRedirected = outputRedirected,
        };
        Variables[CliEnvironment.ApiUrlVariable] = ApiUrl;
    }

    public StubHttpHandler Http { get; }

    public TestConsole Out { get; } = new TestConsole().Width(160);

    public TestConsole Error { get; } = new TestConsole().Width(160);

    public StringWriter Help { get; } = new();

    public Dictionary<string, string> Variables { get; } = new(StringComparer.Ordinal);

    public CliEnvironment Environment { get; }

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

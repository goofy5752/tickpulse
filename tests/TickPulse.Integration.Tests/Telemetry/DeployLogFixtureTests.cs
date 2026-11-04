using Serilog.Events;
using Serilog.Formatting.Compact;
using Serilog.Parsing;
using TickPulse.Integration.Tests.Fixtures;

namespace TickPulse.Integration.Tests.Telemetry;

/// <summary>
/// <c>deploy/tests/mask-env.test.sh</c> masks api log lines in the form the api writes them: Serilog's
/// <see cref="RenderedCompactJsonFormatter"/>, which escapes <c>"</c> and <c>\</c> in JSON strings and quotes string
/// properties in the rendered message. This test keeps its fixture byte-identical to that formatter's output, so
/// the masking test exercises the real escaping and fails if a Serilog upgrade changes it.
/// </summary>
public sealed class DeployLogFixtureTests
{
    // The same secrets as the fixture .env in mask-env.test.sh; both hold a double quote and a backslash.
    internal const string PostgresPassword = "pa\"ss\\wo\"rd-9";
    internal const string AdminApiKey = "key\\with\"quote";

    private const string FixturePath = "deploy/tests/fixtures/serilog-events.jsonl";

    [Fact]
    public void Fixture_IsWhatTheApisFormatterWrites()
    {
        var expected = Render();
        var actual = File.ReadAllText(Path.Combine(ComposeFile.Root, FixturePath));

        actual.ShouldBe(expected, $"{FixturePath} is out of date; replace its content with:\n{expected}");
    }

    private static string Render()
    {
        var connection = $"Host=db;Port=5432;Database=tickpulse;Username=tickpulse;Password={PostgresPassword};Maximum Pool Size=20";
        var at = new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
        var parser = new MessageTemplateParser();
        LogEvent[] events =
        [
            new(at, LogEventLevel.Error, new InvalidOperationException($"cannot open {connection}"),
                parser.Parse("Database connection {ConnectionString} failed"),
                [new LogEventProperty("ConnectionString", new ScalarValue(connection))]),
            new(at, LogEventLevel.Warning, exception: null,
                parser.Parse("Admin request with key {ApiKey} rejected"),
                [new LogEventProperty("ApiKey", new ScalarValue(AdminApiKey))]),
        ];

        var formatter = new RenderedCompactJsonFormatter();
        using var output = new StringWriter { NewLine = "\n" };
        foreach (var e in events)
        {
            formatter.Format(e, output);
        }

        return output.ToString().ReplaceLineEndings("\n");
    }
}

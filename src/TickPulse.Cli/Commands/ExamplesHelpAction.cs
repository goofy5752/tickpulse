using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Invocation;

namespace TickPulse.Cli.Commands;

/// <summary>
/// The standard help, followed by an <c>Examples:</c> section for the command it describes. Command descriptions
/// stay one line, so the command list of <c>tickpulse --help</c> stays readable.
/// </summary>
internal sealed class ExamplesHelpAction(HelpAction standard, IReadOnlyDictionary<Command, string[]> examples) : SynchronousCommandLineAction
{
    /// <summary>Help works even when the rest of the command line is incomplete, e.g. <c>tickpulse sma --help</c>.</summary>
    public override bool ClearsParseErrors => true;

    public override int Invoke(ParseResult parseResult)
    {
        ArgumentNullException.ThrowIfNull(parseResult);
        var exit = standard.Invoke(parseResult);
        if (examples.TryGetValue(parseResult.CommandResult.Command, out var lines))
        {
            var output = parseResult.InvocationConfiguration.Output;
            output.WriteLine("Examples:");
            foreach (var line in lines)
            {
                output.WriteLine("  " + line);
            }

            output.WriteLine();
        }

        return exit;
    }
}

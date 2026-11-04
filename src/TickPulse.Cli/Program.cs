using Spectre.Console;
using TickPulse.Cli;

var environment = CliEnvironment.CreateSystem();
try
{
    return await TickPulseCli.RunAsync(args, environment, CancellationToken.None);
}
catch (Exception e) when (e is not OutOfMemoryException)
{
    // A bug, not an API or network failure: say so plainly instead of dumping a raw stack trace.
    environment.Error.MarkupLine("[red]✗[/] [bold]tickpulse hit an unexpected error.[/]");
    environment.Error.WriteException(e, ExceptionFormats.ShortenEverything);
    return ExitCodes.Internal;
}

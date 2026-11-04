namespace TickPulse.Cli;

/// <summary>The process exit codes of <c>tickpulse</c>.</summary>
internal static class ExitCodes
{
    /// <summary>The command succeeded.</summary>
    public const int Ok = 0;

    /// <summary>The API rejected the request with a 4xx problem (unknown symbol, validation, bad key, …).</summary>
    public const int ApiError = 1;

    /// <summary>The API could not be reached, timed out, failed with a 5xx, or answered with something unreadable.</summary>
    public const int Unavailable = 2;

    /// <summary>The command line is invalid (<c>EX_USAGE</c>); nothing was sent.</summary>
    public const int Usage = 64;

    /// <summary>A one-shot command was interrupted with Ctrl+C (<c>128 + SIGINT</c>); <c>watch</c> exits with 0 instead.</summary>
    public const int Interrupted = 130;

    /// <summary>A defect in the CLI itself (<c>EX_SOFTWARE</c>); reported with a short stack trace.</summary>
    public const int Internal = 70;

    /// <summary>The exit code of a failed response: 1 for 4xx, otherwise 2.</summary>
    public static int ForStatus(int status) => status is >= 400 and < 500 ? ApiError : Unavailable;
}

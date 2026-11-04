namespace TickPulse.Cli;

/// <summary>How a command prints its result: Spectre tables, or the API's raw JSON or XML body.</summary>
internal enum OutputFormat
{
    Table,
    Json,
    Xml,
}

internal static class OutputFormats
{
    public const string Table = "table";
    public const string Json = "json";
    public const string Xml = "xml";

    public static OutputFormat Parse(string? value) => value switch
    {
        Json => OutputFormat.Json,
        Xml => OutputFormat.Xml,
        _ => OutputFormat.Table,
    };

    /// <summary>The <c>Accept</c> header for the format; tables are rendered from JSON.</summary>
    public static string AcceptHeader(this OutputFormat format) =>
        format == OutputFormat.Xml ? "application/xml" : "application/json";
}

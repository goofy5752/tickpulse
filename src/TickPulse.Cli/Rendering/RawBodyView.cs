using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Spectre.Console;
using Spectre.Console.Json;

namespace TickPulse.Cli.Rendering;

/// <summary>
/// Prints a response body for <c>--format json|xml</c>. On a terminal it is indented and syntax-highlighted; when stdout
/// is redirected it is written byte for byte, so <c>tickpulse avg BTCUSDT -f json | jq</c> sees exactly what the API sent.
/// </summary>
internal static partial class RawBodyView
{
    public static void Write(CliEnvironment environment, string raw, string contentType)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return;
        }

        var console = environment.Out;
        if (environment.IsOutputRedirected)
        {
            // Exactly the body, without an added newline, so the output can be piped into another tool.
            console.Profile.Out.Writer.Write(raw);
            console.Profile.Out.Writer.Flush();
            return;
        }

        if (contentType.Contains("json", StringComparison.OrdinalIgnoreCase) && IsJson(raw))
        {
            console.Write(new JsonText(raw)
                .MemberColor(Color.DeepSkyBlue1)
                .StringColor(Color.Green)
                .NumberColor(Color.Yellow)
                .BooleanColor(Color.Magenta1)
                .NullColor(Color.Grey)
                .BracesColor(Color.Grey)
                .BracketColor(Color.Grey)
                .ColonColor(Color.Grey)
                .CommaColor(Color.Grey));
            console.WriteLine();
            return;
        }

        if (contentType.Contains("xml", StringComparison.OrdinalIgnoreCase) && Indent(raw) is { } xml)
        {
            console.Write(new Markup(HighlightXml(xml)));
            console.WriteLine();
            return;
        }

        console.Write(new Text(raw));
        console.WriteLine();
    }

    /// <summary>Spectre markup for indented XML: tag names, attribute names and values, and text in distinct colours.</summary>
    internal static string HighlightXml(string xml)
    {
        var markup = new StringBuilder(xml.Length * 2);
        var position = 0;
        foreach (Match tag in TagPattern().Matches(xml))
        {
            markup.Append(Markup.Escape(xml[position..tag.Index]));
            markup.Append("[grey]").Append(Markup.Escape(tag.Groups["open"].Value)).Append("[/]");
            markup.Append("[deepskyblue1]").Append(Markup.Escape(tag.Groups["name"].Value)).Append("[/]");
            markup.Append(AttributePattern().Replace(
                Markup.Escape(tag.Groups["attributes"].Value),
                a => $"[magenta1]{a.Groups["key"].Value}[/][grey]=[/][green]{a.Groups["value"].Value}[/]"));
            markup.Append("[grey]").Append(Markup.Escape(tag.Groups["close"].Value)).Append("[/]");
            position = tag.Index + tag.Length;
        }

        markup.Append(Markup.Escape(xml[position..]));
        return markup.ToString();
    }

    private static bool IsJson(string raw)
    {
        try
        {
            using var _ = JsonDocument.Parse(raw);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? Indent(string raw)
    {
        try
        {
            return XDocument.Parse(raw).ToString(SaveOptions.None);
        }
        catch (XmlException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"(?<open></?|<\?)(?<name>[^\s/>?]+)(?<attributes>[^>]*?)(?<close>\??/?>)", RegexOptions.CultureInvariant)]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"(?<key>[\w:.\-]+)=(?<value>""[^""]*""|'[^']*')", RegexOptions.CultureInvariant)]
    private static partial Regex AttributePattern();
}

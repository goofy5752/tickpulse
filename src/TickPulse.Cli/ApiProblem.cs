using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using TickPulse.Contracts;

namespace TickPulse.Cli;

/// <summary>
/// An RFC 9457 problem as the CLI understands it: the <see cref="ProblemResponse"/> members plus the per-parameter
/// <see cref="Errors"/> of a validation problem. Read from <c>application/problem+json</c>, from
/// <c>application/problem+xml</c> (namespace <c>urn:ietf:rfc:7807</c>, best effort), or synthesised from the status
/// when the body is not a problem at all (e.g. a proxy's HTML error page).
/// </summary>
internal sealed record ApiProblem(
    string? Type,
    string? Title,
    int Status,
    string? Detail,
    string? TraceId,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Errors)
{
    public const string TypeBase = "https://tickpulse.dev/problems/";

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> NoErrors =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

    /// <summary>The TickPulse slug (<c>unknown-symbol</c>, <c>validation</c>, …), or <see langword="null"/> for other types.</summary>
    public string? Slug =>
        Type is not null && Type.StartsWith(TypeBase, StringComparison.Ordinal) ? Type[TypeBase.Length..] : null;

    /// <summary>Parses a failed response's body; never throws.</summary>
    public static ApiProblem Parse(int status, string? mediaType, string raw)
    {
        var parsed = mediaType switch
        {
            _ when string.IsNullOrWhiteSpace(raw) => null,
            not null when mediaType.Contains("json", StringComparison.OrdinalIgnoreCase) => FromJson(raw),
            not null when mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase) => FromXml(raw),
            _ => null,
        };

        var fallbackTitle = Enum.IsDefined((HttpStatusCode)status) ? Humanize(((HttpStatusCode)status).ToString()) : "HTTP error";
        return parsed is null
            ? new ApiProblem(null, fallbackTitle, status, null, null, NoErrors)
            : parsed with
            {
                // The HTTP status decides the exit code, whatever the body claims.
                Status = status,
                Title = string.IsNullOrWhiteSpace(parsed.Title) ? fallbackTitle : parsed.Title,
            };
    }

    private static ApiProblem? FromJson(string raw)
    {
        try
        {
            var problem = JsonSerializer.Deserialize<ProblemResponse>(raw, ApiJson.Options);
            if (problem is null)
            {
                return null;
            }

            using var document = JsonDocument.Parse(raw);
            var errors = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("errors", out var element)
                && element.ValueKind == JsonValueKind.Object)
            {
                foreach (var entry in element.EnumerateObject())
                {
                    errors[entry.Name] = entry.Value.ValueKind == JsonValueKind.Array
                        ? [.. entry.Value.EnumerateArray().Select(m => m.ValueKind == JsonValueKind.String ? m.GetString() ?? string.Empty : m.GetRawText())]
                        : [entry.Value.ToString()];
                }
            }

            return new ApiProblem(problem.Type, problem.Title, problem.Status, problem.Detail, problem.TraceId, errors);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ApiProblem? FromXml(string raw)
    {
        try
        {
            var root = XDocument.Parse(raw).Root;
            if (root is null)
            {
                return null;
            }

            // Matched by local name so a problem in another namespace is still read; TickPulse uses urn:ietf:rfc:7807.
            string? Child(string name) => root.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value;

            var errors = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            foreach (var message in root.Elements().Where(e => e.Name.LocalName == "errors").Elements())
            {
                var parameter = XmlConvert.DecodeName(message.Name.LocalName);
                errors[parameter] = errors.TryGetValue(parameter, out var existing) ? [.. existing, message.Value] : [message.Value];
            }

            var status = int.TryParse(Child("status"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : 0;
            return new ApiProblem(Child("type"), Child("title"), status, Child("detail"), Child("traceId"), errors);
        }
        catch (XmlException)
        {
            return null;
        }
    }

    private static string Humanize(string name) =>
        string.Concat(name.Select((c, i) => i > 0 && char.IsUpper(c) ? " " + char.ToLowerInvariant(c) : c.ToString()));
}

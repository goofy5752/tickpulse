using System.Net.Http.Headers;
using System.Text.Json;
using System.Xml.Linq;

namespace TickPulse.Integration.Tests.Api;

/// <summary>The RFC 9457 fields of a problem body, read from JSON or XML.</summary>
internal sealed record Problem(string? Type, string? Title, int? Status, string? Detail, string? TraceId, string Raw);

internal static class ProblemReader
{
    public const string TypeBase = "https://tickpulse.dev/problems/";
    public const string JsonMediaType = "application/problem+json";
    public const string XmlMediaType = "application/problem+xml";

    private static readonly XNamespace Rfc7807 = "urn:ietf:rfc:7807";

    /// <summary>Parses the body according to its content type; fails the test for anything but a problem type.</summary>
    public static async Task<Problem> ReadAsync(HttpResponseMessage response)
    {
        var raw = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        return mediaType switch
        {
            JsonMediaType => FromJson(raw),
            XmlMediaType => FromXml(raw),
            _ => throw new ShouldAssertException($"Expected a problem body, got '{mediaType}': {raw}"),
        };
    }

    public static HttpRequestMessage Get(string path, string? accept)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        if (accept is not null)
        {
            request.Headers.TryAddWithoutValidation("Accept", accept);
        }

        return request;
    }

    public static string? MediaType(HttpResponseMessage response) => response.Content.Headers.ContentType?.MediaType;

    public static MediaTypeHeaderValue? ContentType(HttpResponseMessage response) => response.Content.Headers.ContentType;

    private static Problem FromJson(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        return new Problem(
            String(root, "type"),
            String(root, "title"),
            root.TryGetProperty("status", out var status) ? status.GetInt32() : null,
            String(root, "detail"),
            String(root, "traceId"),
            raw);

        static string? String(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) ? value.GetString() : null;
    }

    private static Problem FromXml(string raw)
    {
        var root = XDocument.Parse(raw).Root!;
        root.Name.ShouldBe(Rfc7807 + "problem");
        return new Problem(
            Element(root, "type"),
            Element(root, "title"),
            Element(root, "status") is { } status ? int.Parse(status, System.Globalization.CultureInfo.InvariantCulture) : null,
            Element(root, "detail"),
            Element(root, "traceId"),
            raw);

        static string? Element(XElement element, string name) => element.Element(Rfc7807 + name)?.Value;
    }
}

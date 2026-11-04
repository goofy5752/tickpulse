using System.Runtime.Serialization;
using System.Text;
using System.Xml;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Formatters.Xml;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace TickPulse.Api.Problems;

/// <summary>
/// Writes problems as <c>application/problem+xml</c> (RFC 9457 Appendix B, namespace <c>urn:ietf:rfc:7807</c>) with
/// the <see cref="DataContractSerializer"/>, for clients whose <c>Accept</c> header prefers XML over JSON. Registered
/// before <see cref="JsonProblemDetailsWriter"/>, so every problem written through <see cref="IProblemDetailsService"/>
/// (controllers, status code pages, the exception handler, the rate limiter) is negotiated the same way.
/// </summary>
public sealed class XmlProblemDetailsWriter(IOptions<ProblemDetailsOptions> options) : IProblemDetailsWriter
{
    public const string ContentType = "application/problem+xml; charset=utf-8";

    private static readonly DataContractSerializer ProblemSerializer = new(typeof(ProblemDetailsWrapper));
    private static readonly DataContractSerializer ValidationSerializer = new(typeof(ValidationProblemDetailsWrapper));

    private static readonly XmlWriterSettings WriterSettings = new()
    {
        Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        OmitXmlDeclaration = true,
        CloseOutput = false,
    };

    /// <summary>
    /// Whether <paramref name="accept"/> prefers XML (<c>application/xml</c>, <c>text/xml</c>,
    /// <c>application/problem+xml</c>) over JSON. Wildcards count as JSON, the default; the higher quality wins, and on
    /// a tie the type listed first. Entries with <c>q=0</c> are ignored. A missing or unparsable header means JSON.
    /// </summary>
    public static bool PrefersXml(StringValues accept)
    {
        if (StringValues.IsNullOrEmpty(accept) || !MediaTypeHeaderValue.TryParseList(accept, out var mediaTypes))
        {
            return false;
        }

        var (xmlQuality, xmlIndex) = (0d, int.MaxValue);
        var (jsonQuality, jsonIndex) = (0d, int.MaxValue);
        for (var i = 0; i < mediaTypes.Count; i++)
        {
            var quality = mediaTypes[i].Quality ?? 1d;
            if (quality <= 0d)
            {
                continue;
            }

            var mediaType = mediaTypes[i].MediaType;
            if (IsXml(mediaType) && quality > xmlQuality)
            {
                (xmlQuality, xmlIndex) = (quality, i);
            }
            else if (IsJsonOrWildcard(mediaType) && quality > jsonQuality)
            {
                (jsonQuality, jsonIndex) = (quality, i);
            }
        }

        return xmlQuality > jsonQuality || (xmlQuality > 0d && xmlQuality.Equals(jsonQuality) && xmlIndex < jsonIndex);
    }

    public bool CanWrite(ProblemDetailsContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return PrefersXml(context.HttpContext.Request.Headers.Accept);
    }

    public async ValueTask WriteAsync(ProblemDetailsContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ProblemDefaults.Apply(context, options.Value);

        using var buffer = new MemoryStream();
        using (var writer = XmlWriter.Create(buffer, WriterSettings))
        {
            switch (context.ProblemDetails)
            {
                case ValidationProblemDetails validation:
                    ValidationSerializer.WriteObject(writer, new ValidationProblemDetailsWrapper(validation));
                    break;
                case HttpValidationProblemDetails validation:
                    ValidationSerializer.WriteObject(writer, new ValidationProblemDetailsWrapper(ToMvc(validation)));
                    break;
                default:
                    ProblemSerializer.WriteObject(writer, new ProblemDetailsWrapper(context.ProblemDetails));
                    break;
            }
        }

        var response = context.HttpContext.Response;
        response.ContentType = ContentType;
        await response.Body.WriteAsync(buffer.GetBuffer().AsMemory(0, (int)buffer.Length), context.HttpContext.RequestAborted);
    }

    private static bool IsXml(StringSegment mediaType) =>
        mediaType.Equals("application/xml", StringComparison.OrdinalIgnoreCase)
        || mediaType.Equals("text/xml", StringComparison.OrdinalIgnoreCase)
        || mediaType.Equals("application/problem+xml", StringComparison.OrdinalIgnoreCase);

    private static bool IsJsonOrWildcard(StringSegment mediaType) =>
        mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
        || mediaType.Equals("text/json", StringComparison.OrdinalIgnoreCase)
        || mediaType.Equals("application/problem+json", StringComparison.OrdinalIgnoreCase)
        || mediaType.Equals("*/*", StringComparison.Ordinal)
        || mediaType.Equals("application/*", StringComparison.OrdinalIgnoreCase)
        || mediaType.Equals("text/*", StringComparison.OrdinalIgnoreCase);

    private static ValidationProblemDetails ToMvc(HttpValidationProblemDetails source)
    {
        var copy = new ValidationProblemDetails(source.Errors)
        {
            Type = source.Type,
            Title = source.Title,
            Status = source.Status,
            Detail = source.Detail,
            Instance = source.Instance,
        };
        foreach (var (key, value) in source.Extensions)
        {
            copy.Extensions[key] = value;
        }

        return copy;
    }
}

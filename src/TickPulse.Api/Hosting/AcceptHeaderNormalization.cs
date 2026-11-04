using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace TickPulse.Api.Hosting;

/// <summary>
/// Reorders the <c>Accept</c> header by quality, then specificity (concrete type, then <c>type/*</c>, then
/// <c>*/*</c>), keeping the client's order otherwise. MVC's formatter selector sorts by quality only and takes the
/// first match, so <c>*/*, application/xml</c> would get JSON while RFC 9110 §12.5.1 lets the more specific range
/// win. Order carries no meaning in the header, so the reordering does not change what the client accepts. The problem
/// writers apply the same rule (<see cref="Problems.XmlProblemDetailsWriter.PrefersXml"/>), so successes and errors
/// are negotiated alike.
/// </summary>
internal static class AcceptHeaderNormalization
{
    public const int AnyType = 0;
    public const int AnySubtype = 1;
    public const int ConcreteType = 2;

    public static IApplicationBuilder UseAcceptHeaderNormalization(this IApplicationBuilder app) =>
        app.Use((context, next) =>
        {
            if (Normalize(context.Request.Headers.Accept) is { } normalized)
            {
                context.Request.Headers.Accept = normalized;
            }

            return next(context);
        });

    /// <summary>The reordered header, or <see langword="null"/> when it is already in order or cannot be parsed.</summary>
    public static string? Normalize(StringValues accept)
    {
        if (StringValues.IsNullOrEmpty(accept)
            || !MediaTypeHeaderValue.TryParseList(accept, out var mediaTypes)
            || mediaTypes.Count < 2)
        {
            return null;
        }

        var ordered = mediaTypes
            .Select((value, index) => (Value: value, Index: index))
            .OrderByDescending(e => e.Value.Quality ?? 1d)
            .ThenByDescending(e => Specificity(e.Value.MediaType))
            .ThenBy(e => e.Index)
            .ToList();

        for (var i = 0; i < ordered.Count; i++)
        {
            if (ordered[i].Index != i)
            {
                return string.Join(", ", ordered.Select(e => e.Value.ToString()));
            }
        }

        return null;
    }

    /// <summary><see cref="AnyType"/> for <c>*/*</c>, <see cref="AnySubtype"/> for <c>type/*</c>, else <see cref="ConcreteType"/>.</summary>
    public static int Specificity(StringSegment mediaType) =>
        mediaType.Equals("*/*", StringComparison.Ordinal) ? AnyType
        : mediaType.EndsWith("/*", StringComparison.Ordinal) ? AnySubtype
        : ConcreteType;
}

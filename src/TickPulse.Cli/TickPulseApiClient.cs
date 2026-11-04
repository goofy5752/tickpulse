using System.Globalization;
using System.Net.Http.Json;
using System.Runtime.Serialization;
using System.Text.Json;
using System.Xml;
using TickPulse.Contracts;

namespace TickPulse.Cli;

/// <summary>
/// The outcome of one API call: the parsed <see cref="Body"/> of a success, the <see cref="Raw"/> body exactly as
/// received, its <see cref="ContentType"/> and <see cref="Status"/>, and the <see cref="Problem"/> of a failure.
/// </summary>
internal sealed record ApiResponse<T>(T? Body, string Raw, string ContentType, int Status, ApiProblem? Problem)
    where T : class
{
    public bool IsSuccess => Problem is null;
}

/// <summary>A 2xx answer whose body could not be read as the expected contract, e.g. HTML from a wrong <c>--api-url</c>.</summary>
internal sealed class UnexpectedResponseException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>JSON settings matching the API: camelCase members, ISO-8601 timestamps, numbers as exact decimals.</summary>
internal static class ApiJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}

/// <summary>
/// Typed client of the TickPulse HTTP API. It never throws for an HTTP error status: failures come back as an
/// <see cref="ApiResponse{T}"/> with a <see cref="ApiProblem"/>. Network failures and timeouts surface as
/// <see cref="HttpRequestException"/> or <see cref="TaskCanceledException"/>.
/// </summary>
internal sealed class TickPulseApiClient(HttpClient http)
{
    public const string ApiKeyHeader = "X-Api-Key";

    private static readonly XmlReaderSettings XmlSettings = new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };

    /// <summary>The address requests are resolved against.</summary>
    public Uri? BaseAddress => http.BaseAddress;

    /// <summary><c>GET /api/{symbol}/24hAvgPrice</c>.</summary>
    public Task<ApiResponse<AveragePriceResponse>> GetAverageAsync(
        string symbol, OutputFormat format = OutputFormat.Table, CancellationToken ct = default) =>
        SendAsync<AveragePriceResponse>(HttpMethod.Get, $"api/{Escape(symbol)}/24hAvgPrice", format, apiKey: null, body: null, ct);

    /// <summary><c>GET /api/{symbol}/SimpleMovingAverage?n=&amp;p=&amp;s=</c>; <paramref name="s"/> is sent as UTC ISO-8601.</summary>
    public Task<ApiResponse<SmaResponse>> GetSmaAsync(
        string symbol, int n, string p, DateTime? s, OutputFormat format = OutputFormat.Table, CancellationToken ct = default) =>
        SendAsync<SmaResponse>(HttpMethod.Get, $"api/{Escape(symbol)}/SimpleMovingAverage?{SmaQuery(n, p, s)}", format, apiKey: null, body: null, ct);

    /// <summary><c>GET /api/symbols</c>: the public list with latest prices.</summary>
    public Task<ApiResponse<SymbolListResponse>> ListSymbolsAsync(OutputFormat format = OutputFormat.Table, CancellationToken ct = default) =>
        SendAsync<SymbolListResponse>(HttpMethod.Get, "api/symbols", format, apiKey: null, body: null, ct);

    /// <summary><c>GET /api/admin/symbols</c>: every symbol, active or not, with its backfill state.</summary>
    public Task<ApiResponse<AdminSymbolListResponse>> ListAdminSymbolsAsync(
        string apiKey, OutputFormat format = OutputFormat.Table, CancellationToken ct = default) =>
        SendAsync<AdminSymbolListResponse>(HttpMethod.Get, "api/admin/symbols", format, apiKey, body: null, ct);

    /// <summary><c>POST /api/admin/symbols</c>: 201 when added or reactivated, 200 when already active.</summary>
    public Task<ApiResponse<AdminSymbolResponse>> AddSymbolAsync(
        string symbol, string apiKey, OutputFormat format = OutputFormat.Table, CancellationToken ct = default) =>
        SendAsync<AdminSymbolResponse>(
            HttpMethod.Post,
            "api/admin/symbols",
            format,
            apiKey,
            JsonContent.Create(new AddSymbolRequest { Symbol = symbol }, options: ApiJson.Options),
            ct);

    /// <summary><c>DELETE /api/admin/symbols/{symbol}</c>: 204 when the symbol stopped being collected.</summary>
    public Task<ApiResponse<object>> RemoveSymbolAsync(
        string symbol, string apiKey, OutputFormat format = OutputFormat.Table, CancellationToken ct = default) =>
        SendAsync<object>(HttpMethod.Delete, $"api/admin/symbols/{Escape(symbol)}", format, apiKey, body: null, ct);

    /// <summary>The SMA query string in a fixed order, e.g. <c>n=20&amp;p=1d&amp;s=2026-10-01T00:00:00Z</c>.</summary>
    public static string SmaQuery(int n, string p, DateTime? s)
    {
        var query = $"n={n.ToString(CultureInfo.InvariantCulture)}&p={Uri.EscapeDataString(p)}";
        return s is { } start ? $"{query}&s={FormatTimestamp(start)}" : query;
    }

    /// <summary>UTC ISO-8601 with a <c>Z</c>, and fractional seconds only when there are any.</summary>
    public static string FormatTimestamp(DateTime value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture);

    private static string Escape(string symbol) => Uri.EscapeDataString(symbol);

    private async Task<ApiResponse<T>> SendAsync<T>(
        HttpMethod method, string path, OutputFormat format, string? apiKey, HttpContent? body, CancellationToken ct)
        where T : class
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative)) { Content = body };
        request.Headers.Accept.ParseAdd(format.AcceptHeader());
        if (apiKey is not null)
        {
            request.Headers.TryAddWithoutValidation(ApiKeyHeader, apiKey);
        }

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var contentType = response.Content.Headers.ContentType?.ToString() ?? string.Empty;
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        var status = (int)response.StatusCode;

        return response.IsSuccessStatusCode
            ? new ApiResponse<T>(ReadBody<T>(raw, mediaType, format), raw, contentType, status, Problem: null)
            : new ApiResponse<T>(Body: null, raw, contentType, status, ApiProblem.Parse(status, mediaType, raw));
    }

    /// <summary>
    /// Reads a 2xx body, which must be a document in the format that was asked for: JSON for tables and
    /// <c>--format json</c>, XML for <c>--format xml</c>. Anything else (HTML from a wrong <c>--api-url</c>, a
    /// truncated body, JSON when XML was asked for) is an <see cref="UnexpectedResponseException"/>, exit code 2.
    /// Tables also need the expected contract; raw output only needs a well-formed document.
    /// </summary>
    private static T? ReadBody<T>(string raw, string? mediaType, OutputFormat format)
        where T : class
    {
        // DELETE's 204 is the one success without a document.
        if (typeof(T) == typeof(object))
        {
            return null;
        }

        if (format == OutputFormat.Xml)
        {
            return IsWellFormedXml(raw) ? ReadXml<T>(raw) : throw Unreadable("an XML", mediaType, raw);
        }

        try
        {
            return JsonSerializer.Deserialize<T>(raw, ApiJson.Options) ?? throw Unreadable("a TickPulse JSON", mediaType, raw);
        }
        catch (JsonException e) when (format == OutputFormat.Table || !IsWellFormedJson(raw))
        {
            throw Unreadable(format == OutputFormat.Table ? "a TickPulse JSON" : "a JSON", mediaType, raw, e);
        }
        catch (JsonException)
        {
            // Well-formed JSON of another shape: --format json prints it as it is.
            return null;
        }
    }

    private static UnexpectedResponseException Unreadable(string expected, string? mediaType, string raw, Exception? inner = null)
    {
        var sent = string.IsNullOrWhiteSpace(raw) ? "an empty body" : mediaType ?? "a body without a media type";
        return new UnexpectedResponseException($"Expected {expected} document, but the server sent {sent}. Is --api-url right?", inner);
    }

    private static bool IsWellFormedJson(string raw)
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

    private static bool IsWellFormedXml(string raw)
    {
        try
        {
            using var reader = XmlReader.Create(new StringReader(raw), XmlSettings);
            while (reader.Read())
            {
            }

            return true;
        }
        catch (XmlException)
        {
            return false;
        }
    }

    // Best effort: the raw XML is what --format xml prints, the parsed body is a convenience.
    private static T? ReadXml<T>(string raw)
        where T : class
    {
        try
        {
            using var reader = XmlReader.Create(new StringReader(raw), XmlSettings);
            return new DataContractSerializer(typeof(T)).ReadObject(reader) as T;
        }
        catch (Exception e) when (e is SerializationException or XmlException)
        {
            return null;
        }
    }
}

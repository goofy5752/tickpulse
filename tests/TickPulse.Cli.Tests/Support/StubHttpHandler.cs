using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace TickPulse.Cli.Tests.Support;

/// <summary>A request as the stub saw it, copied before the message is disposed.</summary>
internal sealed record RecordedRequest(HttpMethod Method, Uri Uri, IReadOnlyDictionary<string, string> Headers, string? Body)
{
    public string? Header(string name) => Headers.GetValueOrDefault(name);
}

/// <summary>Answers every request with a canned response (or exception) and records what was sent.</summary>
internal sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<RecordedRequest> Requests { get; } = [];

    /// <summary>Runs after each response is produced, with the number of requests so far (e.g. to stop a watch).</summary>
    public Action<int>? AfterResponse { get; set; }

    public RecordedRequest Last => Requests[^1];

    public static StubHttpHandler Returns(HttpStatusCode status, string contentType, string body) =>
        new(_ => Response(status, contentType, body));

    public static StubHttpHandler Throws(Exception exception) => new(_ => throw exception);

    public static HttpResponseMessage Response(HttpStatusCode status, string contentType, string body)
    {
        var content = new StringContent(body, Encoding.UTF8);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        return new HttpResponseMessage(status) { Content = content };
    }

    public static HttpResponseMessage Empty(HttpStatusCode status) => new(status);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        Requests.Add(new RecordedRequest(request.Method, request.RequestUri!, headers, body));

        var response = respond(request);
        AfterResponse?.Invoke(Requests.Count);
        return response;
    }
}

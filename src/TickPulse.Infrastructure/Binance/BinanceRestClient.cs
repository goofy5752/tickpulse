using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TickPulse.Application.Abstractions;
using TickPulse.Domain;

namespace TickPulse.Infrastructure.Binance;

/// <summary>Binance answered a REST call with an error status (other than a ban).</summary>
public sealed class BinanceApiException : HttpRequestException
{
    public BinanceApiException()
    {
    }

    public BinanceApiException(string message)
        : base(message)
    {
    }

    public BinanceApiException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public BinanceApiException(HttpStatusCode statusCode, int? errorCode, string? errorMessage)
        : base(
            $"Binance REST call failed with {(int)statusCode}: {errorCode?.ToString(CultureInfo.InvariantCulture) ?? "?"} {errorMessage}",
            null,
            statusCode)
    {
        ErrorCode = errorCode;
    }

    /// <summary>The Binance error code from the body (e.g. <c>-1121</c>), when there was one.</summary>
    public int? ErrorCode { get; }
}

/// <summary>Binance has banned this IP (HTTP 418). No request may be sent before <see cref="BannedUntilUtc"/>.</summary>
public sealed class BinanceBannedException : Exception
{
    public BinanceBannedException()
    {
    }

    public BinanceBannedException(string message)
        : base(message)
    {
    }

    public BinanceBannedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public BinanceBannedException(DateTime bannedUntilUtc)
        : base($"Binance has banned this IP until {bannedUntilUtc.ToString("O", CultureInfo.InvariantCulture)}")
    {
        BannedUntilUtc = bannedUntilUtc;
    }

    public DateTime BannedUntilUtc { get; }
}

/// <summary>
/// Typed <see cref="HttpClient"/> for the Binance spot REST API (<c>Binance:RestBaseUrl</c>): exchange metadata,
/// 1-minute klines for backfill and aggregated trades for gap-fill.
/// </summary>
/// <remarks>
/// <para>Every request goes through the shared <see cref="BinanceWeightGuard"/> first. A <c>429</c> is retried
/// here, after its <c>Retry-After</c>, up to <see cref="MaxRateLimitRetries"/> times; a <c>418</c> throws
/// <see cref="BinanceBannedException"/>. The resilience pipeline registered by
/// <see cref="BinanceServiceCollectionExtensions.AddBinanceRest"/> retries the other transient failures and leaves
/// both statuses to this class.</para>
/// <para>Decimals arrive as JSON strings and are parsed with the invariant culture, never the current one.</para>
/// </remarks>
public sealed partial class BinanceRestClient : IExchangeMetadata, IKlineSource, IAggTradeSource
{
    /// <summary>Largest <c>limit</c> Binance accepts on <c>klines</c> and <c>aggTrades</c>.</summary>
    public const int MaxLimit = 1000;

    /// <summary>How many times one call retries after a <c>429</c> before it gives up.</summary>
    public const int MaxRateLimitRetries = 3;

    private const int InvalidSymbolCode = -1121;
    private const HttpStatusCode IpBanned = (HttpStatusCode)418;
    private const NumberStyles DecimalStyle = NumberStyles.AllowDecimalPoint;

    private readonly HttpClient _http;
    private readonly BinanceWeightGuard _guard;
    private readonly ILogger _logger;

    /// <param name="httpClient">Client whose <see cref="HttpClient.BaseAddress"/> is the REST base URL.</param>
    /// <param name="weightGuard">The process-wide request gate.</param>
    /// <param name="logger">Logger for pauses and bans.</param>
    public BinanceRestClient(HttpClient httpClient, BinanceWeightGuard weightGuard, ILogger<BinanceRestClient>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(weightGuard);
        _http = httpClient;
        _guard = weightGuard;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary><c>GET /api/v3/exchangeInfo?symbol=X</c>: true when the symbol exists and its status is <c>TRADING</c>.</summary>
    public async Task<bool> IsTradingSymbolAsync(SymbolName name, CancellationToken ct)
    {
        var symbol = Require(name);
        using var response = await SendAsync($"api/v3/exchangeInfo?symbol={symbol}", ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            var (code, message) = await ReadErrorAsync(response, ct).ConfigureAwait(false);
            return code == InvalidSymbolCode ? false : throw new BinanceApiException(response.StatusCode, code, message);
        }

        using var json = await ReadJsonAsync(response, ct).ConfigureAwait(false);
        var symbols = json.RootElement.GetProperty("symbols");
        return symbols.GetArrayLength() > 0
            && symbols[0].TryGetProperty("status", out var status)
            && status.ValueEquals("TRADING");
    }

    /// <summary>
    /// <c>GET /api/v3/klines?interval=1m</c> for open times in [<paramref name="fromUtc"/>, <paramref name="toUtc"/>],
    /// ascending, at most <paramref name="limit"/> (1..1000). Minutes without trades have no kline.
    /// </summary>
    public async Task<IReadOnlyList<Kline>> GetMinuteKlinesAsync(SymbolName s, DateTime fromUtc, DateTime toUtc, int limit, CancellationToken ct)
    {
        var symbol = Require(s);
        RequireLimit(limit);
        var startTime = ToUnixMs(fromUtc, nameof(fromUtc));
        var endTime = ToUnixMs(toUtc, nameof(toUtc));
        if (endTime < startTime)
        {
            throw new ArgumentException("toUtc must not be before fromUtc.", nameof(toUtc));
        }

        var uri = string.Create(
            CultureInfo.InvariantCulture,
            $"api/v3/klines?symbol={symbol}&interval=1m&startTime={startTime}&endTime={endTime}&limit={limit}");
        using var response = await SendAsync(uri, ct).ConfigureAwait(false);
        using var json = await ReadJsonAsync(response, ct).ConfigureAwait(false);

        // [openTime, open, high, low, close, volume, closeTime, quoteVolume, trades, takerBase, takerQuote, ignore]
        var klines = new List<Kline>(json.RootElement.GetArrayLength());
        foreach (var row in json.RootElement.EnumerateArray())
        {
            klines.Add(new Kline(
                FromUnixMs(row[0].GetInt64()),
                ParseDecimal(row[1]),
                ParseDecimal(row[2]),
                ParseDecimal(row[3]),
                ParseDecimal(row[4]),
                ParseDecimal(row[5]),
                ParseDecimal(row[7])));
        }

        return klines;
    }

    /// <summary><c>GET /api/v3/aggTrades?fromId</c>: trades with id ≥ <paramref name="fromId"/>, ascending, at most <paramref name="limit"/> (1..1000).</summary>
    public async Task<IReadOnlyList<(long Id, decimal Price, decimal Qty, DateTime TimeUtc)>> GetAggTradesFromAsync(
        SymbolName s, long fromId, int limit, CancellationToken ct)
    {
        var symbol = Require(s);
        ArgumentOutOfRangeException.ThrowIfNegative(fromId);
        RequireLimit(limit);

        var uri = string.Create(CultureInfo.InvariantCulture, $"api/v3/aggTrades?symbol={symbol}&fromId={fromId}&limit={limit}");
        using var response = await SendAsync(uri, ct).ConfigureAwait(false);
        using var json = await ReadJsonAsync(response, ct).ConfigureAwait(false);

        var trades = new List<(long, decimal, decimal, DateTime)>(json.RootElement.GetArrayLength());
        foreach (var trade in json.RootElement.EnumerateArray())
        {
            trades.Add((
                trade.GetProperty("a").GetInt64(),
                ParseDecimal(trade.GetProperty("p")),
                ParseDecimal(trade.GetProperty("q")),
                FromUnixMs(trade.GetProperty("T").GetInt64())));
        }

        return trades;
    }

    // Waits on the guard, sends, feeds the guard, and handles 429 (retry after Retry-After) and 418 (throw).
    // The guard is awaited before anything else, so a paused call has sent nothing when it returns its task.
    private async Task<HttpResponseMessage> SendAsync(string relativeUri, CancellationToken ct)
    {
        for (var rateLimited = 0; ; rateLimited++)
        {
            await _guard.WaitAsync(ct).ConfigureAwait(false);
            var response = await _http.GetAsync(relativeUri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            if (_guard.ObserveUsedWeight(response) is { } pause)
            {
                LogWeightPause(pause.UsedWeight, BinanceWeightGuard.UsedWeightLimit, pause.ResumeAt);
            }

            if (response.StatusCode == IpBanned)
            {
                var bannedUntil = _guard.Banned(RetryAfter(response));
                response.Dispose();
                LogBanned(bannedUntil);
                throw new BinanceBannedException(bannedUntil.UtcDateTime);
            }

            if (response.StatusCode != HttpStatusCode.TooManyRequests)
            {
                return response;
            }

            var resumeAt = _guard.RateLimited(RetryAfter(response));
            if (rateLimited >= MaxRateLimitRetries)
            {
                var (code, message) = await ReadErrorAsync(response, ct).ConfigureAwait(false);
                response.Dispose();
                throw new BinanceApiException(HttpStatusCode.TooManyRequests, code, message);
            }

            response.Dispose();
            LogRateLimited(resumeAt);
        }
    }

    private TimeSpan? RetryAfter(HttpResponseMessage response) => response.Headers.RetryAfter switch
    {
        { Delta: { } delta } => delta,
        { Date: { } date } => date - _guard.TimeProvider.GetUtcNow(),
        _ => null,
    };

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            var (code, message) = await ReadErrorAsync(response, ct).ConfigureAwait(false);
            throw new BinanceApiException(response.StatusCode, code, message);
        }

        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        }
    }

    // Binance error bodies are {"code":-1121,"msg":"Invalid symbol."}; anything else is reported as raw text.
    private static async Task<(int? Code, string? Message)> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("code", out var code) && code.TryGetInt32(out var value))
            {
                return (value, root.TryGetProperty("msg", out var msg) ? msg.GetString() : null);
            }
        }
        catch (JsonException)
        {
        }

        return (null, body.Length > 200 ? body[..200] : body);
    }

    private static decimal ParseDecimal(JsonElement element) =>
        decimal.Parse(element.GetString() ?? throw new JsonException("Expected a decimal string."), DecimalStyle, CultureInfo.InvariantCulture);

    private static string Require(SymbolName name) =>
        name.Value ?? throw new ArgumentException("Symbol must not be default.", nameof(name));

    private static void RequireLimit(int limit)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, MaxLimit);
    }

    private static long ToUnixMs(DateTime utc, string paramName) => utc.Kind == DateTimeKind.Local
        ? throw new ArgumentException("Expected a UTC time.", paramName)
        : new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    private static DateTime FromUnixMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Binance used weight {UsedWeight} is above {Limit}; pausing REST calls until {ResumeAt:O}")]
    private partial void LogWeightPause(int usedWeight, int limit, DateTimeOffset resumeAt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Binance answered 429; retrying after {ResumeAt:O}")]
    private partial void LogRateLimited(DateTimeOffset resumeAt);

    [LoggerMessage(Level = LogLevel.Error, Message = "Binance answered 418 (IP banned); no REST calls until {BannedUntil:O}")]
    private partial void LogBanned(DateTimeOffset bannedUntil);
}

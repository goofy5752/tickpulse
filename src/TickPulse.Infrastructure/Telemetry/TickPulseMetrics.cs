using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Threading.Channels;
using TickPulse.Application.Ingestion;
using TickPulse.Domain;
using TickPulse.Infrastructure.Ingestion;

namespace TickPulse.Infrastructure.Telemetry;

/// <summary>Why a WebSocket connection was opened (the <c>reason</c> tag of <c>tickpulse.ws.connections</c>).</summary>
public enum WsConnectionReason
{
    /// <summary>The first connection of a leadership session.</summary>
    Initial,

    /// <summary>A connection that replaced one that was lost.</summary>
    Reconnect,

    /// <summary>The planned make-before-break replacement.</summary>
    Rotation,
}

/// <summary>
/// The <c>TickPulse</c> meter and activity source. Instrument names, units and tags are a contract with the Grafana
/// dashboards and alert rules (<c>deploy/observability/README.md</c>); change them only together.
/// </summary>
/// <remarks>
/// Instruments are created once and every tag is a cached <see cref="KeyValuePair{TKey, TValue}"/> or a
/// <see cref="TagList"/> on the stack, so recording allocates nothing. The hot path records per batch or per burst of
/// trades, never per tick. Recording into an instrument nobody listens to costs a branch.
/// </remarks>
public sealed class TickPulseMetrics
{
    /// <summary>Name of the meter and of the activity source.</summary>
    public const string MeterName = "TickPulse";

    /// <summary>Name of the activity source (the same as the meter's).</summary>
    public const string ActivitySourceName = MeterName;

    /// <summary>Span around one batch write (COPY, insert, candle upsert, retries).</summary>
    public const string FlushSpan = "ingest.flush";

    /// <summary>Span around one page of kline backfill (REST fetch and insert).</summary>
    public const string BackfillPageSpan = "backfill.page";

    /// <summary>Span around one symbol's gap-fill after a reconnect.</summary>
    public const string GapFillSpan = "gapfill.run";

    /// <summary>Ingest lag buckets in milliseconds, around the 5 s alert threshold.</summary>
    public static readonly IReadOnlyList<double> LagBucketsMs = [50, 100, 250, 500, 1000, 2500, 5000, 10000, 30000];

    /// <summary>Batch write duration buckets in seconds.</summary>
    public static readonly IReadOnlyList<double> BatchDurationBucketsSeconds = [0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30];

    /// <summary>Batch size buckets in ticks, up to the default <c>Ingestion:BatchMaxSize</c>.</summary>
    public static readonly IReadOnlyList<long> BatchSizeBuckets = [1, 10, 50, 100, 250, 500, 1000, 2500, 5000];

    private static readonly KeyValuePair<string, object?>[] ReasonTags =
    [
        new("reason", "initial"),
        new("reason", "reconnect"),
        new("reason", "rotation"),
    ];

    // "100".."599", so a response never formats its status code.
    private static readonly string[] StatusCodes = [.. Enumerable.Range(0, 600).Select(i => i.ToString(CultureInfo.InvariantCulture))];

    private readonly IngestionState _state;
    private readonly SymbolIdCache _symbols;
    private readonly Counter<long> _ticksReceived;
    private readonly Counter<long> _ticksPersisted;
    private readonly Counter<long> _wsConnections;
    private readonly Counter<long> _backfillCandles;
    private readonly Counter<long> _gapFillTrades;
    private readonly Counter<long> _apiResponses;
    private volatile ChannelReader<Tick>? _channel;

    public TickPulseMetrics(IMeterFactory meterFactory, IngestionState state, SymbolIdCache symbols)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _symbols = symbols ?? throw new ArgumentNullException(nameof(symbols));
        var meter = meterFactory.Create(MeterName);

        _ticksReceived = meter.CreateCounter<long>(
            "tickpulse.ingest.ticks.received", "{tick}", "Trades received from the Binance stream.");
        _ticksPersisted = meter.CreateCounter<long>(
            "tickpulse.ingest.ticks.persisted", "{tick}", "Ticks newly stored (replays are not counted).");
        IngestLag = meter.CreateHistogram(
            "tickpulse.ingest.lag",
            "ms",
            "Time from the trade to its storage, for the newest tick of each stored batch.",
            tags: null,
            new InstrumentAdvice<double> { HistogramBucketBoundaries = LagBucketsMs });
        BatchSize = meter.CreateHistogram(
            "tickpulse.ingest.batch.size",
            "{tick}",
            "Ticks per stored batch.",
            tags: null,
            new InstrumentAdvice<long> { HistogramBucketBoundaries = BatchSizeBuckets });
        BatchDuration = meter.CreateHistogram(
            "tickpulse.ingest.batch.duration",
            "s",
            "Time to store one batch, retries included.",
            tags: null,
            new InstrumentAdvice<double> { HistogramBucketBoundaries = BatchDurationBucketsSeconds });
        meter.CreateObservableGauge(
            "tickpulse.ingest.channel.depth", () => _channel?.Count ?? 0, "{tick}", "Ticks waiting in the ingestion channel.");
        meter.CreateObservableGauge(
            "tickpulse.ingest.symbol.active",
            ObserveActiveSymbols,
            unit: null,
            "1 while this leader streams the symbol, 0 once it was removed.");
        _wsConnections = meter.CreateCounter<long>(
            "tickpulse.ws.connections", "{connection}", "WebSocket connections opened, by reason.");
        meter.CreateObservableGauge(
            "tickpulse.ws.state",
            () => (int)_state.Status,
            unit: null,
            "WebSocket state: 0 Standby, 1 Connecting, 2 Connected, 3 Disconnected.");
        meter.CreateObservableGauge(
            "tickpulse.leader.is_leader", () => _state.IsLeader ? 1 : 0, unit: null, "1 while this instance holds the leader lock.");
        _backfillCandles = meter.CreateCounter<long>(
            "tickpulse.backfill.candles", "{candle}", "1-minute klines stored by the backfill.");
        _gapFillTrades = meter.CreateCounter<long>(
            "tickpulse.gapfill.trades", "{trade}", "Trades recovered over REST after a reconnect.");
        _apiResponses = meter.CreateCounter<long>(
            "tickpulse.api.responses", "{response}", "API responses by route template, status code and negotiated media type.");
    }

    /// <summary>The activity source of the <c>ingest.flush</c>, <c>backfill.page</c> and <c>gapfill.run</c> spans.</summary>
    public static ActivitySource ActivitySource { get; } = new(ActivitySourceName);

    internal Histogram<double> IngestLag { get; }

    internal Histogram<long> BatchSize { get; }

    internal Histogram<double> BatchDuration { get; }

    /// <summary>Follows the depth of a session's tick channel; <see langword="null"/> when the session ends.</summary>
    public void TrackChannel(ChannelReader<Tick>? reader) => _channel = reader;

    public void TicksReceived(string symbol, long count) => _ticksReceived.Add(count, SymbolTag(symbol));

    public void TicksPersisted(string symbol, long count) => _ticksPersisted.Add(count, SymbolTag(symbol));

    /// <summary>A stored batch: its size, how long storing it took, and the ingest lag of its newest tick.</summary>
    public void BatchStored(int size, TimeSpan duration, double? lagMs)
    {
        BatchSize.Record(size);
        BatchDuration.Record(duration.TotalSeconds);
        if (lagMs is { } lag)
        {
            IngestLag.Record(lag);
        }
    }

    public void WsConnected(WsConnectionReason reason) => _wsConnections.Add(1, ReasonTags[(int)reason]);

    public void BackfillCandles(string symbol, long count) => _backfillCandles.Add(count, SymbolTag(symbol));

    public void GapFillTrades(string symbol, long count) => _gapFillTrades.Add(count, SymbolTag(symbol));

    /// <summary>One API response.</summary>
    /// <param name="route">The route template as in <c>http.route</c>; <see langword="null"/> when nothing matched.</param>
    /// <param name="status">The status code.</param>
    /// <param name="contentType">The media type without parameters (<c>none</c> for an empty response).</param>
    public void ApiResponse(string? route, int status, string contentType)
    {
        var tags = new TagList
        {
            { "status", (uint)status < StatusCodes.Length ? StatusCodes[status] : status.ToString(CultureInfo.InvariantCulture) },
            { "content_type", contentType },
        };
        if (route is not null)
        {
            tags.Add("route", route);
        }

        _apiResponses.Add(1, tags);
    }

    private static KeyValuePair<string, object?> SymbolTag(string symbol) => new("symbol", symbol);

    // Only the leader streams, so only the leader reports its symbols; a standby instance reports none.
    private IEnumerable<Measurement<int>> ObserveActiveSymbols()
    {
        if (!_state.IsLeader)
        {
            yield break;
        }

        foreach (var name in _symbols.KnownNames)
        {
            yield return new Measurement<int>(_symbols.IsActive(name) ? 1 : 0, SymbolTag(name));
        }
    }
}

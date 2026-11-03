using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace TickPulse.Infrastructure.Binance;

/// <summary>One Binance <c>aggTrade</c> event. Times are UTC.</summary>
public sealed record AggTradeEvent(
    string Symbol,
    long AggTradeId,
    decimal Price,
    decimal Quantity,
    DateTime TradeTimeUtc,
    DateTime EventTimeUtc);

/// <summary>A message read from the Binance combined stream.</summary>
public abstract record StreamMessage
{
    private StreamMessage()
    {
    }

    /// <summary>A <c>{"stream":"…@aggTrade","data":{…}}</c> payload.</summary>
    public sealed record AggTrade(AggTradeEvent Trade) : StreamMessage;

    /// <summary>A command reply, <c>{"result":…,"id":N}</c>.</summary>
    public sealed record CommandAck(long Id) : StreamMessage;

    /// <summary>A command error, <c>{"code":N,"msg":"…","id":N|null}</c>.</summary>
    public sealed record CommandError(long? Id, int Code, string Msg) : StreamMessage;

    /// <summary>Anything else, including malformed JSON. Callers skip it.</summary>
    public sealed record Unknown : StreamMessage
    {
        private Unknown()
        {
        }

        public static Unknown Instance { get; } = new();
    }
}

/// <summary>
/// Allocation-light parser for Binance combined-stream messages, built on <see cref="Utf8JsonReader"/>.
/// Prices and quantities are parsed from their UTF-8 strings with the invariant culture. It never throws:
/// anything it does not recognise is <see cref="StreamMessage.Unknown"/>.
/// </summary>
public static class StreamMessageParser
{
    private const int MaxSymbolLength = 32;
    private const int MaxCachedSymbols = 4096;

    // Unix ms bounds of DateTime.MinValue / MaxValue, so conversion never throws.
    private const long MinUnixMs = -62_135_596_800_000;
    private const long MaxUnixMs = 253_402_300_799_999;

    // Symbol strings are interned so that a steady stream of trades does not allocate one string per message.
    private static readonly ConcurrentDictionary<string, string> Symbols = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> SymbolLookup =
        Symbols.GetAlternateLookup<ReadOnlySpan<char>>();

    public static StreamMessage Parse(ReadOnlySpan<byte> utf8)
    {
        try
        {
            return ParseCore(utf8);
        }
        catch (JsonException)
        {
            return StreamMessage.Unknown.Instance;
        }
    }

    private static StreamMessage ParseCore(ReadOnlySpan<byte> utf8)
    {
        var reader = new Utf8JsonReader(utf8);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            return StreamMessage.Unknown.Instance;
        }

        AggTradeEvent? trade = null;
        bool hasResult = false, hasData = false;
        long? id = null;
        int? code = null;
        string? msg = null;

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("data"u8))
            {
                reader.Read();
                hasData = true;
                trade = reader.TokenType == JsonTokenType.StartObject ? ReadAggTrade(ref reader) : null;
                if (trade is null)
                {
                    return StreamMessage.Unknown.Instance;
                }
            }
            else if (reader.ValueTextEquals("id"u8))
            {
                reader.Read();
                if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var value))
                {
                    id = value;
                }
                else if (reader.TokenType != JsonTokenType.Null)
                {
                    return StreamMessage.Unknown.Instance;
                }
            }
            else if (reader.ValueTextEquals("result"u8))
            {
                hasResult = true;
                reader.Read();
                reader.Skip();
            }
            else if (reader.ValueTextEquals("code"u8))
            {
                reader.Read();
                if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out var value))
                {
                    return StreamMessage.Unknown.Instance;
                }

                code = value;
            }
            else if (reader.ValueTextEquals("msg"u8))
            {
                reader.Read();
                msg = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
            }
            else
            {
                reader.Read();
                reader.Skip();
            }
        }

        if (reader.TokenType != JsonTokenType.EndObject)
        {
            return StreamMessage.Unknown.Instance;
        }

        if (hasData)
        {
            return new StreamMessage.AggTrade(trade!);
        }

        if (code is { } errorCode)
        {
            return new StreamMessage.CommandError(id, errorCode, msg ?? string.Empty);
        }

        return hasResult && id is { } ackId
            ? new StreamMessage.CommandAck(ackId)
            : StreamMessage.Unknown.Instance;
    }

    // Reader is on the StartObject of "data"; leaves it on the matching EndObject. Null when it is not a valid aggTrade.
    private static AggTradeEvent? ReadAggTrade(ref Utf8JsonReader reader)
    {
        bool isAggTrade = false;
        string? symbol = null;
        long? aggTradeId = null, tradeTime = null, eventTime = null;
        decimal? price = null, quantity = null;

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            // Binance field names are case-sensitive ("e"/"E", "t"/"T").
            if (reader.ValueTextEquals("e"u8))
            {
                reader.Read();
                isAggTrade = reader.TokenType == JsonTokenType.String && reader.ValueTextEquals("aggTrade"u8);
            }
            else if (reader.ValueTextEquals("s"u8))
            {
                reader.Read();
                symbol = reader.TokenType == JsonTokenType.String ? ReadSymbol(ref reader) : null;
            }
            else if (reader.ValueTextEquals("a"u8))
            {
                reader.Read();
                aggTradeId = ReadInt64(ref reader);
            }
            else if (reader.ValueTextEquals("p"u8))
            {
                reader.Read();
                price = ReadDecimal(ref reader);
            }
            else if (reader.ValueTextEquals("q"u8))
            {
                reader.Read();
                quantity = ReadDecimal(ref reader);
            }
            else if (reader.ValueTextEquals("T"u8))
            {
                reader.Read();
                tradeTime = ReadInt64(ref reader);
            }
            else if (reader.ValueTextEquals("E"u8))
            {
                reader.Read();
                eventTime = ReadInt64(ref reader);
            }
            else
            {
                reader.Read();
                reader.Skip();
            }
        }

        if (reader.TokenType != JsonTokenType.EndObject
            || !isAggTrade
            || symbol is null
            || aggTradeId is null
            || price is not > 0m
            || quantity is not > 0m
            || !TryToUtc(tradeTime, out var tradeTimeUtc)
            || !TryToUtc(eventTime ?? tradeTime, out var eventTimeUtc))
        {
            return null;
        }

        return new AggTradeEvent(symbol, aggTradeId.Value, price.Value, quantity.Value, tradeTimeUtc, eventTimeUtc);
    }

    private static long? ReadInt64(ref Utf8JsonReader reader) =>
        reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var value) ? value : null;

    // Binance sends prices and quantities as strings ("67012.34000000"); plain numbers are accepted too.
    private static decimal? ReadDecimal(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            return reader.TryGetDecimal(out var number) ? number : null;
        }

        if (reader.TokenType != JsonTokenType.String || reader.ValueIsEscaped || reader.HasValueSequence)
        {
            return null;
        }

        return decimal.TryParse(
            reader.ValueSpan,
            NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture,
            out var value)
            ? value
            : null;
    }

    private static string? ReadSymbol(ref Utf8JsonReader reader)
    {
        var raw = reader.ValueSpan;
        if (raw.Length is 0 or > MaxSymbolLength || reader.ValueIsEscaped || reader.HasValueSequence)
        {
            return null;
        }

        Span<char> chars = stackalloc char[MaxSymbolLength];
        var length = Encoding.UTF8.GetChars(raw, chars);
        var span = chars[..length];
        if (SymbolLookup.TryGetValue(span, out var cached))
        {
            return cached;
        }

        var symbol = new string(span);
        return Symbols.Count < MaxCachedSymbols ? Symbols.GetOrAdd(symbol, symbol) : symbol;
    }

    private static bool TryToUtc(long? unixMs, out DateTime utc)
    {
        if (unixMs is not { } ms || ms < MinUnixMs || ms > MaxUnixMs)
        {
            utc = default;
            return false;
        }

        utc = DateTime.UnixEpoch.AddMilliseconds(ms);
        return true;
    }
}

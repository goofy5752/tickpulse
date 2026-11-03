using System.Text;
using TickPulse.Infrastructure.Binance;

namespace TickPulse.Integration.Tests.Binance;

public sealed class StreamMessageParserTests
{
    private const string AggTradePayload =
        """{"stream":"btcusdt@aggTrade","data":{"e":"aggTrade","E":1672531200456,"s":"BTCUSDT","a":26129,"p":"67012.34000000","q":"0.00150000","f":100,"l":105,"T":1672531200123,"m":true,"M":true}}""";

    [Fact]
    public void Parse_AggTrade_MapsFields()
    {
        var message = StreamMessageParser.Parse(Encoding.UTF8.GetBytes(AggTradePayload));

        var trade = message.ShouldBeOfType<StreamMessage.AggTrade>().Trade;
        trade.Symbol.ShouldBe("BTCUSDT");
        trade.AggTradeId.ShouldBe(26129L);
        trade.Price.ShouldBe(67012.34m);
        trade.Quantity.ShouldBe(0.0015m);
        trade.TradeTimeUtc.ShouldBe(new DateTime(2023, 1, 1, 0, 0, 0, 123, DateTimeKind.Utc));
        trade.TradeTimeUtc.Kind.ShouldBe(DateTimeKind.Utc);
        trade.EventTimeUtc.ShouldBe(new DateTime(2023, 1, 1, 0, 0, 0, 456, DateTimeKind.Utc));
        trade.EventTimeUtc.Kind.ShouldBe(DateTimeKind.Utc);
    }

    [Fact]
    public void Parse_AggTradeWithFieldsInAnyOrder_MapsFields()
    {
        const string payload =
            """{"data":{"T":1672531200123,"q":"2","p":"0.25","a":7,"s":"ADAUSDT","E":1672531200123,"e":"aggTrade"},"stream":"adausdt@aggTrade"}""";

        var trade = StreamMessageParser.Parse(Encoding.UTF8.GetBytes(payload))
            .ShouldBeOfType<StreamMessage.AggTrade>().Trade;

        trade.ShouldBe(new AggTradeEvent(
            "ADAUSDT",
            7,
            0.25m,
            2m,
            new DateTime(2023, 1, 1, 0, 0, 0, 123, DateTimeKind.Utc),
            new DateTime(2023, 1, 1, 0, 0, 0, 123, DateTimeKind.Utc)));
    }

    [Fact]
    public void Parse_SameSymbolTwice_ReusesSymbolString()
    {
        var bytes = Encoding.UTF8.GetBytes(AggTradePayload);

        var first = StreamMessageParser.Parse(bytes).ShouldBeOfType<StreamMessage.AggTrade>().Trade;
        var second = StreamMessageParser.Parse(bytes).ShouldBeOfType<StreamMessage.AggTrade>().Trade;

        ReferenceEquals(first.Symbol, second.Symbol).ShouldBeTrue();
    }

    [Theory]
    [InlineData("""{"result":null,"id":1}""", 1L)]
    [InlineData("""{"id":42,"result":null}""", 42L)]
    [InlineData("""{"result":["btcusdt@aggTrade"],"id":3}""", 3L)]
    public void Parse_Ack(string json, long expectedId)
    {
        var message = StreamMessageParser.Parse(Encoding.UTF8.GetBytes(json));

        message.ShouldBe(new StreamMessage.CommandAck(expectedId));
    }

    [Theory]
    [InlineData("""{"code":2,"msg":"Invalid request: unknown method","id":5}""", 5L, 2, "Invalid request: unknown method")]
    [InlineData("""{"code":3,"msg":"Invalid JSON","id":null}""", null, 3, "Invalid JSON")]
    [InlineData("""{"id":9,"msg":"Invalid request","code":1}""", 9L, 1, "Invalid request")]
    public void Parse_Error(string json, long? expectedId, int expectedCode, string expectedMsg)
    {
        var message = StreamMessageParser.Parse(Encoding.UTF8.GetBytes(json));

        message.ShouldBe(new StreamMessage.CommandError(expectedId, expectedCode, expectedMsg));
    }

    [Fact]
    public void Parse_ErrorWithNestedMsgObject_SkipsTheNestedValue()
    {
        const string json = """{"code":2,"msg":{"id":99,"detail":[1,{"code":7}]},"id":5}""";

        var message = StreamMessageParser.Parse(Encoding.UTF8.GetBytes(json));

        message.ShouldBe(new StreamMessage.CommandError(5, 2, string.Empty));
    }

    [Fact]
    public void Parse_AggTradeWithNestedEventObject_IsUnknown()
    {
        // A nested "e" must be skipped as one value, not read as if its members belonged to "data".
        const string json =
            """{"stream":"btcusdt@aggTrade","data":{"E":1,"s":"BTCUSDT","a":1,"p":"1","q":"1","T":1,"e":{"e":"aggTrade"}}}""";

        var message = StreamMessageParser.Parse(Encoding.UTF8.GetBytes(json));

        message.ShouldBeSameAs(StreamMessage.Unknown.Instance);
    }

    [Fact]
    public void Parse_AggTradeWithNestedUnknownFields_MapsFields()
    {
        const string json =
            """{"stream":"btcusdt@aggTrade","extra":{"a":[1,2]},"data":{"x":{"p":"9"},"e":"aggTrade","E":1,"s":"BTCUSDT","a":1,"p":"2","q":"3","T":1,"y":[{"q":"8"}]}}""";

        var trade = StreamMessageParser.Parse(Encoding.UTF8.GetBytes(json))
            .ShouldBeOfType<StreamMessage.AggTrade>().Trade;

        trade.Price.ShouldBe(2m);
        trade.Quantity.ShouldBe(3m);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"result":null}""")]
    [InlineData("""{"stream":"btcusdt@trade","data":{"e":"trade","E":1,"s":"BTCUSDT","t":1,"p":"1","q":"1","T":1}}""")]
    [InlineData("""{"stream":"btcusdt@aggTrade","data":{"e":"aggTrade","E":1,"s":"BTCUSDT","a":1,"p":"abc","q":"1","T":1}}""")]
    [InlineData("""{"stream":"btcusdt@aggTrade","data":{"e":"aggTrade","E":1,"s":"BTCUSDT","a":1,"p":"-1","q":"1","T":1}}""")]
    [InlineData("""{"stream":"btcusdt@aggTrade","data":{"e":"aggTrade","E":1,"s":"BTCUSDT","a":1,"p":"0","q":"1","T":1}}""")]
    [InlineData("""{"stream":"btcusdt@aggTrade","data":{"e":"aggTrade","E":1,"s":"BTCUSDT","a":1,"p":"1","q":"0","T":1}}""")]
    [InlineData("""{"stream":"btcusdt@aggTrade","data":{"e":"aggTrade","E":1,"s":"BTCUSDT","a":1,"p":"1,5","q":"1","T":1}}""")]
    [InlineData("""{"stream":"btcusdt@aggTrade","data":{"e":"aggTrade","E":1,"s":"BTCUSDT","a":1,"p":"1","T":1}}""")]
    [InlineData("""{"stream":"btcusdt@aggTrade","data":{"e":"aggTrade","E":1,"s":"","a":1,"p":"1","q":"1","T":1}}""")]
    [InlineData("""{"stream":"btcusdt@aggTrade","data":{"e":"aggTrade","E":1,"s":"BTCUSDT","a":1,"p":"1","q":"1","T":999999999999999999}}""")]
    [InlineData("""{"stream":"btcusdt@aggTrade","data":"oops"}""")]
    [InlineData("""{"stream":"btcusdt@aggTrade","data":{"e":"aggTrade","E":1,"s":"BTCUSDT","a":1,"p":"1","q":"1","T":1""")]
    public void Parse_Garbage_IsUnknown(string json)
    {
        var message = StreamMessageParser.Parse(Encoding.UTF8.GetBytes(json));

        message.ShouldBeSameAs(StreamMessage.Unknown.Instance);
    }
}

using JsonStudio.Core.Editing;
using JsonStudio.Core.Model;
using JsonStudio.Core.Serialization;

namespace JsonStudio.Core.Tests;

public class ConversionTests
{
    private static JsonNode Conv(string json, NodeType to)
    {
        var n = JsonParser.ParseOrThrow(json);
        long id = n.Id;
        TypeConversion.Convert(n, to);
        Assert.Equal(id, n.Id);
        Assert.Equal(to, n.Type);
        return n;
    }

    private static string C(JsonNode n) => JsonWriter.Compact(n);

    [Fact] public void NumberToString() => Assert.Equal("\"1.5\"", C(Conv("1.5", NodeType.String)));
    [Fact] public void IntegerNumberToString() => Assert.Equal("\"3\"", C(Conv("3.0", NodeType.String)));
    [Fact] public void BooleanToString() => Assert.Equal("\"true\"", C(Conv("true", NodeType.String)));
    [Fact] public void ObjectToString() => Assert.Equal("\"{\\\"a\\\":[1,2]}\"", C(Conv("{\"a\": [1, 2]}", NodeType.String)));
    [Fact] public void ArrayToString() => Assert.Equal("\"[1,\\\"x\\\"]\"", C(Conv("[1, \"x\"]", NodeType.String)));
    [Fact] public void ValidStringToNumber() => Assert.Equal("12.5", C(Conv("\"12.5\"", NodeType.Number)));
    [Fact] public void InvalidStringToNumber() => Assert.Equal("0", C(Conv("\"abc\"", NodeType.Number)));
    [Fact] public void TrueToNumber() => Assert.Equal("1", C(Conv("true", NodeType.Number)));
    [Fact] public void FalseToNumber() => Assert.Equal("0", C(Conv("false", NodeType.Number)));
    [Fact] public void StringTrueAnyCaseToBoolean() => Assert.Equal("true", C(Conv("\"TrUe\"", NodeType.Boolean)));
    [Fact] public void OtherStringToBoolean() => Assert.Equal("false", C(Conv("\"yes\"", NodeType.Boolean)));
    [Fact] public void NonZeroNumberToBoolean() => Assert.Equal("true", C(Conv("-2", NodeType.Boolean)));
    [Fact] public void ZeroToBoolean() => Assert.Equal("false", C(Conv("0", NodeType.Boolean)));

    [Theory]
    [InlineData("\"x\"")]
    [InlineData("1")]
    [InlineData("{\"a\":1}")]
    [InlineData("[1]")]
    [InlineData("true")]
    public void AnyToNull(string json) => Assert.Equal("null", C(Conv(json, NodeType.Null)));

    [Fact] public void JsonStringToObject() => Assert.Equal("{\"a\":1}", C(Conv("\"{\\\"a\\\":1}\"", NodeType.Object)));
    [Fact] public void JsonStringToArray() => Assert.Equal("[1,2]", C(Conv("\"[1,2]\"", NodeType.Array)));
    [Fact] public void WrongJsonStringToObject() => Assert.Equal("{}", C(Conv("\"[1,2]\"", NodeType.Object)));
    [Fact] public void TextToArray() => Assert.Equal("[]", C(Conv("\"hello\"", NodeType.Array)));
    [Fact] public void ArrayToObject() => Assert.Equal("{\"0\":\"a\",\"1\":\"b\",\"2\":{}}", C(Conv("[\"a\",\"b\",{}]", NodeType.Object)));
    [Fact] public void ObjectToArray() => Assert.Equal("[1,[2]]", C(Conv("{\"x\":1,\"y\":[2]}", NodeType.Array)));
    [Fact] public void PrimitiveToObject() => Assert.Equal("{}", C(Conv("42", NodeType.Object)));
    [Fact] public void PrimitiveToArray() => Assert.Equal("[]", C(Conv("true", NodeType.Array)));

    [Fact]
    public void ArrayToObject_KeepsItemIds()
    {
        var n = JsonParser.ParseOrThrow("[\"a\",\"b\"]");
        var ids = n.Children.Select(c => c.Id).ToList();
        TypeConversion.Convert(n, NodeType.Object);
        Assert.Equal(ids, n.Children.Select(c => c.Id).ToList());
        Assert.Equal(new[] { "0", "1" }, n.Children.Select(c => c.Key));
    }
}

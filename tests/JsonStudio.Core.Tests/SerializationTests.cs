using JsonStudio.Core.Model;
using JsonStudio.Core.Serialization;

namespace JsonStudio.Core.Tests;

public class SerializationTests
{
    private static string Data(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", name));

    public static IEnumerable<object[]> Cases()
    {
        foreach (var c in new[] { "sample", "empties", "strings", "numbers", "primitive", "deep" })
            foreach (var i in new[] { "2", "4", "tab" })
                yield return new object[] { c, i };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void RoundTrip_MatchesJsonStringify(string name, string indentName)
    {
        string indent = indentName switch { "2" => "  ", "4" => "    ", _ => "\t" };
        var root = JsonParser.ParseOrThrow(Data(name + ".input.json"));
        var text = JsonWriter.Serialize(root, indent);
        Assert.Equal(Data($"{name}.{indentName}.expected.json"), text);
    }

    [Fact]
    public void DuplicateKeys_AreKeptInOrder()
    {
        var root = JsonParser.ParseOrThrow("{\"a\":1,\"b\":2,\"a\":3}");
        Assert.Equal(3, root.Children.Count);
        Assert.Equal("{\n  \"a\": 1,\n  \"b\": 2,\n  \"a\": 3\n}\n", JsonWriter.Serialize(root, "  "));
    }

    [Fact]
    public void Sample_SerializesLikeJsonStringify()
    {
        var root = JsonParser.ParseOrThrow(JsonDocument.SampleText);
        Assert.Equal(Data("sample.2.expected.json"), JsonWriter.Serialize(root, "  "));
    }

    [Fact]
    public void LineSpans_PointToTheNodeText()
    {
        var root = JsonParser.ParseOrThrow(JsonDocument.SampleText);
        const string indent = "  ";
        var result = JsonWriter.SerializeWithSpans(root, indent);
        var lines = result.Text.Split('\n');

        foreach (var node in root.Descendants())
        {
            var span = result.Spans[node.Id];
            var slice = lines[(span.StartLine - 1)..span.EndLine].ToList();
            // First line: strip the indent and the key.
            var first = slice[0].TrimStart(' ');
            if (node.Parent?.IsObject == true)
            {
                var keyPrefix = JsonWriter.QuoteString(node.Key) + ": ";
                Assert.StartsWith(keyPrefix, first);
                first = first[keyPrefix.Length..];
            }
            slice[0] = first;
            slice[^1] = slice[^1].TrimEnd(',');

            // Expected: the node serialized alone, re-indented to its depth.
            var alone = JsonWriter.Serialize(node, indent).TrimEnd('\n').Split('\n').ToList();
            string pad = string.Concat(Enumerable.Repeat(indent, node.Depth));
            for (int i = 1; i < alone.Count; i++) alone[i] = pad + alone[i];
            Assert.Equal(string.Join("\n", alone), string.Join("\n", slice));
        }
    }

    [Fact]
    public void LineSpans_SpecificLines()
    {
        var root = JsonParser.ParseOrThrow(JsonDocument.SampleText);
        var r = JsonWriter.SerializeWithSpans(root, "  ");
        var measures = root.Get("measures")!;
        var accel = measures.Children[0].Get("accelerators")!;
        var rate = accel.Children[1].Get("rate")!;
        Assert.Equal(new LineSpan(1, r.Text.Count(c => c == '\n')), r.Spans[root.Id]);
        Assert.Equal(new LineSpan(8, 38), r.Spans[measures.Id]);
        Assert.Equal(new LineSpan(14, 23), r.Spans[accel.Id]);
        Assert.Equal(new LineSpan(21, 21), r.Spans[rate.Id]);
        Assert.Contains("\"rate\": 2", r.Text.Split('\n')[20]);
    }

    [Theory]
    [InlineData("{\"a\": 1,}", 1)]
    [InlineData("{\n  \"a\": tru\n}", 2)]
    [InlineData("[1, 2\n", 2)]
    [InlineData("", 1)]
    public void ParseErrors_ReportLineAndColumn(string text, int line)
    {
        var r = JsonParser.Parse(text);
        Assert.False(r.Success);
        Assert.Equal(line, r.Error!.Line);
        Assert.True(r.Error.Column >= 1);
        Assert.False(string.IsNullOrWhiteSpace(r.Error.Message));
        Assert.DoesNotContain("LineNumber", r.Error.Message);
    }

    [Fact]
    public void ParseError_Column()
    {
        var r = JsonParser.Parse("{\n  \"a\": x\n}");
        Assert.Equal(2, r.Error!.Line);
        Assert.Equal(8, r.Error.Column);
    }

    [Theory]
    [InlineData("{\n  \"a\": 1\n}", "  ")]
    [InlineData("{\n    \"a\": 1\n}", "    ")]
    [InlineData("{\n\t\"a\": 1\n}", "\t")]
    [InlineData("{\"a\": 1}", null)]
    public void DetectsIndent(string text, string? indent)
    {
        Assert.Equal(indent, JsonParser.Parse(text).Indent);
    }

    [Theory]
    [InlineData(1.0, "1")]
    [InlineData(0.1, "0.1")]
    [InlineData(1e21, "1e+21")]
    [InlineData(1e-7, "1e-7")]
    [InlineData(123e-20, "1.23e-18")]
    [InlineData(-0.0, "0")]
    [InlineData(double.NaN, "null")]
    [InlineData(1234567.125, "1234567.125")]
    public void FormatNumber_LikeJavaScript(double v, string expected) =>
        Assert.Equal(expected, JsonWriter.FormatNumber(v));

    [Fact]
    public void Compact_LikeStringifyWithoutIndent()
    {
        var root = JsonParser.ParseOrThrow("{ \"a\": [1, {\"b\": null}], \"c\": \"x\" }");
        Assert.Equal("{\"a\":[1,{\"b\":null}],\"c\":\"x\"}", JsonWriter.Compact(root));
    }

    [Theory]
    [InlineData("true", NodeType.Boolean)]
    [InlineData("false", NodeType.Boolean)]
    [InlineData("null", NodeType.Null)]
    [InlineData("42", NodeType.Number)]
    [InlineData("-1.5e3", NodeType.Number)]
    [InlineData("[]", NodeType.Array)]
    [InlineData("{}", NodeType.Object)]
    [InlineData("hello", NodeType.String)]
    [InlineData("12abc", NodeType.String)]
    [InlineData("Infinity", NodeType.String)]
    public void LiteralParsing(string text, NodeType type) =>
        Assert.Equal(type, LiteralParser.Parse(text).Type);

    [Fact]
    public void LiteralParsing_StringColumnAlwaysString()
    {
        var n = LiteralParser.Parse("42", forceString: true);
        Assert.Equal(NodeType.String, n.Type);
        Assert.Equal("42", n.StringValue);
    }

    [Fact]
    public void Paths()
    {
        var root = JsonParser.ParseOrThrow(JsonDocument.SampleText);
        var rate = root.Get("measures")!.Children[0].Get("accelerators")!.Children[1].Get("rate")!;
        Assert.Equal("$.measures[0].accelerators[1].rate", NodePath.Format(rate));
        Assert.Same(rate, NodePath.Resolve(root, NodePath.Segments(rate)));
        var odd = JsonParser.ParseOrThrow("{\"a b\":1}").Children[0];
        Assert.Equal("$[\"a b\"]", NodePath.Format(odd));
    }
}

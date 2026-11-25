using JsonStudio.Core.Model;
using JsonStudio.Core.Serialization;

namespace JsonStudio.Core.Editing;

/// <summary>Converts a node to another type in place (spec 4.5). The node keeps its id and key.</summary>
public static class TypeConversion
{
    public static void Convert(JsonNode node, NodeType target)
    {
        var from = node.Type;
        if (from == target) return;

        switch (target)
        {
            case NodeType.Null:
                node.SetPrimitive(NodeType.Null, null);
                return;

            case NodeType.String:
                string text = from switch
                {
                    NodeType.Number => JsonWriter.FormatNumber(node.NumberValue),
                    NodeType.Boolean => node.BoolValue ? "true" : "false",
                    NodeType.Object or NodeType.Array => JsonWriter.Compact(node),
                    _ => "",
                };
                node.SetPrimitive(NodeType.String, text);
                return;

            case NodeType.Number:
                double d = from switch
                {
                    NodeType.String => LiteralParser.TryParseNumber(node.StringValue, out var v) ? v : 0,
                    NodeType.Boolean => node.BoolValue ? 1 : 0,
                    _ => 0,
                };
                node.SetPrimitive(NodeType.Number, d);
                return;

            case NodeType.Boolean:
                bool b = from switch
                {
                    NodeType.String => string.Equals(node.StringValue.Trim(), "true", StringComparison.OrdinalIgnoreCase),
                    NodeType.Number => node.NumberValue != 0,
                    _ => false,
                };
                node.SetPrimitive(NodeType.Boolean, b);
                return;

            case NodeType.Object:
            case NodeType.Array:
                ConvertToContainer(node, target);
                return;
        }
    }

    private static void ConvertToContainer(JsonNode node, NodeType target)
    {
        var from = node.Type;
        if (from == NodeType.String)
        {
            var parsed = JsonParser.Parse(node.StringValue);
            node.SetPrimitive(target, null);
            if (parsed.Root != null && parsed.Root.Type == target)
            {
                foreach (var c in parsed.Root.Children.ToList()) node.Add(c);
            }
            return;
        }

        if (from == NodeType.Array && target == NodeType.Object)
        {
            node.Type = NodeType.Object;
            for (int i = 0; i < node.Children.Count; i++) node.Children[i].Key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return;
        }

        if (from == NodeType.Object && target == NodeType.Array)
        {
            node.Type = NodeType.Array;
            foreach (var c in node.Children) c.Key = "";
            return;
        }

        node.SetPrimitive(target, null);
    }

    /// <summary>The empty value for a type: "" / 0 / false / null / {} / [].</summary>
    public static JsonNode Blank(NodeType type) => type switch
    {
        NodeType.String => JsonNode.NewString(""),
        NodeType.Number => JsonNode.NewNumber(0),
        NodeType.Boolean => JsonNode.NewBoolean(false),
        NodeType.Object => JsonNode.NewObject(),
        NodeType.Array => JsonNode.NewArray(),
        _ => JsonNode.NewNull(),
    };

    public static string TypeName(NodeType t) => t switch
    {
        NodeType.Object => "object",
        NodeType.Array => "array",
        NodeType.String => "string",
        NodeType.Number => "number",
        NodeType.Boolean => "boolean",
        _ => "null",
    };

    public static NodeType? ParseTypeName(string name) => name switch
    {
        "object" => NodeType.Object,
        "array" => NodeType.Array,
        "string" => NodeType.String,
        "number" => NodeType.Number,
        "boolean" => NodeType.Boolean,
        "null" => NodeType.Null,
        _ => null,
    };
}

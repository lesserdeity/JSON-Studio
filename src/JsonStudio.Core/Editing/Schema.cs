using System.Text;
using JsonStudio.Core.Model;
using JsonStudio.Core.Serialization;

namespace JsonStudio.Core.Editing;

/// <summary>Column types that the user can choose for a column.</summary>
public enum ColumnKind
{
    String,
    Number,
    Boolean,
    Null,
    Object,
    ArrayOfObjects,
    ArrayOfStrings,
    ArrayOfNumbers,
    ArrayOfBooleans,
}

/// <summary>One column of a table.</summary>
public sealed class ColumnInfo
{
    public const string ValueColumnId = "\u0000value";

    public required string Name { get; init; }
    public bool IsValueColumn { get; init; }

    /// <summary>string, number, boolean, null, object, array of objects, …, array, mixed or empty.</summary>
    public required string TypeLabel { get; init; }

    /// <summary>Unique column id: the key, or <see cref="ValueColumnId"/> for the value column.</summary>
    public string Id => IsValueColumn ? ValueColumnId : Name;

    /// <summary>The container type of this column, for the "+ [ ]" / "+ { }" pill of missing cells.</summary>
    public NodeType? ContainerType => TypeLabel switch
    {
        "object" => NodeType.Object,
        _ when TypeLabel.StartsWith("array", StringComparison.Ordinal) => NodeType.Array,
        _ => null,
    };

    public bool IsStringColumn => TypeLabel == "string";

    public override string ToString() => $"{Name} ({TypeLabel})";
}

/// <summary>Peer groups, shared table schema and item-type hints (spec 6.6).</summary>
public static class Schema
{
    public static readonly ColumnKind[] AllKinds = Enum.GetValues<ColumnKind>();

    public static string Label(ColumnKind k) => k switch
    {
        ColumnKind.String => "string",
        ColumnKind.Number => "number",
        ColumnKind.Boolean => "boolean",
        ColumnKind.Null => "null",
        ColumnKind.Object => "object",
        ColumnKind.ArrayOfObjects => "array of objects",
        ColumnKind.ArrayOfStrings => "array of strings",
        ColumnKind.ArrayOfNumbers => "array of numbers",
        ColumnKind.ArrayOfBooleans => "array of booleans",
        _ => "string",
    };

    /// <summary>For "array of X", the item type X; otherwise null.</summary>
    public static NodeType? ItemType(ColumnKind k) => k switch
    {
        ColumnKind.ArrayOfObjects => NodeType.Object,
        ColumnKind.ArrayOfStrings => NodeType.String,
        ColumnKind.ArrayOfNumbers => NodeType.Number,
        ColumnKind.ArrayOfBooleans => NodeType.Boolean,
        _ => null,
    };

    public static NodeType NodeTypeOf(ColumnKind k) => k switch
    {
        ColumnKind.String => NodeType.String,
        ColumnKind.Number => NodeType.Number,
        ColumnKind.Boolean => NodeType.Boolean,
        ColumnKind.Null => NodeType.Null,
        ColumnKind.Object => NodeType.Object,
        _ => NodeType.Array,
    };

    // ---- peer groups ----------------------------------------------------

    /// <summary>
    /// The peer key of a node: its path with each array index replaced by <c>[*]</c>,
    /// for example <c>$.measures[*].accelerators</c>.
    /// </summary>
    public static string PeerKey(JsonNode node)
    {
        var chain = new List<JsonNode>();
        for (var n = node; n.Parent != null; n = n.Parent) chain.Add(n);
        chain.Reverse();
        var sb = new StringBuilder("$");
        foreach (var n in chain)
        {
            if (n.Parent!.Type == NodeType.Array) sb.Append("[*]");
            else NodePath.AppendKey(sb, n.Key);
        }
        return sb.ToString();
    }

    /// <summary>Peer key of the arrays/objects in column <paramref name="column"/> of a table's rows.</summary>
    public static string ColumnPeerKey(JsonNode table, string column)
    {
        var sb = new StringBuilder(PeerKey(table));
        if (table.Type == NodeType.Array) sb.Append("[*]");
        NodePath.AppendKey(sb, column);
        return sb.ToString();
    }

    /// <summary>All nodes in the peer group of <paramref name="node"/>, including the node. Document order.</summary>
    public static List<JsonNode> GetPeers(JsonNode root, JsonNode node)
    {
        var segs = new List<string?>(); // null = any index
        for (var n = node; n.Parent != null; n = n.Parent)
            segs.Add(n.Parent.Type == NodeType.Array ? null : n.Key);
        segs.Reverse();

        var current = new List<JsonNode> { root };
        foreach (var s in segs)
        {
            var next = new List<JsonNode>();
            foreach (var c in current)
            {
                if (s == null)
                {
                    if (c.Type == NodeType.Array) next.AddRange(c.Children);
                }
                else if (c.Type == NodeType.Object)
                {
                    foreach (var k in c.Children)
                        if (k.Key == s) next.Add(k);
                }
            }
            current = next;
            if (current.Count == 0) break;
        }
        if (!current.Contains(node)) current.Add(node);
        return current;
    }

    // ---- tables -----------------------------------------------------------

    /// <summary>The rows of a table: the items of an array, otherwise the node itself.</summary>
    public static List<JsonNode> Rows(JsonNode table) =>
        table.Type == NodeType.Array ? table.Children.ToList() : new List<JsonNode> { table };

    /// <summary>Columns of a table: its own rows plus the rows of all peer tables.</summary>
    public static List<ColumnInfo> GetColumns(JsonNode root, JsonNode table, IReadOnlyDictionary<string, string>? hints = null)
    {
        var own = Rows(table);
        var peerRows = new List<JsonNode>();
        if (table.Type == NodeType.Array)
        {
            foreach (var p in GetPeers(root, table))
                if (p != table && p.Type == NodeType.Array) peerRows.AddRange(p.Children);
        }

        string? hint = null;
        hints?.TryGetValue(PeerKey(table), out hint);

        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in own.Concat(peerRows))
        {
            if (r.Type != NodeType.Object) continue;
            foreach (var c in r.Children)
                if (seen.Add(c.Key)) names.Add(c.Key);
        }

        var cols = new List<ColumnInfo>();
        bool ownPlain = own.Any(r => r.IsPrimitive);
        bool peerPlain = peerRows.Any(r => r.IsPrimitive);
        bool hintPlain = hint != null && hint != "object" && TypeConversion.ParseTypeName(hint) is { } ht && ht is not NodeType.Object and not NodeType.Array;
        bool hasValueCol = ownPlain || (table.Type == NodeType.Array && own.Count == 0 &&
                                        (peerPlain || (hintPlain && names.Count == 0)));
        if (table.IsPrimitive) hasValueCol = true;

        if (hasValueCol)
        {
            var vals = own.Where(r => r.IsPrimitive).ToList();
            if (vals.Count == 0) vals = peerRows.Where(r => r.IsPrimitive).ToList();
            string label = TypeLabel(vals, null);
            if (label == "empty" && hint != null) label = hint;
            cols.Add(new ColumnInfo { Name = "value", IsValueColumn = true, TypeLabel = label });
        }

        foreach (var name in names)
        {
            var vals = CellsOf(own, name);
            if (vals.Count == 0) vals = CellsOf(peerRows, name);
            string? colHint = null;
            hints?.TryGetValue(ColumnPeerKey(table, name), out colHint);
            cols.Add(new ColumnInfo { Name = name, TypeLabel = TypeLabel(vals, colHint) });
        }
        return cols;
    }

    private static List<JsonNode> CellsOf(List<JsonNode> rows, string name)
    {
        var list = new List<JsonNode>();
        foreach (var r in rows)
        {
            if (r.Type != NodeType.Object) continue;
            var c = r.Get(name);
            if (c != null) list.Add(c);
        }
        return list;
    }

    /// <summary>The column type label for a set of values.</summary>
    public static string TypeLabel(IReadOnlyList<JsonNode> values, string? arrayItemHint)
    {
        if (values.Count == 0) return "empty";
        var nonNull = values.Where(v => v.Type != NodeType.Null).ToList();
        if (nonNull.Count == 0) return "null";
        var types = nonNull.Select(v => v.Type).Distinct().ToList();
        if (types.Count > 1) return "mixed";
        switch (types[0])
        {
            case NodeType.String: return "string";
            case NodeType.Number: return "number";
            case NodeType.Boolean: return "boolean";
            case NodeType.Object: return "object";
        }
        var items = nonNull.SelectMany(a => a.Children).Where(i => i.Type != NodeType.Null).ToList();
        if (items.Count == 0)
            return arrayItemHint != null ? "array of " + Plural(arrayItemHint) : "array";
        var itemTypes = items.Select(i => i.Type).Distinct().ToList();
        if (itemTypes.Count > 1) return "array";
        return itemTypes[0] switch
        {
            NodeType.Object => "array of objects",
            NodeType.String => "array of strings",
            NodeType.Number => "array of numbers",
            NodeType.Boolean => "array of booleans",
            _ => "array",
        };
    }

    private static string Plural(string typeName) => typeName switch
    {
        "object" => "objects",
        "string" => "strings",
        "number" => "numbers",
        "boolean" => "booleans",
        _ => typeName,
    };

    /// <summary>A blank value for a column type label: "" / 0 / false / null / {} / [].</summary>
    public static JsonNode BlankFor(string typeLabel) => typeLabel switch
    {
        "string" => JsonNode.NewString(""),
        "number" => JsonNode.NewNumber(0),
        "boolean" => JsonNode.NewBoolean(false),
        "object" => JsonNode.NewObject(),
        _ when typeLabel.StartsWith("array", StringComparison.Ordinal) => JsonNode.NewArray(),
        _ => JsonNode.NewNull(),
    };

    public static JsonNode BlankFor(ColumnKind kind) => BlankFor(Label(kind));

    /// <summary>The cell node for a row and column, or null when the key is missing / not applicable.</summary>
    public static JsonNode? Cell(JsonNode row, ColumnInfo col)
    {
        if (col.IsValueColumn) return row.IsPrimitive ? row : null;
        return row.Type == NodeType.Object ? row.Get(col.Name) : null;
    }

    /// <summary>True when the cell can never hold a value (value column of an object row, key column of a plain row).</summary>
    public static bool IsNotApplicable(JsonNode row, ColumnInfo col) =>
        col.IsValueColumn ? !row.IsPrimitive : row.Type != NodeType.Object;

    /// <summary>A row label such as <c>[0] growth</c>, using name, title, label, id, key or code.</summary>
    public static string RowLabel(JsonNode row)
    {
        string head = row.IsArrayItem ? $"[{row.Index}]" : (row.Parent == null ? "root" : row.Key);
        var name = DisplayName(row);
        return name == null ? head : $"{head} {name}";
    }

    public static string? DisplayName(JsonNode row)
    {
        if (row.Type != NodeType.Object) return null;
        foreach (var k in new[] { "name", "id", "key", "code", "title", "label" })
        {
            var c = row.Get(k);
            if (c == null) continue;
            if (c.Type == NodeType.String && c.StringValue.Length > 0) return c.StringValue;
            if (c.Type == NodeType.Number) return JsonWriter.FormatNumber(c.NumberValue);
        }
        return null;
    }

    /// <summary>The field name that keeps a plain value when an array of plain values becomes objects.</summary>
    public static string SingularFieldName(JsonNode array, string? avoid = null)
    {
        string name = "value";
        if (array.Parent?.Type == NodeType.Object)
        {
            var k = array.Key;
            if (k.Length > 1 && k.EndsWith('s')) name = k[..^1];
        }
        if (name == avoid) name = name == "value" ? "item" : "value";
        return name;
    }
}

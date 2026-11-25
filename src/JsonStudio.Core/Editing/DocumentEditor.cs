using JsonStudio.Core.Model;
using JsonStudio.Core.Serialization;

namespace JsonStudio.Core.Editing;

public enum ChangeKind
{
    /// <summary>A primitive value changed; type and structure are the same.</summary>
    Value,
    /// <summary>Nodes were added, removed, moved, renamed or changed type.</summary>
    Structure,
    /// <summary>The whole root was replaced (load, undo, redo, code edit).</summary>
    Root,
}

public sealed class DocumentChangedEventArgs : EventArgs
{
    public DocumentChangedEventArgs(ChangeKind kind, JsonNode? node, object? source)
    {
        Kind = kind;
        Node = node;
        Source = source;
    }

    public ChangeKind Kind { get; }
    public JsonNode? Node { get; }

    /// <summary>The view that made the change, so it can skip refreshing itself.</summary>
    public object? Source { get; }
}

public sealed class AddColumnResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string? Message { get; init; }
    public int Converted { get; init; }
    public string? FieldName { get; init; }
}

/// <summary>All edits to a document, as testable methods. Each structural edit pushes one undo snapshot.</summary>
public sealed class DocumentEditor
{
    private Dictionary<long, JsonNode>? _index;

    public DocumentEditor(JsonNode? root = null)
    {
        Root = root ?? JsonNode.NewObject();
    }

    public JsonNode Root { get; private set; }
    public UndoStack History { get; } = new();

    /// <summary>Item-type hints per peer key (spec 6.6). Saved in a sidecar setting, not in the JSON.</summary>
    public Dictionary<string, string> Hints { get; } = new(StringComparer.Ordinal);

    /// <summary>Increases with every change.</summary>
    public long Version { get; private set; }

    public event EventHandler<DocumentChangedEventArgs>? Changed;

    // ---- infrastructure ---------------------------------------------------

    public void Load(JsonNode root)
    {
        Root = root;
        History.Clear();
        _index = null;
        Raise(ChangeKind.Root, null, null);
    }

    public JsonNode? FindById(long id)
    {
        _index ??= Root.Descendants().ToDictionary(n => n.Id);
        if (_index.TryGetValue(id, out var n) && (n == Root || Root.IsAncestorOf(n))) return n;
        _index = Root.Descendants().ToDictionary(n => n.Id);
        return _index.GetValueOrDefault(id);
    }

    private void Snapshot() => History.Push(Root);

    private void Raise(ChangeKind kind, JsonNode? node, object? source)
    {
        if (kind != ChangeKind.Value) _index = null;
        Version++;
        Changed?.Invoke(this, new DocumentChangedEventArgs(kind, node, source));
    }

    /// <summary>Marks the document as changed without an undo step (for example, an indent change).</summary>
    public void Touch() => Raise(ChangeKind.Value, null, null);

    public void EndBurst() => History.EndBurst();

    public bool Undo()
    {
        var r = History.Undo(Root);
        if (r == null) return false;
        Root = r;
        Raise(ChangeKind.Root, null, null);
        return true;
    }

    public bool Redo()
    {
        var r = History.Redo(Root);
        if (r == null) return false;
        Root = r;
        Raise(ChangeKind.Root, null, null);
        return true;
    }

    /// <summary>Replaces the root (code pane). One snapshot per burst.</summary>
    public void ReplaceRoot(JsonNode newRoot, string burstKey = "code")
    {
        History.PushForBurst(burstKey, Root);
        Root = newRoot;
        Raise(ChangeKind.Root, null, null);
    }

    // ---- value edits (typing bursts) --------------------------------------

    private static string BurstKey(JsonNode node, string field) => $"{node.Id}:{field}";

    public void SetString(JsonNode node, string text, object? source = null, string field = "value")
    {
        if (node.Type == NodeType.String && node.StringValue == text) return;
        History.PushForBurst(BurstKey(node, field), Root);
        bool typeChange = node.Type != NodeType.String;
        node.SetPrimitive(NodeType.String, text);
        Raise(typeChange ? ChangeKind.Structure : ChangeKind.Value, node, source);
    }

    public void SetNumber(JsonNode node, double value, object? source = null, string field = "value")
    {
        if (node.Type == NodeType.Number && node.NumberValue.Equals(value)) return;
        History.PushForBurst(BurstKey(node, field), Root);
        bool typeChange = node.Type != NodeType.Number;
        node.SetPrimitive(NodeType.Number, value);
        Raise(typeChange ? ChangeKind.Structure : ChangeKind.Value, node, source);
    }

    public void SetBoolean(JsonNode node, bool value, object? source = null)
    {
        if (node.Type == NodeType.Boolean && node.BoolValue == value) return;
        Snapshot();
        bool typeChange = node.Type != NodeType.Boolean;
        node.SetPrimitive(NodeType.Boolean, value);
        Raise(typeChange ? ChangeKind.Structure : ChangeKind.Value, node, source);
    }

    /// <summary>Replaces a node's value with a literal typed into an empty or null cell (spec 4.6).</summary>
    public void SetLiteral(JsonNode node, string text, bool forceString, object? source = null)
    {
        Snapshot();
        node.ReplaceContent(LiteralParser.Parse(text, forceString));
        Raise(ChangeKind.Structure, node, source);
    }

    // ---- structural edits ---------------------------------------------------

    public string UniqueKey(JsonNode obj, string baseName)
    {
        if (!obj.HasKey(baseName)) return baseName;
        for (int i = 2; ; i++)
        {
            var k = baseName + i;
            if (!obj.HasKey(k)) return k;
        }
    }

    /// <summary>Adds a child to a container. For objects the key defaults to a unique "newField".</summary>
    public JsonNode AddChild(JsonNode container, JsonNode? child = null, int index = -1, string? key = null)
    {
        if (!container.IsContainer) throw new InvalidOperationException("Only objects and arrays have children.");
        Snapshot();
        child ??= JsonNode.NewString("");
        child.Key = container.IsObject ? key ?? UniqueKey(container, "newField") : "";
        container.Insert(index, child);
        Raise(ChangeKind.Structure, child, null);
        return child;
    }

    public void Delete(JsonNode node)
    {
        if (node.Parent == null) return;
        Snapshot();
        var parent = node.Parent;
        parent.Remove(node);
        Raise(ChangeKind.Structure, parent, null);
    }

    public JsonNode? Duplicate(JsonNode node)
    {
        if (node.Parent == null) return null;
        Snapshot();
        var copy = node.Clone(keepIds: false);
        var parent = node.Parent;
        if (parent.IsObject) copy.Key = UniqueKey(parent, node.Key + "_copy");
        parent.Insert(node.Index + 1, copy);
        Raise(ChangeKind.Structure, copy, null);
        return copy;
    }

    /// <summary>
    /// Moves a node into <paramref name="newParent"/> at <paramref name="index"/> (an index in the parent's
    /// child list before the move). Returns false if the move is not possible.
    /// </summary>
    public bool Move(JsonNode node, JsonNode newParent, int index)
    {
        if (node.Parent == null || !newParent.IsContainer) return false;
        if (node == newParent || node.IsAncestorOf(newParent)) return false;
        var oldParent = node.Parent;
        int oldIndex = node.Index;
        if (oldParent == newParent && (index == oldIndex || index == oldIndex + 1)) return false;

        Snapshot();
        oldParent.Remove(node);
        if (oldParent == newParent && index > oldIndex) index--;
        if (newParent.IsObject)
        {
            if (oldParent != newParent)
                node.Key = UniqueKey(newParent, string.IsNullOrEmpty(node.Key) ? "newField" : node.Key);
        }
        else node.Key = "";
        newParent.Insert(Math.Clamp(index, 0, newParent.Children.Count), node);
        Raise(ChangeKind.Structure, node, null);
        return true;
    }

    /// <summary>Renames one property. Duplicate keys are allowed (they are shown as errors).</summary>
    public bool Rename(JsonNode node, string newKey)
    {
        if (node.Parent?.IsObject != true || node.Key == newKey) return false;
        Snapshot();
        node.Key = newKey;
        Raise(ChangeKind.Structure, node, null);
        return true;
    }

    public void ChangeType(JsonNode node, NodeType type)
    {
        if (node.Type == type) return;
        Snapshot();
        TypeConversion.Convert(node, type);
        Raise(ChangeKind.Structure, node, null);
    }

    /// <summary>Sorts the keys of every object, in natural number order. One undo step.</summary>
    public void SortKeys()
    {
        Snapshot();
        foreach (var n in Root.Descendants())
        {
            if (!n.IsObject || n.Children.Count < 2) continue;
            var sorted = n.Children.OrderBy(c => c.Key, NaturalComparer.Instance).ToList(); // stable
            n.Children.Clear();
            n.Children.AddRange(sorted);
        }
        Raise(ChangeKind.Structure, null, null);
    }

    /// <summary>Pastes a JSON text as a new child or replaces a node. Returns null if the text is not valid.</summary>
    public JsonNode? Paste(JsonNode container, string json, int index = -1)
    {
        var r = JsonParser.Parse(json);
        if (r.Root == null || !container.IsContainer) return null;
        Snapshot();
        var child = r.Root;
        child.Key = container.IsObject ? UniqueKey(container, "pasted") : "";
        container.Insert(index, child);
        Raise(ChangeKind.Structure, child, null);
        return child;
    }

    // ---- tables -------------------------------------------------------------

    public List<ColumnInfo> Columns(JsonNode table) => Schema.GetColumns(Root, table, Hints);

    private List<JsonNode> PeerTables(JsonNode table) => Schema.GetPeers(Root, table);

    private static IEnumerable<JsonNode> ObjectRows(JsonNode table) =>
        Schema.Rows(table).Where(r => r.IsObject);

    /// <summary>Renames a column in all rows of the table and all peer tables.</summary>
    public bool RenameColumn(JsonNode table, string oldName, string newName, out string? error)
    {
        error = null;
        newName ??= "";
        if (oldName == newName) return false;
        if (newName.Length == 0)
        {
            error = "A column name can't be empty.";
            return false;
        }
        if (Columns(table).Any(c => !c.IsValueColumn && c.Name == newName))
        {
            error = $"A column named \"{newName}\" already exists.";
            return false;
        }
        Snapshot();
        foreach (var p in PeerTables(table))
            foreach (var r in ObjectRows(p))
                foreach (var c in r.Children)
                    if (c.Key == oldName) c.Key = newName;

        // Move hints of the old column (and anything below it) to the new name.
        var oldKey = Schema.ColumnPeerKey(table, oldName);
        var newKey = Schema.ColumnPeerKey(table, newName);
        foreach (var k in Hints.Keys.ToList())
        {
            if (k == oldKey || k.StartsWith(oldKey + ".", StringComparison.Ordinal) || k.StartsWith(oldKey + "[", StringComparison.Ordinal))
            {
                var v = Hints[k];
                Hints.Remove(k);
                Hints[newKey + k[oldKey.Length..]] = v;
            }
        }
        Raise(ChangeKind.Structure, table, null);
        return true;
    }

    /// <summary>Deletes a column from all rows of the table and all peer tables.</summary>
    public void DeleteColumn(JsonNode table, string columnId)
    {
        Snapshot();
        foreach (var p in PeerTables(table))
        {
            if (columnId == ColumnInfo.ValueColumnId)
            {
                if (p.IsArray)
                    foreach (var r in p.Children.Where(c => c.IsPrimitive).ToList()) p.Remove(r);
                continue;
            }
            foreach (var r in ObjectRows(p))
                foreach (var c in r.Children.Where(c => c.Key == columnId).ToList()) r.Remove(c);
        }
        Raise(ChangeKind.Structure, table, null);
    }

    /// <summary>Changes the type of a column in all rows of the table and all peer tables.</summary>
    public void ChangeColumnType(JsonNode table, string columnId, ColumnKind kind)
    {
        Snapshot();
        bool isValue = columnId == ColumnInfo.ValueColumnId;
        foreach (var p in PeerTables(table))
        {
            if (isValue)
            {
                foreach (var r in Schema.Rows(p).Where(r => r.IsPrimitive))
                    ConvertCell(r, kind, Schema.SingularFieldName(p));
                continue;
            }
            foreach (var r in ObjectRows(p))
                foreach (var c in r.Children.Where(c => c.Key == columnId))
                    ConvertCell(c, kind, Schema.SingularFieldName(c));
        }

        if (isValue)
        {
            if (kind is not ColumnKind.Object && Schema.ItemType(kind) == null)
                Hints[Schema.PeerKey(table)] = Schema.Label(kind);
        }
        else if (Schema.ItemType(kind) is { } item)
        {
            Hints[Schema.ColumnPeerKey(table, columnId)] = TypeConversion.TypeName(item);
        }
        Raise(ChangeKind.Structure, table, null);
    }

    /// <summary>Converts one cell to a column kind. "array of X" converts the cell to an array and each item to X.</summary>
    internal static void ConvertCell(JsonNode cell, ColumnKind kind, string fieldName)
    {
        var item = Schema.ItemType(kind);
        if (item == null)
        {
            TypeConversion.Convert(cell, Schema.NodeTypeOf(kind));
            return;
        }
        TypeConversion.Convert(cell, NodeType.Array);
        foreach (var i in cell.Children)
        {
            if (item == NodeType.Object && i.IsPrimitive) WrapInObject(i, fieldName);
            else TypeConversion.Convert(i, item.Value);
        }
    }

    /// <summary>Turns a plain value into an object that keeps the value in <paramref name="field"/>. Id is kept.</summary>
    private static void WrapInObject(JsonNode item, string field)
    {
        var inner = new JsonNode(item.Type, item.Value) { Key = field };
        item.SetPrimitive(NodeType.Object, null);
        item.Add(inner);
    }

    /// <summary>
    /// Adds a column to the current table only. If the table is an array of plain values, each plain value in
    /// this table and all peer tables first becomes an object (spec 6.7). One undo step.
    /// </summary>
    public AddColumnResult AddColumn(JsonNode table, string name, ColumnKind kind)
    {
        name ??= "";
        if (name.Length == 0) return new AddColumnResult { Error = "A column name can't be empty." };
        if (Columns(table).Any(c => !c.IsValueColumn && c.Name == name))
            return new AddColumnResult { Error = $"A column named \"{name}\" already exists." };
        if (table.IsPrimitive) return new AddColumnResult { Error = "A plain value can't have columns." };

        Snapshot();
        int converted = 0;
        string? field = null;
        if (table.IsArray && table.Children.Any(c => c.IsPrimitive))
        {
            field = Schema.SingularFieldName(table, name);
            foreach (var p in PeerTables(table).Where(p => p.IsArray))
            {
                foreach (var r in p.Children.Where(c => c.IsPrimitive))
                {
                    WrapInObject(r, field);
                    converted++;
                }
            }
            var tk = Schema.PeerKey(table);
            if (Hints.ContainsKey(tk)) Hints[tk] = "object";
        }

        if (table.IsArray && table.Children.Count == 0)
        {
            var row = JsonNode.NewObject();
            row.Add(name, Schema.BlankFor(kind));
            table.Add(row);
        }
        else
        {
            foreach (var r in ObjectRows(table))
                r.Add(name, Schema.BlankFor(kind));
        }

        if (Schema.ItemType(kind) is { } item)
            Hints[Schema.ColumnPeerKey(table, name)] = TypeConversion.TypeName(item);

        Raise(ChangeKind.Structure, table, null);
        string? msg = converted > 0
            ? $"Turned {converted} plain value{(converted == 1 ? "" : "s")} into objects. Each value is now in the \"{field}\" column."
            : null;
        return new AddColumnResult { Success = true, Message = msg, Converted = converted, FieldName = field };
    }

    /// <summary>Suggests a new column name: newField, newField2, …</summary>
    public string NewColumnName(JsonNode table)
    {
        var names = Columns(table).Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        if (!names.Contains("newField")) return "newField";
        for (int i = 2; ; i++)
            if (!names.Contains("newField" + i)) return "newField" + i;
    }

    /// <summary>Creates a blank row that has every column (spec 6.6).</summary>
    public JsonNode CreateBlankRow(JsonNode table)
    {
        var cols = Columns(table);
        var objCols = cols.Where(c => !c.IsValueColumn).ToList();
        if (objCols.Count > 0)
        {
            var row = JsonNode.NewObject();
            foreach (var c in objCols) row.Add(c.Name, Schema.BlankFor(c.TypeLabel));
            return row;
        }
        var vc = cols.FirstOrDefault(c => c.IsValueColumn);
        if (vc != null) return vc.TypeLabel == "empty" ? JsonNode.NewString("") : Schema.BlankFor(vc.TypeLabel);
        if (Hints.TryGetValue(Schema.PeerKey(table), out var hint) && TypeConversion.ParseTypeName(hint) is { } ht)
            return TypeConversion.Blank(ht);
        return JsonNode.NewObject();
    }

    /// <summary>Adds a blank row to an array table.</summary>
    public JsonNode AddRow(JsonNode table, int index = -1)
    {
        if (!table.IsArray) throw new InvalidOperationException("Rows can be added only to arrays.");
        var row = CreateBlankRow(table);
        Snapshot();
        table.Insert(index, row);
        Raise(ChangeKind.Structure, row, null);
        return row;
    }

    /// <summary>Inserts a key into a row at the position of its column (spec 6.3, missing key).</summary>
    private void InsertAtColumn(JsonNode table, JsonNode row, string column, JsonNode value)
    {
        value.Key = column;
        var order = Columns(table).Where(c => !c.IsValueColumn).Select(c => c.Name).ToList();
        int pos = order.IndexOf(column);
        int insertAt = row.Children.Count;
        if (pos >= 0)
        {
            var after = new HashSet<string>(order.Skip(pos + 1), StringComparer.Ordinal);
            for (int i = 0; i < row.Children.Count; i++)
            {
                if (after.Contains(row.Children[i].Key))
                {
                    insertAt = i;
                    break;
                }
            }
        }
        row.Insert(insertAt, value);
    }

    /// <summary>Writes typed text into an empty (missing) or null cell, with literal parsing (spec 4.6).</summary>
    public JsonNode SetCellLiteral(JsonNode table, JsonNode row, ColumnInfo column, string text, object? source = null)
    {
        Snapshot();
        var value = LiteralParser.Parse(text, column.IsStringColumn);
        JsonNode target;
        if (column.IsValueColumn)
        {
            row.ReplaceContent(value);
            target = row;
        }
        else
        {
            var cell = row.Get(column.Name);
            if (cell == null)
            {
                InsertAtColumn(table, row, column.Name, value);
                target = value;
            }
            else
            {
                cell.ReplaceContent(value);
                target = cell;
            }
        }
        Raise(ChangeKind.Structure, target, source);
        return target;
    }

    /// <summary>Creates an empty container for a missing cell of a container column ("+ [ ]" / "+ { }").</summary>
    public JsonNode CreateContainerCell(JsonNode table, JsonNode row, string column, NodeType type)
    {
        Snapshot();
        var node = type == NodeType.Object ? JsonNode.NewObject() : JsonNode.NewArray();
        InsertAtColumn(table, row, column, node);
        Raise(ChangeKind.Structure, node, null);
        return node;
    }

    // ---- statistics -------------------------------------------------------------

    public static (int Count, int Depth) Stats(JsonNode root)
    {
        int count = 0, depth = 0;
        var stack = new Stack<(JsonNode, int)>();
        stack.Push((root, 0));
        while (stack.Count > 0)
        {
            var (n, d) = stack.Pop();
            count++;
            if (d > depth) depth = d;
            foreach (var c in n.Children) stack.Push((c, d + 1));
        }
        return (count, depth);
    }

    /// <summary>Nodes whose key or value contains the text, case-insensitive.</summary>
    public static bool Matches(JsonNode n, string find)
    {
        if (string.IsNullOrEmpty(find)) return false;
        if (n.Parent?.IsObject == true && n.Key.Contains(find, StringComparison.OrdinalIgnoreCase)) return true;
        return n.Type switch
        {
            NodeType.String => n.StringValue.Contains(find, StringComparison.OrdinalIgnoreCase),
            NodeType.Number => JsonWriter.FormatNumber(n.NumberValue).Contains(find, StringComparison.OrdinalIgnoreCase),
            NodeType.Boolean => (n.BoolValue ? "true" : "false").Contains(find, StringComparison.OrdinalIgnoreCase),
            NodeType.Null => "null".Contains(find, StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }
}

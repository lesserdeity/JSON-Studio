using System.Threading;

namespace JsonStudio.Core.Model;

/// <summary>The type of a JSON value.</summary>
public enum NodeType
{
    Object,
    Array,
    String,
    Number,
    Boolean,
    Null,
}

/// <summary>
/// One JSON value. Objects and arrays hold ordered children; object children carry a <see cref="Key"/>.
/// Duplicate keys are allowed in the model.
/// </summary>
public sealed class JsonNode
{
    private static long _lastId;

    /// <summary>Returns a new id that has never been used in this session.</summary>
    public static long NextId() => Interlocked.Increment(ref _lastId);

    public JsonNode(NodeType type, object? value = null)
        : this(NextId(), type, value)
    {
    }

    private JsonNode(long id, NodeType type, object? value)
    {
        Id = id;
        Type = type;
        Value = value;
    }

    public long Id { get; private set; }

    public NodeType Type { get; set; }

    /// <summary>The property name. Used only when the parent is an object.</summary>
    public string Key { get; set; } = "";

    /// <summary><c>string</c> for String, <c>double</c> for Number, <c>bool</c> for Boolean, otherwise null.</summary>
    public object? Value { get; set; }

    public List<JsonNode> Children { get; } = new();

    public JsonNode? Parent { get; set; }

    /// <summary>Tree view state only.</summary>
    public bool Collapsed { get; set; }

    // ---- factories -------------------------------------------------------

    public static JsonNode NewObject() => new(NodeType.Object);
    public static JsonNode NewArray() => new(NodeType.Array);
    public static JsonNode NewString(string s) => new(NodeType.String, s);
    public static JsonNode NewNumber(double d) => new(NodeType.Number, d);
    public static JsonNode NewBoolean(bool b) => new(NodeType.Boolean, b);
    public static JsonNode NewNull() => new(NodeType.Null);

    // ---- convenience -----------------------------------------------------

    public bool IsContainer => Type is NodeType.Object or NodeType.Array;
    public bool IsPrimitive => !IsContainer;
    public bool IsObject => Type == NodeType.Object;
    public bool IsArray => Type == NodeType.Array;

    public string StringValue => Value as string ?? "";
    public double NumberValue => Value is double d ? d : 0;
    public bool BoolValue => Value is bool b && b;

    /// <summary>Position in the parent's child list, or -1 for the root.</summary>
    public int Index => Parent?.Children.IndexOf(this) ?? -1;

    public bool IsArrayItem => Parent?.Type == NodeType.Array;

    public JsonNode Root
    {
        get
        {
            var n = this;
            while (n.Parent != null) n = n.Parent;
            return n;
        }
    }

    public int Depth
    {
        get
        {
            int d = 0;
            for (var n = Parent; n != null; n = n.Parent) d++;
            return d;
        }
    }

    /// <summary>First child with the given key (objects only).</summary>
    public JsonNode? Get(string key)
    {
        if (Type != NodeType.Object) return null;
        foreach (var c in Children)
            if (c.Key == key) return c;
        return null;
    }

    public bool HasKey(string key) => Get(key) != null;

    public JsonNode Add(JsonNode child)
    {
        child.Parent = this;
        Children.Add(child);
        return child;
    }

    public JsonNode Add(string key, JsonNode child)
    {
        child.Key = key;
        return Add(child);
    }

    public JsonNode Insert(int index, JsonNode child)
    {
        child.Parent = this;
        if (index < 0 || index > Children.Count) index = Children.Count;
        Children.Insert(index, child);
        return child;
    }

    public bool Remove(JsonNode child)
    {
        if (!Children.Remove(child)) return false;
        child.Parent = null;
        return true;
    }

    public bool IsAncestorOf(JsonNode other)
    {
        for (var n = other.Parent; n != null; n = n.Parent)
            if (n == this) return true;
        return false;
    }

    public IEnumerable<JsonNode> Descendants(bool includeSelf = true)
    {
        var stack = new Stack<JsonNode>();
        stack.Push(this);
        bool first = true;
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (!first || includeSelf) yield return n;
            first = false;
            for (int i = n.Children.Count - 1; i >= 0; i--) stack.Push(n.Children[i]);
        }
    }

    /// <summary>Deep clone. With <paramref name="keepIds"/> the clone has the same ids (undo snapshots).</summary>
    public JsonNode Clone(bool keepIds)
    {
        var copy = new JsonNode(keepIds ? Id : NextId(), Type, Value)
        {
            Key = Key,
            Collapsed = Collapsed,
        };
        foreach (var c in Children) copy.Add(c.Clone(keepIds));
        return copy;
    }

    /// <summary>Makes this node a primitive of the given type. Children are removed.</summary>
    public void SetPrimitive(NodeType type, object? value)
    {
        foreach (var c in Children) c.Parent = null;
        Children.Clear();
        Type = type;
        Value = value;
    }

    /// <summary>Replaces type, value and children with those of <paramref name="other"/>. Id and Key are kept.</summary>
    public void ReplaceContent(JsonNode other)
    {
        foreach (var c in Children) c.Parent = null;
        Children.Clear();
        Type = other.Type;
        Value = other.Value;
        var kids = other.Children.ToList();
        other.Children.Clear();
        foreach (var k in kids) Add(k);
    }

    public override string ToString() => $"{Type} #{Id} {Key}";
}

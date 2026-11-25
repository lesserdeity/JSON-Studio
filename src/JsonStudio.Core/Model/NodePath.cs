using System.Text;
using JsonStudio.Core.Serialization;

namespace JsonStudio.Core.Model;

/// <summary>One step in a path: a key (object child) or an index (array item).</summary>
public readonly record struct PathSegment(string? Key, int Index)
{
    public bool IsIndex => Key == null;
}

/// <summary>JSON path helpers, for example <c>$.measures[0].accelerators[1].rate</c>.</summary>
public static class NodePath
{
    public static List<PathSegment> Segments(JsonNode node)
    {
        var list = new List<PathSegment>();
        for (var n = node; n.Parent != null; n = n.Parent)
        {
            if (n.Parent.Type == NodeType.Array) list.Add(new PathSegment(null, n.Index));
            else list.Add(new PathSegment(n.Key, n.Parent.Children.Where(c => c.Key == n.Key).ToList().IndexOf(n)));
        }
        list.Reverse();
        return list;
    }

    /// <summary>Finds the node at the given path. For a key segment, Index selects among duplicate keys.</summary>
    public static JsonNode? Resolve(JsonNode root, IReadOnlyList<PathSegment> segments)
    {
        var cur = root;
        foreach (var s in segments)
        {
            if (s.IsIndex)
            {
                if (cur.Type != NodeType.Array || s.Index < 0 || s.Index >= cur.Children.Count) return null;
                cur = cur.Children[s.Index];
            }
            else
            {
                if (cur.Type != NodeType.Object) return null;
                var matches = cur.Children.Where(c => c.Key == s.Key).ToList();
                if (matches.Count == 0) return null;
                cur = matches[Math.Clamp(s.Index, 0, matches.Count - 1)];
            }
        }
        return cur;
    }

    /// <summary>Formats a path such as <c>$.measures[0].rate</c>.</summary>
    public static string Format(JsonNode node)
    {
        var sb = new StringBuilder("$");
        var chain = new List<JsonNode>();
        for (var n = node; n.Parent != null; n = n.Parent) chain.Add(n);
        chain.Reverse();
        foreach (var n in chain)
        {
            if (n.Parent!.Type == NodeType.Array) sb.Append('[').Append(n.Index).Append(']');
            else AppendKey(sb, n.Key);
        }
        return sb.ToString();
    }

    internal static void AppendKey(StringBuilder sb, string key)
    {
        if (IsIdentifier(key)) sb.Append('.').Append(key);
        else
        {
            sb.Append('[');
            JsonWriter.WriteString(sb, key);
            sb.Append(']');
        }
    }

    public static bool IsIdentifier(string key)
    {
        if (key.Length == 0) return false;
        if (!(char.IsLetter(key[0]) || key[0] == '_' || key[0] == '$')) return false;
        foreach (var c in key)
            if (!(char.IsLetterOrDigit(c) || c == '_' || c == '$')) return false;
        return true;
    }
}

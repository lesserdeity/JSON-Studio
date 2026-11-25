using System.Globalization;
using System.Text;
using JsonStudio.Core.Model;

namespace JsonStudio.Core.Serialization;

/// <summary>Lines (1-based, inclusive) that a node occupies in the serialized text.</summary>
public readonly record struct LineSpan(int StartLine, int EndLine);

public sealed class SerializeResult
{
    public SerializeResult(string text, Dictionary<long, LineSpan> spans)
    {
        Text = text;
        Spans = spans;
    }

    public string Text { get; }
    public IReadOnlyDictionary<long, LineSpan> Spans { get; }
}

/// <summary>
/// Writes JSON exactly as JavaScript <c>JSON.stringify(value, null, indent)</c> does, followed by one newline,
/// and records the line span of each node.
/// </summary>
public static class JsonWriter
{
    public static string Serialize(JsonNode root, string indent) => SerializeWithSpans(root, indent, recordSpans: false).Text;

    public static SerializeResult SerializeWithSpans(JsonNode root, string indent, bool recordSpans = true)
    {
        var w = new Writer(indent, recordSpans);
        w.Write(root, 0);
        w.Sb.Append('\n');
        return new SerializeResult(w.Sb.ToString(), w.Spans);
    }

    /// <summary>Compact form, like <c>JSON.stringify(value)</c>.</summary>
    public static string Compact(JsonNode node)
    {
        var sb = new StringBuilder();
        WriteCompact(sb, node);
        return sb.ToString();
    }

    private static void WriteCompact(StringBuilder sb, JsonNode n)
    {
        switch (n.Type)
        {
            case NodeType.Object:
                sb.Append('{');
                for (int i = 0; i < n.Children.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    WriteString(sb, n.Children[i].Key);
                    sb.Append(':');
                    WriteCompact(sb, n.Children[i]);
                }
                sb.Append('}');
                break;
            case NodeType.Array:
                sb.Append('[');
                for (int i = 0; i < n.Children.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    WriteCompact(sb, n.Children[i]);
                }
                sb.Append(']');
                break;
            default:
                WritePrimitive(sb, n);
                break;
        }
    }

    private sealed class Writer
    {
        private readonly string _indent;
        private readonly bool _record;
        public readonly StringBuilder Sb = new();
        public readonly Dictionary<long, LineSpan> Spans = new();
        private int _line = 1;

        public Writer(string indent, bool record)
        {
            _indent = indent;
            _record = record;
        }

        private void NewLine(int depth)
        {
            if (_indent.Length == 0) return;
            Sb.Append('\n');
            _line++;
            for (int i = 0; i < depth; i++) Sb.Append(_indent);
        }

        public void Write(JsonNode n, int depth)
        {
            int start = _line;
            switch (n.Type)
            {
                case NodeType.Object:
                case NodeType.Array:
                    bool obj = n.Type == NodeType.Object;
                    Sb.Append(obj ? '{' : '[');
                    if (n.Children.Count > 0)
                    {
                        for (int i = 0; i < n.Children.Count; i++)
                        {
                            if (i > 0) Sb.Append(',');
                            NewLine(depth + 1);
                            var c = n.Children[i];
                            if (obj)
                            {
                                WriteString(Sb, c.Key);
                                Sb.Append(_indent.Length == 0 ? ":" : ": ");
                            }
                            Write(c, depth + 1);
                        }
                        NewLine(depth);
                    }
                    Sb.Append(obj ? '}' : ']');
                    break;
                default:
                    WritePrimitive(Sb, n);
                    break;
            }
            if (_record) Spans[n.Id] = new LineSpan(start, _line);
        }
    }

    private static void WritePrimitive(StringBuilder sb, JsonNode n)
    {
        switch (n.Type)
        {
            case NodeType.String: WriteString(sb, n.StringValue); break;
            case NodeType.Number: sb.Append(FormatNumber(n.NumberValue)); break;
            case NodeType.Boolean: sb.Append(n.BoolValue ? "true" : "false"); break;
            default: sb.Append("null"); break;
        }
    }

    /// <summary>Writes a quoted string escaped as <c>JSON.stringify</c> does.</summary>
    public static void WriteString(StringBuilder sb, string s)
    {
        sb.Append('"');
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20)
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else if (char.IsHighSurrogate(c))
                    {
                        if (i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                        {
                            sb.Append(c).Append(s[i + 1]);
                            i++;
                        }
                        else sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else if (char.IsLowSurrogate(c))
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }

    public static string QuoteString(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        WriteString(sb, s);
        return sb.ToString();
    }

    /// <summary>
    /// Formats a number as JavaScript <c>Number.prototype.toString()</c>: the shortest digits that read back to
    /// the same value, decimal notation for 1e-7 &lt;= |x| &lt; 1e21, otherwise exponent notation.
    /// </summary>
    public static string FormatNumber(double v)
    {
        if (double.IsNaN(v) || double.IsInfinity(v)) return "null";
        if (v == 0) return "0";

        string r = v.ToString("R", CultureInfo.InvariantCulture);
        bool neg = r[0] == '-';
        if (neg) r = r[1..];

        int exp = 0;
        string mant = r;
        int ePos = r.IndexOfAny(new[] { 'E', 'e' });
        if (ePos >= 0)
        {
            exp = int.Parse(r[(ePos + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            mant = r[..ePos];
        }

        int dot = mant.IndexOf('.');
        string digits;
        int intLen;
        if (dot >= 0)
        {
            digits = mant.Remove(dot, 1);
            intLen = dot;
        }
        else
        {
            digits = mant;
            intLen = mant.Length;
        }

        int lead = 0;
        while (lead < digits.Length - 1 && digits[lead] == '0') lead++;
        digits = digits[lead..];
        intLen -= lead;
        digits = digits.TrimEnd('0');
        if (digits.Length == 0) return "0";

        int k = digits.Length;
        int n = intLen + exp;
        var sb = new StringBuilder();
        if (neg) sb.Append('-');

        if (k <= n && n <= 21)
        {
            sb.Append(digits).Append('0', n - k);
        }
        else if (0 < n && n <= 21)
        {
            sb.Append(digits, 0, n).Append('.').Append(digits, n, k - n);
        }
        else if (-6 < n && n <= 0)
        {
            sb.Append("0.").Append('0', -n).Append(digits);
        }
        else
        {
            int e = n - 1;
            sb.Append(digits[0]);
            if (k > 1) sb.Append('.').Append(digits, 1, k - 1);
            sb.Append('e').Append(e < 0 ? '-' : '+').Append(Math.Abs(e).ToString(CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }
}

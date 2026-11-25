using System.Globalization;
using System.Text;
using System.Text.Json;
using JsonStudio.Core.Model;

namespace JsonStudio.Core.Serialization;

/// <summary>A parse error. Line and column are 1-based.</summary>
public sealed record ParseError(int Line, int Column, string Message)
{
    public override string ToString() => $"Line {Line}, column {Column}: {Message}";
}

public sealed class ParseResult
{
    public JsonNode? Root { get; init; }
    public ParseError? Error { get; init; }

    /// <summary>The indent detected from the first indented line, or null if no line is indented.</summary>
    public string? Indent { get; init; }

    public bool Success => Root != null;
}

/// <summary>
/// Parses JSON text into <see cref="JsonNode"/>s with <see cref="Utf8JsonReader"/>.
/// Key order and duplicate keys are kept.
/// </summary>
public static class JsonParser
{
    public static ParseResult Parse(string text)
    {
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];
        var bytes = Encoding.UTF8.GetBytes(text);
        var options = new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 4096,
        };
        var reader = new Utf8JsonReader(bytes, isFinalBlock: true, state: new JsonReaderState(options));
        var stack = new Stack<JsonNode>();
        JsonNode? root = null;
        string pendingKey = "";

        try
        {
            while (reader.Read())
            {
                JsonNode? node = null;
                switch (reader.TokenType)
                {
                    case JsonTokenType.PropertyName:
                        pendingKey = ReadString(ref reader);
                        continue;
                    case JsonTokenType.StartObject:
                        node = JsonNode.NewObject();
                        break;
                    case JsonTokenType.StartArray:
                        node = JsonNode.NewArray();
                        break;
                    case JsonTokenType.EndObject:
                    case JsonTokenType.EndArray:
                        stack.Pop();
                        continue;
                    case JsonTokenType.String:
                        node = JsonNode.NewString(ReadString(ref reader));
                        break;
                    case JsonTokenType.Number:
                        node = JsonNode.NewNumber(double.Parse(Encoding.UTF8.GetString(reader.ValueSpan),
                            NumberStyles.Float, CultureInfo.InvariantCulture));
                        break;
                    case JsonTokenType.True:
                        node = JsonNode.NewBoolean(true);
                        break;
                    case JsonTokenType.False:
                        node = JsonNode.NewBoolean(false);
                        break;
                    case JsonTokenType.Null:
                        node = JsonNode.NewNull();
                        break;
                    default:
                        continue;
                }

                if (stack.Count == 0) root = node;
                else
                {
                    var parent = stack.Peek();
                    if (parent.Type == NodeType.Object) node.Key = pendingKey;
                    parent.Add(node);
                }
                if (node.IsContainer) stack.Push(node);
            }
        }
        catch (JsonException ex)
        {
            return new ParseResult { Error = MakeError(bytes, ex) };
        }

        if (root == null)
            return new ParseResult { Error = new ParseError(1, 1, "The text does not contain any JSON value.") };

        return new ParseResult { Root = root, Indent = DetectIndent(text) };
    }

    /// <summary>Parses and throws <see cref="FormatException"/> on error. For tests and internal use.</summary>
    public static JsonNode ParseOrThrow(string text)
    {
        var r = Parse(text);
        if (r.Root == null) throw new FormatException(r.Error!.ToString());
        return r.Root;
    }

    private static ParseError MakeError(byte[] bytes, JsonException ex)
    {
        int line = (int)(ex.LineNumber ?? 0);
        int bytePos = (int)(ex.BytePositionInLine ?? 0);

        // Find the start of the line in bytes and convert the byte position to a character column.
        int lineStart = 0, l = 0;
        for (int i = 0; i < bytes.Length && l < line; i++)
        {
            if (bytes[i] == (byte)'\n')
            {
                l++;
                lineStart = i + 1;
            }
        }
        int len = Math.Clamp(bytePos, 0, Math.Max(0, bytes.Length - lineStart));
        int column = Encoding.UTF8.GetCharCount(bytes, lineStart, len) + 1;

        string msg = ex.Message;
        int cut = msg.IndexOf(" LineNumber:", StringComparison.Ordinal);
        if (cut >= 0) msg = msg[..cut];
        msg = msg.Trim();
        if (msg.EndsWith('.')) msg = msg[..^1];
        return new ParseError(line + 1, column, msg);
    }

    private static string ReadString(ref Utf8JsonReader reader)
    {
        var raw = Encoding.UTF8.GetString(reader.ValueSpan);
        return reader.ValueIsEscaped ? Unescape(raw) : raw;
    }

    /// <summary>Decodes JSON escapes. Lone surrogates are kept, as JavaScript does.</summary>
    internal static string Unescape(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c != '\\' || i + 1 >= s.Length)
            {
                sb.Append(c);
                continue;
            }
            char e = s[++i];
            switch (e)
            {
                case '"': sb.Append('"'); break;
                case '\\': sb.Append('\\'); break;
                case '/': sb.Append('/'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'u':
                    if (i + 4 < s.Length &&int.TryParse(s.AsSpan(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int code))
                    {
                        sb.Append((char)code);
                        i += 4;
                    }
                    else sb.Append('u');
                    break;
                default: sb.Append(e); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>Detects the indent (2 spaces, 4 spaces or a tab) from the first indented line.</summary>
    public static string? DetectIndent(string text)
    {
        int i = text.IndexOf('\n');
        while (i >= 0 && i + 1 < text.Length)
        {
            char c = text[i + 1];
            if (c == '\t') return "\t";
            if (c == ' ')
            {
                int n = 0;
                while (i + 1 + n < text.Length && text[i + 1 + n] == ' ') n++;
                // Skip lines that are only whitespace.
                int after = i + 1 + n;
                if (after < text.Length && text[after] != '\r' && text[after] != '\n')
                    return n >= 4 && n % 4 == 0 ? "    " : "  ";
            }
            i = text.IndexOf('\n', i + 1);
        }
        return null;
    }
}

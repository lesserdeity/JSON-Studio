using System.Globalization;
using JsonStudio.Core.Model;

namespace JsonStudio.Core.Serialization;

/// <summary>Turns text typed into an empty or null cell into a value (spec 4.6).</summary>
public static class LiteralParser
{
    public static JsonNode Parse(string text, bool forceString = false)
    {
        if (forceString) return JsonNode.NewString(text);
        var t = text.Trim();
        switch (t)
        {
            case "true": return JsonNode.NewBoolean(true);
            case "false": return JsonNode.NewBoolean(false);
            case "null": return JsonNode.NewNull();
            case "[]": return JsonNode.NewArray();
            case "{}": return JsonNode.NewObject();
        }
        if (TryParseNumber(text, out var d)) return JsonNode.NewNumber(d);
        return JsonNode.NewString(text);
    }

    /// <summary>True when the text is a finite number (leading and trailing spaces are allowed).</summary>
    public static bool TryParseNumber(string text, out double value)
    {
        value = 0;
        var t = text.Trim();
        if (t.Length == 0) return false;
        // Reject forms that .NET accepts but a JSON user would not mean as a number.
        foreach (var c in t)
        {
            if (!(char.IsAsciiDigit(c) || c is '.' or '-' or '+' or 'e' or 'E')) return false;
        }
        if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return false;
        if (double.IsNaN(d) || double.IsInfinity(d)) return false;
        value = d;
        return true;
    }
}

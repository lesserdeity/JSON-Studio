using System.IO;
using JsonStudio.Core.Model;
using JsonStudio.Core.Serialization;

namespace JsonStudio.App.Services;

/// <summary>User settings, stored as JSON in %APPDATA%\JsonStudio\settings.json (read and written with the app's own JSON code).</summary>
public sealed class AppSettings
{
    public string DesignerMode { get; set; } = "Tables";
    public double PropertyWidth { get; set; } = 360;
    public bool PropertyCollapsed { get; set; }
    public bool ShowJson { get; set; }
    public double CodeWidth { get; set; } = 460;
    public string Indent { get; set; } = "  ";
    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;
    public double WindowWidth { get; set; } = 1360;
    public double WindowHeight { get; set; } = 840;
    public bool WindowMaximized { get; set; }
    public List<string> RecentFiles { get; set; } = new();

    /// <summary>Item-type hints per full file path: peer key → item type.</summary>
    public Dictionary<string, Dictionary<string, string>> Hints { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class SettingsService
{
    public const int MaxRecent = 8;

    private readonly string _path;

    public SettingsService()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JsonStudio");
        _path = Path.Combine(dir, "settings.json");
        Settings = Load();
    }

    public AppSettings Settings { get; }

    public event EventHandler? RecentChanged;

    public void AddRecent(string path)
    {
        var list = Settings.RecentFiles;
        list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, path);
        while (list.Count > MaxRecent) list.RemoveAt(list.Count - 1);
        Save();
        RecentChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RemoveRecent(string path)
    {
        Settings.RecentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        Save();
        RecentChanged?.Invoke(this, EventArgs.Empty);
    }

    public Dictionary<string, string> GetHints(string? filePath) =>
        filePath != null && Settings.Hints.TryGetValue(filePath, out var h) ? new Dictionary<string, string>(h) : new();

    public void SetHints(string? filePath, IReadOnlyDictionary<string, string> hints)
    {
        if (filePath == null) return;
        if (hints.Count == 0) Settings.Hints.Remove(filePath);
        else Settings.Hints[filePath] = new Dictionary<string, string>(hints);
        Save();
    }

    private AppSettings Load()
    {
        var s = new AppSettings();
        try
        {
            if (!File.Exists(_path)) return s;
            var root = JsonParser.Parse(File.ReadAllText(_path)).Root;
            if (root?.IsObject != true) return s;
            s.DesignerMode = Str(root, "designerMode") ?? s.DesignerMode;
            s.PropertyWidth = Num(root, "propertyWidth") ?? s.PropertyWidth;
            s.PropertyCollapsed = Bool(root, "propertyCollapsed") ?? s.PropertyCollapsed;
            s.ShowJson = Bool(root, "showJson") ?? s.ShowJson;
            s.CodeWidth = Num(root, "codeWidth") ?? s.CodeWidth;
            var ind = Str(root, "indent");
            if (ind is "  " or "    " or "\t") s.Indent = ind;
            if (root.Get("window") is { IsObject: true } w)
            {
                s.WindowLeft = Num(w, "left") ?? s.WindowLeft;
                s.WindowTop = Num(w, "top") ?? s.WindowTop;
                s.WindowWidth = Num(w, "width") ?? s.WindowWidth;
                s.WindowHeight = Num(w, "height") ?? s.WindowHeight;
                s.WindowMaximized = Bool(w, "maximized") ?? false;
            }
            if (root.Get("recentFiles") is { IsArray: true } r)
                s.RecentFiles = r.Children.Where(c => c.Type == NodeType.String).Select(c => c.StringValue).Take(MaxRecent).ToList();
            if (root.Get("hints") is { IsObject: true } hints)
            {
                foreach (var file in hints.Children.Where(c => c.IsObject))
                    s.Hints[file.Key] = file.Children.Where(c => c.Type == NodeType.String)
                        .GroupBy(c => c.Key).ToDictionary(g => g.Key, g => g.Last().StringValue);
            }
        }
        catch
        {
            // Bad settings file: use defaults.
        }
        return s;
    }

    public void Save()
    {
        try
        {
            var s = Settings;
            var root = JsonNode.NewObject();
            root.Add("designerMode", JsonNode.NewString(s.DesignerMode));
            root.Add("propertyWidth", JsonNode.NewNumber(Math.Round(s.PropertyWidth)));
            root.Add("propertyCollapsed", JsonNode.NewBoolean(s.PropertyCollapsed));
            root.Add("showJson", JsonNode.NewBoolean(s.ShowJson));
            root.Add("codeWidth", JsonNode.NewNumber(Math.Round(s.CodeWidth)));
            root.Add("indent", JsonNode.NewString(s.Indent));
            var w = JsonNode.NewObject();
            if (!double.IsNaN(s.WindowLeft)) w.Add("left", JsonNode.NewNumber(Math.Round(s.WindowLeft)));
            if (!double.IsNaN(s.WindowTop)) w.Add("top", JsonNode.NewNumber(Math.Round(s.WindowTop)));
            w.Add("width", JsonNode.NewNumber(Math.Round(s.WindowWidth)));
            w.Add("height", JsonNode.NewNumber(Math.Round(s.WindowHeight)));
            w.Add("maximized", JsonNode.NewBoolean(s.WindowMaximized));
            root.Add("window", w);
            var r = JsonNode.NewArray();
            foreach (var p in s.RecentFiles) r.Add(JsonNode.NewString(p));
            root.Add("recentFiles", r);
            var h = JsonNode.NewObject();
            foreach (var (file, map) in s.Hints)
            {
                var o = JsonNode.NewObject();
                foreach (var (k, v) in map) o.Add(k, JsonNode.NewString(v));
                h.Add(file, o);
            }
            root.Add("hints", h);
            FileService.WriteAtomic(_path, JsonWriter.Serialize(root, "  "));
        }
        catch
        {
            // Settings are best effort.
        }
    }

    private static string? Str(JsonNode o, string k) => o.Get(k) is { Type: NodeType.String } n ? n.StringValue : null;
    private static double? Num(JsonNode o, string k) => o.Get(k) is { Type: NodeType.Number } n ? n.NumberValue : null;
    private static bool? Bool(JsonNode o, string k) => o.Get(k) is { Type: NodeType.Boolean } n ? n.BoolValue : null;
}

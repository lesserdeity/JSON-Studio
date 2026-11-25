using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace JsonStudio.App.Services;

/// <summary>A recovery draft written by auto-save.</summary>
public sealed record RecoveryDraft(string DraftPath, string MetaPath, string? OriginalPath, DateTime SavedUtc);

/// <summary>Auto-saves recovery drafts to %LOCALAPPDATA%\JsonStudio\Recovery.</summary>
public static class RecoveryService
{
    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JsonStudio", "Recovery");

    private static string Name(string? originalPath)
    {
        var key = originalPath?.ToUpperInvariant() ?? "untitled";
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(hash)[..16];
    }

    public static void Write(string? originalPath, string text)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var name = Name(originalPath);
            FileService.WriteAtomic(Path.Combine(Folder, name + ".json"), text);
            File.WriteAllText(Path.Combine(Folder, name + ".meta"), originalPath ?? "");
        }
        catch
        {
            // Best effort.
        }
    }

    public static void Delete(string? originalPath)
    {
        try
        {
            var name = Name(originalPath);
            File.Delete(Path.Combine(Folder, name + ".json"));
            File.Delete(Path.Combine(Folder, name + ".meta"));
        }
        catch
        {
            // ignore
        }
    }

    public static void Delete(RecoveryDraft d)
    {
        try
        {
            File.Delete(d.DraftPath);
            File.Delete(d.MetaPath);
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>Drafts that are newer than their file (or whose file is gone / untitled), newest first.</summary>
    public static List<RecoveryDraft> FindRestorable()
    {
        var list = new List<RecoveryDraft>();
        if (!Directory.Exists(Folder)) return list;
        foreach (var meta in Directory.GetFiles(Folder, "*.meta"))
        {
            var draft = Path.ChangeExtension(meta, ".json");
            if (!File.Exists(draft))
            {
                try { File.Delete(meta); } catch { /* ignore */ }
                continue;
            }
            string? orig = File.ReadAllText(meta).Trim();
            if (orig.Length == 0) orig = null;
            var saved = File.GetLastWriteTimeUtc(draft);
            bool newer = orig == null || !File.Exists(orig) || saved > File.GetLastWriteTimeUtc(orig);
            var d = new RecoveryDraft(draft, meta, orig, saved);
            if (newer) list.Add(d);
            else Delete(d);
        }
        return list.OrderByDescending(d => d.SavedUtc).ToList();
    }
}

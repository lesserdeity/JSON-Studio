using System.IO;
using System.Text;
using Microsoft.Win32;

namespace JsonStudio.App.Services;

/// <summary>Native Open/Save dialogs and safe file writes.</summary>
public static class FileService
{
    private const string Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*";
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static string? ShowOpenDialog(string? initialPath)
    {
        var dlg = new OpenFileDialog { Filter = Filter, Title = "Open JSON file", CheckFileExists = true };
        if (initialPath != null && Directory.Exists(Path.GetDirectoryName(initialPath)))
            dlg.InitialDirectory = Path.GetDirectoryName(initialPath);
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    public static string? ShowSaveDialog(string? currentPath, string suggestedName)
    {
        var dlg = new SaveFileDialog
        {
            Filter = Filter,
            Title = "Save JSON file",
            DefaultExt = ".json",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = suggestedName,
        };
        if (currentPath != null && Directory.Exists(Path.GetDirectoryName(currentPath)))
            dlg.InitialDirectory = Path.GetDirectoryName(currentPath);
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    public static string ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        // Honour a BOM if one is present; otherwise UTF-8.
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>
    /// Writes UTF-8 without BOM to a temporary file in the same folder, then replaces the original,
    /// so a failure cannot leave a half-written file.
    /// </summary>
    public static void WriteAtomic(string path, string text)
    {
        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = Utf8NoBom.GetBytes(text);
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(flushToDisk: true);
            }

            if (File.Exists(full))
            {
                try
                {
                    File.Replace(temp, full, destinationBackupFileName: null, ignoreMetadataErrors: true);
                }
                catch (IOException)
                {
                    // Some file systems (for example network or cloud folders) do not support Replace.
                    File.Copy(temp, full, overwrite: true);
                    File.Delete(temp);
                }
            }
            else
            {
                File.Move(temp, full);
            }
        }
        finally
        {
            if (File.Exists(temp))
            {
                try { File.Delete(temp); } catch { /* ignore */ }
            }
        }
    }
}

using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Shell;
using Microsoft.Win32;

namespace JsonStudio.App.Services;

/// <summary>"Open with" registration for .json files and the Windows jump list of recent files.</summary>
public static class ShellIntegration
{
    private const string AppKey = @"Software\Classes\Applications\JsonStudio.exe";

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    /// <summary>Registers this exe as an "Open with" choice for .json files (per user, no admin needed).</summary>
    public static void RegisterOpenWith()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe == null || !string.Equals(Path.GetFileName(exe), "JsonStudio.exe", StringComparison.OrdinalIgnoreCase)) return;
            string command = $"\"{exe}\" \"%1\"";

            using (var existing = Registry.CurrentUser.OpenSubKey(AppKey + @"\shell\open\command"))
            {
                if (existing?.GetValue(null) as string == command) return;
            }

            using (var app = Registry.CurrentUser.CreateSubKey(AppKey))
            {
                app.SetValue("FriendlyAppName", "JSON Studio");
                using (var types = app.CreateSubKey("SupportedTypes")) types.SetValue(".json", "");
                using (var icon = app.CreateSubKey("DefaultIcon")) icon.SetValue(null, $"\"{exe}\",0");
                using (var cmd = app.CreateSubKey(@"shell\open\command")) cmd.SetValue(null, command);
            }
            using (Registry.CurrentUser.CreateSubKey(@"Software\Classes\.json\OpenWithList\JsonStudio.exe")) { }

            SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero); // SHCNE_ASSOCCHANGED
        }
        catch
        {
            // Registration is optional.
        }
    }

    public static void UnregisterOpenWith()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(AppKey, throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\.json\OpenWithList\JsonStudio.exe", throwOnMissingSubKey: false);
            SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>Shows the recent files in the taskbar jump list.</summary>
    public static void UpdateJumpList(IEnumerable<string> recent)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe == null) return;
            var list = new JumpList { ShowRecentCategory = false, ShowFrequentCategory = false };
            foreach (var path in recent)
            {
                list.JumpItems.Add(new JumpTask
                {
                    Title = Path.GetFileName(path),
                    Description = path,
                    ApplicationPath = exe,
                    Arguments = $"\"{path}\"",
                    IconResourcePath = exe,
                    CustomCategory = "Recent",
                });
            }
            JumpList.SetJumpList(Application.Current, list);
            list.Apply();
        }
        catch
        {
            // Jump lists are optional.
        }
    }
}

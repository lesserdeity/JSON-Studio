using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace JsonStudio.App.Services;

/// <summary>Follows the Windows app theme (light or dark) and swaps the brush dictionary.</summary>
public static class ThemeService
{
    private static ResourceDictionary? _current;

    public static bool IsDark { get; private set; }

    public static event EventHandler? ThemeChanged;

    public static void Initialize()
    {
        Apply(ReadSystemIsDark());
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    bool dark = ReadSystemIsDark();
                    if (dark != IsDark) Apply(dark);
                });
        };
    }

    public static bool ReadSystemIsDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch
        {
            return false;
        }
    }

    public static void Apply(bool dark)
    {
        IsDark = dark;
        var dicts = Application.Current.Resources.MergedDictionaries;
        var next = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/JsonStudio;component/Themes/{(dark ? "Dark" : "Light")}.xaml"),
        };
        if (_current != null) dicts.Remove(_current);
        dicts.Insert(0, next);
        _current = next;
        foreach (Window w in Application.Current.Windows) ApplyTitleBar(w);
        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Uses a dark title bar in the dark theme (Windows 10 2004+ / 11).</summary>
    public static void ApplyTitleBar(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        int v = IsDark ? 1 : 0;
        if (DwmSetWindowAttribute(hwnd, 20, ref v, sizeof(int)) != 0)
            DwmSetWindowAttribute(hwnd, 19, ref v, sizeof(int));
    }
}

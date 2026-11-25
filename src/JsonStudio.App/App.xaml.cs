using System.IO;
using System.Windows;
using System.Windows.Threading;
using JsonStudio.App.Services;
using JsonStudio.App.ViewModels;
using JsonStudio.App.Views;

namespace JsonStudio.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Length == 1 && e.Args[0] == "--unregister")
        {
            ShellIntegration.UnregisterOpenWith();
            Shutdown();
            return;
        }

        // The Light dictionary in App.xaml is only a design-time default.
        var first = Resources.MergedDictionaries.FirstOrDefault(d => d.Source?.OriginalString.Contains("Light.xaml") == true);
        if (first != null) Resources.MergedDictionaries.Remove(first);
        ThemeService.Initialize();

        DispatcherUnhandledException += OnUnhandledException;

        var settings = new SettingsService();
        var vm = new MainViewModel(settings);
        var window = new MainWindow(vm);
        MainWindow = window;
        window.Show();

        ShellIntegration.RegisterOpenWith();
        ShellIntegration.UpdateJumpList(settings.Settings.RecentFiles);

        var file = e.Args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
        window.Dispatcher.BeginInvoke(() =>
        {
            vm.OfferRecovery();
            if (file != null)
            {
                if (File.Exists(file)) vm.Open(file);
                else MessageBox.Show(window, $"The file \"{file}\" does not exist.", "JSON Studio", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }, DispatcherPriority.ApplicationIdle);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JsonStudio");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "errors.log"), $"{DateTime.Now:O}\n{e.Exception}\n\n");
        }
        catch
        {
            // ignore
        }
        MessageBox.Show(MainWindow!, $"Something went wrong:\n\n{e.Exception.Message}\n\nYour document is still open. Save your work.",
            "JSON Studio", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}

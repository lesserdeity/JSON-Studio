using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using JsonStudio.App.Controls;
using JsonStudio.App.Services;
using JsonStudio.App.ViewModels;

namespace JsonStudio.App.Views;

public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value == null || value as string == "" ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly TablesView _tables;
    private readonly TreeDesigner _tree;
    private readonly CodePane _code;
    private readonly PaneSplitter _codeSplitter = new();
    private double _codeDragWidth;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;

        _tables = new TablesView(vm);
        _tree = new TreeDesigner(vm);
        _code = new CodePane(vm);
        TablesHost.Content = _tables;
        TreeHost.Content = _tree;
        CodeHost.Content = _code;
        CodeSplitterHost.Content = _codeSplitter;

        _codeSplitter.DragStarted += (_, _) => _codeDragWidth = CodeColumn.ActualWidth;
        _codeSplitter.DragDelta += (_, e) =>
        {
            _codeDragWidth -= e.HorizontalChange;
            double w = Math.Clamp(_codeDragWidth, 240, Math.Max(240, WorkGrid.ActualWidth - 380));
            CodeColumn.Width = new GridLength(w);
            vm.Settings.CodeWidth = w;
        };
        _codeSplitter.DragCompleted += (_, _) => vm.SettingsService.Save();

        vm.PropertyChanged += OnVmPropertyChanged;
        vm.CommitEditsRequested += CommitEdits;
        ApplyMode();
        ApplyCodeVisibility();

        FileNameLabel.MouseEnter += (_, _) => FileNameLabel.SetResourceReference(Border.BackgroundProperty, "Toolbar.Hover");
        FileNameLabel.MouseLeave += (_, _) => FileNameLabel.Background = System.Windows.Media.Brushes.Transparent;
        FileNameLabel.MouseLeftButtonUp += (_, _) => BeginRename();
        FileNameBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                CommitRename(keepEditingOnError: true);
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                EndRename();
            }
        };
        FileNameBox.LostKeyboardFocus += (_, _) => CommitRename(keepEditingOnError: false);

        NewButton.Click += (_, _) => ShowNewMenu();
        RecentButton.Click += (_, _) => ShowRecentMenu();

        RestorePlacement();
        SourceInitialized += (_, _) => ThemeService.ApplyTitleBar(this);
        Closing += OnClosing;
        Closed += (_, _) => vm.OnClosed();
        PreviewKeyDown += OnWindowPreviewKeyDown;
        DragOver += OnDragOver;
        Drop += OnDrop;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.DesignerMode):
                ApplyMode();
                break;
            case nameof(MainViewModel.ShowJson):
                ApplyCodeVisibility();
                break;
        }
    }

    private void ApplyMode()
    {
        TablesHost.Visibility = _vm.IsTablesMode ? Visibility.Visible : Visibility.Collapsed;
        TreeHost.Visibility = _vm.IsTreeMode ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyCodeVisibility()
    {
        bool show = _vm.ShowJson;
        CodeHost.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        CodeSplitterHost.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        CodeColumn.Width = show ? new GridLength(Math.Max(240, _vm.Settings.CodeWidth)) : new GridLength(0);
    }

    /// <summary>Commits the field being edited, synchronously (before save, undo, open, close).</summary>
    // ---- rename from the toolbar ---------------------------------------------------------------

    private bool _renaming;

    private void BeginRename()
    {
        _renaming = true;
        var name = _vm.FileName;
        FileNameBox.Text = name;
        FileNameLabel.Visibility = Visibility.Collapsed;
        FileNameBox.Visibility = Visibility.Visible;
        FileNameBox.Focus();
        Keyboard.Focus(FileNameBox);
        // Select the name without the extension, as Explorer does.
        int len = Path.HasExtension(name) ? name.Length - Path.GetExtension(name).Length : name.Length;
        FileNameBox.Select(0, len);
    }

    private void CommitRename(bool keepEditingOnError)
    {
        if (!_renaming) return;
        if (_vm.RenameDocument(FileNameBox.Text, out var error))
        {
            EndRename();
            if (_vm.HasPendingName)
                _vm.ShowMessage(_vm.FilePath != null
                    ? $"The file will be saved as {_vm.FileName} the next time you save."
                    : $"{_vm.FileName} will be suggested when you save.");
            return;
        }
        _vm.ShowMessage(error ?? "That file name isn't valid.");
        if (keepEditingOnError)
        {
            FileNameBox.Focus();
            FileNameBox.SelectAll();
        }
        else EndRename();
    }

    private void EndRename()
    {
        if (!_renaming) return;
        _renaming = false;
        FileNameBox.Visibility = Visibility.Collapsed;
        FileNameLabel.Visibility = Visibility.Visible;
        // Give focus back to the window so its shortcuts (Ctrl+S, ...) keep working.
        if (FileNameBox.IsKeyboardFocusWithin || Keyboard.FocusedElement == null) Keyboard.Focus(this);
    }

    private void CommitEdits()
    {
        CommitRename(keepEditingOnError: false);
        ValueEditor.CommitActive();
        _code.FlushPending();
    }

    private void ShowNewMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(Ui.Item("Empty object", () => _vm.New(NewKind.EmptyObject)));
        menu.Items.Add(Ui.Item("Empty array", () => _vm.New(NewKind.EmptyArray)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Ui.Item("Sample incentive plan", () => _vm.New(NewKind.Sample)));
        Ui.ShowMenu(NewButton, menu);
    }

    private void ShowRecentMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(Ui.Label("Recent files"));
        if (_vm.RecentFiles.Count == 0) menu.Items.Add(new MenuItem { Header = "No recent files", IsEnabled = false });
        foreach (var path in _vm.RecentFiles)
        {
            var p = path;
            var item = Ui.Item(Path.GetFileName(p), () => _vm.Open(p));
            item.ToolTip = p;
            menu.Items.Add(item);
        }
        Ui.ShowMenu(RecentButton, menu);
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        bool inCode = _code.Editor.IsEditing;

        if (!inCode && mods == ModifierKeys.Control && key == Key.Z || !inCode && mods == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.Z)
        {
            var cmd = mods.HasFlag(ModifierKeys.Shift) ? _vm.RedoCommand : _vm.UndoCommand;
            if (cmd.CanExecute(null)) cmd.Execute(null);
            e.Handled = true;
        }
        else if (!inCode && mods == ModifierKeys.Control && key == Key.Y)
        {
            if (_vm.RedoCommand.CanExecute(null)) _vm.RedoCommand.Execute(null);
            e.Handled = true;
        }
        else if (mods == ModifierKeys.Control && key == Key.F)
        {
            FindBox.Focus();
            FindBox.SelectAll();
            e.Handled = true;
        }
        else if (mods == ModifierKeys.Alt && key == Key.Left && _vm.IsTablesMode && !inCode)
        {
            e.Handled = _tables.PropertyBack();
        }
        else if (e.Key == Key.Escape && FindBox.IsKeyboardFocused)
        {
            _vm.FindText = "";
            e.Handled = true;
        }
    }

    // ---- drag and drop a file -------------------------------------------------------------------

    private static string? DroppedJson(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return null;
        return (e.Data.GetData(DataFormats.FileDrop) as string[])?.FirstOrDefault(p =>
            p.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && File.Exists(p));
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DroppedJson(e) != null ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        var path = DroppedJson(e);
        if (path == null) return;
        e.Handled = true;
        Dispatcher.BeginInvoke(() => _vm.Open(path));
    }

    // ---- window placement & close ------------------------------------------------------------------

    private void RestorePlacement()
    {
        var s = _vm.Settings;
        Width = Math.Max(MinWidth, s.WindowWidth);
        Height = Math.Max(MinHeight, s.WindowHeight);
        if (!double.IsNaN(s.WindowLeft) && !double.IsNaN(s.WindowTop))
        {
            var left = s.WindowLeft;
            var top = s.WindowTop;
            // Keep the window on the virtual screen.
            if (left + 100 < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth &&
                top + 50 < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight &&
                left + Width > SystemParameters.VirtualScreenLeft + 50 && top > SystemParameters.VirtualScreenTop - 20)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = left;
                Top = top;
            }
            else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (s.WindowMaximized) WindowState = WindowState.Maximized;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_vm.ConfirmClose())
        {
            e.Cancel = true;
            return;
        }
        var s = _vm.Settings;
        var r = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        s.WindowLeft = r.Left;
        s.WindowTop = r.Top;
        s.WindowWidth = r.Width;
        s.WindowHeight = r.Height;
        s.WindowMaximized = WindowState == WindowState.Maximized;
        _vm.SettingsService.Save();
    }
}

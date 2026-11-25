using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using JsonStudio.App.Controls;
using JsonStudio.App.ViewModels;
using JsonStudio.Core.Editing;
using JsonStudio.Core.Model;
using JsonStudio.Core.Serialization;

namespace JsonStudio.App.Views;

/// <summary>The JSON code pane (spec 7.5, 7.6).</summary>
public sealed class CodePane : UserControl
{
    private readonly MainViewModel _vm;
    private readonly CodeEditor _code = new();
    private readonly DispatcherTimer _parseTimer;
    private readonly Border _errorPill;
    private readonly Border _errorBar;
    private readonly TextBlock _errorText;
    private SerializeResult? _model;
    private bool _isModelText;
    private bool _applying;
    private bool _resetQueued;
    private bool _stale = true;
    private readonly DispatcherTimer _syncTimer;
    private int _caretLine;

    public CodePane(MainViewModel vm)
    {
        _vm = vm;
        this.Res(BackgroundProperty, "Bg.Panel");

        var title = Ui.Text("JSON", "Fg.Text", 14, FontWeights.SemiBold);
        title.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Title");
        _errorPill = new Border
        {
            Child = Ui.Text("Error", "Accent.Fg", 11, FontWeights.SemiBold),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(8, 1, 8, 2),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        _errorPill.Res(Border.BackgroundProperty, "Error");
        var format = Ui.Button("Format", "CmdButton", "Rewrite the code in the standard format");
        format.Click += (_, _) => Format();
        var copy = Ui.Button("Copy", "CmdButton", "Copy the JSON");
        copy.Margin = new Thickness(6, 0, 0, 0);
        copy.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(_code.Text);
                _vm.ShowMessage("JSON copied.");
            }
            catch
            {
                // clipboard busy
            }
        };

        var head = new DockPanel { Margin = new Thickness(12, 0, 8, 0), Height = 44 };
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(format);
        right.Children.Add(copy);
        DockPanel.SetDock(right, Dock.Right);
        head.Children.Add(right);
        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(title);
        left.Children.Add(_errorPill);
        head.Children.Add(left);
        var headBorder = new Border { Child = head, BorderThickness = new Thickness(0, 0, 0, 1) };
        headBorder.Res(Border.BorderBrushProperty, "Border");
        headBorder.Res(Border.BackgroundProperty, "Bg.Header");

        _errorText = Ui.Text("", "Error", 12.5);
        _errorText.TextWrapping = TextWrapping.Wrap;
        var go = Ui.Button("Go to line", "CmdButton");
        go.Margin = new Thickness(8, 0, 0, 0);
        go.Click += (_, _) => GoToError();
        var bar = new DockPanel { Margin = new Thickness(12, 6, 8, 6) };
        DockPanel.SetDock(go, Dock.Right);
        bar.Children.Add(go);
        bar.Children.Add(_errorText);
        _errorBar = new Border { Child = bar, Visibility = Visibility.Collapsed, BorderThickness = new Thickness(0, 0, 0, 1) };
        _errorBar.Res(Border.BackgroundProperty, "Error.Soft");
        _errorBar.Res(Border.BorderBrushProperty, "Border");

        var dock = new DockPanel();
        DockPanel.SetDock(headBorder, Dock.Top);
        dock.Children.Add(headBorder);
        DockPanel.SetDock(_errorBar, Dock.Top);
        dock.Children.Add(_errorBar);
        dock.Children.Add(_code);
        Content = dock;

        _parseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _parseTimer.Tick += (_, _) =>
        {
            _parseTimer.Stop();
            Parse();
        };

        _code.Indent = vm.Indent;
        _code.UserTextChanged += () =>
        {
            _isModelText = false;
            _code.Band = null;
            _parseTimer.Stop();
            _parseTimer.Start();
        };
        _code.EditorLostFocus += OnLostFocus;
        _syncTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _syncTimer.Tick += (_, _) =>
        {
            _syncTimer.Stop();
            SyncDesignerToCaret();
        };
        _code.CaretLineChanged += line =>
        {
            _caretLine = line;
            _syncTimer.Stop();
            _syncTimer.Start();
        };

        vm.Editor.Changed += OnModelChanged;
        vm.SelectionChanged += (_, _) => UpdateBand(reveal: true);
        vm.RawCodeRequested += text =>
        {
            _isModelText = false;
            _stale = false;
            _code.Band = null;
            _code.SetText(text);
            ShowError();
        };
        vm.GoToErrorRequested += GoToError;
        vm.CodeResetRequested += () => ResetFromModel(force: true);
        vm.PropertyChanged += OnVmPropertyChanged;
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible && (_stale || (_isModelText && !_vm.IsLocked)))
            {
                ResetFromModel();
                UpdateBand(reveal: true);
            }
        };
    }

    public CodeEditor Editor => _code;

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.Indent):
                _code.Indent = _vm.Indent;
                break;
            case nameof(MainViewModel.CodeError):
                ShowError();
                break;
        }
    }

    private void ShowError()
    {
        var err = _vm.CodeError;
        _errorPill.Visibility = err != null ? Visibility.Visible : Visibility.Collapsed;
        _errorBar.Visibility = err != null ? Visibility.Visible : Visibility.Collapsed;
        _errorText.Text = err?.ToString() ?? "";
        _code.ErrorLine = err?.Line;
    }

    private void OnModelChanged(object? sender, DocumentChangedEventArgs e)
    {
        if (_applying || _code.IsEditing) return;
        if (!IsVisible)
        {
            _stale = true;
            return;
        }
        if (_resetQueued) return;
        _resetQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _resetQueued = false;
            if (!_code.IsEditing && !_vm.IsLocked) ResetFromModel();
        }, DispatcherPriority.Background);
    }

    /// <summary>Replaces the text with the serializer output (the "model text").</summary>
    public void ResetFromModel(bool force = false)
    {
        if (!IsVisible && !force)
        {
            _stale = true;
            return;
        }
        _stale = false;
        _model = JsonWriter.SerializeWithSpans(_vm.Editor.Root, _vm.Indent);
        _code.SetText(_model.Text);
        _isModelText = true;
        if (_vm.CodeError != null) _vm.CodeError = null;
        ShowError();
        UpdateBand(reveal: false);
    }

    private void UpdateBand(bool reveal)
    {
        if (!_isModelText || _model == null || !IsVisible)
        {
            _code.Band = null;
            return;
        }
        var node = _vm.SelectedNode;
        if (node != null && node.Parent != null && _model.Spans.TryGetValue(node.Id, out var span))
        {
            _code.Band = span;
            if (reveal) Dispatcher.BeginInvoke(_code.RevealBand, DispatcherPriority.Loaded);
        }
        else _code.Band = null;
    }

    /// <summary>Parses the code. Valid: rebuilds the model. Invalid: shows the error state.</summary>
    private bool Parse()
    {
        var r = JsonParser.Parse(_code.Text);
        if (r.Root == null)
        {
            _vm.CodeError = r.Error;
            ShowError();
            return false;
        }
        _applying = true;
        try
        {
            _vm.ApplyCode(r.Root);
        }
        finally
        {
            _applying = false;
        }
        ShowError();
        // If the edited text is still in the standard format, the line map stays valid.
        var m = JsonWriter.SerializeWithSpans(_vm.Editor.Root, _vm.Indent);
        if (m.Text == _code.Text)
        {
            _model = m;
            _isModelText = true;
        }
        return true;
    }

    /// <summary>Selects the node on the caret line and shows it in the Tables / Tree view.</summary>
    private void SyncDesignerToCaret()
    {
        if (!_isModelText || _model == null || _vm.IsLocked || _caretLine <= 0) return;
        var node = NodeAtLine(_model, _caretLine);
        if (node == null || node == _vm.SelectedNode) return;
        _vm.RevealFromCode(node);
    }

    /// <summary>The innermost node whose line span contains the line.</summary>
    private JsonNode? NodeAtLine(SerializeResult model, int line)
    {
        long bestId = -1;
        int bestSize = int.MaxValue, bestStart = -1;
        foreach (var (id, span) in model.Spans)
        {
            if (line < span.StartLine || line > span.EndLine) continue;
            int size = span.EndLine - span.StartLine;
            if (size < bestSize || (size == bestSize && span.StartLine > bestStart))
            {
                bestId = id;
                bestSize = size;
                bestStart = span.StartLine;
            }
        }
        return bestId < 0 ? null : _vm.Editor.FindById(bestId);
    }

    private void OnLostFocus()
    {
        bool valid;
        if (_parseTimer.IsEnabled)
        {
            _parseTimer.Stop();
            valid = Parse();
        }
        else valid = _vm.CodeError == null;
        _vm.Editor.EndBurst();
        if (valid && !_isModelText) ResetFromModel(); // rewrite in the standard format
    }

    private void Format()
    {
        _parseTimer.Stop();
        if (Parse()) ResetFromModel(force: true);
        else GoToError();
    }

    /// <summary>Parses pending code edits now (before save, undo, close).</summary>
    public void FlushPending()
    {
        if (!_parseTimer.IsEnabled) return;
        _parseTimer.Stop();
        Parse();
    }

    public void GoToError()
    {
        if (_vm.CodeError is { } err)
        {
            if (!_vm.ShowJson) _vm.ShowJson = true;
            Dispatcher.BeginInvoke(() => _code.GoTo(err.Line, err.Column), DispatcherPriority.Loaded);
        }
    }
}

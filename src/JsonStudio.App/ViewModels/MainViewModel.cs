using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using JsonStudio.App.Services;
using JsonStudio.Core.Editing;
using JsonStudio.Core.Model;
using JsonStudio.Core.Serialization;

namespace JsonStudio.App.ViewModels;

public enum NewKind
{
    EmptyObject,
    EmptyArray,
    Sample,
}

/// <summary>The application shell: file state, toolbar/command bar commands, selection, find, code error state.</summary>
public sealed class MainViewModel : ViewModelBase
{
    private readonly SettingsService _settings;
    private readonly DispatcherTimer _statsTimer;
    private readonly DispatcherTimer _recoveryTimer;
    private readonly DispatcherTimer _toastTimer;
    private FileSystemWatcher? _watcher;
    private DateTime _knownWriteUtc;

    private string? _filePath;
    private bool _isSample;
    private long _savedVersion;
    private bool _forceDirty;
    private string? _pendingName;
    private string _indent;
    private bool _showJson;
    private string _designerMode;
    private string _findText = "";
    private int _findCount;
    private NodeRef? _selected;
    private string _statusPath = "$";
    private int _nodeCount;
    private int _depth;
    private string _sizeText = "";
    private ParseError? _codeError;
    private bool _fileChangedOnDisk;
    private string? _toast;

    public MainViewModel(SettingsService settings)
    {
        _settings = settings;
        Editor = new DocumentEditor(JsonNode.NewObject());
        _indent = settings.Settings.Indent;
        _showJson = settings.Settings.ShowJson;
        _designerMode = settings.Settings.DesignerMode == "Tree" ? "Tree" : "Tables";
        RecentFiles = new ObservableCollection<string>(settings.Settings.RecentFiles);
        settings.RecentChanged += (_, _) =>
        {
            RecentFiles.Clear();
            foreach (var r in settings.Settings.RecentFiles) RecentFiles.Add(r);
            ShellIntegration.UpdateJumpList(RecentFiles);
        };

        Editor.Changed += OnEditorChanged;

        _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _statsTimer.Tick += (_, _) =>
        {
            _statsTimer.Stop();
            UpdateStats();
        };
        _recoveryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _recoveryTimer.Tick += (_, _) => WriteRecoveryDraft();
        _recoveryTimer.Start();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            Toast = null;
        };

        NewCommand = new RelayCommand(p => New(p is NewKind k ? k : NewKind.EmptyObject));
        OpenCommand = new RelayCommand(() => Open(null));
        OpenRecentCommand = new RelayCommand(p => { if (p is string s) Open(s); });
        SaveCommand = new RelayCommand(() => Save(false));
        SaveAsCommand = new RelayCommand(() => Save(true));
        UndoCommand = new RelayCommand(() => Undo(), () => Editor.History.CanUndo && !IsLocked);
        RedoCommand = new RelayCommand(() => Redo(), () => Editor.History.CanRedo && !IsLocked);
        SortCommand = new RelayCommand(() => Editor.SortKeys(), () => !IsLocked);
        ToggleJsonCommand = new RelayCommand(() => ShowJson = !ShowJson);
        ExpandAllCommand = new RelayCommand(() => SetAllCollapsed(false), () => IsTreeMode);
        CollapseAllCommand = new RelayCommand(() => SetAllCollapsed(true), () => IsTreeMode);
        CopyPathCommand = new RelayCommand(() => TrySetClipboard(StatusPath));
        ReloadCommand = new RelayCommand(ReloadFromDisk);
        DismissReloadCommand = new RelayCommand(() => FileChangedOnDisk = false);
        GoToErrorCommand = new RelayCommand(() => GoToErrorRequested?.Invoke());
        DiscardCodeCommand = new RelayCommand(DiscardCodeEdits);
        DismissToastCommand = new RelayCommand(() => Toast = null);

        LoadDocument(JsonNode.NewObject(), null, isSample: false, indent: null);
        Editor.Root.Add("newField", JsonNode.NewString(""));
        _savedVersion = Editor.Version;
        UpdateStats();
    }

    public DocumentEditor Editor { get; }
    public AppSettings Settings => _settings.Settings;
    public SettingsService SettingsService => _settings;
    public ObservableCollection<string> RecentFiles { get; }

    // ---- events for views ----------------------------------------------------

    /// <summary>The selected node changed.</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>The find text changed.</summary>
    public event EventHandler? FindChanged;

    /// <summary>Tree collapsed state changed (expand/collapse all).</summary>
    public event EventHandler? TreeStateChanged;

    /// <summary>A document was loaded with text that cannot be parsed: show it in the code pane.</summary>
    public event Action<string>? RawCodeRequested;

    public event Action? GoToErrorRequested;

    /// <summary>The code pane asks the designer to show a node (caret moved to its line).</summary>
    public event Action<JsonNode>? RevealRequested;

    /// <summary>Asks the views to commit the field being edited (before save, undo, open, …).</summary>
    public event Action? CommitEditsRequested;

    /// <summary>The code pane should replace its text with the serializer output.</summary>
    public event Action? CodeResetRequested;

    /// <summary>The whole document was replaced by a new one (new, open, reload).</summary>
    public event EventHandler? DocumentLoaded;

    // ---- commands ------------------------------------------------------------------

    public ICommand NewCommand { get; }
    public ICommand OpenCommand { get; }
    public ICommand OpenRecentCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand SaveAsCommand { get; }
    public ICommand UndoCommand { get; }
    public ICommand RedoCommand { get; }
    public ICommand SortCommand { get; }
    public ICommand ToggleJsonCommand { get; }
    public ICommand ExpandAllCommand { get; }
    public ICommand CollapseAllCommand { get; }
    public ICommand CopyPathCommand { get; }
    public ICommand ReloadCommand { get; }
    public ICommand DismissReloadCommand { get; }
    public ICommand GoToErrorCommand { get; }
    public ICommand DiscardCodeCommand { get; }
    public ICommand DismissToastCommand { get; }

    // ---- file state ------------------------------------------------------------------

    public string? FilePath
    {
        get => _filePath;
        private set
        {
            if (Set(ref _filePath, value))
            {
                OnPropertyChanged(nameof(FileName));
                OnPropertyChanged(nameof(Title));
                OnPropertyChanged(nameof(FilePathText));
            }
        }
    }

    public string FilePathText => _pendingName != null && _filePath != null
        ? Path.Combine(Path.GetDirectoryName(_filePath) ?? "", _pendingName) + "  (on next save)"
        : _filePath ?? "Not saved";

    public bool IsSample
    {
        get => _isSample;
        private set => Set(ref _isSample, value);
    }

    public string FileName => _pendingName ?? (_filePath != null ? Path.GetFileName(_filePath) : _isSample ? "sample.json" : "untitled.json");

    /// <summary>The file name will change to this on the next save (null when unchanged).</summary>
    public bool HasPendingName => _pendingName != null;

    /// <summary>
    /// Renames the document. The new name is used on the next Save: a saved document is written to the new name in the
    /// same folder; a new document suggests the name in the Save dialog. Returns false with a message when invalid.
    /// </summary>
    public bool RenameDocument(string name, out string? error)
    {
        error = null;
        name = (name ?? "").Trim();
        if (name.Length == 0)
        {
            error = "The file name can't be empty.";
            return false;
        }
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Trim('.').Length == 0)
        {
            error = "A file name can't contain any of these characters: \\ / : * ? \" < > |";
            return false;
        }
        if (!Path.HasExtension(name)) name += ".json";
        string current = _filePath != null ? Path.GetFileName(_filePath) : _isSample ? "sample.json" : "untitled.json";
        _pendingName = string.Equals(name, current, StringComparison.Ordinal) ? null : name;
        OnPropertyChanged(nameof(FileName));
        OnPropertyChanged(nameof(FilePathText));
        OnPropertyChanged(nameof(HasPendingName));
        RaiseDirty();
        return true;
    }

    public bool IsDirty => _forceDirty || _pendingName != null || Editor.Version != _savedVersion;

    public string Title => (IsDirty ? "• " : "") + FileName + " — JSON Studio";

    private void RaiseDirty()
    {
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(Title));
    }

    // ---- settings-backed state ---------------------------------------------------

    public string Indent
    {
        get => _indent;
        set
        {
            if (!Set(ref _indent, value)) return;
            Settings.Indent = value;
            _settings.Save();
            OnPropertyChanged(nameof(IndentIs2));
            OnPropertyChanged(nameof(IndentIs4));
            OnPropertyChanged(nameof(IndentIsTab));
            Editor.Touch(); // marks the document as changed and refreshes the code pane
        }
    }

    public bool IndentIs2 { get => _indent == "  "; set { if (value) Indent = "  "; } }
    public bool IndentIs4 { get => _indent == "    "; set { if (value) Indent = "    "; } }
    public bool IndentIsTab { get => _indent == "\t"; set { if (value) Indent = "\t"; } }

    public bool ShowJson
    {
        get => _showJson;
        set
        {
            if (!Set(ref _showJson, value)) return;
            Settings.ShowJson = value;
            _settings.Save();
            OnPropertyChanged(nameof(ShowJsonText));
        }
    }

    public string ShowJsonText => _showJson ? "Hide JSON" : "Show JSON";

    public string DesignerMode
    {
        get => _designerMode;
        set
        {
            if (!Set(ref _designerMode, value)) return;
            Settings.DesignerMode = value;
            _settings.Save();
            OnPropertyChanged(nameof(IsTablesMode));
            OnPropertyChanged(nameof(IsTreeMode));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsTablesMode { get => _designerMode == "Tables"; set { if (value) DesignerMode = "Tables"; } }
    public bool IsTreeMode { get => _designerMode == "Tree"; set { if (value) DesignerMode = "Tree"; } }

    // ---- find ------------------------------------------------------------------------

    public string FindText
    {
        get => _findText;
        set
        {
            if (!Set(ref _findText, value ?? "")) return;
            if (_findText.Length > 0) ExpandToMatches();
            UpdateFindCount();
            FindChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public int FindCount
    {
        get => _findCount;
        private set
        {
            if (Set(ref _findCount, value)) OnPropertyChanged(nameof(FindCountText));
        }
    }

    public string FindCountText => _findText.Length == 0 ? "" : _findCount == 1 ? "1 match" : $"{_findCount} matches";

    public bool IsMatch(JsonNode n) => _findText.Length > 0 && DocumentEditor.Matches(n, _findText);

    private void UpdateFindCount()
    {
        FindCount = _findText.Length == 0 ? 0 : Editor.Root.Descendants().Count(n => DocumentEditor.Matches(n, _findText));
        OnPropertyChanged(nameof(FindCountText));
    }

    private void ExpandToMatches()
    {
        foreach (var n in Editor.Root.Descendants())
        {
            if (!DocumentEditor.Matches(n, _findText)) continue;
            for (var p = n.Parent; p != null; p = p.Parent) p.Collapsed = false;
        }
        TreeStateChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---- selection & status ---------------------------------------------------------

    public JsonNode? SelectedNode => _selected?.Resolve(Editor);

    public void Select(JsonNode? node)
    {
        var old = SelectedNode;
        _selected = node == null ? null : NodeRef.Of(node);
        StatusPath = node == null ? "$" : NodePath.Format(node);
        if (old != node) SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Shows a node in the designer (Tables and Tree) and selects it.</summary>
    public void RevealFromCode(JsonNode node)
    {
        RevealRequested?.Invoke(node);
        Select(node);
    }

    public string StatusPath
    {
        get => _statusPath;
        private set => Set(ref _statusPath, value);
    }

    public int NodeCount { get => _nodeCount; private set => Set(ref _nodeCount, value); }
    public int Depth { get => _depth; private set => Set(ref _depth, value); }
    public string SizeText { get => _sizeText; private set => Set(ref _sizeText, value); }
    public string NodeCountText => $"{_nodeCount:N0} nodes";

    private void UpdateStats()
    {
        var (count, depth) = DocumentEditor.Stats(Editor.Root);
        NodeCount = count;
        Depth = depth;
        OnPropertyChanged(nameof(NodeCountText));
        long bytes = System.Text.Encoding.UTF8.GetByteCount(JsonWriter.Serialize(Editor.Root, _indent));
        SizeText = bytes < 1024 ? $"{bytes} B" : bytes < 1024 * 1024 ? $"{bytes / 1024.0:0.0} KB" : $"{bytes / 1048576.0:0.00} MB";
    }

    // ---- toast messages ---------------------------------------------------------------

    public string? Toast
    {
        get => _toast;
        private set => Set(ref _toast, value);
    }

    public void ShowMessage(string message)
    {
        Toast = message;
        _toastTimer.Stop();
        _toastTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(message.Length / 12.0, 4, 9));
        _toastTimer.Start();
    }

    // ---- code error state ---------------------------------------------------------------

    public ParseError? CodeError
    {
        get => _codeError;
        set
        {
            if (!Set(ref _codeError, value)) return;
            OnPropertyChanged(nameof(IsLocked));
            OnPropertyChanged(nameof(CodeErrorText));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsLocked => _codeError != null;

    public string CodeErrorText => _codeError?.ToString() ?? "";

    /// <summary>Applies valid code from the code pane: rebuilds the model, keeps collapsed state by path.</summary>
    public void ApplyCode(JsonNode newRoot)
    {
        var collapsed = Editor.Root.Descendants().Where(n => n.Collapsed).Select(NodePath.Format).ToHashSet();
        foreach (var n in newRoot.Descendants())
            if (n.IsContainer && collapsed.Contains(NodePath.Format(n))) n.Collapsed = true;
        CodeError = null;
        Editor.ReplaceRoot(newRoot, "code");
    }

    private void DiscardCodeEdits()
    {
        CodeError = null;
        CodeResetRequested?.Invoke();
    }

    // ---- editor events --------------------------------------------------------------------

    private void OnEditorChanged(object? sender, DocumentChangedEventArgs e)
    {
        RaiseDirty();
        _statsTimer.Stop();
        _statsTimer.Start();
        if (_findText.Length > 0 && e.Kind != ChangeKind.Value) UpdateFindCount();
        if (e.Kind != ChangeKind.Value)
        {
            // Keep the status path current (the selected node may have moved or been removed).
            var sel = SelectedNode;
            StatusPath = sel == null ? "$" : NodePath.Format(sel);
            _settings.SetHints(_filePath, Editor.Hints);
        }
        CommandManager.InvalidateRequerySuggested();
    }

    private void Undo()
    {
        CommitEditsRequested?.Invoke();
        Editor.Undo();
    }

    private void Redo()
    {
        CommitEditsRequested?.Invoke();
        Editor.Redo();
    }

    private void SetAllCollapsed(bool collapsed)
    {
        foreach (var n in Editor.Root.Descendants())
            if (n.IsContainer) n.Collapsed = collapsed && n.Parent != null;
        TreeStateChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---- file commands --------------------------------------------------------------------

    /// <summary>Asks before discarding unsaved changes. Returns true when it is OK to continue.</summary>
    public bool ConfirmDiscard()
    {
        CommitEditsRequested?.Invoke();
        if (!IsDirty) return true;
        return MessageBox.Show(Application.Current.MainWindow!, "Discard unsaved changes?", "JSON Studio",
            MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK;
    }

    /// <summary>Close prompt: Save, Don't save, Cancel. Returns true when the window may close.</summary>
    public bool ConfirmClose()
    {
        CommitEditsRequested?.Invoke();
        if (!IsDirty) return true;
        var r = MessageBox.Show(Application.Current.MainWindow!,
            $"Do you want to save the changes to {FileName}?\n\nYes = Save, No = Don't save",
            "JSON Studio", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
        if (r == MessageBoxResult.Cancel) return false;
        if (r == MessageBoxResult.Yes) return Save(false);
        RecoveryService.Delete(_filePath);
        return true;
    }

    public void New(NewKind kind)
    {
        if (!ConfirmDiscard()) return;
        RecoveryService.Delete(_filePath);
        JsonNode root;
        switch (kind)
        {
            case NewKind.Sample:
                root = JsonParser.ParseOrThrow(JsonDocument.SampleText);
                break;
            case NewKind.EmptyArray:
                root = JsonNode.NewArray();
                var item = JsonNode.NewObject();
                item.Add("newField", JsonNode.NewString(""));
                root.Add(item);
                break;
            default:
                root = JsonNode.NewObject();
                root.Add("newField", JsonNode.NewString(""));
                break;
        }
        LoadDocument(root, null, kind == NewKind.Sample, indent: null);
    }

    public void Open(string? path)
    {
        if (!ConfirmDiscard()) return;
        path ??= FileService.ShowOpenDialog(_filePath ?? Settings.RecentFiles.FirstOrDefault());
        if (path == null) return;
        OpenPath(path);
    }

    /// <summary>Opens a file without asking about unsaved changes (the caller already asked).</summary>
    public void OpenPath(string path, string? overrideText = null)
    {
        string text;
        try
        {
            path = Path.GetFullPath(path);
            text = overrideText ?? FileService.ReadText(path);
        }
        catch (Exception ex)
        {
            MessageBox.Show(Application.Current.MainWindow!, $"Can't open the file.\n\n{ex.Message}", "JSON Studio",
                MessageBoxButton.OK, MessageBoxImage.Error);
            _settings.RemoveRecent(path);
            return;
        }

        RecoveryService.Delete(_filePath);
        var result = JsonParser.Parse(text);
        if (result.Root != null)
        {
            LoadDocument(result.Root, path, false, result.Indent);
        }
        else
        {
            // Show the raw text in the code pane and lock the designer.
            LoadDocument(JsonNode.NewObject(), path, false, null);
            ShowJson = true;
            CodeError = result.Error;
            RawCodeRequested?.Invoke(text);
        }
        if (overrideText != null) _forceDirty = true;
        RaiseDirty();
        _settings.AddRecent(path);
    }

    private void ClearPendingName()
    {
        if (_pendingName == null) return;
        _pendingName = null;
        OnPropertyChanged(nameof(FileName));
        OnPropertyChanged(nameof(FilePathText));
        OnPropertyChanged(nameof(HasPendingName));
    }

    private void LoadDocument(JsonNode root, string? path, bool isSample, string? indent)
    {
        ClearPendingName();
        CodeError = null;
        FileChangedOnDisk = false;
        _forceDirty = false;
        Editor.Hints.Clear();
        foreach (var (k, v) in _settings.GetHints(path)) Editor.Hints[k] = v;
        if (indent != null && indent != _indent)
        {
            _indent = indent;
            Settings.Indent = indent;
            _settings.Save();
            OnPropertyChanged(nameof(Indent));
            OnPropertyChanged(nameof(IndentIs2));
            OnPropertyChanged(nameof(IndentIs4));
            OnPropertyChanged(nameof(IndentIsTab));
        }
        FilePath = path;
        IsSample = isSample;
        _selected = null;
        StatusPath = "$";
        Editor.Load(root);
        _savedVersion = Editor.Version;
        WatchFile();
        UpdateStats();
        UpdateFindCount();
        RaiseDirty();
        DocumentLoaded?.Invoke(this, EventArgs.Empty);
        Select(Editor.Root);
    }

    /// <summary>Save (overwrite with no prompt when the document has a path) or Save as. Returns true when saved.</summary>
    public bool Save(bool saveAs)
    {
        CommitEditsRequested?.Invoke();
        if (IsLocked)
        {
            MessageBox.Show(Application.Current.MainWindow!,
                "The code has an error, so the document can't be saved. Fix the error or discard the code edits first.",
                "JSON Studio", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        string? path = _filePath;
        if (saveAs || path == null)
        {
            path = FileService.ShowSaveDialog(_filePath, FileName);
            if (path == null) return false;
        }
        else if (_pendingName != null)
        {
            // Renamed in the toolbar: write to the new name in the same folder.
            path = Path.Combine(Path.GetDirectoryName(path)!, _pendingName);
            if (File.Exists(path) && MessageBox.Show(Application.Current.MainWindow!,
                    $"{_pendingName} already exists in this folder. Replace it?", "JSON Studio",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return false;
        }

        try
        {
            var text = JsonWriter.Serialize(Editor.Root, _indent);
            StopWatching();
            FileService.WriteAtomic(path, text);
        }
        catch (Exception ex)
        {
            WatchFile();
            MessageBox.Show(Application.Current.MainWindow!, $"Can't save the file.\n\n{ex.Message}", "JSON Studio",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }

        RecoveryService.Delete(_filePath);
        bool pathChanged = !string.Equals(path, _filePath, StringComparison.OrdinalIgnoreCase);
        ClearPendingName();
        FilePath = path;
        IsSample = false;
        _savedVersion = Editor.Version;
        _forceDirty = false;
        FileChangedOnDisk = false;
        WatchFile();
        RaiseDirty();
        if (pathChanged) _settings.SetHints(path, Editor.Hints);
        _settings.AddRecent(path);
        ShowMessage($"Saved {Path.GetFileName(path)}");
        return true;
    }

    // ---- file change detection -----------------------------------------------------------

    public bool FileChangedOnDisk
    {
        get => _fileChangedOnDisk;
        private set => Set(ref _fileChangedOnDisk, value);
    }

    private void StopWatching()
    {
        if (_watcher == null) return;
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _watcher = null;
    }

    private void WatchFile()
    {
        StopWatching();
        if (_filePath == null || !File.Exists(_filePath)) return;
        try
        {
            _knownWriteUtc = File.GetLastWriteTimeUtc(_filePath);
            _watcher = new FileSystemWatcher(Path.GetDirectoryName(_filePath)!, Path.GetFileName(_filePath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            };
            FileSystemEventHandler h = (_, _) => Application.Current?.Dispatcher.BeginInvoke(CheckDisk);
            _watcher.Changed += h;
            _watcher.Created += h;
            _watcher.Renamed += (_, _) => Application.Current?.Dispatcher.BeginInvoke(CheckDisk);
            _watcher.EnableRaisingEvents = true;
        }
        catch
        {
            _watcher = null;
        }
    }

    private void CheckDisk()
    {
        if (_filePath == null || !File.Exists(_filePath)) return;
        var t = File.GetLastWriteTimeUtc(_filePath);
        if (t != _knownWriteUtc)
        {
            _knownWriteUtc = t;
            FileChangedOnDisk = true;
        }
    }

    private void ReloadFromDisk()
    {
        FileChangedOnDisk = false;
        if (_filePath == null) return;
        if (IsDirty && MessageBox.Show(Application.Current.MainWindow!, "Reload the file and discard your unsaved changes?",
                "JSON Studio", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        _forceDirty = false;
        _savedVersion = Editor.Version;
        OpenPath(_filePath);
    }

    // ---- recovery -----------------------------------------------------------------------------

    private void WriteRecoveryDraft()
    {
        if (!IsDirty || IsLocked) return;
        RecoveryService.Write(_filePath, JsonWriter.Serialize(Editor.Root, _indent));
    }

    /// <summary>On start, offers to restore drafts that are newer than their files.</summary>
    public void OfferRecovery()
    {
        foreach (var d in RecoveryService.FindRestorable())
        {
            var name = d.OriginalPath != null ? Path.GetFileName(d.OriginalPath) : "an unsaved document";
            var when = d.SavedUtc.ToLocalTime().ToString("g");
            var r = MessageBox.Show(Application.Current.MainWindow!,
                $"JSON Studio found a recovery draft of {name} from {when} that is newer than the saved file.\n\nRestore it?",
                "Restore recovery draft", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r == MessageBoxResult.Yes)
            {
                string text;
                try { text = File.ReadAllText(d.DraftPath); }
                catch { RecoveryService.Delete(d); continue; }
                RecoveryService.Delete(d);
                if (d.OriginalPath != null) OpenPath(d.OriginalPath, text);
                else
                {
                    var p = JsonParser.Parse(text);
                    if (p.Root != null)
                    {
                        LoadDocument(p.Root, null, false, p.Indent);
                        _forceDirty = true;
                        RaiseDirty();
                    }
                }
                return;
            }
            RecoveryService.Delete(d);
        }
    }

    public void OnClosed()
    {
        StopWatching();
        if (!IsDirty) RecoveryService.Delete(_filePath);
    }

    private static void TrySetClipboard(string text)
    {
        try { Clipboard.SetText(text); } catch { /* clipboard busy */ }
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using JsonStudio.App.Controls;
using JsonStudio.App.ViewModels;
using JsonStudio.Core.Editing;
using JsonStudio.Core.Model;

namespace JsonStudio.App.Views;

/// <summary>
/// The property window (spec 6.5): shows a row object as a property list, an array as a full grid, and lets the
/// user drill in and out of containers.
/// </summary>
public sealed class PropertyWindow : UserControl, ITableHost
{
    private sealed class Entry
    {
        public required NodeRef Node { get; init; }
        public NodeRef? SelectedRow { get; set; }
        public string? HighlightKey { get; set; }

        /// <summary>Cell outlined in the array grid (set when revealing a node from the code pane).</summary>
        public NodeRef? ActiveRow { get; set; }
        public string? ActiveColumn { get; set; }
    }

    private readonly MainViewModel _vm;
    private readonly List<Entry> _stack = new();
    private readonly ContentControl _content = new() { Focusable = false };
    private readonly Button _back;
    private readonly Button _openAsTable;
    private readonly TextBlock _title;
    private readonly TextBlock _crumbs;
    private readonly ScrollViewer _objScroll;
    private readonly StackPanel _objList = new() { Margin = new Thickness(0, 0, 0, 12) };
    private readonly Dictionary<long, ValueEditor> _editors = new();
    private readonly Dictionary<long, TextBox> _keyBoxes = new();
    private TableGrid? _grid;
    private NodeRef? _openedRow;
    private string? _openedColId;

    public PropertyWindow(MainViewModel vm)
    {
        _vm = vm;
        this.Res(BackgroundProperty, "Bg.Panel");

        _back = Ui.Button("←", "IconButton", "Back (Alt+←)");
        _back.FontSize = 15;
        _back.Click += (_, _) => Back();
        _title = Ui.Text("Properties", "Fg.Text", 14, FontWeights.SemiBold);
        _title.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Title");
        _title.TextTrimming = TextTrimming.CharacterEllipsis;
        _crumbs = Ui.Text("", "Fg.Muted", 11.5);
        _crumbs.TextTrimming = TextTrimming.CharacterEllipsis;
        _openAsTable = Ui.Button("⊞", "IconButton", "Open as the main table");
        _openAsTable.FontSize = 14;
        _openAsTable.Click += (_, _) =>
        {
            if (Current is { } n) OpenAsMainTableRequested?.Invoke(n);
        };
        var collapse = Ui.Button("›", "IconButton", "Collapse the property window");
        collapse.FontSize = 16;
        collapse.Click += (_, _) => CollapseRequested?.Invoke();

        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0) };
        titles.Children.Add(_title);
        titles.Children.Add(_crumbs);
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(_back);
        Grid.SetColumn(titles, 1);
        head.Children.Add(titles);
        Grid.SetColumn(_openAsTable, 2);
        head.Children.Add(_openAsTable);
        Grid.SetColumn(collapse, 3);
        head.Children.Add(collapse);
        var headBorder = new Border { Child = head, Padding = new Thickness(6, 6, 6, 6), BorderThickness = new Thickness(0, 0, 0, 1), MinHeight = 52 };
        headBorder.Res(Border.BorderBrushProperty, "Border");
        headBorder.Res(Border.BackgroundProperty, "Bg.Header");

        _objScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _objList,
            Focusable = false,
        };

        var dock = new DockPanel();
        DockPanel.SetDock(headBorder, Dock.Top);
        dock.Children.Add(headBorder);
        dock.Children.Add(_content);
        Content = dock;

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.System && e.SystemKey == Key.Left && Keyboard.Modifiers == ModifierKeys.Alt)
            {
                Back();
                e.Handled = true;
            }
        };
        ShowEmpty();
    }

    public event Action<JsonNode>? OpenAsMainTableRequested;
    public event Action? CollapseRequested;

    /// <summary>The opened cell (row + column) changed; the main grid shows it with a magenta outline.</summary>
    public event Action? OpenedCellChanged;

    public JsonNode? OpenedRow => _openedRow?.Resolve(_vm.Editor);
    public string? OpenedColumnId => _openedColId;

    private JsonNode? Current => _stack.Count > 0 ? _stack[^1].Node.Resolve(_vm.Editor) : null;

    // ---- what to show ------------------------------------------------------------------

    /// <summary>Shows a row's object and highlights the property of the selected column.</summary>
    public void ShowRow(JsonNode row, ColumnInfo? column)
    {
        _stack.Clear();
        _stack.Add(new Entry { Node = NodeRef.Of(row), HighlightKey = column is { IsValueColumn: false } ? column.Name : null });
        SetOpened(row, column?.Id);
        Rebuild(scrollToHighlight: true, resetScroll: true);
    }

    /// <summary>Shows a container cell of the main grid.</summary>
    public void ShowContainer(JsonNode container, JsonNode? row, string? columnId)
    {
        _stack.Clear();
        _stack.Add(new Entry { Node = NodeRef.Of(container) });
        SetOpened(row, columnId);
        Rebuild(scrollToHighlight: false, resetScroll: true);
    }

    /// <summary>
    /// Drills from the container that is shown now down to <paramref name="target"/>: selects its row in array grids,
    /// highlights its property in object lists, and opens the containers on the way.
    /// </summary>
    public void RevealInside(JsonNode target)
    {
        var start = Current;
        if (start == null || !start.IsAncestorOf(target))
        {
            Rebuild(scrollToHighlight: true, resetScroll: false);
            return;
        }
        var chain = new List<JsonNode>();
        for (var n = target; n != start; n = n.Parent!) chain.Add(n);
        chain.Reverse();

        var x = start;
        int i = 0;
        while (i < chain.Count)
        {
            var entry = _stack[^1];
            var next = chain[i];
            JsonNode? open = null;
            if (x.IsArray)
            {
                entry.SelectedRow = NodeRef.Of(next);
                entry.ActiveRow = NodeRef.Of(next);
                entry.ActiveColumn = null;
                if (next == target) break;
                if (next.IsArray)
                {
                    open = next;
                    i += 1;
                }
                else if (next.IsObject && i + 1 < chain.Count)
                {
                    var cell = chain[i + 1];
                    entry.ActiveColumn = cell.Key;
                    if (cell == target || !cell.IsContainer) break;
                    open = cell;
                    i += 2;
                }
                else break;
            }
            else
            {
                entry.HighlightKey = next.Key;
                if (next == target || !next.IsContainer) break;
                open = next;
                i += 1;
            }
            _stack.Add(new Entry { Node = NodeRef.Of(open) });
            x = open;
        }
        Rebuild(scrollToHighlight: true, resetScroll: true);
    }

    public void Clear()
    {
        _stack.Clear();
        SetOpened(null, null);
        ShowEmpty();
    }

    private void SetOpened(JsonNode? row, string? colId)
    {
        _openedRow = row == null ? null : NodeRef.Of(row);
        _openedColId = colId;
        OpenedCellChanged?.Invoke();
    }

    private void Push(JsonNode container)
    {
        _stack.Add(new Entry { Node = NodeRef.Of(container) });
        Rebuild(scrollToHighlight: false, resetScroll: true);
        _vm.Select(container);
    }

    public bool Back()
    {
        if (_stack.Count <= 1) return false;
        var left = _stack[^1].Node.Resolve(_vm.Editor);
        _stack.RemoveAt(_stack.Count - 1);
        var top = _stack[^1];
        // Coming back to an object list: highlight the property we came from.
        if (left != null && Current is { IsObject: true } && left.Parent == Current) top.HighlightKey = left.Key;
        Rebuild(scrollToHighlight: true, resetScroll: false);
        if (Current is { } c) _vm.Select(top.SelectedRow?.Resolve(_vm.Editor) ?? c);
        return true;
    }

    // ---- document changes ------------------------------------------------------------------

    public void OnDocumentChanged(DocumentChangedEventArgs e)
    {
        // Drop entries that no longer exist.
        for (int i = 0; i < _stack.Count; i++)
        {
            if (_stack[i].Node.Resolve(_vm.Editor) == null)
            {
                _stack.RemoveRange(i, _stack.Count - i);
                break;
            }
        }
        if (_stack.Count == 0)
        {
            if (_content.Content != null && _openedRow != null) Clear();
            return;
        }

        if (e.Kind == ChangeKind.Value)
        {
            if (e.Node != null && _editors.TryGetValue(e.Node.Id, out var ed) && e.Source != ed) ed.RefreshFromModel();
            _grid?.OnDocumentChanged(e);
            UpdateHeader();
            return;
        }
        var cur = Current!;
        if (cur.IsArray && _grid != null && _content.Content == _grid)
        {
            if (_grid.Table != cur) _grid.SetTable(cur, _stack[^1].SelectedRow?.Resolve(_vm.Editor));
            else _grid.OnDocumentChanged(e);
            UpdateHeader();
        }
        else Rebuild(scrollToHighlight: false, resetScroll: false);
    }

    public void RefreshFind()
    {
        if (_content.Content == _grid) _grid?.RefreshFind();
        else Rebuild(scrollToHighlight: false, resetScroll: false);
    }

    // ---- building ------------------------------------------------------------------------------

    private void ShowEmpty()
    {
        _editors.Clear();
        _keyBoxes.Clear();
        var t = Ui.Text("Select a row or a cell to see its properties.", "Fg.Faint", 12.5);
        t.TextWrapping = TextWrapping.Wrap;
        t.Margin = new Thickness(16);
        _content.Content = t;
        _title.Text = "Properties";
        _crumbs.Text = "";
        _back.IsEnabled = false;
        _openAsTable.IsEnabled = false;
    }

    private void UpdateHeader()
    {
        var cur = Current;
        if (cur == null) return;
        _title.Text = cur.Parent == null ? "root" : cur.IsArrayItem ? Schema.RowLabel(cur) : cur.Key;
        _back.IsEnabled = _stack.Count > 1;
        _openAsTable.IsEnabled = cur.IsContainer;

        // Breadcrumb from the opened cell to the current node.
        var first = _stack[0].Node.Resolve(_vm.Editor);
        // Start at the opened row when the current node is inside it (measures › [0] growth › accelerators).
        if (OpenedRow is { } openedRow && first != null && (openedRow == first || openedRow.IsAncestorOf(first))) first = openedRow;
        var chain = new List<JsonNode>();
        for (var n = cur; n != null; n = n.Parent)
        {
            chain.Add(n);
            if (n == first) break;
        }
        chain.Reverse();
        var parts = new List<string>();
        if (first?.IsArrayItem == true && first.Parent!.Parent != null) parts.Add(first.Parent.Parent.IsArray ? Schema.RowLabel(first.Parent) : first.Parent.Key);
        foreach (var n in chain) parts.Add(n.Parent == null ? "root" : n.IsArrayItem ? Schema.RowLabel(n) : n.Key);
        _crumbs.Text = string.Join(" › ", parts);
        _crumbs.ToolTip = Current is { } c ? NodePath.Format(c) : null;
    }

    private void Rebuild(bool scrollToHighlight, bool resetScroll)
    {
        var cur = Current;
        if (cur == null)
        {
            ShowEmpty();
            return;
        }
        UpdateHeader();
        var entry = _stack[^1];

        if (cur.IsArray)
        {
            _editors.Clear();
            _keyBoxes.Clear();
            _grid ??= new TableGrid(_vm, this);
            _content.Content = _grid;
            _grid.SetTable(cur, entry.SelectedRow?.Resolve(_vm.Editor));
            _grid.SetActiveCell(entry.ActiveRow?.Resolve(_vm.Editor), entry.ActiveColumn);
            return;
        }

        // Remember focus (node id + key/value) and scroll position.
        long? focusId = null;
        bool focusKey = false;
        if (Keyboard.FocusedElement is DependencyObject f && Ui.IsDescendantOf(f, _objList))
        {
            foreach (var (id, box) in _keyBoxes)
                if (box.IsKeyboardFocusWithin) { focusId = id; focusKey = true; }
            foreach (var (id, ed) in _editors)
                if (ed.Element.IsKeyboardFocusWithin) focusId = id;
        }
        double offset = resetScroll ? 0 : _objScroll.VerticalOffset;

        _editors.Clear();
        _keyBoxes.Clear();
        _objList.Children.Clear();
        FrameworkElement? highlight = null;

        if (cur.IsObject)
        {
            var dupKeys = cur.Children.GroupBy(c => c.Key).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
            foreach (var child in cur.Children)
            {
                bool hl = entry.HighlightKey != null && child.Key == entry.HighlightKey && highlight == null;
                var card = BuildProperty(cur, child, hl, dupKeys.Contains(child.Key));
                if (hl) highlight = card;
                _objList.Children.Add(card);
            }
            var add = Ui.Button("+  Add property", "CmdButton");
            add.HorizontalAlignment = HorizontalAlignment.Left;
            add.Margin = new Thickness(12, 10, 12, 0);
            add.Click += (_, _) =>
            {
                var node = _vm.Editor.AddChild(cur);
                if (_keyBoxes.TryGetValue(node.Id, out var kb))
                {
                    kb.Focus();
                    kb.SelectAll();
                }
            };
            _objList.Children.Add(add);
        }
        else
        {
            // A plain value (for example a row of an array of strings).
            var card = BuildProperty(null, cur, false, false);
            _objList.Children.Add(card);
        }

        _content.Content = _objScroll;
        _objScroll.UpdateLayout();
        _objScroll.ScrollToVerticalOffset(offset);

        if (focusId is long fid)
        {
            if (focusKey && _keyBoxes.TryGetValue(fid, out var kb)) kb.Focus();
            else if (_editors.TryGetValue(fid, out var ed)) ed.FocusTarget?.Focus();
        }
        if (scrollToHighlight && highlight != null)
            Dispatcher.BeginInvoke(() => highlight.BringIntoView(), DispatcherPriority.Loaded);
    }

    private FrameworkElement BuildProperty(JsonNode? parent, JsonNode node, bool highlighted, bool duplicate)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        FrameworkElement keyEl;
        if (parent != null)
        {
            var key = new TextBox { Text = node.Key, FontWeight = FontWeights.SemiBold };
            key.Styled("CellBox");
            key.SetResourceReference(Control.ForegroundProperty, "Key");
            key.MinHeight = 24;
            HookKeyRename(key, node);
            _keyBoxes[node.Id] = key;
            if (duplicate)
            {
                var wrap = new Grid();
                wrap.Children.Add(key);
                var wavy = new WavyUnderline { VerticalAlignment = VerticalAlignment.Bottom, Height = 4, Margin = new Thickness(5, 0, 5, 0) };
                wavy.ToolTip = "Duplicate key";
                wrap.Children.Add(wavy);
                keyEl = wrap;
            }
            else keyEl = key;
        }
        else
        {
            var t = Ui.Mono(node.IsArrayItem ? $"[{node.Index}]" : "value", "Fg.Muted");
            t.Margin = new Thickness(5, 3, 0, 3);
            keyEl = t;
        }
        grid.Children.Add(keyEl);

        var typeBtn = Ui.Button(TypeConversion.TypeName(node.Type) + " ▾", "IconButton", "Change type");
        typeBtn.FontSize = 11.5;
        typeBtn.Click += (_, _) => ShowTypeMenu(typeBtn, node);
        Grid.SetColumn(typeBtn, 1);
        grid.Children.Add(typeBtn);

        if (parent != null || node.Parent != null)
        {
            var del = Ui.Button("✕", "IconButton", "Delete");
            del.Click += (_, _) =>
            {
                if (node.Parent != null) _vm.Editor.Delete(node);
            };
            Grid.SetColumn(del, 2);
            grid.Children.Add(del);
        }

        var editor = new ValueEditor(node, ValueEditor.KindOf(node), new ValueEditorOptions
        {
            Vm = _vm,
            Source = this,
            GridPills = false,
            OpenContainer = c => Push(c),
            OpenAsTable = c => OpenAsMainTableRequested?.Invoke(c),
            Activated = () => _vm.Select(node),
        });
        _editors[node.Id] = editor;
        editor.Element.HorizontalAlignment = node.IsContainer || node.Type == NodeType.Boolean ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        editor.Element.Margin = new Thickness(0, 2, 0, 0);
        if (editor.Box != null)
        {
            editor.Box.Styled("InputBox");
            editor.Box.SetResourceReference(Control.FontFamilyProperty, "Font.Mono");
            editor.Box.SetResourceReference(Control.ForegroundProperty, Ui.ValueBrushKey(node.Type));
            if (node.Type == NodeType.Null) editor.Box.Tag = "null";
        }
        if (_vm.IsMatch(node) && editor.Box != null) editor.Box.SetResourceReference(Control.BackgroundProperty, "Find");
        Grid.SetRow(editor.Element, 1);
        Grid.SetColumnSpan(editor.Element, 3);
        grid.Children.Add(editor.Element);

        var accent = new Border { Child = grid, Padding = new Thickness(10, 6, 8, 8), BorderThickness = new Thickness(3, 0, 0, 0) };
        if (highlighted)
        {
            accent.Res(Border.BackgroundProperty, "Bg.Selected");
            accent.Res(Border.BorderBrushProperty, "Accent");
        }
        else accent.BorderBrush = Brushes.Transparent;
        var card = new Border { Child = accent, BorderThickness = new Thickness(0, 0, 0, 1) };
        card.Res(Border.BorderBrushProperty, "Border");
        return card;
    }

    private void HookKeyRename(TextBox box, JsonNode node)
    {
        void Commit()
        {
            if (node.Parent == null || box.Text == node.Key) return;
            _vm.Editor.Rename(node, box.Text);
        }
        box.GotKeyboardFocus += (_, _) => _vm.Select(node);
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                Commit();
                if (_editors.TryGetValue(node.Id, out var ed)) ed.FocusAndSelect();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                box.Text = node.Key;
            }
        };
        box.LostKeyboardFocus += (_, _) => Dispatcher.BeginInvoke(Commit, DispatcherPriority.Input);
    }

    private void ShowTypeMenu(FrameworkElement owner, JsonNode node)
    {
        var menu = new ContextMenu();
        foreach (var t in new[] { NodeType.String, NodeType.Number, NodeType.Boolean, NodeType.Null, NodeType.Object, NodeType.Array })
        {
            var tt = t;
            menu.Items.Add(Ui.Item(TypeConversion.TypeName(t), () => _vm.Editor.ChangeType(node, tt), isChecked: node.Type == t));
        }
        Ui.ShowMenu(owner, menu);
    }

    // ---- ITableHost (the array grid inside the window) -------------------------------------------

    void ITableHost.CellActivated(TableGrid grid, JsonNode row, ColumnInfo column)
    {
        ClearGridActive();
        if (_stack.Count > 0) _stack[^1].SelectedRow = NodeRef.Of(row);
        _vm.Select(Schema.Cell(row, column) ?? row);
    }

    void ITableHost.RowSelected(TableGrid grid, JsonNode row)
    {
        ClearGridActive();
        if (_stack.Count > 0) _stack[^1].SelectedRow = NodeRef.Of(row);
        _vm.Select(row);
    }

    void ITableHost.OpenContainer(TableGrid grid, JsonNode row, ColumnInfo column, JsonNode container)
    {
        ClearGridActive();
        if (_stack.Count > 0) _stack[^1].SelectedRow = NodeRef.Of(row);
        Push(container);
    }

    private void ClearGridActive()
    {
        if (_stack.Count == 0 || _stack[^1].ActiveColumn == null && _stack[^1].ActiveRow == null) return;
        _stack[^1].ActiveRow = null;
        _stack[^1].ActiveColumn = null;
        _grid?.SetActiveCell(null, null);
    }

    void ITableHost.OpenAsTable(TableGrid grid, JsonNode container) => OpenAsMainTableRequested?.Invoke(container);

    void ITableHost.NavigateUp(TableGrid grid) => Back();
}

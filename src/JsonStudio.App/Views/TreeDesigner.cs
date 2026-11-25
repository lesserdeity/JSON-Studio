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

/// <summary>The Tree designer (spec 7.1): one row per node with drag and drop.</summary>
public sealed class TreeDesigner : UserControl
{
    private const string DragFormat = "JsonStudio.NodeId";
    private const double IndentWidth = 18;

    private enum DropMode { None, Before, After, Into }

    private sealed class RowView
    {
        public required JsonNode Node { get; init; }
        public required Border Border { get; init; }
        public TextBox? KeyBox { get; set; }
        public ValueEditor? Editor { get; set; }
        public required FrameworkElement Buttons { get; init; }
    }

    private readonly MainViewModel _vm;
    private readonly ScrollViewer _scroll;
    private readonly StackPanel _list = new() { Margin = new Thickness(0, 4, 0, 24) };
    private readonly List<RowView> _rows = new();
    private NodeRef? _selected;
    private RowView? _hover;
    private Point _dragStart;
    private JsonNode? _dragNode;
    private RowView? _dropRow;
    private DropMode _dropMode;

    public TreeDesigner(MainViewModel vm)
    {
        _vm = vm;
        _scroll = new ScrollViewer
        {
            Content = _list,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Focusable = true,
            FocusVisualStyle = null,
        };
        _scroll.PreviewMouseDown += (_, e) =>
        {
            if (e.OriginalSource == _scroll || e.OriginalSource == _list) _scroll.Focus();
        };
        Content = _scroll;
        this.Res(BackgroundProperty, "Bg.Panel");
        PreviewKeyDown += OnPreviewKeyDown;

        vm.Editor.Changed += OnDocumentChanged;
        vm.TreeStateChanged += (_, _) => Rebuild();
        vm.FindChanged += (_, _) => Rebuild();
        vm.DocumentLoaded += (_, _) =>
        {
            _selected = null;
            _scroll.ScrollToVerticalOffset(0);
            Rebuild();
        };
        vm.SelectionChanged += (_, _) =>
        {
            var n = vm.SelectedNode;
            if (n == null || (_selected?.Resolve(vm.Editor) == n)) return;
            if (!IsVisible) return;
            // Selected elsewhere: expand to it and select it here.
            bool expanded = false;
            for (var p = n.Parent; p != null; p = p.Parent)
                if (p.Collapsed) { p.Collapsed = false; expanded = true; }
            _selected = NodeRef.Of(n);
            if (expanded) Rebuild();
            else ApplyVisuals();
            BringSelectedIntoView();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) Rebuild();
        };
        Rebuild();
    }

    private JsonNode? Selected => _selected?.Resolve(_vm.Editor);

    private void OnDocumentChanged(object? sender, DocumentChangedEventArgs e)
    {
        if (!IsVisible) return;
        if (e.Kind == ChangeKind.Value)
        {
            if (e.Node == null) return;
            foreach (var r in _rows)
                if (r.Node.Id == e.Node.Id && r.Editor != null) r.Editor.RefreshFromModel();
            return;
        }
        Rebuild();
    }

    // ---- building ---------------------------------------------------------------------------

    public void Rebuild()
    {
        if (!IsVisible && _rows.Count > 0) return;
        long? focusId = null;
        bool focusKey = false;
        if (Keyboard.FocusedElement is DependencyObject f && Ui.IsDescendantOf(f, _list))
        {
            foreach (var r in _rows)
            {
                if (r.KeyBox?.IsKeyboardFocusWithin == true) { focusId = r.Node.Id; focusKey = true; }
                else if (r.Editor?.Element.IsKeyboardFocusWithin == true) focusId = r.Node.Id;
            }
        }
        double v = _scroll.VerticalOffset, h = _scroll.HorizontalOffset;

        _rows.Clear();
        _list.Children.Clear();
        _hover = null;
        AddRows(_vm.Editor.Root, 0);
        ApplyVisuals();

        _scroll.UpdateLayout();
        _scroll.ScrollToVerticalOffset(v);
        _scroll.ScrollToHorizontalOffset(h);
        if (focusId is long id)
        {
            var row = _rows.FirstOrDefault(r => r.Node.Id == id);
            if (row != null)
            {
                if (focusKey) row.KeyBox?.Focus();
                else row.Editor?.FocusTarget?.Focus();
            }
        }
    }

    private void AddRows(JsonNode node, int depth)
    {
        _list.Children.Add(BuildRow(node, depth));
        if (node.IsContainer && !node.Collapsed)
            foreach (var c in node.Children) AddRows(c, depth + 1);
    }

    private FrameworkElement BuildRow(JsonNode node, int depth)
    {
        var grid = new Grid { MinHeight = 30 };
        for (int i = 0; i < 7; i++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions[6].Width = new GridLength(1, GridUnitType.Star);
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Indent guides
        var guides = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 0, 0) };
        for (int i = 0; i < depth; i++)
        {
            var g = new Border { Width = IndentWidth, BorderThickness = new Thickness(1, 0, 0, 0), Margin = new Thickness(i == 0 ? 8 : 0, 0, 0, 0) };
            g.Res(Border.BorderBrushProperty, "Border");
            guides.Children.Add(g);
        }
        grid.Children.Add(guides);

        // Drag handle
        var handle = Ui.Text("⠿", "Fg.Faint", 13);
        handle.Width = 16;
        handle.TextAlignment = TextAlignment.Center;
        handle.Cursor = node.Parent != null ? Cursors.SizeAll : Cursors.Arrow;
        handle.ToolTip = node.Parent != null ? "Drag to move" : null;
        handle.Opacity = node.Parent != null ? 1 : 0;
        if (node.Parent != null)
        {
            handle.MouseLeftButtonDown += (_, e) =>
            {
                _dragStart = e.GetPosition(this);
                _dragNode = node;
                handle.CaptureMouse();
                e.Handled = true;
            };
            handle.MouseMove += (_, e) =>
            {
                if (_dragNode != node || e.LeftButton != MouseButtonState.Pressed) return;
                var p = e.GetPosition(this);
                if (Math.Abs(p.X - _dragStart.X) < 4 && Math.Abs(p.Y - _dragStart.Y) < 4) return;
                handle.ReleaseMouseCapture();
                var data = new DataObject(DragFormat, node.Id);
                DragDrop.DoDragDrop(handle, data, DragDropEffects.Move);
                _dragNode = null;
                ClearDrop();
            };
            handle.MouseLeftButtonUp += (_, _) =>
            {
                handle.ReleaseMouseCapture();
                _dragNode = null;
                SelectNode(node);
            };
        }
        Grid.SetColumn(handle, 1);
        grid.Children.Add(handle);

        // Expander
        var arrow = Ui.Text(node.IsContainer ? (node.Collapsed ? "▸" : "▾") : "", "Fg.Muted", 13);
        arrow.Width = 18;
        arrow.TextAlignment = TextAlignment.Center;
        if (node.IsContainer)
        {
            arrow.Cursor = Cursors.Hand;
            arrow.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                node.Collapsed = !node.Collapsed;
                SelectNode(node);
                Rebuild();
            };
        }
        Grid.SetColumn(arrow, 2);
        grid.Children.Add(arrow);

        // Key
        TextBox? keyBox = null;
        FrameworkElement keyEl;
        if (node.Parent?.IsObject == true)
        {
            keyBox = new TextBox { Text = node.Key, MinWidth = 40, MaxWidth = 280, HorizontalAlignment = HorizontalAlignment.Left };
            keyBox.Styled("CellBox");
            keyBox.SetResourceReference(Control.ForegroundProperty, "Key");
            if (_vm.FindText.Length > 0 && node.Key.Contains(_vm.FindText, StringComparison.OrdinalIgnoreCase))
                keyBox.SetResourceReference(Control.BackgroundProperty, "Find");
            HookKey(keyBox, node);
            if (node.Parent.Children.Count(c => c.Key == node.Key) > 1)
            {
                var wrap = new Grid();
                wrap.Children.Add(keyBox);
                wrap.Children.Add(new WavyUnderline { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(5, 0, 5, 2), ToolTip = "Duplicate key" });
                keyEl = wrap;
            }
            else keyEl = keyBox;
        }
        else
        {
            var t = Ui.Mono(node.Parent == null ? "root" : $"{node.Index}", node.Parent == null ? "Fg.Text" : "Fg.Muted");
            if (node.Parent == null) t.FontWeight = FontWeights.SemiBold;
            t.Margin = new Thickness(5, 0, 3, 0);
            keyEl = t;
        }
        Grid.SetColumn(keyEl, 3);
        grid.Children.Add(keyEl);

        var colon = Ui.Mono(":", "Fg.Faint");
        colon.Margin = new Thickness(0, 0, 6, 0);
        Grid.SetColumn(colon, 4);
        grid.Children.Add(colon);

        // Type pill
        var type = Ui.Button(TypeConversion.TypeName(node.Type), "PillButton", "Change type");
        type.FontSize = 11;
        type.Height = 20;
        type.Padding = new Thickness(7, 0, 7, 0);
        type.SetResourceReference(Control.ForegroundProperty, Ui.ValueBrushKey(node.Type) == "Fg.Text" ? "Fg.Muted" : Ui.ValueBrushKey(node.Type));
        type.Click += (_, _) =>
        {
            SelectNode(node);
            var menu = new ContextMenu();
            foreach (var t in new[] { NodeType.String, NodeType.Number, NodeType.Boolean, NodeType.Null, NodeType.Object, NodeType.Array })
            {
                var tt = t;
                menu.Items.Add(Ui.Item(TypeConversion.TypeName(t), () => _vm.Editor.ChangeType(node, tt), isChecked: node.Type == t));
            }
            Ui.ShowMenu(type, menu);
        };
        Grid.SetColumn(type, 5);
        grid.Children.Add(type);

        // Value
        ValueEditor? editor = null;
        FrameworkElement valueEl;
        if (node.IsContainer)
        {
            var summary = Ui.Mono(node.IsArray ? $"[ {node.Children.Count} ]" : $"{{ {node.Children.Count} }}", "Fg.Faint", 12);
            summary.Margin = new Thickness(8, 0, 0, 0);
            valueEl = summary;
        }
        else
        {
            editor = new ValueEditor(node, ValueEditor.KindOf(node), new ValueEditorOptions
            {
                Vm = _vm,
                Source = this,
                Activated = () => SelectNode(node, fromEditor: true),
            });
            editor.Element.Margin = new Thickness(4, 0, 8, 0);
            editor.Element.MaxWidth = 640;
            editor.Element.HorizontalAlignment = HorizontalAlignment.Left;
            if (editor.Box != null) editor.Box.MinWidth = 160;
            if (_vm.IsMatch(node) && editor.Box != null && !(node.Key.Contains(_vm.FindText, StringComparison.OrdinalIgnoreCase) && node.Parent?.IsObject == true))
                editor.Box.SetResourceReference(Control.BackgroundProperty, "Find");
            valueEl = editor.Element;
        }
        Grid.SetColumn(valueEl, 6);
        grid.Children.Add(valueEl);

        // Hover buttons
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Visibility = Visibility.Hidden, Margin = new Thickness(0, 0, 8, 0) };
        if (node.IsContainer)
        {
            var add = Ui.Button("+", "IconButton", "Add child (Insert)");
            add.Click += (_, _) => AddChild(node);
            buttons.Children.Add(add);
        }
        if (node.Parent != null)
        {
            var dup = Ui.Button("⧉", "IconButton", "Duplicate (Ctrl+D)");
            dup.Click += (_, _) => Duplicate(node);
            var del = Ui.Button("✕", "IconButton", "Delete (Delete)");
            del.Click += (_, _) => Delete(node);
            buttons.Children.Add(dup);
            buttons.Children.Add(del);
        }
        Grid.SetColumn(buttons, 7);
        grid.Children.Add(buttons);

        var border = new Border { Child = grid, BorderThickness = new Thickness(0, 1, 0, 1), BorderBrush = Brushes.Transparent, Background = Brushes.Transparent, AllowDrop = true };
        var rv = new RowView { Node = node, Border = border, KeyBox = keyBox, Editor = editor, Buttons = buttons };
        _rows.Add(rv);

        border.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && (Ui.FindAncestor<TextBox>(d) != null)) return;
            SelectNode(node);
            if (Ui.FindAncestor<Button>(e.OriginalSource as DependencyObject) == null &&
                Ui.FindAncestor<CheckBox>(e.OriginalSource as DependencyObject) == null)
                _scroll.Focus();
        };
        border.MouseEnter += (_, _) =>
        {
            _hover = rv;
            ApplyVisuals();
        };
        border.MouseLeave += (_, _) =>
        {
            if (_hover == rv) _hover = null;
            ApplyVisuals();
        };
        border.DragOver += (_, e) => OnDragOver(rv, e);
        border.DragLeave += (_, _) => ClearDrop();
        border.Drop += (_, e) => OnDrop(rv, e);
        return border;
    }

    private void HookKey(TextBox box, JsonNode node)
    {
        void Commit()
        {
            if (node.Parent?.IsObject == true && box.Text != node.Key) _vm.Editor.Rename(node, box.Text);
        }
        box.GotKeyboardFocus += (_, _) => SelectNode(node, fromEditor: true);
        box.LostKeyboardFocus += (_, _) => Dispatcher.BeginInvoke(Commit, DispatcherPriority.Input);
    }

    // ---- selection & visuals ------------------------------------------------------------------

    private void SelectNode(JsonNode node, bool fromEditor = false)
    {
        _selected = NodeRef.Of(node);
        ApplyVisuals();
        _vm.Select(node);
    }

    private void ApplyVisuals()
    {
        var sel = Selected;
        foreach (var r in _rows)
        {
            bool selected = r.Node == sel;
            bool match = _vm.IsMatch(r.Node);
            if (selected) r.Border.Res(Border.BackgroundProperty, "Bg.Selected");
            else if (r == _hover) r.Border.Res(Border.BackgroundProperty, "Bg.Hover");
            else r.Border.Background = Brushes.Transparent;
            if (r != _dropRow)
            {
                if (selected) r.Border.Res(Border.BorderBrushProperty, "Accent");
                else r.Border.BorderBrush = match ? Ui.Brush("Find.Border") : Brushes.Transparent;
                r.Border.BorderThickness = selected ? new Thickness(3, 0, 0, 0) : new Thickness(match ? 1 : 0, 1, 0, 1);
            }
            r.Buttons.Visibility = selected || r == _hover ? Visibility.Visible : Visibility.Hidden;
        }
    }

    private void BringSelectedIntoView()
    {
        var sel = Selected;
        var row = _rows.FirstOrDefault(r => r.Node == sel);
        if (row != null) Dispatcher.BeginInvoke(() => row.Border.BringIntoView(), DispatcherPriority.Loaded);
    }

    // ---- commands -----------------------------------------------------------------------------

    private void AddChild(JsonNode container)
    {
        container.Collapsed = false;
        var child = _vm.Editor.AddChild(container);
        SelectNode(child);
        var row = _rows.FirstOrDefault(r => r.Node == child);
        if (row?.KeyBox != null)
        {
            row.KeyBox.Focus();
            row.KeyBox.SelectAll();
        }
        else row?.Editor?.FocusAndSelect();
    }

    private void Duplicate(JsonNode node)
    {
        var copy = _vm.Editor.Duplicate(node);
        if (copy != null) SelectNode(copy);
        _scroll.Focus();
    }

    private void Delete(JsonNode node)
    {
        if (node.Parent == null) return;
        int idx = _rows.FindIndex(r => r.Node == node);
        _vm.Editor.Delete(node);
        if (_rows.Count > 0) SelectNode(_rows[Math.Clamp(idx, 0, _rows.Count - 1)].Node);
        _scroll.Focus();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        if (Keyboard.FocusedElement is TextBox tb && Ui.IsDescendantOf(tb, _list))
        {
            if (e.Key == Key.Enter && mods == ModifierKeys.None && !tb.AcceptsReturn || e.Key == Key.Escape)
            {
                e.Handled = true;
                _scroll.Focus();
            }
            return;
        }
        if (mods == ModifierKeys.Control && e.Key == Key.D)
        {
            if (Selected is { } n) Duplicate(n);
            e.Handled = true;
            return;
        }
        if (!_scroll.IsKeyboardFocused) return;

        var sel = Selected;
        int idx = sel == null ? -1 : _rows.FindIndex(r => r.Node == sel);
        switch (e.Key)
        {
            case Key.Up:
                if (_rows.Count > 0) SelectNode(_rows[Math.Max(0, idx - 1)].Node);
                BringSelectedIntoView();
                e.Handled = true;
                break;
            case Key.Down:
                if (_rows.Count > 0) SelectNode(_rows[Math.Min(_rows.Count - 1, idx + 1)].Node);
                BringSelectedIntoView();
                e.Handled = true;
                break;
            case Key.Right when sel != null:
                if (sel.IsContainer && sel.Collapsed)
                {
                    sel.Collapsed = false;
                    Rebuild();
                }
                else if (sel.IsContainer && sel.Children.Count > 0) SelectNode(sel.Children[0]);
                BringSelectedIntoView();
                e.Handled = true;
                break;
            case Key.Left when sel != null:
                if (sel.IsContainer && !sel.Collapsed && sel.Parent != null)
                {
                    sel.Collapsed = true;
                    Rebuild();
                }
                else if (sel.Parent != null) SelectNode(sel.Parent);
                BringSelectedIntoView();
                e.Handled = true;
                break;
            case Key.Enter when sel != null:
                var row = _rows.FirstOrDefault(r => r.Node == sel);
                if (row?.KeyBox != null)
                {
                    row.KeyBox.Focus();
                    row.KeyBox.SelectAll();
                }
                else row?.Editor?.FocusAndSelect();
                e.Handled = true;
                break;
            case Key.Delete when sel != null:
                Delete(sel);
                e.Handled = true;
                break;
            case Key.Insert or Key.Add or Key.OemPlus when sel != null:
                if (sel.IsContainer) AddChild(sel);
                else if (sel.Parent != null) AddChild(sel.Parent);
                e.Handled = true;
                break;
        }
    }

    // ---- drag and drop -----------------------------------------------------------------------------


    private JsonNode? DraggedNode(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DragFormat)) return null;
        return e.Data.GetData(DragFormat) is long id ? _vm.Editor.FindById(id) : null;
    }

    private void OnDragOver(RowView rv, DragEventArgs e)
    {
        e.Handled = true;
        var dragged = DraggedNode(e);
        var target = rv.Node;
        DropMode mode = DropMode.None;
        if (dragged != null && dragged != target && !dragged.IsAncestorOf(target))
        {
            double y = e.GetPosition(rv.Border).Y, h = rv.Border.ActualHeight;
            if (target.Parent == null) mode = DropMode.Into;
            else if (y < h * 0.3) mode = DropMode.Before;
            else if (y > h * 0.7) mode = target.IsContainer && !target.Collapsed && target.Children.Count > 0 ? DropMode.Into : DropMode.After;
            else mode = target.IsContainer ? DropMode.Into : (y < h / 2 ? DropMode.Before : DropMode.After);
        }
        e.Effects = mode == DropMode.None ? DragDropEffects.None : DragDropEffects.Move;
        ClearDrop();
        if (mode == DropMode.None) return;
        _dropRow = rv;
        _dropMode = mode;
        rv.Border.Res(Border.BorderBrushProperty, "Accent");
        rv.Border.BorderThickness = mode switch
        {
            DropMode.Before => new Thickness(0, 2, 0, 0),
            DropMode.After => new Thickness(0, 0, 0, 2),
            _ => new Thickness(2),
        };
    }

    private void ClearDrop()
    {
        var r = _dropRow;
        _dropRow = null;
        _dropMode = DropMode.None;
        if (r != null) ApplyVisuals();
    }

    private void OnDrop(RowView rv, DragEventArgs e)
    {
        e.Handled = true;
        var dragged = DraggedNode(e);
        var mode = _dropMode;
        ClearDrop();
        if (dragged == null || mode == DropMode.None) return;
        var target = rv.Node;
        bool moved = mode switch
        {
            DropMode.Before => _vm.Editor.Move(dragged, target.Parent!, target.Index),
            DropMode.After => _vm.Editor.Move(dragged, target.Parent!, target.Index + 1),
            _ => MoveInto(dragged, target),
        };
        if (moved) SelectNode(dragged);
    }

    private bool MoveInto(JsonNode dragged, JsonNode target)
    {
        target.Collapsed = false;
        return _vm.Editor.Move(dragged, target, target.Children.Count);
    }
}

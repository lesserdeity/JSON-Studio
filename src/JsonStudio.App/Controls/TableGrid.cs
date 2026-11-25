using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using JsonStudio.App.ViewModels;
using JsonStudio.Core.Editing;
using JsonStudio.Core.Model;
using JsonStudio.Core.Serialization;

namespace JsonStudio.App.Controls;

/// <summary>The owner of a <see cref="TableGrid"/>: the Tables view (main grid) or the property window.</summary>
public interface ITableHost
{
    /// <summary>A cell was clicked or focused.</summary>
    void CellActivated(TableGrid grid, JsonNode row, ColumnInfo column);

    /// <summary>A row was selected without a cell (keyboard, row number).</summary>
    void RowSelected(TableGrid grid, JsonNode row);

    /// <summary>One click on a container pill.</summary>
    void OpenContainer(TableGrid grid, JsonNode row, ColumnInfo column, JsonNode container);

    /// <summary>Double-click on a container pill: make it the main table.</summary>
    void OpenAsTable(TableGrid grid, JsonNode container);

    /// <summary>Alt+Up.</summary>
    void NavigateUp(TableGrid grid);
}

/// <summary>
/// The data grid: one row per array item (or one row for an object), columns from the shared schema,
/// sticky header row and row-number column, typed cells, column menu and New column popup.
/// </summary>
public sealed class TableGrid : UserControl
{
    private const double RowNumWidth = 46;
    private const double PlusWidth = 40;
    private const double ActionsWidth = 66;

    private readonly MainViewModel _vm;
    private readonly ITableHost _host;
    private readonly ScrollViewer _scroll;
    private readonly Grid _grid = new() { SnapsToDevicePixels = true };
    private readonly Border _addRowBar;
    private readonly List<RowView> _rows = new();
    private readonly List<HeaderView> _headers = new();
    private readonly List<FrameworkElement> _stickyTop = new();
    private readonly List<FrameworkElement> _stickyLeft = new();
    private readonly List<FrameworkElement> _stickyBoth = new();
    private List<ColumnInfo> _cols = new();
    private NodeRef? _tableRef;
    private NodeRef? _selectedRef;
    private RowView? _hover;
    private bool _restoring;
    private (long RowId, string ColId)? _active;
    private string? _activeColumnId;

    public TableGrid(MainViewModel vm, ITableHost host)
    {
        _vm = vm;
        _host = host;
        Focusable = false;

        _scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _grid,
            Focusable = true,
            FocusVisualStyle = null,
            CanContentScroll = false,
        };
        _scroll.ScrollChanged += (_, _) => UpdateSticky();
        _scroll.MouseLeave += (_, _) => SetHover(null);
        _grid.MouseMove += (_, e) => SetHover(RowAt(e.GetPosition(_grid).Y));
        _scroll.PreviewMouseDown += (_, e) =>
        {
            // Clicking empty space focuses the grid itself (row selection mode).
            if (e.OriginalSource == _scroll || e.OriginalSource == _grid) _scroll.Focus();
        };

        var addRow = Ui.Button("+  Add row", "CmdButton");
        addRow.HorizontalAlignment = HorizontalAlignment.Left;
        addRow.Click += (_, _) => AddRowAndFocus(-1);
        _addRowBar = new Border { Padding = new Thickness(8, 6, 8, 6), Child = addRow };
        _addRowBar.Res(Border.BorderBrushProperty, "Border");
        _addRowBar.BorderThickness = new Thickness(0, 1, 0, 0);

        var dock = new DockPanel();
        DockPanel.SetDock(_addRowBar, Dock.Bottom);
        dock.Children.Add(_addRowBar);
        dock.Children.Add(_scroll);
        Content = dock;
        this.Res(BackgroundProperty, "Bg.Panel");

        PreviewKeyDown += OnPreviewKeyDown;
    }

    // ---- public state ----------------------------------------------------------------

    public JsonNode? Table => _tableRef?.Resolve(_vm.Editor);

    public JsonNode? SelectedRow
    {
        get
        {
            var r = _selectedRef?.Resolve(_vm.Editor);
            var t = Table;
            return r != null && t != null && Schema.Rows(t).Contains(r) ? r : null;
        }
    }

    public IReadOnlyList<ColumnInfo> Columns => _cols;

    public void SetTable(JsonNode table, JsonNode? selectedRow = null)
    {
        bool same = _tableRef != null && _tableRef.Resolve(_vm.Editor) == table;
        _tableRef = NodeRef.Of(table);
        if (selectedRow != null) _selectedRef = NodeRef.Of(selectedRow);
        else if (!same) _selectedRef = null;
        Rebuild(resetScroll: !same);
        if (selectedRow != null) EnsureRowVisible();
    }

    /// <summary>The cell that is open in the property window: magenta outline and magenta header.</summary>
    public void SetActiveCell(JsonNode? row, string? columnId)
    {
        _active = row != null && columnId != null ? (row.Id, columnId) : null;
        _activeColumnId = columnId;
        ApplyActive(scrollIntoView: true);
    }

    public void SelectRow(JsonNode? row, bool notify, bool scrollIntoView = false)
    {
        _selectedRef = row == null ? null : NodeRef.Of(row);
        ApplyRowVisuals();
        if (scrollIntoView) EnsureRowVisible();
        if (notify && row != null) _host.RowSelected(this, row);
    }

    /// <summary>Called by the host for every document change.</summary>
    public void OnDocumentChanged(DocumentChangedEventArgs e)
    {
        if (e.Kind == ChangeKind.Value)
        {
            if (e.Node == null) return;
            foreach (var rv in _rows)
                foreach (var cv in rv.Cells.Values)
                    if (cv.Editor.Node?.Id == e.Node.Id && e.Source != cv.Editor) cv.Editor.RefreshFromModel();
            return;
        }
        Rebuild();
    }

    public void RefreshFind() => Rebuild();

    // ---- building ------------------------------------------------------------------------

    private sealed class RowView
    {
        public required JsonNode Row { get; init; }
        public int Index { get; init; }
        public int GridRow { get; init; }
        public required Border Back { get; init; }
        public required Border Num { get; init; }
        public FrameworkElement? Actions { get; set; }
        public Dictionary<string, CellView> Cells { get; } = new();
    }

    private sealed class CellView
    {
        public required RowView Row { get; init; }
        public required ColumnInfo Col { get; init; }
        public int ColIndex { get; init; }
        public required Border Border { get; init; }
        public required ValueEditor Editor { get; init; }
        public bool Matched { get; init; }
    }

    private sealed class HeaderView
    {
        public required ColumnInfo Col { get; init; }
        public required Border Border { get; init; }
        public required FrameworkElement Name { get; init; }
        public required Border Underline { get; init; }
        public bool Matched { get; init; }
    }

    public void Rebuild() => Rebuild(resetScroll: false);

    private void Rebuild(bool resetScroll)
    {
        var table = Table;
        // Remember focus (by row id + column) and scroll.
        var focused = FocusedCell();
        (long RowId, int Index, string ColId, int Caret, int SelStart, int SelLen)? focusKey = null;
        if (focused != null)
        {
            var box = focused.Editor.Box;
            focusKey = (focused.Row.Row.Id, focused.Row.Index, focused.Col.Id, box?.CaretIndex ?? 0, box?.SelectionStart ?? 0, box?.SelectionLength ?? 0);
        }
        bool gridFocused = _scroll.IsKeyboardFocused;
        double h = resetScroll ? 0 : _scroll.HorizontalOffset, v = resetScroll ? 0 : _scroll.VerticalOffset;

        _grid.Children.Clear();
        _grid.RowDefinitions.Clear();
        _grid.ColumnDefinitions.Clear();
        _rows.Clear();
        _headers.Clear();
        _stickyTop.Clear();
        _stickyLeft.Clear();
        _stickyBoth.Clear();
        _hover = null;

        if (table == null)
        {
            _cols = new();
            _addRowBar.Visibility = Visibility.Collapsed;
            return;
        }

        _cols = _vm.Editor.Columns(table);
        var rows = Schema.Rows(table);
        _addRowBar.Visibility = table.IsArray ? Visibility.Visible : Visibility.Collapsed;

        // Columns: row number | data columns | + | actions
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(RowNumWidth) });
        foreach (var c in _cols) _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ColumnWidth(c, rows)) });
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(PlusWidth) });
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ActionsWidth) });
        int plusCol = _cols.Count + 1;
        int lastCol = _cols.Count + 2;

        _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        BuildHeader(table, plusCol, lastCol);

        for (int i = 0; i < rows.Count; i++)
        {
            _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            BuildRow(table, rows[i], i, i + 1, lastCol);
        }

        if (rows.Count == 0)
        {
            _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var empty = Ui.Text(table.IsArray ? "No rows. Use Add row or + to add a column." : "No fields.", "Fg.Faint", 12.5);
            empty.Margin = new Thickness(12, 10, 12, 10);
            Grid.SetRow(empty, 1);
            Grid.SetColumn(empty, 1);
            Grid.SetColumnSpan(empty, Math.Max(1, _cols.Count + 1));
            _grid.Children.Add(empty);
        }

        ApplyRowVisuals();
        ApplyActive(scrollIntoView: false);

        _grid.UpdateLayout();
        _scroll.UpdateLayout();
        _scroll.ScrollToHorizontalOffset(h);
        _scroll.ScrollToVerticalOffset(v);
        UpdateSticky();

        if (focusKey is { } fk)
        {
            var rv = _rows.FirstOrDefault(r => r.Row.Id == fk.RowId) ?? (fk.Index < _rows.Count ? _rows[fk.Index] : null);
            if (rv != null && rv.Cells.TryGetValue(fk.ColId, out var cv))
            {
                _restoring = true;
                try
                {
                    cv.Editor.FocusTarget?.Focus();
                    if (cv.Editor.Box is { } b)
                    {
                        b.CaretIndex = Math.Min(fk.Caret, b.Text.Length);
                        if (fk.SelLen > 0 && fk.SelStart + fk.SelLen <= b.Text.Length) b.Select(fk.SelStart, fk.SelLen);
                    }
                }
                finally
                {
                    _restoring = false;
                }
            }
        }
        else if (gridFocused)
        {
            _scroll.Focus();
        }
    }

    private static int DisplayLength(JsonNode? n) => n == null ? 3 : n.Type switch
    {
        NodeType.String => Math.Min(n.StringValue.Split('\n')[0].Length, 40),
        NodeType.Number => JsonWriter.FormatNumber(n.NumberValue).Length,
        NodeType.Boolean => 10,
        NodeType.Null => 4,
        _ => 13,
    };

    private static double ColumnWidth(ColumnInfo c, List<JsonNode> rows)
    {
        int len = Math.Max(c.Name.Length + 4, c.TypeLabel.Length + 3);
        foreach (var r in rows.Take(200)) len = Math.Max(len, DisplayLength(Schema.Cell(r, c)) + 2);
        return Math.Clamp(len * 7.4 + 20, 100, 340);
    }

    private void BuildHeader(JsonNode table, int plusCol, int lastCol)
    {
        var corner = HeaderCell(new TextBlock(), 0);
        corner.Child = Ui.Text(table.IsArray ? "#" : "{ }", "Fg.Faint", 11.5);
        ((TextBlock)corner.Child).HorizontalAlignment = HorizontalAlignment.Center;
        Panel.SetZIndex(corner, 30);
        _stickyBoth.Add(corner);

        for (int i = 0; i < _cols.Count; i++)
        {
            var col = _cols[i];
            bool matched = _vm.FindText.Length > 0 && !col.IsValueColumn && col.Name.Contains(_vm.FindText, StringComparison.OrdinalIgnoreCase);

            FrameworkElement name;
            if (col.IsValueColumn)
            {
                var t = Ui.Mono("value", "Fg.Muted", 12.5);
                t.FontStyle = FontStyles.Italic;
                t.Margin = new Thickness(5, 0, 0, 0);
                t.ToolTip = "The plain values of this array";
                name = t;
            }
            else
            {
                var box = new TextBox { Text = col.Name, FontWeight = FontWeights.SemiBold };
                box.Styled("CellBox");
                box.SetResourceReference(Control.ForegroundProperty, "Key");
                box.MinHeight = 22;
                box.Padding = new Thickness(4, 1, 4, 1);
                box.ToolTip = "Column name. Renaming changes this key in every row and every peer table.";
                HookRename(box, col);
                name = box;
            }

            var type = Ui.Text(col.TypeLabel, "Fg.Faint", 11);
            type.Margin = new Thickness(5, 0, 0, 2);
            var more = Ui.Button("⋯", "IconButton", "Column type and delete");
            more.VerticalAlignment = VerticalAlignment.Top;
            more.Click += (_, _) => ShowColumnMenu(more, col);

            var underline = new Border { Height = 2, VerticalAlignment = VerticalAlignment.Bottom, Visibility = Visibility.Collapsed };
            underline.Res(Border.BackgroundProperty, "Accent");

            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.Children.Add(name);
            Grid.SetRow(type, 1);
            g.Children.Add(type);
            Grid.SetColumn(more, 1);
            Grid.SetRowSpan(more, 2);
            g.Children.Add(more);

            var outer = new Grid();
            outer.Children.Add(g);
            outer.Children.Add(underline);

            var cell = HeaderCell(outer, i + 1);
            if (matched) cell.Res(Border.BackgroundProperty, "Find");
            _headers.Add(new HeaderView { Col = col, Border = cell, Name = name, Underline = underline, Matched = matched });
        }

        var plus = Ui.Button("+", "IconButton", "New column");
        plus.FontSize = 16;
        plus.HorizontalAlignment = HorizontalAlignment.Center;
        plus.VerticalAlignment = VerticalAlignment.Center;
        plus.Click += (_, _) => ShowNewColumnPopup(plus);
        HeaderCell(plus, plusCol);
        HeaderCell(new Border(), lastCol);
    }

    private Border HeaderCell(UIElement child, int col)
    {
        var b = new Border
        {
            Child = child,
            Padding = new Thickness(4, 4, 2, 3),
            BorderThickness = new Thickness(0, 0, 1, 1),
            RenderTransform = new TranslateTransform(),
            MinHeight = 44,
        };
        b.Res(Border.BackgroundProperty, "Bg.Header");
        b.Res(Border.BorderBrushProperty, "Border");
        Grid.SetRow(b, 0);
        Grid.SetColumn(b, col);
        Panel.SetZIndex(b, 20);
        _grid.Children.Add(b);
        if (col != 0) _stickyTop.Add(b);
        return b;
    }

    private void BuildRow(JsonNode table, JsonNode row, int index, int gridRow, int lastCol)
    {
        var back = new Border { Background = Brushes.Transparent };
        Grid.SetRow(back, gridRow);
        Grid.SetColumnSpan(back, lastCol + 1);
        Panel.SetZIndex(back, 0);
        _grid.Children.Add(back);

        var numText = Ui.Mono(table.IsArray ? index.ToString() : "{ }", "Fg.Faint", 11.5);
        numText.HorizontalAlignment = HorizontalAlignment.Center;
        var num = new Border
        {
            Child = numText,
            BorderThickness = new Thickness(0, 0, 1, 1),
            RenderTransform = new TranslateTransform(),
            Cursor = Cursors.Hand,
        };
        num.Res(Border.BackgroundProperty, "Bg.Header");
        num.Res(Border.BorderBrushProperty, "Border");
        Grid.SetRow(num, gridRow);
        Panel.SetZIndex(num, 10);
        _grid.Children.Add(num);
        _stickyLeft.Add(num);

        var rv = new RowView { Row = row, Index = index, GridRow = gridRow, Back = back, Num = num };
        _rows.Add(rv);
        num.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            SelectRow(row, notify: true);
            _scroll.Focus();
        };

        for (int c = 0; c < _cols.Count; c++)
        {
            var col = _cols[c];
            var cellNode = Schema.Cell(row, col);
            ValueEditorKind kind;
            if (Schema.IsNotApplicable(row, col)) kind = ValueEditorKind.NotApplicable;
            else if (cellNode == null && col.ContainerType != null) kind = ValueEditorKind.NewContainer;
            else kind = ValueEditor.KindOf(cellNode);

            var editor = new ValueEditor(cellNode, kind, new ValueEditorOptions
            {
                Vm = _vm,
                Source = this,
                GridPills = true,
                Placeholder = cellNode == null ? "—" : "null",
                NewContainerType = col.ContainerType,
                CommitLiteral = text => IsLive(row) ? _vm.Editor.SetCellLiteral(table, row, col, text, this) : null,
                CreateContainer = () => IsLive(row) ? _vm.Editor.CreateContainerCell(table, row, col.Name, col.ContainerType ?? NodeType.Array) : null,
                OpenContainer = n => _host.OpenContainer(this, row, col, n),
                OpenAsTable = n => _host.OpenAsTable(this, n),
                Activated = () => OnCellActivated(rv, col),
            });
            editor.Element.VerticalAlignment = kind is ValueEditorKind.NotApplicable ? VerticalAlignment.Stretch : VerticalAlignment.Center;

            bool matched = cellNode != null && cellNode.IsPrimitive && ValueMatches(cellNode);
            var border = new Border
            {
                Child = editor.Element,
                BorderThickness = new Thickness(0, 0, 1, 1),
                Padding = kind == ValueEditorKind.NotApplicable ? new Thickness(0) : new Thickness(1),
                MinHeight = 30,
            };
            border.Res(Border.BorderBrushProperty, "Border");
            if (matched) border.Res(Border.BackgroundProperty, "Find");
            border.MouseLeftButtonDown += (_, _) =>
            {
                // Click on the cell padding / hatched area selects the row.
                OnCellActivated(rv, col);
            };
            Grid.SetRow(border, gridRow);
            Grid.SetColumn(border, c + 1);
            Panel.SetZIndex(border, 1);
            _grid.Children.Add(border);
            rv.Cells[col.Id] = new CellView { Row = rv, Col = col, ColIndex = c, Border = border, Editor = editor, Matched = matched };
        }

        var filler = new Border { BorderThickness = new Thickness(0, 0, 0, 1) };
        filler.Res(Border.BorderBrushProperty, "Border");
        Grid.SetRow(filler, gridRow);
        Grid.SetColumn(filler, _cols.Count + 1);
        Grid.SetColumnSpan(filler, 2);
        _grid.Children.Add(filler);

        if (table.IsArray)
        {
            var dup = Ui.Button("⧉", "IconButton", "Duplicate row (Ctrl+D)");
            dup.Click += (_, _) => DuplicateRow(row);
            var del = Ui.Button("✕", "IconButton", "Delete row (Delete)");
            del.Click += (_, _) => DeleteRow(row);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Visibility = Visibility.Hidden, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
            actions.Children.Add(dup);
            actions.Children.Add(del);
            Grid.SetRow(actions, gridRow);
            Grid.SetColumn(actions, lastCol);
            Panel.SetZIndex(actions, 2);
            _grid.Children.Add(actions);
            rv.Actions = actions;
        }
    }

    private bool IsLive(JsonNode n) => n.Root == _vm.Editor.Root;

    private bool ValueMatches(JsonNode n)
    {
        var f = _vm.FindText;
        if (f.Length == 0) return false;
        string text = n.Type switch
        {
            NodeType.String => n.StringValue,
            NodeType.Number => JsonWriter.FormatNumber(n.NumberValue),
            NodeType.Boolean => n.BoolValue ? "true" : "false",
            _ => "null",
        };
        return text.Contains(f, StringComparison.OrdinalIgnoreCase);
    }

    // ---- visuals ---------------------------------------------------------------------------------

    private void UpdateSticky()
    {
        double x = _scroll.HorizontalOffset, y = _scroll.VerticalOffset;
        foreach (var e in _stickyTop) ((TranslateTransform)e.RenderTransform).Y = y;
        foreach (var e in _stickyLeft) ((TranslateTransform)e.RenderTransform).X = x;
        foreach (var e in _stickyBoth)
        {
            var t = (TranslateTransform)e.RenderTransform;
            t.X = x;
            t.Y = y;
        }
    }

    private RowView? RowAt(double y)
    {
        double top = 0;
        for (int i = 0; i < _grid.RowDefinitions.Count; i++)
        {
            double hgt = _grid.RowDefinitions[i].ActualHeight;
            if (y >= top && y < top + hgt) return _rows.FirstOrDefault(r => r.GridRow == i);
            top += hgt;
        }
        return null;
    }

    private void SetHover(RowView? rv)
    {
        if (_hover == rv) return;
        _hover = rv;
        ApplyRowVisuals();
    }

    private void ApplyRowVisuals()
    {
        var sel = _selectedRef?.Resolve(_vm.Editor);
        foreach (var rv in _rows)
        {
            bool selected = rv.Row == sel;
            if (selected) rv.Back.Res(Border.BackgroundProperty, "Bg.Selected");
            else if (rv == _hover) rv.Back.Res(Border.BackgroundProperty, "Bg.Hover");
            else rv.Back.Background = Brushes.Transparent;
            if (selected)
            {
                rv.Num.Res(Border.BackgroundProperty, "Accent");
                ((TextBlock)rv.Num.Child).SetResourceReference(TextBlock.ForegroundProperty, "Accent.Fg");
            }
            else
            {
                rv.Num.Res(Border.BackgroundProperty, "Bg.Header");
                ((TextBlock)rv.Num.Child).SetResourceReference(TextBlock.ForegroundProperty, "Fg.Faint");
            }
            if (rv.Actions != null) rv.Actions.Visibility = selected || rv == _hover ? Visibility.Visible : Visibility.Hidden;
        }
    }

    private void ApplyActive(bool scrollIntoView)
    {
        Border? activeBorder = null;
        foreach (var rv in _rows)
        {
            foreach (var cv in rv.Cells.Values)
            {
                bool on = _active is { } a && a.RowId == rv.Row.Id && a.ColId == cv.Col.Id;
                if (on)
                {
                    cv.Border.Res(Border.BorderBrushProperty, "Accent");
                    cv.Border.BorderThickness = new Thickness(2);
                    Panel.SetZIndex(cv.Border, 3);
                    activeBorder = cv.Border;
                }
                else
                {
                    cv.Border.Res(Border.BorderBrushProperty, "Border");
                    cv.Border.BorderThickness = new Thickness(0, 0, 1, 1);
                    Panel.SetZIndex(cv.Border, 1);
                }
            }
        }
        foreach (var h in _headers)
        {
            bool on = _activeColumnId != null && h.Col.Id == _activeColumnId;
            h.Underline.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            if (h.Name is TextBox tb) tb.SetResourceReference(Control.ForegroundProperty, on ? "Accent" : "Key");
            else if (h.Name is TextBlock t) t.SetResourceReference(TextBlock.ForegroundProperty, on ? "Accent" : "Fg.Muted");
        }
        if (scrollIntoView && activeBorder != null)
            Dispatcher.BeginInvoke(() => EnsureVisible(activeBorder), DispatcherPriority.Loaded);
    }

    /// <summary>Scrolls only enough to show the element below the sticky header and right of the row numbers.</summary>
    private void EnsureVisible(FrameworkElement el)
    {
        if (!el.IsLoaded || !Ui.IsDescendantOf(el, _grid)) return;
        Rect r;
        try
        {
            r = el.TransformToAncestor(_grid).TransformBounds(new Rect(el.RenderSize));
        }
        catch (InvalidOperationException)
        {
            return;
        }
        double headerH = _grid.RowDefinitions.Count > 0 ? _grid.RowDefinitions[0].ActualHeight : 0;
        double left = _scroll.HorizontalOffset + RowNumWidth, right = _scroll.HorizontalOffset + _scroll.ViewportWidth;
        double top = _scroll.VerticalOffset + headerH, bottom = _scroll.VerticalOffset + _scroll.ViewportHeight;

        if (r.Left < left) _scroll.ScrollToHorizontalOffset(r.Left - RowNumWidth);
        else if (r.Right > right) _scroll.ScrollToHorizontalOffset(Math.Min(r.Left - RowNumWidth, r.Right - _scroll.ViewportWidth));
        if (r.Top < top) _scroll.ScrollToVerticalOffset(r.Top - headerH);
        else if (r.Bottom > bottom) _scroll.ScrollToVerticalOffset(Math.Min(r.Top - headerH, r.Bottom - _scroll.ViewportHeight));
    }

    // ---- interaction --------------------------------------------------------------------------

    private void OnCellActivated(RowView rv, ColumnInfo col)
    {
        if (_restoring) return;
        _selectedRef = NodeRef.Of(rv.Row);
        ApplyRowVisuals();
        _host.CellActivated(this, rv.Row, col);
    }

    private CellView? FocusedCell()
    {
        var f = Keyboard.FocusedElement as DependencyObject;
        if (f == null || !Ui.IsDescendantOf(f, _grid)) return null;
        foreach (var rv in _rows)
            foreach (var cv in rv.Cells.Values)
                if (Ui.IsDescendantOf(f, cv.Border)) return cv;
        return null;
    }

    public bool FocusCell(int rowIndex, string colId, bool selectAll = true)
    {
        if (rowIndex < 0 || rowIndex >= _rows.Count) return false;
        var rv = _rows[rowIndex];
        if (!rv.Cells.TryGetValue(colId, out var cv)) return false;
        if (cv.Editor.FocusTarget == null)
        {
            // Hatched cell: select the row instead.
            SelectRow(rv.Row, notify: true);
            _scroll.Focus();
            return true;
        }
        if (selectAll) cv.Editor.FocusAndSelect();
        else cv.Editor.FocusTarget.Focus();
        Dispatcher.BeginInvoke(() => EnsureVisible(cv.Border), DispatcherPriority.Loaded);
        return true;
    }

    public bool FocusCell(JsonNode row, string colId)
    {
        var rv = _rows.FirstOrDefault(r => r.Row == row);
        return rv != null && FocusCell(rv.Index, colId);
    }

    private string? FirstEditableColumn(RowView rv)
    {
        foreach (var c in _cols)
            if (rv.Cells.TryGetValue(c.Id, out var cv) && cv.Editor.Kind != ValueEditorKind.NotApplicable) return c.Id;
        return _cols.FirstOrDefault()?.Id;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        var table = Table;
        if (table == null) return;

        if (mods == ModifierKeys.Alt && key == Key.Down)
        {
            var row = SelectedRow;
            var first = row == null ? null : _cols.Select(c => Schema.Cell(row, c)).FirstOrDefault(n => n?.IsContainer == true);
            if (row != null && first != null)
            {
                var col = _cols.First(c => Schema.Cell(row, c) == first);
                _host.OpenContainer(this, row, col, first);
            }
            e.Handled = true;
            return;
        }
        if (mods == ModifierKeys.Alt && key == Key.Up)
        {
            _host.NavigateUp(this);
            e.Handled = true;
            return;
        }
        if (mods == ModifierKeys.Control && key == Key.D)
        {
            if (SelectedRow is { } r && table.IsArray) DuplicateRow(r);
            e.Handled = true;
            return;
        }

        var cv = FocusedCell();
        if (cv != null)
        {
            int rowIndex = cv.Row.Index;
            string colId = cv.Col.Id;
            switch (key)
            {
                case Key.Up when mods == ModifierKeys.None:
                    if (!cv.Editor.CaretOnFirstLine) return;
                    e.Handled = true;
                    cv.Editor.Commit();
                    FocusCell(Math.Max(0, rowIndex - 1), colId);
                    return;
                case Key.Down when mods == ModifierKeys.None:
                    if (!cv.Editor.CaretOnLastLine) return;
                    e.Handled = true;
                    cv.Editor.Commit();
                    FocusCell(Math.Min(_rows.Count - 1, rowIndex + 1), colId);
                    return;
                case Key.Enter when mods == ModifierKeys.None:
                    e.Handled = true;
                    if (cv.Editor.Kind == ValueEditorKind.Boolean && cv.Editor.Switch is { } sw)
                    {
                        // Enter moves on; Space toggles.
                    }
                    cv.Editor.Commit();
                    _vm.Editor.EndBurst();
                    bool lastRow = rowIndex == _rows.Count - 1;
                    bool lastCol = _cols.Count > 0 && colId == _cols[^1].Id;
                    if (lastRow && lastCol && table.IsArray)
                    {
                        AddRowAndFocus(-1);
                    }
                    else if (!lastRow)
                    {
                        FocusCell(rowIndex + 1, colId);
                    }
                    else if (cv.Editor.Box is { } b && b.IsKeyboardFocused)
                    {
                        b.SelectAll();
                    }
                    return;
                case Key.Escape:
                    e.Handled = true;
                    cv.Editor.Commit();
                    _vm.Editor.EndBurst();
                    _scroll.Focus();
                    return;
                case Key.Tab when mods is ModifierKeys.None or ModifierKeys.Shift:
                    e.Handled = true;
                    cv.Editor.Commit();
                    _vm.Editor.EndBurst();
                    MoveByTab(rowIndex, cv.ColIndex, forward: mods == ModifierKeys.None);
                    return;
            }
            return;
        }

        if (!_scroll.IsKeyboardFocused) return;
        var sel = SelectedRow;
        int idx = sel == null ? -1 : _rows.FindIndex(r => r.Row == sel);
        switch (key)
        {
            case Key.Up when mods == ModifierKeys.None:
                if (_rows.Count > 0) SelectRow(_rows[Math.Max(0, idx - 1)].Row, notify: true);
                EnsureRowVisible();
                e.Handled = true;
                break;
            case Key.Down when mods == ModifierKeys.None:
                if (_rows.Count > 0) SelectRow(_rows[Math.Min(_rows.Count - 1, idx + 1)].Row, notify: true);
                EnsureRowVisible();
                e.Handled = true;
                break;
            case Key.Enter when mods == ModifierKeys.None:
                if (idx < 0 && _rows.Count > 0) idx = 0;
                if (idx >= 0 && FirstEditableColumn(_rows[idx]) is { } c) FocusCell(idx, c);
                e.Handled = true;
                break;
            case Key.Delete when mods == ModifierKeys.None:
                if (sel != null && table.IsArray) DeleteRow(sel);
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// Tab / Shift+Tab: the next (or previous) editable cell, row by row. Tab in the last cell of an array adds a row
    /// and goes to its first cell, as Enter does.
    /// </summary>
    private void MoveByTab(int rowIndex, int colIndex, bool forward)
    {
        int r = rowIndex, c = colIndex;
        while (true)
        {
            c += forward ? 1 : -1;
            if (c >= _cols.Count)
            {
                c = 0;
                r++;
            }
            else if (c < 0)
            {
                c = _cols.Count - 1;
                r--;
            }
            if (r < 0) return; // before the first cell: stay
            if (r >= _rows.Count)
            {
                if (Table?.IsArray == true) AddRowAndFocus(-1);
                return;
            }
            if (_rows[r].Cells.TryGetValue(_cols[c].Id, out var next) && next.Editor.FocusTarget != null)
            {
                FocusCell(r, _cols[c].Id);
                return;
            }
        }
    }

    private void EnsureRowVisible()
    {
        var sel = SelectedRow;
        var rv = _rows.FirstOrDefault(r => r.Row == sel);
        if (rv != null) Dispatcher.BeginInvoke(() => EnsureVisible(rv.Num), DispatcherPriority.Loaded);
    }

    private void AddRowAndFocus(int index)
    {
        var table = Table;
        if (table?.IsArray != true) return;
        var row = _vm.Editor.AddRow(table, index); // rebuild happens through the host
        _selectedRef = NodeRef.Of(row);
        ApplyRowVisuals();
        var rv = _rows.FirstOrDefault(r => r.Row == row);
        if (rv != null && FirstEditableColumn(rv) is { } c) FocusCell(rv.Index, c);
        else
        {
            _host.RowSelected(this, row);
            _scroll.Focus();
        }
    }

    private void DuplicateRow(JsonNode row)
    {
        var copy = _vm.Editor.Duplicate(row);
        if (copy != null) SelectRow(copy, notify: true);
        _scroll.Focus();
    }

    private void DeleteRow(JsonNode row)
    {
        var table = Table;
        if (table == null) return;
        int idx = table.Children.IndexOf(row);
        _vm.Editor.Delete(row);
        var rows = Schema.Rows(table);
        if (rows.Count > 0) SelectRow(rows[Math.Clamp(idx, 0, rows.Count - 1)], notify: true);
        else _selectedRef = null;
        _scroll.Focus();
    }

    // ---- column header actions -------------------------------------------------------------

    private void HookRename(TextBox box, ColumnInfo col)
    {
        bool done = false;
        void Commit()
        {
            if (done) return;
            var table = Table;
            var text = box.Text;
            if (table == null || text == col.Name) return;
            done = true;
            if (!_vm.Editor.RenameColumn(table, col.Name, text, out var error))
            {
                done = false;
                box.Text = col.Name;
                if (error != null) _vm.ShowMessage(error);
            }
        }
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                Commit();
                if (!done) box.SelectAll();
                else _scroll.Focus();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                box.Text = col.Name;
                _scroll.Focus();
            }
        };
        box.LostKeyboardFocus += (_, _) => Dispatcher.BeginInvoke(Commit, DispatcherPriority.Input);
    }

    private void ShowColumnMenu(FrameworkElement owner, ColumnInfo col)
    {
        var table = Table;
        if (table == null) return;
        var menu = new ContextMenu();
        menu.Items.Add(Ui.Label("Change type of column"));
        foreach (var k in Schema.AllKinds)
        {
            if (col.IsValueColumn && k is ColumnKind.Object or ColumnKind.ArrayOfObjects or ColumnKind.ArrayOfStrings or ColumnKind.ArrayOfNumbers or ColumnKind.ArrayOfBooleans)
                continue;
            var kind = k;
            menu.Items.Add(Ui.Item(Schema.Label(k), () =>
            {
                _vm.Editor.ChangeColumnType(table, col.Id, kind);
            }, isChecked: col.TypeLabel == Schema.Label(k)));
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(Ui.Item(col.IsValueColumn ? "Delete plain values" : "Delete column", () => _vm.Editor.DeleteColumn(table, col.Id)));
        Ui.ShowMenu(owner, menu);
    }

    private void ShowNewColumnPopup(Button plus)
    {
        var table = Table;
        if (table == null) return;
        if (table.IsPrimitive)
        {
            _vm.ShowMessage("A plain value can't have columns.");
            return;
        }
        var kind = ColumnKind.String;

        var name = new TextBox { Text = _vm.Editor.NewColumnName(table), Width = 220 };
        name.Styled("InputBox");
        name.SetResourceReference(TextBox.FontFamilyProperty, "Font.Mono");
        var typeBtn = Ui.Button(Schema.Label(kind) + "  ▾", "CmdButton");
        typeBtn.HorizontalAlignment = HorizontalAlignment.Stretch;
        typeBtn.HorizontalContentAlignment = HorizontalAlignment.Left;
        typeBtn.Height = 30;
        typeBtn.Padding = new Thickness(10, 2, 10, 2);
        var add = Ui.Button("Add column", "SmallAccentButton");
        add.HorizontalAlignment = HorizontalAlignment.Right;

        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(Ui.Text("New column", "Fg.Text", 14, FontWeights.SemiBold));
        var l1 = Ui.Text("Name", "Fg.Muted", 12);
        l1.Margin = new Thickness(0, 10, 0, 3);
        panel.Children.Add(l1);
        panel.Children.Add(name);
        var l2 = Ui.Text("Type", "Fg.Muted", 12);
        l2.Margin = new Thickness(0, 10, 0, 3);
        panel.Children.Add(l2);
        panel.Children.Add(typeBtn);
        add.Margin = new Thickness(0, 14, 0, 0);
        panel.Children.Add(add);

        var border = new Border { Child = panel, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1) };
        border.Res(Border.BackgroundProperty, "Bg.Panel");
        border.Res(Border.BorderBrushProperty, "Border.Strong");
        border.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Opacity = 0.25 };

        var popup = new Popup
        {
            Child = border,
            PlacementTarget = plus,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
        };
        border.Margin = new Thickness(0, 0, 12, 12);
        border.SetResourceReference(TextElement.FontFamilyProperty, "Font.Ui");

        typeBtn.Click += (_, _) =>
        {
            var menu = new ContextMenu();
            foreach (var k in Schema.AllKinds)
            {
                var kk = k;
                menu.Items.Add(Ui.Item(Schema.Label(k), () =>
                {
                    kind = kk;
                    typeBtn.Content = Schema.Label(kk) + "  ▾";
                }, isChecked: kk == kind));
            }
            Ui.ShowMenu(typeBtn, menu);
        };

        void DoAdd()
        {
            var t = Table;
            if (t == null) return;
            var colName = name.Text;
            var sel = SelectedRow;
            int selIndex = sel != null ? _rows.FindIndex(r => r.Row == sel) : -1;
            var result = _vm.Editor.AddColumn(t, colName, kind);
            if (!result.Success)
            {
                _vm.ShowMessage(result.Error ?? "Can't add the column.");
                name.Focus();
                name.SelectAll();
                return;
            }
            popup.IsOpen = false;
            if (result.Message != null) _vm.ShowMessage(result.Message);
            // Focus the new column's cell of the selected row.
            int rowIdx = selIndex >= 0 ? selIndex : 0;
            Dispatcher.BeginInvoke(() =>
            {
                if (!FocusCell(rowIdx, colName)) FocusCell(0, colName);
            }, DispatcherPriority.Input);
        }

        add.Click += (_, _) => DoAdd();
        name.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                DoAdd();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                popup.IsOpen = false;
            }
        };
        popup.Opened += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            name.Focus();
            Keyboard.Focus(name);
            name.SelectAll();
        }, DispatcherPriority.Input);
        popup.IsOpen = true;
    }
}

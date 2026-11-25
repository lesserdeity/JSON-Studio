using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using JsonStudio.App.Controls;
using JsonStudio.App.ViewModels;
using JsonStudio.Core.Editing;
using JsonStudio.Core.Model;

namespace JsonStudio.App.Views;

/// <summary>The Tables designer: breadcrumb + Up, the main grid, and the property window on the right.</summary>
public sealed class TablesView : UserControl, ITableHost
{
    private const double MinPropWidth = 240;
    private const double CollapseBelow = 150;
    private const double StripWidth = 42;

    private readonly MainViewModel _vm;
    private readonly TableGrid _main;
    private readonly PropertyWindow _props;
    private readonly StackPanel _crumbs = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _up;
    private readonly Grid _layout = new();
    private readonly ColumnDefinition _propColumn;
    private readonly Border _strip;
    private readonly PaneSplitter _splitter;
    private NodeRef _context;
    private double _dragWidth;

    public TablesView(MainViewModel vm)
    {
        _vm = vm;
        _context = NodeRef.Of(vm.Editor.Root);
        _main = new TableGrid(vm, this);
        _props = new PropertyWindow(vm);
        _props.OpenAsMainTableRequested += n => SetContext(n);
        _props.CollapseRequested += () => SetCollapsed(true);
        _props.OpenedCellChanged += () => _main.SetActiveCell(_props.OpenedRow, _props.OpenedColumnId);

        _up = Ui.Button("↑  Up", "CmdButton", "Go up one level (Alt+↑)");
        _up.Height = 26;
        _up.Click += (_, _) => Up();
        var bar = new DockPanel { Margin = new Thickness(10, 6, 10, 6) };
        DockPanel.SetDock(_up, Dock.Left);
        bar.Children.Add(_up);
        _crumbs.Margin = new Thickness(10, 0, 0, 0);
        bar.Children.Add(_crumbs);
        var barBorder = new Border { Child = bar, BorderThickness = new Thickness(0, 0, 0, 1) };
        barBorder.Res(Border.BorderBrushProperty, "Border");
        barBorder.Res(Border.BackgroundProperty, "Bg.Panel");

        var left = new DockPanel();
        DockPanel.SetDock(barBorder, Dock.Top);
        left.Children.Add(barBorder);
        left.Children.Add(_main);

        _splitter = new PaneSplitter();
        _splitter.DragStarted += (_, _) => _dragWidth = vm.Settings.PropertyCollapsed ? StripWidth : _propColumn!.ActualWidth;
        _splitter.DragDelta += OnSplitterDrag;
        _splitter.DragCompleted += (_, _) => vm.SettingsService.Save();
        _splitter.MouseDoubleClick += (_, e) =>
        {
            e.Handled = true;
            SetCollapsed(!vm.Settings.PropertyCollapsed);
        };

        var stripText = Ui.Text("Properties", "Fg.Muted", 12.5, FontWeights.SemiBold);
        stripText.LayoutTransform = new RotateTransform(90);
        stripText.HorizontalAlignment = HorizontalAlignment.Center;
        stripText.VerticalAlignment = VerticalAlignment.Top;
        stripText.Margin = new Thickness(0, 14, 0, 0);
        _strip = new Border { Child = stripText, Cursor = Cursors.Hand, ToolTip = "Show the property window" };
        _strip.Res(Border.BackgroundProperty, "Bg.Header");
        _strip.MouseLeftButtonUp += (_, _) => SetCollapsed(false);

        _propColumn = new ColumnDefinition();
        _layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 200 });
        _layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _layout.ColumnDefinitions.Add(_propColumn);
        _layout.Children.Add(left);
        Grid.SetColumn(_splitter, 1);
        _layout.Children.Add(_splitter);
        Grid.SetColumn(_props, 2);
        _layout.Children.Add(_props);
        Grid.SetColumn(_strip, 2);
        _layout.Children.Add(_strip);
        Content = _layout;

        ApplyCollapsed();

        vm.Editor.Changed += OnDocumentChanged;
        vm.DocumentLoaded += (_, _) =>
        {
            _props.Clear();
            SetContext(vm.Editor.Root);
        };
        vm.RevealRequested += Reveal;
        vm.FindChanged += (_, _) =>
        {
            _main.RefreshFind();
            _props.RefreshFind();
        };

        SetContext(vm.Editor.Root);
    }

    public JsonNode Context => _context.Resolve(_vm.Editor) ?? _vm.Editor.Root;

    // ---- property window size ------------------------------------------------------------------

    private void OnSplitterDrag(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        _dragWidth -= e.HorizontalChange;
        double max = Math.Max(MinPropWidth, ActualWidth - 260);
        if (_dragWidth < CollapseBelow)
        {
            if (!_vm.Settings.PropertyCollapsed) SetCollapsed(true, save: false);
            return;
        }
        _vm.Settings.PropertyCollapsed = false;
        _vm.Settings.PropertyWidth = Math.Clamp(_dragWidth, MinPropWidth, max);
        ApplyCollapsed();
    }

    public void SetCollapsed(bool collapsed, bool save = true)
    {
        _vm.Settings.PropertyCollapsed = collapsed;
        if (!collapsed && _vm.Settings.PropertyWidth < MinPropWidth) _vm.Settings.PropertyWidth = MinPropWidth;
        ApplyCollapsed();
        if (save) _vm.SettingsService.Save();
    }

    private void ApplyCollapsed()
    {
        bool c = _vm.Settings.PropertyCollapsed;
        _propColumn.Width = new GridLength(c ? StripWidth : Math.Max(MinPropWidth, _vm.Settings.PropertyWidth));
        _props.Visibility = c ? Visibility.Collapsed : Visibility.Visible;
        _strip.Visibility = c ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- context (the main grid's table) -----------------------------------------------------------

    public void SetContext(JsonNode node, JsonNode? selectRow = null)
    {
        _context = NodeRef.Of(node);
        _main.SetTable(node, selectRow);
        UpdateCrumbs();
    }

    public void Up()
    {
        var ctx = Context;
        if (ctx.Parent == null) return;
        var parent = ctx.Parent;
        SetContext(parent, parent.IsArray ? ctx : parent);
        _vm.Select(ctx);
    }

    private void UpdateCrumbs()
    {
        _crumbs.Children.Clear();
        var ctx = Context;
        var chain = new List<JsonNode>();
        for (var n = ctx; n != null; n = n.Parent) chain.Add(n);
        chain.Reverse();
        for (int i = 0; i < chain.Count; i++)
        {
            var n = chain[i];
            if (i > 0) _crumbs.Children.Add(Ui.Text("›", "Fg.Faint", 13));
            string label = n.Parent == null ? "root" : n.IsArrayItem ? Schema.RowLabel(n) : n.Key;
            if (i == chain.Count - 1)
            {
                var t = Ui.Text(label, "Fg.Text", 13, FontWeights.SemiBold);
                t.Margin = new Thickness(4, 0, 4, 0);
                _crumbs.Children.Add(t);
            }
            else
            {
                var b = Ui.Button(label, "LinkButton");
                var target = n;
                b.Click += (_, _) => SetContext(target);
                _crumbs.Children.Add(b);
            }
        }
        _up.IsEnabled = ctx.Parent != null;
    }

    // ---- document changes -------------------------------------------------------------------------

    private void OnDocumentChanged(object? sender, DocumentChangedEventArgs e)
    {
        var ctx = _context.Resolve(_vm.Editor);
        if (ctx == null)
        {
            ctx = _vm.Editor.Root;
            _context = NodeRef.Of(ctx);
        }
        if (_main.Table != ctx) _main.SetTable(ctx);
        else _main.OnDocumentChanged(e);
        _props.OnDocumentChanged(e);
        if (e.Kind != ChangeKind.Value) UpdateCrumbs();
        _main.SetActiveCell(_props.OpenedRow, _props.OpenedColumnId);
    }

    /// <summary>
    /// Shows a node (selected in the code pane): keeps the main table when the node is inside it, otherwise opens the
    /// table that holds the node; selects the row, outlines the cell and drills the property window down to the node.
    /// </summary>
    public void Reveal(JsonNode node)
    {
        var root = _vm.Editor.Root;
        if (node == root)
        {
            if (Context != root) SetContext(root);
            _main.SelectRow(null, notify: false);
            _props.Clear();
            return;
        }

        var ctx = Context;
        if (ctx == node || !ctx.IsAncestorOf(node))
        {
            // The table that holds the node: its array, or the array/object that holds its parent object.
            var p = node.Parent!;
            ctx = p.IsArray ? p : p.IsArrayItem ? p.Parent! : p;
            SetContext(ctx);
        }

        // The row of the main table on the way to the node, and the cell (child of the row) on the way.
        JsonNode row = ctx;
        if (ctx.IsArray)
        {
            row = node;
            while (row.Parent != ctx) row = row.Parent!;
        }
        _main.SelectRow(row, notify: false, scrollIntoView: true);
        if (row == node)
        {
            _props.ShowRow(row, null);
            return;
        }

        var cell = node;
        while (cell.Parent != row) cell = cell.Parent!;
        var col = _main.Columns.FirstOrDefault(c => !c.IsValueColumn && c.Name == cell.Key);
        if (col == null) return;
        if (_vm.Settings.PropertyCollapsed && cell.IsContainer) SetCollapsed(false);
        if (cell == node)
        {
            if (node.IsContainer) _props.ShowContainer(node, row, col.Id);
            else _props.ShowRow(row, col);
            return;
        }
        _props.ShowContainer(cell, row, col.Id);
        _props.RevealInside(node);
    }

    /// <summary>Alt+← from anywhere in the Tables view.</summary>
    public bool PropertyBack() => _props.Back();

    // ---- ITableHost (main grid) -------------------------------------------------------------------

    void ITableHost.CellActivated(TableGrid grid, JsonNode row, ColumnInfo column)
    {
        var cell = Schema.Cell(row, column);
        _vm.Select(cell ?? row);
        if (cell is { IsContainer: true }) _props.ShowContainer(cell, row, column.Id);
        else _props.ShowRow(row, column);
    }

    void ITableHost.RowSelected(TableGrid grid, JsonNode row)
    {
        _vm.Select(row);
        _props.ShowRow(row, null);
    }

    void ITableHost.OpenContainer(TableGrid grid, JsonNode row, ColumnInfo column, JsonNode container)
    {
        _vm.Select(container);
        if (_vm.Settings.PropertyCollapsed) SetCollapsed(false);
        _props.ShowContainer(container, row, column.Id);
    }

    void ITableHost.OpenAsTable(TableGrid grid, JsonNode container)
    {
        SetContext(container);
        _vm.Select(container);
    }

    void ITableHost.NavigateUp(TableGrid grid) => Up();
}

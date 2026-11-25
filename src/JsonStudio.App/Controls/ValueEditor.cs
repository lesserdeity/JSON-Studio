using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using JsonStudio.App.ViewModels;
using JsonStudio.Core.Model;
using JsonStudio.Core.Serialization;

namespace JsonStudio.App.Controls;

public enum ValueEditorKind
{
    String,
    Number,
    Boolean,
    /// <summary>Null value or missing key: typed text is parsed as a literal on commit.</summary>
    Literal,
    Container,
    /// <summary>Missing key of a container column: a dashed "+ [ ]" / "+ { }" pill.</summary>
    NewContainer,
    NotApplicable,
}

/// <summary>Callbacks and context for a <see cref="ValueEditor"/>.</summary>
public sealed class ValueEditorOptions
{
    public required MainViewModel Vm { get; init; }

    /// <summary>The view that owns the editor (so it can skip refreshing its own edits).</summary>
    public required object Source { get; init; }

    /// <summary>For Literal editors: writes the typed text and returns the new node.</summary>
    public Func<string, JsonNode?>? CommitLiteral { get; init; }

    /// <summary>For NewContainer editors: creates the empty container and returns it.</summary>
    public Func<JsonNode?>? CreateContainer { get; init; }

    public Action<JsonNode>? OpenContainer { get; init; }
    public Action<JsonNode>? OpenAsTable { get; init; }

    /// <summary>The user focused or clicked the editor.</summary>
    public Action? Activated { get; init; }

    /// <summary>Pills say "3 rows" (grids) instead of "3 items ›" (property list).</summary>
    public bool GridPills { get; init; }

    public string Placeholder { get; init; } = "null";

    public NodeType? NewContainerType { get; init; }
}

/// <summary>
/// The editor for one value: text box, number box, toggle, null/missing literal box, or container pill.
/// Used by the grid cells, the property list and the tree.
/// </summary>
public sealed class ValueEditor
{
    private static ValueEditor? _active;
    private readonly ValueEditorOptions _o;
    private bool _suppress;
    private bool _pending;

    public ValueEditor(JsonNode? node, ValueEditorKind kind, ValueEditorOptions options)
    {
        Node = node;
        Kind = kind;
        _o = options;
        Element = Build();
    }

    public JsonNode? Node { get; }
    public ValueEditorKind Kind { get; }
    public FrameworkElement Element { get; }
    public TextBox? Box { get; private set; }
    public CheckBox? Switch { get; private set; }
    public Button? Pill { get; private set; }

    public bool IsMultiline => Box?.AcceptsReturn == true;

    public static ValueEditorKind KindOf(JsonNode? node) => node == null
        ? ValueEditorKind.Literal
        : node.Type switch
        {
            NodeType.String => ValueEditorKind.String,
            NodeType.Number => ValueEditorKind.Number,
            NodeType.Boolean => ValueEditorKind.Boolean,
            NodeType.Null => ValueEditorKind.Literal,
            _ => ValueEditorKind.Container,
        };

    /// <summary>The element that takes keyboard focus, or null.</summary>
    public IInputElement? FocusTarget => (IInputElement?)Box ?? (IInputElement?)Switch ?? Pill;

    private FrameworkElement Build()
    {
        switch (Kind)
        {
            case ValueEditorKind.String:
            case ValueEditorKind.Number:
            case ValueEditorKind.Literal:
                return BuildTextBox();

            case ValueEditorKind.Boolean:
                Switch = new CheckBox { IsChecked = Node!.BoolValue, Margin = new Thickness(4, 0, 4, 0) };
                Switch.Styled("SwitchBox");
                Switch.Click += (_, _) =>
                {
                    _o.Activated?.Invoke();
                    _o.Vm.Editor.SetBoolean(Node, Switch.IsChecked == true, _o.Source);
                };
                Switch.GotKeyboardFocus += (_, _) => _o.Activated?.Invoke();
                return Switch;

            case ValueEditorKind.Container:
                Pill = Ui.Button(PillText(), "PillButton");
                Pill.SetResourceReference(Control.ForegroundProperty, Node!.IsArray ? "Key" : "Fg.Text");
                Pill.Margin = new Thickness(4, 0, 4, 0);
                Pill.ToolTip = "Click to open in the property window. Double-click to open as the main table.";
                Pill.Click += (_, _) =>
                {
                    _o.Activated?.Invoke();
                    _o.OpenContainer?.Invoke(Node);
                };
                Pill.MouseDoubleClick += (_, e) =>
                {
                    e.Handled = true;
                    _o.OpenAsTable?.Invoke(Node);
                };
                return Pill;

            case ValueEditorKind.NewContainer:
                Pill = Ui.Button(_o.NewContainerType == NodeType.Object ? "+ { }" : "+ [ ]", "DashedPillButton");
                Pill.Margin = new Thickness(4, 0, 4, 0);
                Pill.ToolTip = "Create the empty " + (_o.NewContainerType == NodeType.Object ? "object" : "array");
                Pill.Click += (_, _) =>
                {
                    _o.Activated?.Invoke();
                    var created = _o.CreateContainer?.Invoke();
                    if (created != null) _o.OpenContainer?.Invoke(created);
                };
                return Pill;

            default:
                var hatch = new Border { MinHeight = 26 };
                hatch.SetResourceReference(Border.BackgroundProperty, "HatchBrush");
                hatch.ToolTip = "Not applicable";
                return hatch;
        }
    }

    private string PillText() => Ui.ContainerSummary(Node!, _o.GridPills) + (_o.GridPills ? "" : "  ›");

    private TextBox BuildTextBox()
    {
        var tb = new TextBox();
        tb.Styled("CellBox");
        Box = tb;
        switch (Kind)
        {
            case ValueEditorKind.String:
                tb.SetResourceReference(Control.ForegroundProperty, "String");
                tb.Text = Node!.StringValue;
                SetMultiline(Node.StringValue.Contains('\n'));
                break;
            case ValueEditorKind.Number:
                tb.SetResourceReference(Control.ForegroundProperty, "Number");
                tb.Text = JsonWriter.FormatNumber(Node!.NumberValue);
                break;
            default:
                tb.SetResourceReference(Control.ForegroundProperty, "Fg.Text");
                tb.Tag = _o.Placeholder;
                break;
        }

        tb.TextChanged += OnTextChanged;
        tb.GotKeyboardFocus += (_, _) =>
        {
            _active = this;
            _o.Activated?.Invoke();
        };
        tb.LostKeyboardFocus += (_, _) =>
        {
            // Commit after the focus change has finished, because a commit can rebuild the view.
            tb.Dispatcher.BeginInvoke(() =>
            {
                if (_active == this) _active = null;
                Commit();
                _o.Vm.Editor.EndBurst();
            }, System.Windows.Threading.DispatcherPriority.Input);
        };
        tb.PreviewKeyDown += (_, e) =>
        {
            // Shift+Enter inserts a line break in a string.
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Shift && Kind is ValueEditorKind.String or ValueEditorKind.Literal)
            {
                int caret = tb.CaretIndex;
                if (!tb.AcceptsReturn) SetMultiline(true);
                tb.SelectedText = "\n";
                tb.CaretIndex = caret + 1;
                tb.SelectionLength = 0;
                e.Handled = true;
            }
        };
        return tb;
    }

    private void SetMultiline(bool on)
    {
        if (Box == null) return;
        Box.AcceptsReturn = on;
        Box.TextWrapping = on ? TextWrapping.Wrap : TextWrapping.NoWrap;
        Box.MaxHeight = on ? 140 : double.PositiveInfinity;
        Box.VerticalScrollBarVisibility = on ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden;
        Box.VerticalContentAlignment = on ? VerticalAlignment.Top : VerticalAlignment.Center;
    }

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppress || Box == null) return;
        switch (Kind)
        {
            case ValueEditorKind.String:
                if (IsMultiline) _pending = true; // multi-line strings update the other view on commit
                else _o.Vm.Editor.SetString(Node!, Box.Text, _o.Source);
                break;
            case ValueEditorKind.Number:
                if (LiteralParser.TryParseNumber(Box.Text, out var d))
                {
                    SetInvalid(false);
                    _o.Vm.Editor.SetNumber(Node!, d, _o.Source);
                }
                else
                {
                    SetInvalid(true);
                    _pending = true;
                }
                break;
            case ValueEditorKind.Literal:
                _pending = Box.Text.Length > 0;
                break;
        }
    }

    private void SetInvalid(bool invalid)
    {
        if (Box == null) return;
        if (invalid)
        {
            Box.SetResourceReference(Control.BorderBrushProperty, "Error");
            Box.ToolTip = "Not a valid number";
        }
        else
        {
            Box.ClearValue(Control.BorderBrushProperty);
            Box.ToolTip = null;
        }
    }

    /// <summary>Commits pending text. Returns true when the document structure changed (the view will be rebuilt).</summary>
    public bool Commit()
    {
        if (!_pending || Box == null) return false;
        _pending = false;
        switch (Kind)
        {
            case ValueEditorKind.String:
                _o.Vm.Editor.SetString(Node!, Box.Text, _o.Source);
                _o.Vm.Editor.EndBurst();
                return false;
            case ValueEditorKind.Number:
                if (!LiteralParser.TryParseNumber(Box.Text, out var d))
                {
                    SetInvalid(false);
                    SetText(JsonWriter.FormatNumber(Node!.NumberValue));
                    _o.Vm.ShowMessage("That isn't a number. To store text, change the column type to string.");
                    return false;
                }
                _o.Vm.Editor.SetNumber(Node!, d, _o.Source);
                return false;
            case ValueEditorKind.Literal:
                if (Box.Text.Length == 0) return false;
                var text = Box.Text;
                if (_o.CommitLiteral != null) _o.CommitLiteral(text);
                else if (Node != null) _o.Vm.Editor.SetLiteral(Node, text, forceString: false, _o.Source);
                return true;
        }
        return false;
    }

    /// <summary>Commits the text box that has (or just had) focus. Used before save, undo and close.</summary>
    public static void CommitActive()
    {
        var a = _active;
        if (a == null) return;
        if (a.Commit()) _active = null;
        a._o.Vm.Editor.EndBurst();
    }

    /// <summary>Discards pending text and shows the model value again.</summary>
    public void Revert()
    {
        _pending = false;
        SetInvalid(false);
        RefreshFromModel(force: true);
    }

    private void SetText(string text)
    {
        if (Box == null || Box.Text == text) return;
        _suppress = true;
        int caret = Box.CaretIndex;
        Box.Text = text;
        Box.CaretIndex = Math.Min(caret, text.Length);
        _suppress = false;
    }

    /// <summary>Updates the editor from the model (not while the user is typing in it).</summary>
    public void RefreshFromModel(bool force = false)
    {
        if (Node == null) return;
        if (!force && Box?.IsKeyboardFocusWithin == true) return;
        switch (Kind)
        {
            case ValueEditorKind.String when Node.Type == NodeType.String:
                if (!IsMultiline && Node.StringValue.Contains('\n')) SetMultiline(true);
                SetText(Node.StringValue);
                break;
            case ValueEditorKind.Number when Node.Type == NodeType.Number:
                SetText(JsonWriter.FormatNumber(Node.NumberValue));
                break;
            case ValueEditorKind.Boolean when Node.Type == NodeType.Boolean:
                Switch!.IsChecked = Node.BoolValue;
                break;
            case ValueEditorKind.Container when Node.IsContainer:
                Pill!.Content = PillText();
                break;
        }
    }

    public void FocusAndSelect()
    {
        if (Box != null)
        {
            Box.Focus();
            Keyboard.Focus(Box);
            Box.SelectAll();
        }
        else if (FocusTarget is UIElement u)
        {
            u.Focus();
            Keyboard.Focus(u);
        }
    }

    /// <summary>Caret is on the first line of a multi-line box (or the box is single-line).</summary>
    public bool CaretOnFirstLine => Box == null || !IsMultiline || Box.GetLineIndexFromCharacterIndex(Box.CaretIndex) <= 0;

    public bool CaretOnLastLine => Box == null || !IsMultiline ||
                                   Box.GetLineIndexFromCharacterIndex(Box.CaretIndex) >= Box.LineCount - 1;

    public void SetHighlight(bool find)
    {
        if (Element is Control c)
        {
            if (find) c.SetResourceReference(Control.BackgroundProperty, "Find");
            else if (Kind is ValueEditorKind.Container) c.SetResourceReference(Control.BackgroundProperty, "Pill.Bg");
            else if (Kind is ValueEditorKind.NewContainer or ValueEditorKind.String or ValueEditorKind.Number or ValueEditorKind.Literal)
                c.Background = Brushes.Transparent;
        }
    }
}

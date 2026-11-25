using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using JsonStudio.Core.Serialization;

namespace JsonStudio.App.Controls;

/// <summary>
/// JSON code editor: a TextBox (native caret, selection and undo) with transparent text, drawn over a layer that
/// paints syntax colors and the selection band, plus a line-number gutter.
/// </summary>
public sealed class CodeEditor : UserControl
{
    private readonly TextBox _box;
    private readonly SyntaxLayer _layer;
    private readonly Gutter _gutter;
    private bool _suppress;
    private LineSpan? _band;
    private int? _errorLine;
    private int _lastCaretLine = -1;

    public CodeEditor()
    {
        _box = new TextBox
        {
            AcceptsReturn = true,
            AcceptsTab = false,
            TextWrapping = TextWrapping.NoWrap,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Foreground = Brushes.Transparent,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 6, 8, 6),
            FontSize = 13,
            IsUndoEnabled = true,
            UndoLimit = 500,
            FocusVisualStyle = null,
        };
        _box.SetResourceReference(TextBox.FontFamilyProperty, "Font.Mono");
        _box.SetResourceReference(TextBox.CaretBrushProperty, "Fg.Text");
        _box.SetResourceReference(TextBox.SelectionBrushProperty, "Key");
        _box.SelectionOpacity = 0.3;
        _box.Template = BuildTemplate();
        TextOptions.SetTextFormattingMode(_box, TextFormattingMode.Ideal);

        _layer = new SyntaxLayer(this) { ClipToBounds = true, IsHitTestVisible = false };
        _gutter = new Gutter(this) { ClipToBounds = true };

        var area = new Grid();
        area.Children.Add(_layer);
        area.Children.Add(_box);

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(_gutter);
        Grid.SetColumn(area, 1);
        root.Children.Add(area);
        Content = root;

        _box.TextChanged += (_, _) =>
        {
            _gutter.UpdateWidth();
            Redraw();
            if (!_suppress) UserTextChanged?.Invoke();
        };
        _box.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => Redraw()));
        _box.SizeChanged += (_, _) => Redraw();
        _box.PreviewKeyDown += OnPreviewKeyDown;
        _box.SelectionChanged += (_, _) =>
        {
            if (_suppress || !_box.IsKeyboardFocusWithin) return;
            int line = _box.GetLineIndexFromCharacterIndex(_box.CaretIndex) + 1;
            if (line == _lastCaretLine || line <= 0) return;
            _lastCaretLine = line;
            CaretLineChanged?.Invoke(line);
        };
        _box.GotKeyboardFocus += (_, _) => _lastCaretLine = -1;
        _box.GotKeyboardFocus += (_, _) => EditorGotFocus?.Invoke();
        _box.LostKeyboardFocus += (_, _) => EditorLostFocus?.Invoke();
        Services.ThemeService.ThemeChanged += (_, _) => Redraw();
    }

    /// <summary>The user changed the text (not raised for <see cref="SetText"/>).</summary>
    public event Action? UserTextChanged;

    /// <summary>The user moved the caret to another line (1-based).</summary>
    public event Action<int>? CaretLineChanged;
    public event Action? EditorGotFocus;
    public event Action? EditorLostFocus;

    public string Indent { get; set; } = "  ";
    public TextBox Box => _box;
    public string Text => _box.Text;
    public bool IsEditing => _box.IsKeyboardFocusWithin;

    public void SetText(string text)
    {
        if (_box.Text == text) return;
        _suppress = true;
        double v = _box.VerticalOffset, h = _box.HorizontalOffset;
        int caret = _box.CaretIndex;
        _box.Text = text;
        _box.CaretIndex = Math.Min(caret, text.Length);
        _box.ScrollToVerticalOffset(v);
        _box.ScrollToHorizontalOffset(h);
        _suppress = false;
        Redraw();
    }

    public LineSpan? Band
    {
        get => _band;
        set
        {
            _band = value;
            Redraw();
        }
    }

    public int? ErrorLine
    {
        get => _errorLine;
        set
        {
            _errorLine = value;
            _gutter.InvalidateVisual();
        }
    }

    private void Redraw()
    {
        _layer.InvalidateVisual();
        _gutter.InvalidateVisual();
    }

    private double LineHeight
    {
        get
        {
            var ff = _box.FontFamily;
            return ff.LineSpacing * _box.FontSize;
        }
    }

    /// <summary>Scrolls so that the band is visible, about a quarter of the way down, if it is not already visible.</summary>
    public void RevealBand()
    {
        if (_band is not { } b || _box.LineCount <= 0) return;
        int first = _box.GetFirstVisibleLineIndex();
        int last = _box.GetLastVisibleLineIndex();
        int s = b.StartLine - 1, e = b.EndLine - 1;
        bool visible = first >= 0 && s >= first && Math.Min(e, s + 3) <= last;
        if (visible) return;
        double target = s * LineHeight - _box.ViewportHeight / 4;
        _box.ScrollToVerticalOffset(Math.Max(0, target));
    }

    /// <summary>Puts the caret at a 1-based line and column and shows it.</summary>
    public void GoTo(int line, int column)
    {
        int li = Math.Clamp(line - 1, 0, Math.Max(0, _box.LineCount - 1));
        int start = _box.GetCharacterIndexFromLineIndex(li);
        if (start < 0) start = 0;
        int len = _box.GetLineLength(li);
        _box.Focus();
        Keyboard.Focus(_box);
        _box.CaretIndex = Math.Min(_box.Text.Length, start + Math.Clamp(column - 1, 0, Math.Max(0, len)));
        _box.ScrollToVerticalOffset(Math.Max(0, li * LineHeight - _box.ViewportHeight / 4));
        var r = _box.GetRectFromCharacterIndex(_box.CaretIndex);
        if (!r.IsEmpty && (r.X > _box.ViewportWidth || r.X < 0)) _box.ScrollToHorizontalOffset(Math.Max(0, _box.HorizontalOffset + r.X - 40));
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Tab && Keyboard.Modifiers == ModifierKeys.None)
        {
            int pos = _box.SelectionStart;
            _box.SelectedText = Indent;
            _box.CaretIndex = pos + Indent.Length;
            _box.SelectionLength = 0;
            e.Handled = true;
        }
    }

    private static ControlTemplate BuildTemplate()
    {
        var sv = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
        sv.SetValue(ScrollViewer.FocusableProperty, false);
        sv.SetValue(PaddingProperty, new TemplateBindingExtension(PaddingProperty));
        return new ControlTemplate(typeof(TextBox)) { VisualTree = sv };
    }

    // ---- tokens -----------------------------------------------------------------------------

    internal readonly record struct Token(int Start, int Length, string BrushKey);

    internal static List<Token> Tokenize(string line)
    {
        var list = new List<Token>();
        int n = line.Length, i = 0;
        while (i < n)
        {
            char c = line[i];
            if (c == '"')
            {
                int j = i + 1;
                while (j < n)
                {
                    if (line[j] == '\\') j += 2;
                    else if (line[j] == '"')
                    {
                        j++;
                        break;
                    }
                    else j++;
                }
                j = Math.Min(j, n);
                int k = j;
                while (k < n && (line[k] == ' ' || line[k] == '\t')) k++;
                list.Add(new Token(i, j - i, k < n && line[k] == ':' ? "Key" : "String"));
                i = j;
            }
            else if (c == '-' || char.IsAsciiDigit(c))
            {
                int j = i + 1;
                while (j < n && (char.IsAsciiDigit(line[j]) || line[j] is '.' or 'e' or 'E' or '+' or '-')) j++;
                list.Add(new Token(i, j - i, "Number"));
                i = j;
            }
            else if (char.IsLetter(c))
            {
                int j = i + 1;
                while (j < n && char.IsLetter(line[j])) j++;
                var w = line.Substring(i, j - i);
                list.Add(new Token(i, j - i, w is "true" or "false" ? "Boolean" : w == "null" ? "Null" : "Error"));
                i = j;
            }
            else if (c is '{' or '}' or '[' or ']' or ',' or ':')
            {
                list.Add(new Token(i, 1, "Fg.Muted"));
                i++;
            }
            else if (char.IsWhiteSpace(c)) i++;
            else
            {
                list.Add(new Token(i, 1, "Error"));
                i++;
            }
        }
        return list;
    }

    internal Typeface Typeface => new(_box.FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    /// <summary>Draws the syntax colors and the selection band under the transparent text.</summary>
    private sealed class SyntaxLayer : FrameworkElement
    {
        private readonly CodeEditor _o;

        public SyntaxLayer(CodeEditor owner) => _o = owner;

        protected override void OnRender(DrawingContext dc)
        {
            var tb = _o._box;
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
            if (tb.Text.Length == 0) return;
            int first = tb.GetFirstVisibleLineIndex();
            int last = tb.GetLastVisibleLineIndex();
            if (first < 0 || last < first) return;
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var tf = _o.Typeface;
            var text = tb.Text;

            if (_o._band is { } band)
            {
                int s = Math.Max(first, band.StartLine - 1), e = Math.Min(last, band.EndLine - 1);
                if (s <= e)
                {
                    var r1 = LineRect(tb, s);
                    var r2 = LineRect(tb, e);
                    if (!r1.IsEmpty && !r2.IsEmpty)
                        dc.DrawRectangle(Ui.Brush("Band"), null, new Rect(0, r1.Top, ActualWidth, Math.Max(0, r2.Bottom - r1.Top)));
                }
            }

            var cache = new Dictionary<string, Brush>();
            Brush B(string key) => cache.TryGetValue(key, out var b) ? b : cache[key] = Ui.Brush(key);

            for (int l = first; l <= last && l < tb.LineCount; l++)
            {
                int start = tb.GetCharacterIndexFromLineIndex(l);
                if (start < 0) continue;
                int len = tb.GetLineLength(l);
                if (len <= 0) continue;
                var line = text.Substring(start, Math.Min(len, text.Length - start)).TrimEnd('\r', '\n');
                foreach (var tok in Tokenize(line))
                {
                    var r = tb.GetRectFromCharacterIndex(start + tok.Start);
                    if (r.IsEmpty) continue;
                    if (r.X > ActualWidth) break;
                    var ft = new FormattedText(line.Substring(tok.Start, tok.Length), CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight, tf, tb.FontSize, B(tok.BrushKey), null, TextFormattingMode.Ideal, dpi);
                    dc.DrawText(ft, new Point(r.X, r.Top + (r.Height - ft.Height) / 2));
                }
            }
        }
    }

    private static Rect LineRect(TextBox tb, int line)
    {
        if (line < 0 || line >= tb.LineCount) return Rect.Empty;
        int idx = tb.GetCharacterIndexFromLineIndex(line);
        if (idx < 0) return Rect.Empty;
        return tb.GetRectFromCharacterIndex(idx);
    }

    /// <summary>Line numbers, the error line in red, and the accent bar of the selection band.</summary>
    private sealed class Gutter : FrameworkElement
    {
        private readonly CodeEditor _o;

        public Gutter(CodeEditor owner)
        {
            _o = owner;
            Width = 44;
        }

        public void UpdateWidth()
        {
            int digits = Math.Max(2, _o._box.LineCount.ToString(CultureInfo.InvariantCulture).Length);
            double w = digits * 8.2 + 22;
            if (Math.Abs(Width - w) > 0.5) Width = w;
        }

        protected override void OnRender(DrawingContext dc)
        {
            var tb = _o._box;
            dc.DrawRectangle(Ui.Brush("Bg.Header"), null, new Rect(RenderSize));
            dc.DrawRectangle(Ui.Brush("Border"), null, new Rect(ActualWidth - 1, 0, 1, ActualHeight));
            int first = tb.GetFirstVisibleLineIndex();
            int last = tb.GetLastVisibleLineIndex();
            if (first < 0) first = 0;
            if (last < first) last = first;
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var tf = _o.Typeface;
            var faint = Ui.Brush("Fg.Faint");
            var accent = Ui.Brush("Accent");
            var error = Ui.Brush("Error");

            for (int l = first; l <= last; l++)
            {
                var r = LineRect(tb, l);
                if (r.IsEmpty)
                {
                    if (tb.Text.Length == 0 && l == 0) r = new Rect(0, tb.Padding.Top, 0, _o.LineHeight);
                    else continue;
                }
                bool inBand = _o._band is { } b && l + 1 >= b.StartLine && l + 1 <= b.EndLine;
                bool isError = _o._errorLine == l + 1;
                if (inBand) dc.DrawRectangle(accent, null, new Rect(ActualWidth - 4, r.Top, 3, r.Height));
                if (isError) dc.DrawRectangle(Ui.Brush("Error.Soft"), null, new Rect(0, r.Top, ActualWidth - 1, r.Height));
                var ft = new FormattedText((l + 1).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, tf, tb.FontSize - 1.5, isError ? error : inBand ? accent : faint, null,
                    TextFormattingMode.Ideal, dpi);
                if (isError) ft.SetFontWeight(FontWeights.Bold);
                dc.DrawText(ft, new Point(ActualWidth - 9 - ft.Width, r.Top + (r.Height - ft.Height) / 2));
            }
        }
    }
}

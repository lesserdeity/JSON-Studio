using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using JsonStudio.Core.Model;

namespace JsonStudio.App.Controls;

/// <summary>Small helpers for building UI in code with theme (dynamic) resources.</summary>
internal static class Ui
{
    public static T Res<T>(this T e, DependencyProperty p, string key) where T : FrameworkElement
    {
        e.SetResourceReference(p, key);
        return e;
    }

    public static T Styled<T>(this T e, string styleKey) where T : FrameworkElement
    {
        e.SetResourceReference(FrameworkElement.StyleProperty, styleKey);
        return e;
    }

    public static Brush Brush(string key) => (Application.Current.TryFindResource(key) as Brush) ?? Brushes.Gray;

    public static string ValueBrushKey(NodeType t) => t switch
    {
        NodeType.String => "String",
        NodeType.Number => "Number",
        NodeType.Boolean => "Boolean",
        NodeType.Null => "Null",
        _ => "Fg.Text",
    };

    public static TextBlock Text(string text, string fgKey = "Fg.Text", double size = 13, FontWeight? weight = null)
    {
        var t = new TextBlock { Text = text, FontSize = size, VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, fgKey);
        if (weight != null) t.FontWeight = weight.Value;
        return t;
    }

    public static TextBlock Mono(string text, string fgKey = "Fg.Text", double size = 12.5)
    {
        var t = Text(text, fgKey, size);
        t.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Mono");
        return t;
    }

    public static Button Button(object content, string style, string? tip = null)
    {
        var b = new Button { Content = content };
        b.SetResourceReference(FrameworkElement.StyleProperty, style);
        if (tip != null) b.ToolTip = tip;
        return b;
    }

    public static string ContainerSummary(JsonNode n, bool rows)
    {
        int c = n.Children.Count;
        if (n.IsArray) return rows ? $"[ ]  {c} {(c == 1 ? "row" : "rows")}" : $"[ ]  {c} {(c == 1 ? "item" : "items")}";
        return $"{{ }}  {c} {(c == 1 ? "field" : "fields")}";
    }

    /// <summary>Shows a context menu below an element.</summary>
    public static void ShowMenu(FrameworkElement owner, ContextMenu menu)
    {
        menu.PlacementTarget = owner;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    public static MenuItem Item(string header, Action click, bool isChecked = false, bool enabled = true, string? gesture = null)
    {
        var m = new MenuItem { Header = header, IsChecked = isChecked, IsEnabled = enabled };
        if (gesture != null) m.InputGestureText = gesture;
        m.Click += (_, _) => click();
        return m;
    }

    public static MenuItem Label(string header)
    {
        var m = new MenuItem { Header = header, IsEnabled = false, FontWeight = FontWeights.SemiBold };
        return m;
    }

    public static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T t) return t;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

    public static bool IsDescendantOf(DependencyObject? d, DependencyObject ancestor)
    {
        while (d != null)
        {
            if (d == ancestor) return true;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }
}

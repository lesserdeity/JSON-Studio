using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace JsonStudio.App.Controls;

/// <summary>A red wavy underline, drawn under duplicate keys.</summary>
public sealed class WavyUnderline : FrameworkElement
{
    public WavyUnderline()
    {
        IsHitTestVisible = true;
        Height = 4;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var brush = Ui.Brush("Error");
        var pen = new Pen(brush, 1.1);
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            double h = ActualHeight;
            ctx.BeginFigure(new Point(0, h - 1), false, false);
            bool up = true;
            for (double x = 2; x <= ActualWidth; x += 2)
            {
                ctx.LineTo(new Point(x, up ? 1 : h - 1), true, true);
                up = !up;
            }
        }
        g.Freeze();
        dc.DrawGeometry(null, pen, g);
    }
}

/// <summary>A vertical splitter: drag to resize, double-click to collapse.</summary>
public sealed class PaneSplitter : Thumb
{
    public PaneSplitter()
    {
        Width = 6;
        Cursor = Cursors.SizeWE;
        Focusable = false;
        var line = new FrameworkElementFactory(typeof(Border));
        line.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var inner = new FrameworkElementFactory(typeof(Border));
        inner.SetValue(WidthProperty, 1.0);
        inner.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        inner.SetResourceReference(Border.BackgroundProperty, "Border");
        line.AppendChild(inner);
        Template = new ControlTemplate(typeof(Thumb)) { VisualTree = line };
    }
}

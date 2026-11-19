using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace VNotch.Controls;

/// <summary>Indented settings children with rounded branches back to their parent.</summary>
public sealed class SettingsTreePanel : StackPanel
{
    private static readonly Pen ConnectorPen = CreateConnectorPen();

    static SettingsTreePanel()
    {
        // Keep the gutter outside the content so switches still share the right edge.
        MarginProperty.OverrideMetadata(typeof(SettingsTreePanel),
            new FrameworkPropertyMetadata(new Thickness(28, 6, 0, 4)));
    }

    private static Pen CreateConnectorPen()
    {
        var pen = new Pen(Brushes.White, 2)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        pen.Freeze();
        return pen;
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        var size = base.ArrangeOverride(arrangeSize);
        // Visibility, wrapping and search reparenting can all move the last branch.
        InvalidateVisual();
        return size;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        const double trunkX = -20;
        const double branchX = -6;
        const double radius = 8;
        double trunkStart = 0;
        double previousOpacity = 1;
        // Composite the connector ink once, including where branches meet the trunk.
        drawingContext.PushOpacity(112 / 255d);
        foreach (UIElement item in Children)
        {
            // Standalone notes belong to the preceding setting, not a new branch.
            if (item is not FrameworkElement child || child is TextBlock ||
                child.Visibility != Visibility.Visible || child.RenderSize.Height <= 0)
                continue;

            var header = FindHeaderElement(child);
            double anchor = header.TranslatePoint(new Point(0, header.RenderSize.Height / 2), this).Y;
            double opacity = 1;
            for (DependencyObject? node = header; node != null && node != this; node = VisualTreeHelper.GetParent(node))
                if (node is UIElement layer) opacity *= layer.Opacity;

            double bend = Math.Max(0, anchor - radius);
            double start = trunkStart;
            if (bend > start && previousOpacity < opacity)
            {
                drawingContext.PushOpacity(Math.Min(previousOpacity, opacity));
                drawingContext.DrawLine(ConnectorPen, new Point(trunkX, trunkStart), new Point(trunkX, bend));
                drawingContext.Pop();
                start = bend;
            }
            trunkStart = bend;
            previousOpacity = opacity;
            if (opacity <= 0) continue;
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(new Point(trunkX, Math.Min(start, bend)), false, false);
                if (bend > start) context.LineTo(new Point(trunkX, bend), true, true);
                context.QuadraticBezierTo(new Point(trunkX, anchor),
                    new Point(trunkX + radius, anchor), true, false);
                context.LineTo(new Point(branchX, anchor), true, false);
            }

            geometry.Freeze();
            drawingContext.PushOpacity(opacity);
            drawingContext.DrawGeometry(null, ConnectorPen, geometry);
            drawingContext.Pop();
        }
        drawingContext.Pop();
    }

    private static FrameworkElement FindHeaderElement(FrameworkElement element)
    {
        // Slider templates and row containers can inset the title by any amount.
        // Use the rendered label itself so padding, font size and DPI stay aligned.
        if (element is ElasticSlider slider &&
            slider.Template?.FindName("PART_Label", slider) is FrameworkElement label &&
            label.Visibility == Visibility.Visible && label.RenderSize.Height > 0)
            return label;

        if (element is Decorator { Child: FrameworkElement content })
            return FindHeaderElement(content);

        if (element is Panel panel)
        {
            foreach (UIElement item in panel.Children)
            {
                if (item is FrameworkElement candidate && candidate.Visibility == Visibility.Visible &&
                    candidate.RenderSize.Height > 0)
                    return FindHeaderElement(candidate);
            }
        }
        return element;
    }
}

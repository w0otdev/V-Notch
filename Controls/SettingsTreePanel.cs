using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace VNotch.Controls;

/// <summary>Indented settings children with rounded branches back to their parent.</summary>
public sealed class SettingsTreePanel : StackPanel
{
    private static readonly Pen ConnectorPen = CreateConnectorPen();
    private readonly DrawingGroup _connectors = new();

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
        RefreshConnectors();
        drawingContext.DrawDrawing(_connectors);
    }

    // Update the retained drawing without invalidating WPF layout on every
    // animation tick. Layout changes still go through OnRender normally.
    internal void RefreshConnectors()
    {
        using var drawingContext = _connectors.Open();
        const double trunkX = -20;
        const double branchX = -6;
        const double radius = 8;
        double height = Math.Max(1, RenderSize.Height);
        var mask = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            StartPoint = new Point(), EndPoint = new Point(0, height)
        };
        var geometry = new StreamGeometry();
        double previousAnchor = 0, previousOpacity = 0, lastBend = 0;
        using (var context = geometry.Open())
        {
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
                if (mask.GradientStops.Count > 0)
                {
                    double transition = Math.Clamp((bend - previousAnchor) / 2, 0, radius);
                    double between = Math.Min(previousOpacity, opacity);
                    AddStop(previousAnchor + transition, between);
                    AddStop(bend - transition, between);
                }
                AddStop(bend, opacity);
                AddStop(anchor, opacity);
                previousAnchor = anchor;
                previousOpacity = opacity;
                lastBend = bend;
                context.BeginFigure(new Point(trunkX, bend), false, false);
                context.QuadraticBezierTo(new Point(trunkX, anchor), new Point(trunkX + radius, anchor), true, false);
                context.LineTo(new Point(branchX, anchor), true, false);
            }
            if (lastBend > 0)
            {
                context.BeginFigure(new Point(trunkX, 0), false, false);
                context.LineTo(new Point(trunkX, lastBend), true, false);
            }
        }
        if (mask.GradientStops.Count == 0) return;
        geometry.Freeze();
        mask.Freeze();
        // Apply reveal opacity once to the whole silhouette, even at a fork.
        drawingContext.PushOpacityMask(mask);
        drawingContext.DrawGeometry(null, ConnectorPen, geometry);
        drawingContext.Pop();

        void AddStop(double y, double opacity) => mask.GradientStops.Add(new GradientStop(
            Color.FromArgb((byte)Math.Round(112 * opacity), 255, 255, 255), Math.Clamp(y / height, 0, 1)));
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

using System.Windows;
using System.Windows.Media;

namespace VNotch.Services;

internal static class GlassVisualUpdates
{
    internal static void SetColor(GradientStop stop, Color color)
    {
        if (stop.Color != color) stop.Color = color;
    }

    internal static void SetDouble(DependencyObject target, DependencyProperty property, double value)
    {
        if (Math.Abs((double)target.GetValue(property) - value) > 1e-4)
            target.SetValue(property, value);
    }

    internal static void SetPoint(DependencyObject target, DependencyProperty property, Point value)
    {
        var current = (Point)target.GetValue(property);
        if (Math.Abs(current.X - value.X) > 1e-4 || Math.Abs(current.Y - value.Y) > 1e-4)
            target.SetValue(property, value);
    }
}

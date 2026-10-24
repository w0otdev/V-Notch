using System.Runtime.InteropServices;
using System.Windows;

namespace VNotch.Services.Spotlight;

internal static partial class SpotlightLayoutMetrics
{
    internal static double ResolveMeasureWidth(double actualWidth, double configuredWidth)
    {
        vn_spotlight_layout_resolve_measure_width(actualWidth, configuredWidth, out double width);
        return width;
    }

    internal static Size CalculateEntranceSize(double measureWidth, double desiredHeight, double actualHeight, Thickness margin)
    {
        vn_spotlight_layout_calculate_entrance_size(
            measureWidth, desiredHeight, actualHeight,
            margin.Left, margin.Top, margin.Right, margin.Bottom,
            out double width, out double height);
        return new Size(width, height);
    }

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_spotlight_layout_resolve_measure_width(
        double actual_width, double configured_width, out double out_width);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_spotlight_layout_calculate_entrance_size(
        double measure_width,
        double desired_height,
        double actual_height,
        double margin_left,
        double margin_top,
        double margin_right,
        double margin_bottom,
        out double out_width,
        out double out_height);
}

using System.Windows;

namespace VNotch.Services.Spotlight;

internal static class SpotlightLayoutMetrics
{
    internal static double ResolveMeasureWidth(double actualWidth, double configuredWidth) =>
        IsPositiveFinite(actualWidth) ? actualWidth : IsPositiveFinite(configuredWidth) ? configuredWidth : 768;

    internal static Size CalculateEntranceSize(double measureWidth, double desiredHeight, double actualHeight, Thickness margin)
    {
        // The HWND reserves horizontal shake space that is outside the shell.
        double width = Math.Max(1, measureWidth - margin.Left - margin.Right);
        double height = desiredHeight - margin.Top - margin.Bottom;
        if (!IsPositiveFinite(height)) height = IsPositiveFinite(actualHeight) ? Math.Max(1, actualHeight) : 1;
        return new Size(width, height);
    }

    private static bool IsPositiveFinite(double value) => double.IsFinite(value) && value > 0;
}

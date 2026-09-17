using System.Windows.Media;

namespace VNotch.Services;

internal static class UiPalette
{
    public static readonly Color PrimaryColor = Color.FromRgb(0xB8, 0xB8, 0xB8);
    public static readonly SolidColorBrush PrimaryBrush = CreatePrimaryBrush();

    private static SolidColorBrush CreatePrimaryBrush()
    {
        var brush = new SolidColorBrush(PrimaryColor);
        brush.Freeze();
        return brush;
    }
}

using System.Windows.Media;

namespace VNotch.Services;

internal static class UiPalette
{
    public static readonly Color PrimaryColor = Color.FromRgb(0xC8, 0xC8, 0xC8);
    public static readonly SolidColorBrush PrimaryBrush = CreatePrimaryBrush();

    public static readonly SolidColorBrush IconBrush = CreateIconBrush();

    private static SolidColorBrush CreateIconBrush()
    {
        var brush = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA));
        brush.Freeze();
        return brush;
    }

    private static SolidColorBrush CreatePrimaryBrush()
    {
        var brush = new SolidColorBrush(PrimaryColor);
        brush.Freeze();
        return brush;
    }
}

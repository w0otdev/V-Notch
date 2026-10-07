using System.Windows.Media;

namespace VNotch.Controls;

/// <summary>Semantic surface colors shared by tray resources and card view models.</summary>
public static class ClipboardTrayTokens
{
    public static Color SurfaceBase { get; } = Color.FromRgb(14, 14, 18);
    public static Color SurfaceElevated { get; } = Color.FromRgb(24, 24, 30);
    public static Color SurfaceOverlay { get; } = Color.FromRgb(32, 32, 40);
}

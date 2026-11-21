using System.Windows.Media;

namespace VNotch.Models;

/// <summary>Semantic surface colors shared by tray resources and card view models.</summary>
public static class ClipboardTrayTokens
{
    public static Color SurfaceOverlay { get; } = Color.FromRgb(32, 32, 40);
    public static Color CardTop { get; } = Color.FromRgb(40, 40, 40);
    public static Color CardMiddle { get; } = Color.FromRgb(27, 27, 27);
    public static Color CardBottom { get; } = Color.FromRgb(20, 20, 20);
}

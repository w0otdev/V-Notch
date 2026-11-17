using System.Windows;
using VNotch.Models;

namespace VNotch;

public partial class MainWindow
{
    private bool IsCornerMode => _settings.EnableTopLeftCornerMode;
    private bool IsCornerQuarter => IsCornerMode && !_settings.TopLeftCornerFloatingCircle;

    // Corner mode reuses the two existing layouts: the floating circle is laid out like the
    // Dynamic Island, the flush quarter-circle like the regular notch.
    private bool IslandLayout => IsIslandLayout(_settings);

    private static bool IsIslandLayout(NotchSettings settings) =>
        settings.EnableTopLeftCornerMode ? settings.TopLeftCornerFloatingCircle : settings.EnableDynamicIslandMode;

    private const double CornerSize = NotchSettings.CornerCollapsedSize;
    private double CornerLeftGap => IsCornerMode && IslandLayout ? DynamicIslandTopMargin : 0;

    // Anchors the notch (and the hit/morph rectangles derived from it) to the screen's top-left corner.
    private void ApplyCornerModePlacement()
    {
        bool cornerMode = IsCornerMode;
        NotchWrapper.HorizontalAlignment = NotchShadowWrapper.HorizontalAlignment =
            cornerMode ? HorizontalAlignment.Left : HorizontalAlignment.Center;
        NotchWrapper.RenderTransformOrigin = NotchShadowWrapper.RenderTransformOrigin =
            cornerMode ? new Point(0, 0) : new Point(0.5, 0);
        var margin = NotchContainer.Margin;
        NotchContainer.Margin = new Thickness(CornerLeftGap, margin.Top, margin.Right, margin.Bottom);
        _overlayWindow.AnchorLeft = cornerMode;
        _overlayWindow.LeftInset = CornerLeftGap;
    }
}

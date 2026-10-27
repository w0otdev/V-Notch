using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace VNotch.Controls;

/// <summary>Clips the fully composed card, including child effects, during layout.</summary>
public sealed class ClipboardCardPresentation : Grid
{
    private RectangleGeometry? _roundedClip;

    public ClipboardCardPresentation() => ClipToBounds = true;

    protected override Geometry GetLayoutClip(Size layoutSlotSize)
    {
        var bounds = new Rect(RenderSize);
        if (_roundedClip == null || _roundedClip.Rect != bounds)
        {
            _roundedClip = new RectangleGeometry(bounds, 16, 16);
            _roundedClip.Freeze();
        }
        return _roundedClip;
    }
}

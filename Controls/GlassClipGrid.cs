using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace VNotch.Controls;

// GlassClipBuilder already keeps the silhouette inside RenderSize. Combining it
// with WPF's rectangular layout clip repeatedly flattens the squircle into a
// PathGeometry when live D3DImage updates invalidate the visual's bounds.
public sealed class GlassClipGrid : Grid
{
    protected override Geometry? GetLayoutClip(Size layoutSlotSize)
    {
        if (Clip is StreamGeometry { IsFrozen: true } silhouette)
        {
            var bounds = silhouette.Bounds;
            // Geometry bounds use float precision for fractional DIP sizes.
            const double epsilon = 0.001;
            if (bounds.Left >= -epsilon && bounds.Top >= -epsilon &&
                bounds.Right <= RenderSize.Width + epsilon && bounds.Bottom <= RenderSize.Height + epsilon)
                return null;
        }
        if (Clip is null && !ClipToBounds)
            return null;
        // Preserve layout clipping while a resize is waiting for its new outline.
        return base.GetLayoutClip(layoutSlotSize);
    }
}

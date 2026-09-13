using System.Windows.Controls;
using System.Windows.Shapes;

namespace VNotch.Presenters;

public sealed class SpotifyCanvasViewRefs
{
    public required Border Background { get; init; }
    public required Grid Viewport { get; init; }
    public required MediaElement Video { get; init; }
    public required Rectangle BrightnessOverlay { get; init; }
    public Border? BlurFallbackBackground { get; init; }
    public Image? BlurFallbackImage { get; init; }
}

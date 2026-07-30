using System.Windows.Media;

namespace VNotch;

internal interface ISpotlightMorphHost
{
    (double Left, double Top, double Width, double Height, double TopCornerRadius, double BottomCornerRadius)
        GetSpotlightMorphRect();

    ImageSource? CaptureSpotlightMorphVisual();

    void SetSpotlightMorphSessionActive(bool active);

    void SetSpotlightMorphActive(bool active);

    void BeginSpotlightReturnHandoff(TimeSpan duration);
}

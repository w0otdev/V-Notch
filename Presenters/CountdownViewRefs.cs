using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace VNotch.Presenters;

public sealed class CountdownViewRefs
{
    public required Path StartIcon { get; init; }
    public required Border StartButton { get; init; }
    public required UIElement StepperCapsule { get; init; }
    public required ScaleTransform DisplayScale { get; init; }
    public required SolidColorBrush DigitsBrush { get; init; }
    public required SolidColorBrush PanelBorderBrush { get; init; }
    public required UIElement PlusButton { get; init; }
    public required UIElement MinusButton { get; init; }
    public required UIElement PlusHighlight { get; init; }
    public required UIElement MinusHighlight { get; init; }
    public required UIElement CompleteOverlay { get; init; }
}

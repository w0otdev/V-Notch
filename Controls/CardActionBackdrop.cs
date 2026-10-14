using System.Windows;
using System.Windows.Media;

namespace VNotch.Controls;

/// <summary>Samples the card content behind its sibling action bar, excluding the bar itself.</summary>
public sealed class CardActionBackdrop : FrameworkElement
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(FrameworkElement), typeof(CardActionBackdrop),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public FrameworkElement? Source
    {
        get => (FrameworkElement?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    private Rect _sample;

    public CardActionBackdrop()
    {
        IsHitTestVisible = false;
        LayoutUpdated += (_, _) =>
        {
            if (Source == null || !IsVisible || !Source.IsVisible) return;
            var sample = new Rect(TranslatePoint(default, Source), RenderSize);
            if (sample == _sample) return;
            _sample = sample;
            InvalidateVisual();
        };
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (Source == null || _sample.IsEmpty || _sample.Width <= 0 || _sample.Height <= 0) return;
        var brush = new VisualBrush(Source)
        {
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewbox = _sample,
            Stretch = Stretch.Fill
        };
        dc.DrawRectangle(brush, null, new Rect(RenderSize));
    }
}

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
    private VisualBrush? _brush;
    private FrameworkElement? _brushSource;

    public CardActionBackdrop()
    {
        IsHitTestVisible = false;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        LayoutUpdated -= OnLayoutUpdated;
        LayoutUpdated += OnLayoutUpdated;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        LayoutUpdated -= OnLayoutUpdated;
    }

    private void OnLayoutUpdated(object? sender, System.EventArgs e)
    {
        var source = Source;
        if (source == null || !IsVisible || !source.IsVisible || RenderSize.Width <= 0 || RenderSize.Height <= 0) return;
        var thisSource = PresentationSource.FromVisual(this);
        if (thisSource == null || thisSource != PresentationSource.FromVisual(source)) return;
        var sample = new Rect(TranslatePoint(default, source), RenderSize);
        if (sample == _sample) return;
        _sample = sample;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var source = Source;
        if (source == null || _sample.IsEmpty || _sample.Width <= 0 || _sample.Height <= 0) return;
        if (_brush == null || !ReferenceEquals(_brushSource, source))
        {
            _brushSource = source;
            _brush = new VisualBrush(source)
            {
                ViewboxUnits = BrushMappingMode.Absolute,
                Stretch = Stretch.Fill
            };
        }
        _brush.Viewbox = _sample;
        drawingContext.DrawRectangle(_brush, null, new Rect(RenderSize));
    }
}

using System.Windows;
using System.Windows.Controls;
using VNotch.Services;

namespace VNotch.Presenters;

internal sealed class GlassMaterialClipPresenter(FrameworkElement clipHost, params Border[] layers)
{
    private Size _lastSize = Size.Empty;
    private CornerRadius _lastRadius;
    private double _lastPowerFactor = double.NaN;
    public double PowerFactor { get; set; } = 2;

    public void SyncCornerRadius(CornerRadius radius)
    {
        if (clipHost.Visibility != Visibility.Visible) return;
        foreach (var layer in layers) layer.CornerRadius = radius;
        UpdateClip(radius);
    }

    public void UpdateClip(CornerRadius radius)
    {
        if (clipHost.Visibility != Visibility.Visible) return;
        var size = new Size(clipHost.ActualWidth, clipHost.ActualHeight);
        if (size == _lastSize && radius == _lastRadius && PowerFactor == _lastPowerFactor && clipHost.Clip != null) return;
        var geometry = GlassClipBuilder.CreateClip(size, radius, PowerFactor);
        if (geometry == null) return;
        clipHost.Clip = geometry;
        _lastSize = size;
        _lastRadius = radius;
        _lastPowerFactor = PowerFactor;
    }
}

using System.Windows;
using System.Windows.Controls;
using VNotch.Services;

namespace VNotch.Presenters;

internal sealed class GlassMaterialClipPresenter
{
    private readonly FrameworkElement clipHost;
    private readonly Border[] layers;
    private CornerRadius _requestedRadius;

    public GlassMaterialClipPresenter(FrameworkElement clipHost, params Border[] layers)
    {
        this.clipHost = clipHost;
        this.layers = layers;
        foreach (var layer in layers)
            layer.IsVisibleChanged += (_, _) =>
            {
                if (layer.IsVisible) layer.CornerRadius = _requestedRadius;
            };
    }
    private Size _lastSize = Size.Empty;
    private CornerRadius _lastRadius;
    private double _lastPowerFactor = double.NaN;
    public double PowerFactor { get; set; } = 2;

    public void SyncCornerRadius(CornerRadius radius)
    {
        _requestedRadius = radius;
        if (clipHost.Visibility != Visibility.Visible) return;
        foreach (var layer in layers)
            if (layer.Visibility != Visibility.Collapsed && layer.CornerRadius != radius)
                layer.CornerRadius = radius;
        UpdateClip(radius);
    }

    public void UpdateClip(CornerRadius radius)
    {
        if (clipHost.Visibility != Visibility.Visible) return;
        var size = new Size(clipHost.ActualWidth, clipHost.ActualHeight);
        if (size == _lastSize && radius == _lastRadius && Math.Abs(PowerFactor - _lastPowerFactor) < 1e-6 && clipHost.Clip != null) return;
        var geometry = GlassClipBuilder.CreateClip(size, radius, PowerFactor);
        if (geometry == null) return;
        clipHost.Clip = geometry;
        _lastSize = size;
        _lastRadius = radius;
        _lastPowerFactor = PowerFactor;
    }
}

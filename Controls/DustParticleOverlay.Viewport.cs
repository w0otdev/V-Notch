using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace VNotch.Controls;

public sealed partial class DustParticleOverlay
{
    private ScrollContentPresenter? _viewport;
    private bool _inViewport = true;
    internal bool IsInParticleViewport => _inViewport;

    private void TrackViewport()
    {
        StopTrackingViewport();
        for (DependencyObject? parent = VisualTreeHelper.GetParent(this); parent != null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is not ScrollContentPresenter viewport) continue;
            _viewport = viewport;
            // Scrolling can clip a WPF element without changing IsVisible.
            // Layout notifications wake it when it re-enters; no per-vsync walk.
            LayoutUpdated += OnViewportLayoutUpdated;
            UpdateViewport();
            break;
        }
    }

    private void StopTrackingViewport()
    {
        LayoutUpdated -= OnViewportLayoutUpdated;
        _viewport = null;
        _inViewport = true;
    }

    private void OnViewportLayoutUpdated(object? sender, EventArgs e) => UpdateViewport();

    private void UpdateViewport()
    {
        if (_viewport == null || !IsLoaded) return;
        bool visible;
        try
        {
            var bounds = TransformToAncestor(_viewport).TransformBounds(new Rect(RenderSize));
            visible = bounds.IntersectsWith(new Rect(_viewport.RenderSize));
        }
        catch (InvalidOperationException) { visible = true; }
        if (_inViewport == visible) return;
        _inViewport = visible;
        UpdateRenderingState();
        if (visible) InvalidateVisual();
    }
}

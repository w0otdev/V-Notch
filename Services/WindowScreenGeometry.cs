using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;

namespace VNotch.Services;

// Only the HWND origin and DPI are cached. TransformToAncestor still observes
// animated translations, scales, margins and layout rounding on every frame.
internal sealed partial class WindowScreenGeometry : IDisposable
{
    private const string NativeDll = "VNotch.Native.dll";

    [LibraryImport(NativeDll, EntryPoint = "vn_window_screen_geometry_compute_position")]
    private static partial int NativeComputePosition(
        double originX,
        double originY,
        double localX,
        double localY,
        double dpiScaleX,
        double dpiScaleY,
        out double outX,
        out double outY,
        out byte outFinite);

    private readonly Window _window;
    private bool _valid;
    private bool _disposed;
    private Point _origin;
    private DpiScale _dpi = new(1, 1);

    internal WindowScreenGeometry(Window window)
    {
        _window = window;
        _window.LocationChanged += OnWindowEvent;
        _window.SizeChanged += OnWindowEvent;
        _window.DpiChanged += OnWindowDpiChanged;
        _window.SourceInitialized += OnWindowEvent;
        _window.IsVisibleChanged += OnWindowIsVisibleChanged;
        _window.Closed += OnWindowClosed;
    }

    private void OnWindowEvent(object? sender, EventArgs e) => _valid = false;
    private void OnWindowDpiChanged(object sender, DpiChangedEventArgs e) => _valid = false;
    private void OnWindowIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => _valid = false;
    private void OnWindowClosed(object? sender, EventArgs e) => Dispose();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _window.LocationChanged -= OnWindowEvent;
        _window.SizeChanged -= OnWindowEvent;
        _window.DpiChanged -= OnWindowDpiChanged;
        _window.SourceInitialized -= OnWindowEvent;
        _window.IsVisibleChanged -= OnWindowIsVisibleChanged;
        _window.Closed -= OnWindowClosed;
    }

    internal double DpiScaleX
    {
        get { Refresh(); return _dpi.DpiScaleX; }
    }

    internal void Invalidate() => _valid = false;

    private bool Refresh()
    {
        if (_valid) return true;
        _dpi = VisualTreeHelper.GetDpi(_window);
        if (PresentationSource.FromVisual(_window) == null) return false;
        _origin = _window.PointToScreen(new Point());
        _valid = true;
        return true;
    }

    internal bool TryGetPosition(Visual visual, out Point position)
    {
        position = default;
        if (!Refresh()) return false;
        if (_window.FlowDirection == FlowDirection.RightToLeft || !_window.RenderTransform.Value.IsIdentity)
        {
            position = visual.PointToScreen(new Point());
            return double.IsFinite(position.X) && double.IsFinite(position.Y);
        }
        var local = visual.TransformToAncestor(_window).Transform(new Point());
        // PointToScreen rounds client coordinates before the native conversion.
        // Preserve its pixel placement, including half-pixel animation offsets.
        NativeComputePosition(
            _origin.X,
            _origin.Y,
            local.X,
            local.Y,
            _dpi.DpiScaleX,
            _dpi.DpiScaleY,
            out double posX,
            out double posY,
            out byte finite);
        position = new Point(posX, posY);
        return finite != 0;
    }
}

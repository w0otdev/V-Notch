using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;

namespace VNotch.Services;

// Only the HWND origin and DPI are cached. TransformToAncestor still observes
// animated translations, scales, margins and layout rounding on every frame.
internal sealed partial class WindowScreenGeometry
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
    private Point _origin;
    private DpiScale _dpi = new(1, 1);

    internal WindowScreenGeometry(Window window)
    {
        _window = window;
        window.LocationChanged += (_, _) => _valid = false;
        window.SizeChanged += (_, _) => _valid = false;
        window.DpiChanged += (_, _) => _valid = false;
        window.SourceInitialized += (_, _) => _valid = false;
        window.IsVisibleChanged += (_, _) => _valid = false;
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

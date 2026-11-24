using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms;
using VNotch.Models;

namespace VNotch.Services;

public sealed partial class NotchManager : IDisposable
{
    private const string NativeDll = "VNotch.Native.dll";

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VnNotchGeometryOut
    {
        public double NotchLeft;
        public double NotchTop;
        public double NotchWidth;
        public double NotchHeight;
        public double SafeLeft;
        public double SafeTop;
        public double SafeWidth;
        public double SafeHeight;
    }

    [LibraryImport(NativeDll, EntryPoint = "vn_notch_manager_compute_geometry")]
    private static partial int NativeComputeGeometry(
        int screenLeft,
        int screenTop,
        int screenWidth,
        double settingsWidth,
        double settingsHeight,
        double scale,
        double margin,
        out VnNotchGeometryOut outGeom);

    private NotchSettings _settings;
    private readonly HoverDetectionService? _hoverService;

    private Screen? _currentScreen;
    private VnNotchGeometryOut _geometry;
    internal Func<System.Drawing.Point?>? GetAnchor { get; set; }
    private Rect _safeArea;
    private bool _disposed;

    internal int GeometryComputeCount { get; private set; }

    public HoverDetectionService HoverService => _hoverService!;
    public Rect SafeArea => _safeArea;

    public event EventHandler<Rect>? SafeAreaChanged;
    public event EventHandler? PositionUpdated;

    public NotchManager(Window window, NotchSettings settings)
    {
        _settings = settings;
        if (window != null)
        {
            _hoverService = new HoverDetectionService(settings.HoverZoneMargin, window);
            _hoverService.Start();
        }
    }

    internal NotchManager(NotchSettings settings)
    {
        _settings = settings;
    }

    public void UpdateSettings(NotchSettings settings)
    {
        AnimationConfig.Configure(settings.AnimationFps, settings.AutoAnimationFps);
        UpdatePosition(settings);
    }

    internal void UpdatePosition(NotchSettings settings)
    {
        _settings = settings;
        UpdatePosition();
    }

    public void UpdatePosition()
    {
        _currentScreen = GetTargetScreen();

        AnimationConfig.Refresh(_currentScreen.DeviceName);

        ComputeGeometry(_currentScreen, out _geometry);
        RefreshAnchor();
        PositionUpdated?.Invoke(this, EventArgs.Empty);
    }

    internal void RefreshAnchor()
    {
        var geom = _geometry;
        if (GetAnchor?.Invoke() is { } anchor)
        {
            double dx = anchor.X - geom.NotchWidth / 2 - geom.NotchLeft;
            double dy = anchor.Y - geom.NotchTop;
            geom.NotchLeft += dx;
            geom.SafeLeft += dx;
            geom.NotchTop += dy;
            geom.SafeTop += dy;
        }
        _hoverService?.UpdateNotchBounds(geom.NotchLeft, geom.NotchTop, geom.NotchWidth, geom.NotchHeight);

        ApplySafeArea(in geom);
    }

    private void ComputeGeometry(Screen screen, out VnNotchGeometryOut geom)
    {
        GeometryComputeCount++;
        var workingArea = screen.Bounds;
        double scale = MonitorSelection.GetScale(screen);
        _ = NativeComputeGeometry(
            workingArea.Left,
            workingArea.Top,
            workingArea.Width,
            _settings.Width,
            _settings.Height,
            scale,
            4.0,
            out geom);
    }

    private void ApplySafeArea(in VnNotchGeometryOut geom)
    {
        _safeArea = new Rect(
            geom.SafeLeft,
            geom.SafeTop,
            geom.SafeWidth,
            geom.SafeHeight
        );

        SafeAreaChanged?.Invoke(this, _safeArea);
    }

    private Screen GetTargetScreen()
    {
        return MonitorSelection.Resolve(_settings);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _hoverService?.Dispose();
            _disposed = true;
        }
    }
}

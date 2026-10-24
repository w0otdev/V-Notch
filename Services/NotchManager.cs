using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms;
using VNotch.Contracts;
using VNotch.Controllers;
using VNotch.Models;

namespace VNotch.Services;

public sealed partial class NotchManager : INotchManager
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
    private readonly HoverDetectionService _hoverService;

    private Screen? _currentScreen;
    private Rect _safeArea;
    private bool _disposed;

    public NotchTransitionCoordinator TransitionCoordinator { get; }
    public HoverDetectionService HoverService => _hoverService;
    public Rect SafeArea => _safeArea;

    public event EventHandler<Rect>? SafeAreaChanged;
    public event EventHandler? PositionUpdated;

    public NotchManager(Window window, NotchSettings settings, NotchTransitionCoordinator transitionCoordinator)
    {
        _settings = settings;
        TransitionCoordinator = transitionCoordinator;
        _hoverService = new HoverDetectionService(settings.HoverZoneMargin, window);

        _hoverService.Start();
    }

    public void UpdateSettings(NotchSettings settings)
    {
        _settings = settings;
        AnimationConfig.Configure(settings.AnimationFps, settings.AutoAnimationFps);

        UpdatePosition();
    }

    public void UpdatePosition()
    {
        _currentScreen = GetTargetScreen();
        var workingArea = _currentScreen.Bounds;

        AnimationConfig.Refresh(_currentScreen.DeviceName);

        double scale = MonitorSelection.GetScale(_currentScreen);
        NativeComputeGeometry(
            workingArea.Left,
            workingArea.Top,
            workingArea.Width,
            _settings.Width,
            _settings.Height,
            scale,
            4.0,
            out var geom);
        _hoverService.UpdateNotchBounds(geom.NotchLeft, geom.NotchTop, geom.NotchWidth, geom.NotchHeight);

        UpdateSafeArea();

        PositionUpdated?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateSafeArea()
    {
        if (_currentScreen == null) return;

        var workingArea = _currentScreen.Bounds;
        double scale = MonitorSelection.GetScale(_currentScreen);
        NativeComputeGeometry(
            workingArea.Left,
            workingArea.Top,
            workingArea.Width,
            _settings.Width,
            _settings.Height,
            scale,
            4.0,
            out var geom);

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

    public static int GetMonitorCount()
    {
        return Screen.AllScreens.Length;
    }

    public static string[] GetMonitorNames()
    {
        return Screen.AllScreens
            .Select((s, i) => Loc.Get("settings.display.name", i + 1) + (s.Primary ? Loc.Get("settings.display.primary") : ""))
            .ToArray();
    }

    #region Public Controls

    public void Expand(NotchExpandMode mode = NotchExpandMode.Compact)
    {
        TransitionCoordinator.RequestView(NotchView.Media, $"NotchManager.Expand({mode})");
    }

    public void Collapse()
    {
        TransitionCoordinator.RequestCollapse("NotchManager.Collapse");
    }

    public void Hide()
    {
        _hoverService.Stop();
        TransitionCoordinator.SetEffectivelyVisible(false, "NotchManager.Hide");
    }

    public void Show()
    {
        _hoverService.Start();
        TransitionCoordinator.SetEffectivelyVisible(true, "NotchManager.Show");
    }

    #endregion

    public void Dispose()
    {
        if (!_disposed)
        {
            _hoverService.Dispose();
            _disposed = true;
        }
    }
}

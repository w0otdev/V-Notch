using System.Windows;
using System.Windows.Forms;
using VNotch.Contracts;
using VNotch.Models;
using VNotch.Controllers;

namespace VNotch.Services;

public sealed class NotchManager : INotchManager
{
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
        _ = window;
        _settings = settings;
        TransitionCoordinator = transitionCoordinator;
        _hoverService = new HoverDetectionService(settings.HoverZoneMargin);

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
        double width = _settings.Width * scale;
        double notchLeft = workingArea.Left + (workingArea.Width - width) / 2;
        _hoverService.UpdateNotchBounds(notchLeft, workingArea.Top, width, _settings.Height * scale);

        UpdateSafeArea();

        PositionUpdated?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateSafeArea()
    {
        if (_currentScreen == null) return;

        var workingArea = _currentScreen.Bounds;
        double scale = MonitorSelection.GetScale(_currentScreen);
        double notchWidth = _settings.Width * scale;
        double notchHeight = _settings.Height * scale;

        double margin = 4;
        double notchLeft = workingArea.Left + (workingArea.Width - notchWidth) / 2;

        _safeArea = new Rect(
            notchLeft - margin,
            workingArea.Top,
            notchWidth + margin * 2,
            notchHeight + margin
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

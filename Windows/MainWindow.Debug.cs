using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using VNotch.Models;
using VNotch.Services;

namespace VNotch;

public partial class MainWindow
{
    private bool _isDebugModeEnabled = false;
    private int _frameCount = 0;
    private long _lastFpsUpdate = 0;
    private double _currentMeasuredFps = 0;
    private int _currentDisplayHz = 0;
    private DebugWindow? _debugWindow;

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVMODE
    {
        private const int CCHDEVICENAME = 32;
        private const int CCHFORMNAME = 32;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICENAME)]
        public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHFORMNAME)]
        public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumDisplaySettings(string? deviceName, int modeNum, ref DEVMODE devMode);

    internal void ToggleDebugMode(bool enable)
    {
        if (_isDebugModeEnabled == enable) return;

        _isDebugModeEnabled = enable;

        if (enable)
        {
            if (_debugWindow == null)
            {
                _debugWindow = new DebugWindow(
                    initialX: _settings.DebugWindowX,
                    initialY: _settings.DebugWindowY,
                    onClose: () =>
                    {
                        _settings.EnableDebugMode = false;
                        _settingsService.Save(_settings);
                        ToggleDebugMode(false);
                    },
                    onPositionChanged: (x, y) =>
                    {
                        _settings.DebugWindowX = x;
                        _settings.DebugWindowY = y;
                        _settingsService.Save(_settings);
                    },
                    liveMetricsProvider: () =>
                    {
                        return (_currentMeasuredFps, _currentDisplayHz, _lastNetDownBytesPerSec, _lastNetUpBytesPerSec);
                    },
                    onLockViewChanged: (locked) => SetDebugViewLock(locked),
                    onDragNotchChanged: (draggable) => SetDebugDraggable(draggable),
                    onViewStateChanged: (state) => SetDebugViewState(state),
                    onResetPosition: () => ResetNotchPosition());
            }
            _debugWindow.Show();
            _debugWindow.Activate();

            CompositionTarget.Rendering -= CompositionTarget_Rendering_DebugFps;
            CompositionTarget.Rendering += CompositionTarget_Rendering_DebugFps;
            _lastFpsUpdate = Stopwatch.GetTimestamp();
            _fpsWindowStartTicks = _lastFpsUpdate;
            _fpsWindowFrameCount = 0;
            _frameCount = 0;

            UpdateRefreshRate();

            if (!_systemMonitorModule.IsRunning)
            {
                _systemMonitorModule.Start();
            }
            else
            {
                _systemMonitorModule.Tick();
            }
        }
        else
        {
            _isDebugViewLocked = false;
            _isDebugDraggable = false;
            _debugWindow?.Hide();
            CompositionTarget.Rendering -= CompositionTarget_Rendering_DebugFps;

            if (!IsSystemMonitorWidgetMode && _systemMonitorModule.IsRunning)
            {
                _systemMonitorModule.Stop();
            }
        }

        _collapsedWidth = GetCollapsedWidth();
    }

    private bool _isDebugViewLocked = false;
    private bool _isDebugDraggable = false;

    internal void SetDebugViewLock(bool lockState)
    {
        _isDebugViewLocked = lockState;
    }

    internal void SetDebugDraggable(bool draggable)
    {
        _isDebugDraggable = draggable;
    }

    internal void SetDebugViewState(string viewState)
    {
        switch (viewState)
        {
            case "MediaExpanded":
                if (!_isExpanded) ExpandNotch();
                if (_isSecondaryView) SwitchToPrimaryView();
                break;
            case "SecondaryShelf":
                if (!_isExpanded) ExpandNotch();
                SwitchToSecondaryView();
                break;
            case "TimerStopwatch":
                if (!_isExpanded) ExpandNotch();
                SwitchToTimerView();
                break;
            case "AudioRouting":
                if (!_isExpanded) ExpandNotch();
                SwitchToAudioView();
                break;
            case "CompactMusicPill":
                bool prevLock = _isDebugViewLocked;
                _isDebugViewLocked = false;
                CollapseAll();
                _isDebugViewLocked = prevLock;
                _isMusicCompactMode = true;
                _collapsedWidth = GetCollapsedWidth();
                UpdateProgressSectionLayout();
                break;
            case "CollapsedNotch":
                bool wasLock = _isDebugViewLocked;
                _isDebugViewLocked = false;
                CollapseAll();
                _isDebugViewLocked = wasLock;
                break;
            default:
                break;
        }
    }

    internal void ResetNotchPosition()
    {
        _overlayWindow.ResetToCenteredTop(_windowWidth, _windowHeight);
        _fixedX = _shellState.FixedX;
        _fixedY = _shellState.FixedY;
        _liquidGlass?.SetLiveRegion(GetGlassCaptureRegion());
    }

    private void UpdateRefreshRate()
    {
        try
        {
            DEVMODE devMode = new DEVMODE();
            devMode.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
            if (EnumDisplaySettings(null, -1, ref devMode))
            {
                _currentDisplayHz = devMode.dmDisplayFrequency;
                _debugWindow?.UpdateRefreshRate(_currentDisplayHz);
            }
            else
            {
                _currentDisplayHz = 0;
                _debugWindow?.UpdateRefreshRate(0);
            }
        }
        catch
        {
            _currentDisplayHz = 0;
            _debugWindow?.UpdateRefreshRate(0);
        }
    }

    private int _fpsWindowFrameCount = 0;
    private long _fpsWindowStartTicks = 0;

    private void CompositionTarget_Rendering_DebugFps(object? sender, EventArgs e)
    {
        _fpsWindowFrameCount++;
        long now = Stopwatch.GetTimestamp();

        if (_fpsWindowStartTicks == 0)
        {
            _fpsWindowStartTicks = now;
        }
        else
        {
            double elapsedSec = (double)(now - _fpsWindowStartTicks) / Stopwatch.Frequency;
            if (elapsedSec >= 0.3) // Refresh FPS count every 300ms for stable, accurate readings
            {
                double calculatedFps = _fpsWindowFrameCount / elapsedSec;
                _currentMeasuredFps = Math.Round(calculatedFps);
                _fpsWindowFrameCount = 0;
                _fpsWindowStartTicks = now;
            }
        }

        _frameCount++;
        if ((now - _lastFpsUpdate) / Stopwatch.Frequency >= 1.0)
        {
            UpdateRefreshRate();
            _lastFpsUpdate = now;
            _frameCount = 0;
        }
    }
}

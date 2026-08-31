using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;

namespace VNotch;

public partial class MainWindow
{
    private bool _isDebugModeEnabled = false;
    private int _frameCount = 0;
    private long _lastFpsUpdate = 0;
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
        if (_isDebugModeEnabled == enable && (_debugWindow != null && _debugWindow.IsVisible == enable)) return;

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
                    });
            }
            _debugWindow.Show();
            _debugWindow.Activate();

            CompositionTarget.Rendering -= CompositionTarget_Rendering_DebugFps;
            CompositionTarget.Rendering += CompositionTarget_Rendering_DebugFps;
            _lastFpsUpdate = Stopwatch.GetTimestamp();
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
            _debugWindow?.Hide();
            CompositionTarget.Rendering -= CompositionTarget_Rendering_DebugFps;

            if (!IsSystemMonitorWidgetMode && _systemMonitorModule.IsRunning)
            {
                _systemMonitorModule.Stop();
            }
        }

        _collapsedWidth = GetCollapsedWidth();
        ApplySettings(true);
    }

    private void UpdateRefreshRate()
    {
        try
        {
            DEVMODE devMode = new DEVMODE();
            devMode.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
            if (EnumDisplaySettings(null, -1, ref devMode))
            {
                _debugWindow?.UpdateRefreshRate(devMode.dmDisplayFrequency);
            }
            else
            {
                _debugWindow?.UpdateRefreshRate(0);
            }
        }
        catch
        {
            _debugWindow?.UpdateRefreshRate(0);
        }
    }

    private void CompositionTarget_Rendering_DebugFps(object? sender, EventArgs e)
    {
        _frameCount++;
        long now = Stopwatch.GetTimestamp();
        double elapsedSeconds = (double)(now - _lastFpsUpdate) / Stopwatch.Frequency;

        if (elapsedSeconds >= 1.0)
        {
            double fps = _frameCount / elapsedSeconds;
            _debugWindow?.UpdateFps(fps);

            UpdateRefreshRate();

            _frameCount = 0;
            _lastFpsUpdate = now;
        }
    }
}

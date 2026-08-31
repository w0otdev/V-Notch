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

        if (DebugSection != null)
        {
            if (enable)
            {
                DebugSection.Visibility = Visibility.Visible;
                DebugSection.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(1, TimeSpan.FromMilliseconds(200)));

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
                var anim = new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromMilliseconds(200));
                anim.Completed += (s, e) =>
                {
                    if (!_isDebugModeEnabled) DebugSection.Visibility = Visibility.Collapsed;
                };
                DebugSection.BeginAnimation(OpacityProperty, anim);

                CompositionTarget.Rendering -= CompositionTarget_Rendering_DebugFps;

                if (!IsSystemMonitorWidgetMode && _systemMonitorModule.IsRunning)
                {
                    _systemMonitorModule.Stop();
                }
            }

            _collapsedWidth = GetCollapsedWidth();
            ApplySettings(true);
        }
    }

    private void UpdateRefreshRate()
    {
        if (DebugRefreshRateText == null) return;
        try
        {
            DEVMODE devMode = new DEVMODE();
            devMode.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
            if (EnumDisplaySettings(null, -1, ref devMode))
            {
                DebugRefreshRateText.Text = $"{devMode.dmDisplayFrequency} Hz";
            }
            else
            {
                DebugRefreshRateText.Text = "— Hz";
            }
        }
        catch
        {
            DebugRefreshRateText.Text = "— Hz";
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
            if (DebugFpsText != null)
            {
                DebugFpsText.Text = $"{Math.Round(fps)} FPS";
            }

            UpdateRefreshRate();

            _frameCount = 0;
            _lastFpsUpdate = now;
        }
    }
}

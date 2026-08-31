using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using VNotch.Models;
using VNotch.Services;

namespace VNotch;

public partial class DebugWindow : Window
{
    private readonly Action? _onClose;
    private readonly Action<double, double>? _onPositionChanged;
    private readonly Func<(double Fps, int Hz, double NetDown, double NetUp)>? _liveMetricsProvider;
    private readonly Action<bool>? _onLockViewChanged;
    private readonly Action<bool>? _onDragNotchChanged;
    private readonly Action<string>? _onViewStateChanged;
    private readonly Action? _onResetPosition;
    private readonly double? _initialX;
    private readonly double? _initialY;

    private readonly DispatcherTimer _updateTimer;

    // Cache previous strings to prevent redundant layout updates
    private string _prevFps = "";
    private string _prevHz = "";
    private string _prevVNotchCpu = "";
    private string _prevGlobalCpu = "";
    private string _prevVNotchRam = "";
    private string _prevGlobalRam = "";
    private string _prevVNotchGpu = "";
    private string _prevGlobalGpu = "";
    private string _prevNetDown = "";
    private string _prevNetUp = "";

    public DebugWindow(
        double? initialX = null,
        double? initialY = null,
        Action? onClose = null,
        Action<double, double>? onPositionChanged = null,
        Func<(double Fps, int Hz, double NetDown, double NetUp)>? liveMetricsProvider = null,
        Action<bool>? onLockViewChanged = null,
        Action<bool>? onDragNotchChanged = null,
        Action<string>? onViewStateChanged = null,
        Action? onResetPosition = null)
    {
        _initialX = initialX;
        _initialY = initialY;
        _onClose = onClose;
        _onPositionChanged = onPositionChanged;
        _liveMetricsProvider = liveMetricsProvider;
        _onLockViewChanged = onLockViewChanged;
        _onDragNotchChanged = onDragNotchChanged;
        _onViewStateChanged = onViewStateChanged;
        _onResetPosition = onResetPosition;

        InitializeComponent();
        Loaded += DebugWindow_Loaded;
        IsVisibleChanged += DebugWindow_IsVisibleChanged;

        _updateTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(30)
        };
        _updateTimer.Tick += UpdateTimer_Tick;
    }

    private void DebugWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            int exStyle = Win32Interop.GetWindowLong(hwnd, Win32Interop.GWL_EXSTYLE);
            Win32Interop.SetWindowLong(hwnd, Win32Interop.GWL_EXSTYLE, exStyle | Win32Interop.WS_EX_TOOLWINDOW);
        }

        if (_initialX.HasValue && _initialY.HasValue)
        {
            Left = _initialX.Value;
            Top = _initialY.Value;
        }
        else
        {
            Left = Math.Max(10, SystemParameters.WorkArea.Right - 420);
            Top = Math.Max(10, SystemParameters.WorkArea.Top + 24);
        }

        var (gpuName, vramBytes) = GpuMonitorService.Instance.GetGpuInfo();
        if (GpuNameText != null) GpuNameText.Text = gpuName;
        if (GpuVramText != null) GpuVramText.Text = vramBytes > 0 ? $"{FormatGb(vramBytes)} GB VRAM" : "Integrated VRAM";

        if (IsVisible && !_updateTimer.IsEnabled)
            _updateTimer.Start();
    }

    private void DebugWindow_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            if (!_updateTimer.IsEnabled) _updateTimer.Start();
        }
        else
        {
            if (_updateTimer.IsEnabled) _updateTimer.Stop();
        }
    }

    private void UpdateTimer_Tick(object? sender, EventArgs e)
    {
        if (!IsVisible) return;

        try
        {
            var (fps, hz, netDown, netUp) = _liveMetricsProvider?.Invoke() ?? (0, 0, 0, 0);
            var snapshot = GpuMonitorService.Instance.SampleFastMetrics(fps, hz, netDown, netUp);
            UpdateSnapshot(snapshot);
        }
        catch { }
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
            _onPositionChanged?.Invoke(Left, Top);
        }
    }

    public void UpdateFps(double fps)
    {
        string text = $"{Math.Round(fps)} FPS";
        if (FpsSummaryText != null && _prevFps != text)
        {
            _prevFps = text;
            FpsSummaryText.Text = text;
        }
    }

    public void UpdateRefreshRate(int hz)
    {
        string text = hz > 0 ? $"({hz} Hz)" : "(-- Hz)";
        if (HzSummaryText != null && _prevHz != text)
        {
            _prevHz = text;
            HzSummaryText.Text = text;
        }
    }

    public void UpdateSnapshot(PerformanceDebugSnapshot snapshot)
    {
        if (snapshot == null) return;

        // FPS & Hz
        if (snapshot.Fps > 0)
        {
            string fpsStr = $"{Math.Round(snapshot.Fps)} FPS";
            if (FpsSummaryText != null && _prevFps != fpsStr)
            {
                _prevFps = fpsStr;
                FpsSummaryText.Text = fpsStr;
            }
        }

        if (snapshot.RefreshRateHz > 0)
        {
            string hzStr = $"({snapshot.RefreshRateHz} Hz)";
            if (HzSummaryText != null && _prevHz != hzStr)
            {
                _prevHz = hzStr;
                HzSummaryText.Text = hzStr;
            }
        }

        // GPU Name & VRAM
        if (GpuNameText != null && !string.IsNullOrEmpty(snapshot.GpuName))
        {
            GpuNameText.Text = snapshot.GpuName;
        }
        if (GpuVramText != null)
        {
            GpuVramText.Text = snapshot.DedicatedVramBytes > 0
                ? $"{FormatGb(snapshot.DedicatedVramBytes)} GB VRAM"
                : "VRAM";
        }

        // CPU
        string vCpuStr = $"{snapshot.ProcessCpuPercent:0.0}%";
        if (VNotchCpuText != null && _prevVNotchCpu != vCpuStr)
        {
            _prevVNotchCpu = vCpuStr;
            VNotchCpuText.Text = vCpuStr;
        }
        SetBarWidth(VNotchCpuBar, snapshot.ProcessCpuPercent);

        string gCpuStr = $"{Math.Round(snapshot.GlobalCpuPercent)}%";
        if (GlobalCpuText != null && _prevGlobalCpu != gCpuStr)
        {
            _prevGlobalCpu = gCpuStr;
            GlobalCpuText.Text = gCpuStr;
        }
        SetBarWidth(GlobalCpuBar, snapshot.GlobalCpuPercent);

        // RAM
        string vRamStr = $"{FormatMb(snapshot.ProcessRamBytes)} MB";
        if (VNotchRamText != null && _prevVNotchRam != vRamStr)
        {
            _prevVNotchRam = vRamStr;
            VNotchRamText.Text = vRamStr;
        }
        // Scale process RAM bar assuming 1GB max for notch
        double vRamPercent = Math.Clamp((snapshot.ProcessRamBytes / 1024.0 / 1024.0) / 10.0 * 100.0, 0, 100);
        SetBarWidth(VNotchRamBar, vRamPercent);

        string gRamStr = snapshot.GlobalRamTotalBytes > 0
            ? $"{FormatGb(snapshot.GlobalRamUsedBytes)} GB ({Math.Round(snapshot.GlobalRamPercent)}%)"
            : "—";
        if (GlobalRamText != null && _prevGlobalRam != gRamStr)
        {
            _prevGlobalRam = gRamStr;
            GlobalRamText.Text = gRamStr;
        }
        SetBarWidth(GlobalRamBar, snapshot.GlobalRamPercent);

        // GPU
        string vGpuStr = $"{snapshot.ProcessGpuPercent:0.0}%";
        if (VNotchGpuText != null && _prevVNotchGpu != vGpuStr)
        {
            _prevVNotchGpu = vGpuStr;
            VNotchGpuText.Text = vGpuStr;
        }
        SetBarWidth(VNotchGpuBar, snapshot.ProcessGpuPercent);

        string gGpuStr = $"{Math.Round(snapshot.GlobalGpuPercent)}%";
        if (GlobalGpuText != null && _prevGlobalGpu != gGpuStr)
        {
            _prevGlobalGpu = gGpuStr;
            GlobalGpuText.Text = gGpuStr;
        }
        SetBarWidth(GlobalGpuBar, snapshot.GlobalGpuPercent);

        // Network
        string downStr = FormatRate(snapshot.NetDownBytesPerSec);
        if (NetDownText != null && _prevNetDown != downStr)
        {
            _prevNetDown = downStr;
            NetDownText.Text = downStr;
        }

        string upStr = FormatRate(snapshot.NetUpBytesPerSec);
        if (NetUpText != null && _prevNetUp != upStr)
        {
            _prevNetUp = upStr;
            NetUpText.Text = upStr;
        }
    }

    private static void SetBarWidth(FrameworkElement? bar, double percent)
    {
        if (bar?.Parent is not FrameworkElement track) return;
        double trackWidth = track.ActualWidth;
        if (double.IsNaN(trackWidth) || trackWidth <= 0) return;

        double clamped = Math.Clamp(percent, 0, 100);
        double target = trackWidth * (clamped / 100.0);
        if (Math.Abs(bar.Width - target) > 0.5)
        {
            bar.Width = target;
        }
    }

    private static string FormatMb(ulong bytes) =>
        (bytes / 1024.0 / 1024.0).ToString("0.0");

    private static string FormatGb(ulong bytes) =>
        (bytes / 1024.0 / 1024.0 / 1024.0).ToString("0.0");

    private static string FormatRate(double bytesPerSec)
    {
        if (bytesPerSec < 0) bytesPerSec = 0;
        const double kb = 1024.0;
        const double mb = kb * 1024.0;

        if (bytesPerSec >= mb)
            return $"{bytesPerSec / mb:0.0} MB/s";
        if (bytesPerSec >= kb)
            return $"{bytesPerSec / kb:0.0} KB/s";
        return $"{bytesPerSec:0} B/s";
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        _updateTimer.Stop();
        _onClose?.Invoke();
    }

    private void LockStateCheckBox_Checked(object sender, RoutedEventArgs e)
    {
        _onLockViewChanged?.Invoke(true);
    }

    private void LockStateCheckBox_Unchecked(object sender, RoutedEventArgs e)
    {
        _onLockViewChanged?.Invoke(false);
    }

    private void DragNotchCheckBox_Checked(object sender, RoutedEventArgs e)
    {
        _onDragNotchChanged?.Invoke(true);
    }

    private void DragNotchCheckBox_Unchecked(object sender, RoutedEventArgs e)
    {
        _onDragNotchChanged?.Invoke(false);
    }

    private void ViewStateComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ViewStateComboBox.SelectedItem is System.Windows.Controls.ComboBoxItem item &&
            item.Tag is string tag && !string.IsNullOrEmpty(tag))
        {
            if (tag != "Current")
            {
                // Automatically lock view when forcing a specific state so it doesn't immediately close
                if (LockStateCheckBox != null && LockStateCheckBox.IsChecked != true)
                {
                    LockStateCheckBox.IsChecked = true;
                }
            }
            _onViewStateChanged?.Invoke(tag);
        }
    }

    private void ResetPositionBtn_Click(object sender, RoutedEventArgs e)
    {
        _onResetPosition?.Invoke();
    }
}

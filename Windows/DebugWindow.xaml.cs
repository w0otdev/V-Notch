using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using VNotch.Models;
using VNotch.Services;

namespace VNotch;

public sealed class DiagnosticLogViewModel
{
    public string FormattedTime { get; init; } = "";
    public string Category { get; init; } = "";
    public string Message { get; init; } = "";
    public string FullText => $"{FormattedTime} [{Category}] {Message}";
    public Brush SeverityBackground { get; init; } = Brushes.Transparent;
    public Brush SeverityForeground { get; init; } = Brushes.White;
}

public partial class DebugWindow : Window
{
    private readonly Action? _onClose;
    private readonly Action<double, double>? _onPositionChanged;
    private readonly Func<(double Fps, int Hz, double FrameTimeMs, double NetDown, double NetUp)>? _liveMetricsProvider;
    private readonly Action<bool>? _onLockViewChanged;
    private readonly Action<bool>? _onDragNotchChanged;
    private readonly Action<string>? _onViewStateChanged;
    private readonly Action? _onResetPosition;
    private readonly double? _initialX;
    private readonly double? _initialY;

    private readonly DispatcherTimer _updateTimer;
    private int _logRefreshCounter = 0;

    // Brushes for health levels
    private static readonly SolidColorBrush NominalBg = new(Color.FromArgb(0xFF, 0x1B, 0x33, 0x20));
    private static readonly SolidColorBrush NominalBorder = new(Color.FromArgb(0xFF, 0x2E, 0x7D, 0x32));
    private static readonly SolidColorBrush WarningBg = new(Color.FromArgb(0xFF, 0x3E, 0x2B, 0x14));
    private static readonly SolidColorBrush WarningBorder = new(Color.FromArgb(0xFF, 0xF5, 0x7C, 0x00));
    private static readonly SolidColorBrush CriticalBg = new(Color.FromArgb(0xFF, 0x42, 0x14, 0x14));
    private static readonly SolidColorBrush CriticalBorder = new(Color.FromArgb(0xFF, 0xD3, 0x2F, 0x2F));

    private static readonly SolidColorBrush TagNominalBg = new(Color.FromArgb(0x44, 0x2E, 0x7D, 0x32));
    private static readonly SolidColorBrush TagNominalFg = new(Color.FromArgb(0xFF, 0x81, 0xC7, 0x84));
    private static readonly SolidColorBrush TagWarningBg = new(Color.FromArgb(0x44, 0xF5, 0x7C, 0x00));
    private static readonly SolidColorBrush TagWarningFg = new(Color.FromArgb(0xFF, 0xFF, 0xB7, 0x4D));
    private static readonly SolidColorBrush TagCriticalBg = new(Color.FromArgb(0x55, 0xD3, 0x2F, 0x2F));
    private static readonly SolidColorBrush TagCriticalFg = new(Color.FromArgb(0xFF, 0xE5, 0x73, 0x73));

    // Cache previous strings
    private string _prevFps = "";
    private string _prevHz = "";
    private string _prevFrameTime = "";
    private string _prevVNotchCpu = "";
    private string _prevVNotchCpuThreads = "";
    private string _prevGlobalCpu = "";
    private string _prevVNotchRam = "";
    private string _prevVNotchPrivateRam = "";
    private string _prevGcHeap = "";
    private string _prevAllocRate = "";
    private string _prevGcGen = "";
    private string _prevGlobalRam = "";
    private string _prevGlobalRamAvail = "";
    private string _prevVNotchGpu = "";
    private string _prevRenderLatency = "";
    private string _prevGlobalGpu = "";
    private string _prevGpuName = "";
    private string _prevNetDown = "";
    private string _prevNetUp = "";
    private string _prevHealthSummary = "";
    private PerformanceHealthLevel _prevHealthLevel = PerformanceHealthLevel.Nominal;

    public DebugWindow(
        double? initialX = null,
        double? initialY = null,
        Action? onClose = null,
        Action<double, double>? onPositionChanged = null,
        Func<(double Fps, int Hz, double FrameTimeMs, double NetDown, double NetUp)>? liveMetricsProvider = null,
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
            Interval = TimeSpan.FromMilliseconds(100)
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
            Left = Math.Max(10, SystemParameters.WorkArea.Right - 440);
            Top = Math.Max(10, SystemParameters.WorkArea.Top + 24);
        }

        RefreshDiagnosticLogs();

        if (IsVisible && !_updateTimer.IsEnabled)
            _updateTimer.Start();
    }

    private void DebugWindow_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            RefreshDiagnosticLogs();
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
            var (fps, hz, frameTimeMs, netDown, netUp) = _liveMetricsProvider?.Invoke() ?? (0, 0, 0, 0, 0);
            var snapshot = PerformanceDiagnosticService.Instance.SampleSnapshot(fps, hz, frameTimeMs, netDown, netUp);
            UpdateSnapshot(snapshot);

            _logRefreshCounter++;
            if (_logRefreshCounter % 3 == 0) // Refresh log UI every ~300ms
            {
                RefreshDiagnosticLogs();
            }
        }
        catch { }
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject dep)
        {
            var parent = dep;
            while (parent != null && parent != this)
            {
                if (parent is System.Windows.Controls.TextBox or
                    System.Windows.Controls.Button or
                    System.Windows.Controls.ComboBox or
                    System.Windows.Controls.Primitives.ScrollBar or
                    System.Windows.Controls.Primitives.Thumb or
                    System.Windows.Controls.CheckBox)
                {
                    return;
                }
                parent = VisualTreeHelper.GetParent(parent);
            }
        }

        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
                _onPositionChanged?.Invoke(Left, Top);
            }
            catch { }
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

        // 1. Health Status Banner
        if (snapshot.HealthLevel != _prevHealthLevel || snapshot.HealthStatusSummary != _prevHealthSummary)
        {
            _prevHealthLevel = snapshot.HealthLevel;
            _prevHealthSummary = snapshot.HealthStatusSummary;

            switch (snapshot.HealthLevel)
            {
                case PerformanceHealthLevel.Critical:
                    HealthBanner.Background = CriticalBg;
                    HealthBanner.BorderBrush = CriticalBorder;
                    HealthBadgeBorder.Background = CriticalBorder;
                    HealthBadgeText.Text = "CRITICAL";
                    break;
                case PerformanceHealthLevel.Warning:
                    HealthBanner.Background = WarningBg;
                    HealthBanner.BorderBrush = WarningBorder;
                    HealthBadgeBorder.Background = WarningBorder;
                    HealthBadgeText.Text = "WARNING";
                    break;
                default:
                    HealthBanner.Background = NominalBg;
                    HealthBanner.BorderBrush = NominalBorder;
                    HealthBadgeBorder.Background = NominalBorder;
                    HealthBadgeText.Text = "NOMINAL";
                    break;
            }
            HealthSummaryText.Text = snapshot.HealthStatusSummary;
        }

        // 2. V-Notch CPU & Threads
        string vCpuStr = $"{snapshot.ProcessCpuPercent:0.0}%";
        if (VNotchCpuText != null && _prevVNotchCpu != vCpuStr)
        {
            _prevVNotchCpu = vCpuStr;
            VNotchCpuText.Text = vCpuStr;
        }
        SetBarWidth(VNotchCpuBar, snapshot.ProcessCpuPercent);

        string vCpuThStr = $"CPU ({snapshot.ProcessThreadCount} th / {snapshot.ProcessHandleCount} hd)";
        if (VNotchCpuThreadsText != null && _prevVNotchCpuThreads != vCpuThStr)
        {
            _prevVNotchCpuThreads = vCpuThStr;
            VNotchCpuThreadsText.Text = vCpuThStr;
        }

        // 3. V-Notch RAM (Working Set & Private)
        string vRamStr = $"{FormatMb(snapshot.ProcessWorkingSetBytes)} MB";
        if (VNotchRamText != null && _prevVNotchRam != vRamStr)
        {
            _prevVNotchRam = vRamStr;
            VNotchRamText.Text = vRamStr;
        }
        double vRamPercent = Math.Clamp((snapshot.ProcessWorkingSetBytes / 1024.0 / 1024.0) / 10.0 * 100.0, 0, 100);
        SetBarWidth(VNotchRamBar, vRamPercent);

        string vPrivStr = $"RAM ({FormatMb(snapshot.ProcessPrivateBytes)} MB Priv)";
        if (VNotchPrivateRamText != null && _prevVNotchPrivateRam != vPrivStr)
        {
            _prevVNotchPrivateRam = vPrivStr;
            VNotchPrivateRamText.Text = vPrivStr;
        }

        // 4. Managed GC Heap & Alloc Rate
        string heapStr = $"Heap {FormatMb((ulong)snapshot.ManagedHeapBytes)} MB";
        if (GcHeapText != null && _prevGcHeap != heapStr)
        {
            _prevGcHeap = heapStr;
            GcHeapText.Text = heapStr;
        }

        string allocStr = $"({FormatRate(snapshot.AllocBytesPerSec)})";
        if (AllocRateText != null && _prevAllocRate != allocStr)
        {
            _prevAllocRate = allocStr;
            AllocRateText.Text = allocStr;
        }

        string gcGenStr = $"GC ({snapshot.GcGen0Count}/{snapshot.GcGen1Count}/{snapshot.GcGen2Count})";
        if (GcGenText != null && _prevGcGen != gcGenStr)
        {
            _prevGcGen = gcGenStr;
            GcGenText.Text = gcGenStr;
        }

        // 5. V-Notch GPU & Latency
        string vGpuStr = $"{snapshot.ProcessGpuPercent:0.0}%";
        if (VNotchGpuText != null && _prevVNotchGpu != vGpuStr)
        {
            _prevVNotchGpu = vGpuStr;
            VNotchGpuText.Text = vGpuStr;
        }
        SetBarWidth(VNotchGpuBar, snapshot.ProcessGpuPercent);

        string renderLatStr = $"GPU ({snapshot.FrameTimeMs:0.0}ms / {snapshot.DispatcherLatencyMs:0.0}ms UI)";
        if (RenderLatencyText != null && _prevRenderLatency != renderLatStr)
        {
            _prevRenderLatency = renderLatStr;
            RenderLatencyText.Text = renderLatStr;
        }

        // 6. Global CPU
        string gCpuStr = $"{Math.Round(snapshot.GlobalCpuPercent)}%";
        if (GlobalCpuText != null && _prevGlobalCpu != gCpuStr)
        {
            _prevGlobalCpu = gCpuStr;
            GlobalCpuText.Text = gCpuStr;
        }
        SetBarWidth(GlobalCpuBar, snapshot.GlobalCpuPercent);

        // 7. Global RAM
        string gRamStr = snapshot.GlobalRamTotalBytes > 0
            ? $"{FormatGb(snapshot.GlobalRamUsedBytes)} GB ({Math.Round(snapshot.GlobalRamPercent)}%)"
            : "—";
        if (GlobalRamText != null && _prevGlobalRam != gRamStr)
        {
            _prevGlobalRam = gRamStr;
            GlobalRamText.Text = gRamStr;
        }
        SetBarWidth(GlobalRamBar, snapshot.GlobalRamPercent);

        string gAvailStr = $"RAM ({FormatGb(snapshot.GlobalRamAvailBytes)} GB free)";
        if (GlobalRamAvailText != null && _prevGlobalRamAvail != gAvailStr)
        {
            _prevGlobalRamAvail = gAvailStr;
            GlobalRamAvailText.Text = gAvailStr;
        }

        // 8. Global GPU & VRAM
        string gGpuStr = $"{Math.Round(snapshot.GlobalGpuPercent)}%";
        if (GlobalGpuText != null && _prevGlobalGpu != gGpuStr)
        {
            _prevGlobalGpu = gGpuStr;
            GlobalGpuText.Text = gGpuStr;
        }
        SetBarWidth(GlobalGpuBar, snapshot.GlobalGpuPercent);

        string gpuNameVram = snapshot.DedicatedVramBytes > 0
            ? $"{snapshot.GpuName} ({FormatGb(snapshot.DedicatedVramBytes)} GB)"
            : snapshot.GpuName;
        if (GpuNameText != null && _prevGpuName != gpuNameVram)
        {
            _prevGpuName = gpuNameVram;
            GpuNameText.Text = gpuNameVram;
        }

        // 9. Network
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

        // 10. Footer FPS & Hz
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

        string ftStr = $" • {snapshot.FrameTimeMs:0.0}ms";
        if (FrameTimeSummaryText != null && _prevFrameTime != ftStr)
        {
            _prevFrameTime = ftStr;
            FrameTimeSummaryText.Text = ftStr;
        }
    }

    private int _lastLogCount = -1;
    private DateTime _lastLogTimestamp = DateTime.MinValue;
    private bool _userScrolledUp = false;

    private void RefreshDiagnosticLogs()
    {
        try
        {
            var rawLogs = PerformanceDiagnosticService.Instance.GetRecentLogs();
            if (rawLogs.Count == _lastLogCount && rawLogs.Count > 0 && rawLogs[^1].Timestamp == _lastLogTimestamp)
            {
                return;
            }

            _lastLogCount = rawLogs.Count;
            _lastLogTimestamp = rawLogs.Count > 0 ? rawLogs[^1].Timestamp : DateTime.MinValue;

            // Chronological order: oldest at top, newest at BOTTOM
            var viewModels = rawLogs.Select(l => new DiagnosticLogViewModel
            {
                FormattedTime = l.Timestamp.ToString("HH:mm:ss"),
                Category = l.Category,
                Message = l.Message,
                SeverityBackground = l.Severity switch
                {
                    PerformanceHealthLevel.Critical => TagCriticalBg,
                    PerformanceHealthLevel.Warning => TagWarningBg,
                    _ => TagNominalBg
                },
                SeverityForeground = l.Severity switch
                {
                    PerformanceHealthLevel.Critical => TagCriticalFg,
                    PerformanceHealthLevel.Warning => TagWarningFg,
                    _ => TagNominalFg
                }
            }).ToList();

            DiagnosticLogsItemsControl.ItemsSource = viewModels;

            if (!_userScrolledUp && LogsScrollViewer != null)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        LogsScrollViewer.ScrollToEnd();
                    }
                    catch { }
                }), DispatcherPriority.Loaded);
            }
        }
        catch { }
    }

    private void LogsScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (LogsScrollViewer == null) return;

        if (e.ExtentHeightChange != 0)
        {
            if (!_userScrolledUp || (LogsScrollViewer.VerticalOffset + LogsScrollViewer.ViewportHeight >= LogsScrollViewer.ExtentHeight - 20))
            {
                LogsScrollViewer.ScrollToEnd();
                _userScrolledUp = false;
            }
        }
        else if (e.VerticalChange != 0)
        {
            _userScrolledUp = (LogsScrollViewer.VerticalOffset + LogsScrollViewer.ViewportHeight < LogsScrollViewer.ExtentHeight - 10);
        }
    }

    private void CopyAllLogsBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var rawLogs = PerformanceDiagnosticService.Instance.GetRecentLogs();
            if (rawLogs.Count == 0) return;

            var sb = new System.Text.StringBuilder();
            foreach (var log in rawLogs)
            {
                sb.AppendLine($"{log.Timestamp:HH:mm:ss} [{log.Category}] {log.Message}");
            }

            Clipboard.SetText(sb.ToString());
            CopyAllLogsBtn.Content = "Copied!";

            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            timer.Tick += (s, ev) =>
            {
                timer.Stop();
                if (CopyAllLogsBtn != null) CopyAllLogsBtn.Content = "Copy All";
            };
            timer.Start();
        }
        catch (Exception ex)
        {
            RuntimeLog.Log("DEBUG-WINDOW", $"Clipboard copy failed: {ex.Message}");
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
        (bytes / 1024.0 / 1024.0).ToString("0.0");

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

    private void ClearLogsBtn_Click(object sender, RoutedEventArgs e)
    {
        PerformanceDiagnosticService.Instance.ClearLogs();
        _lastLogCount = -1;
        _lastLogTimestamp = DateTime.MinValue;
        RefreshDiagnosticLogs();
    }
}

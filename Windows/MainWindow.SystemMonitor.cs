using System;
using System.Windows;
using System.Windows.Media.Animation;
using VNotch.Models;
using VNotch.Presenters;
using VNotch.Services;

namespace VNotch;

public partial class MainWindow
{
    #region System Monitor Widget

    private SystemMonitorPresenter? _systemMonitorPresenter;

    internal void InitializeSystemMonitorPresenter()
    {
        if (_systemMonitorPresenter != null) return;

        var refs = new SystemMonitorViewRefs(
            SysMonCpuValueText,
            SysMonCpuBar,
            SysMonRamValueText,
            SysMonRamBar,
            SysMonNetDownText,
            SysMonNetUpText);

        _systemMonitorPresenter = new SystemMonitorPresenter(_systemMonitorModule, new DispatcherService(Dispatcher), refs);
    }

    internal void DisposeSystemMonitorPresenter()
    {
        _systemMonitorPresenter?.Dispose();
        _systemMonitorPresenter = null;
    }

    private void SystemMonitorModule_StatsUpdated(object? sender, SystemMonitorInfo e)
    {
        UpdateSystemMonitorUI(e);
    }

    private void UpdateSystemMonitorUI(SystemMonitorInfo stats)
    {
        if (stats == null) return;
        if (SysMonCpuValueText == null) return;

        SysMonCpuValueText.Text = $"{Math.Round(stats.CpuPercent)}%";
        SetUsageBar(SysMonCpuBar, stats.CpuPercent);

        if (stats.RamTotalBytes > 0)
        {
            SysMonRamValueText.Text =
                $"{FormatGb(stats.RamUsedBytes)} / {FormatGb(stats.RamTotalBytes)} GB";
        }
        else
        {
            SysMonRamValueText.Text = "—";
        }
        SetUsageBar(SysMonRamBar, stats.RamPercent);

        SysMonNetDownText.Text = FormatRate(stats.NetDownBytesPerSec);
        SysMonNetUpText.Text = FormatRate(stats.NetUpBytesPerSec);

        if (_isDebugModeEnabled)
        {
            if (DebugCpuText != null) DebugCpuText.Text = $"{Math.Round(stats.CpuPercent)}% CPU";
            if (DebugRamText != null) DebugRamText.Text = stats.RamTotalBytes > 0
                ? $"{FormatGb(stats.RamUsedBytes)}GB RAM"
                : "— RAM";
        }
    }

    private static void SetUsageBar(System.Windows.FrameworkElement? bar, double percent)
    {
        if (bar?.Parent is not System.Windows.FrameworkElement track) return;

        double trackWidth = track.ActualWidth;
        if (double.IsNaN(trackWidth) || trackWidth <= 0) return;

        double clamped = Math.Clamp(percent, 0, 100);
        double target = trackWidth * (clamped / 100.0);

        double current = double.IsNaN(bar.Width) ? bar.ActualWidth : bar.Width;

        if (Math.Abs(target - current) < 0.5)
        {
            bar.BeginAnimation(System.Windows.FrameworkElement.WidthProperty, null);
            bar.Width = target;
            return;
        }

        var anim = new DoubleAnimation
        {
            From = current,
            To = target,
            Duration = TimeSpan.FromMilliseconds(550),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd
        };

        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(anim, VNotch.Services.AnimationConfig.TargetFps);
        bar.BeginAnimation(System.Windows.FrameworkElement.WidthProperty, anim);
    }

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

    #endregion
}

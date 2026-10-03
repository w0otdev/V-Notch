using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using VNotch.Models;
using VNotch.Modules;
using VNotch.Services;

namespace VNotch.Presenters;

public sealed record SystemMonitorViewRefs(
    TextBlock CpuValueText,
    Border CpuBar,
    TextBlock RamValueText,
    Border RamBar,
    TextBlock NetDownText,
    TextBlock NetUpText)
{
    public SystemMonitorShelfViewRefs? Shelf { get; init; }
}

public sealed record SystemMonitorShelfViewRefs(
    FrameworkElement Section,
    TextBlock CpuValueText,
    Border CpuBar,
    TextBlock RamValueText,
    Border RamBar,
    TextBlock NetDownText,
    TextBlock NetUpText);

public sealed class SystemMonitorPresenter : IDisposable
{
    private readonly SystemMonitorModule _module;
    private readonly IDispatcherService _dispatcher;
    private SystemMonitorViewRefs? _refs;
    private int _disposed;

    public double LastNetDownBytesPerSec { get; private set; }
    public double LastNetUpBytesPerSec { get; private set; }

    public SystemMonitorPresenter(SystemMonitorModule module, IDispatcherService dispatcher, SystemMonitorViewRefs refs)
    {
        _module = module ?? throw new ArgumentNullException(nameof(module));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _refs = refs ?? throw new ArgumentNullException(nameof(refs));

        _module.StatsUpdated += OnStatsUpdated;
    }

    private void OnStatsUpdated(object? sender, SystemMonitorInfo e)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (_dispatcher.CheckAccess())
        {
            UpdateSystemMonitorUI(e);
        }
        else
        {
            _dispatcher.BeginInvoke(() => UpdateSystemMonitorUI(e));
        }
    }

    private void UpdateSystemMonitorUI(SystemMonitorInfo stats)
    {
        var refs = _refs;
        if (Volatile.Read(ref _disposed) != 0 || stats == null || refs == null) return;
        LastNetDownBytesPerSec = stats.NetDownBytesPerSec;
        LastNetUpBytesPerSec = stats.NetUpBytesPerSec;

        refs.CpuValueText.Text = $"{Math.Round(stats.CpuPercent)}%";
        SetUsageBar(refs.CpuBar, stats.CpuPercent);

        if (stats.RamTotalBytes > 0)
        {
            refs.RamValueText.Text =
                $"{FormatGb(stats.RamUsedBytes)} / {FormatGb(stats.RamTotalBytes)} GB";
        }
        else
        {
            refs.RamValueText.Text = "—";
        }
        SetUsageBar(refs.RamBar, stats.RamPercent);

        refs.NetDownText.Text = FormatRate(stats.NetDownBytesPerSec);
        refs.NetUpText.Text = FormatRate(stats.NetUpBytesPerSec);

        if (refs.Shelf is { Section.Visibility: Visibility.Visible } shelf)
        {
            shelf.CpuValueText.Text = refs.CpuValueText.Text;
            SetUsageBar(shelf.CpuBar, stats.CpuPercent);
            shelf.RamValueText.Text = stats.RamTotalBytes > 0 ? $"{FormatGb(stats.RamUsedBytes)} GB" : "—";
            SetUsageBar(shelf.RamBar, stats.RamPercent);
            shelf.NetDownText.Text = $"↓ {refs.NetDownText.Text}";
            shelf.NetUpText.Text = $"↑ {refs.NetUpText.Text}";
        }
    }

    private static void SetUsageBar(FrameworkElement? bar, double percent)
    {
        if (bar?.Parent is not FrameworkElement track) return;

        double trackWidth = track.ActualWidth;
        if (double.IsNaN(trackWidth) || trackWidth <= 0) return;

        double clamped = Math.Clamp(percent, 0, 100);
        double target = trackWidth * (clamped / 100.0);

        double current = double.IsNaN(bar.Width) ? bar.ActualWidth : bar.Width;

        if (AnimationConfig.ReduceMotion || Math.Abs(target - current) < 0.5)
        {
            bar.BeginAnimation(FrameworkElement.WidthProperty, null);
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
        bar.BeginAnimation(FrameworkElement.WidthProperty, anim);
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

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _module.StatsUpdated -= OnStatsUpdated;
        var refs = _refs;
        _refs = null;
        if (refs == null) return;
        refs.CpuBar.BeginAnimation(FrameworkElement.WidthProperty, null);
        refs.RamBar.BeginAnimation(FrameworkElement.WidthProperty, null);
        if (refs.Shelf is { } shelf)
        {
            shelf.CpuBar.BeginAnimation(FrameworkElement.WidthProperty, null);
            shelf.RamBar.BeginAnimation(FrameworkElement.WidthProperty, null);
        }
    }
}

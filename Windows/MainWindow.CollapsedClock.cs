using System.Globalization;
using System.Windows.Threading;
using Microsoft.Win32;

namespace VNotch;

public partial class MainWindow
{
    private DispatcherTimer? _collapsedClockTimer;

    private void InitializeCollapsedClock()
    {
        if (_collapsedClockTimer != null) return;
        _collapsedClockTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher);
        _collapsedClockTimer.Tick += (_, _) => RefreshCollapsedClock();
        CollapsedClockText.IsVisibleChanged += (_, _) => RefreshCollapsedClock();
        CompactClockText.IsVisibleChanged += (_, _) => RefreshCollapsedClock();
        SystemEvents.TimeChanged += OnSystemTimeChangedForCollapsedClock;
        SystemEvents.PowerModeChanged += OnPowerModeChangedForCollapsedClock;
        RefreshCollapsedClock();
    }

    // One tick per minute, aligned to the minute, and only while a clock is on screen.
    private void RefreshCollapsedClock()
    {
        if (_collapsedClockTimer == null) return;
        _collapsedClockTimer.Stop();
        if (!IsEffectivelyNotchVisible || (!CollapsedClockText.IsVisible && !CompactClockText.IsVisible)) return;

        var now = DateTime.Now;
        string format = CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern;
        // The corner shapes are too small for an AM/PM designator.
        if (IsCornerMode) format = format.Replace("tt", "").Trim();
        CollapsedClockText.Text = CompactClockText.Text = now.ToString(format, CultureInfo.CurrentCulture);
        _collapsedClockTimer.Interval = GetDelayToNextMinute(now);
        _collapsedClockTimer.Start();
    }

    internal static TimeSpan GetDelayToNextMinute(DateTime now) =>
        TimeSpan.FromTicks(TimeSpan.TicksPerMinute - now.Ticks % TimeSpan.TicksPerMinute) + TimeSpan.FromMilliseconds(50);

    private void OnSystemTimeChangedForCollapsedClock(object? sender, EventArgs e)
    {
        TimeZoneInfo.ClearCachedData();
        QueueCollapsedClockRefresh();
    }

    private void OnPowerModeChangedForCollapsedClock(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) QueueCollapsedClockRefresh();
    }

    private void QueueCollapsedClockRefresh()
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)(() =>
        {
            if (!_cleanedUp) RefreshCollapsedClock();
        }));
    }

    private void DisposeCollapsedClock()
    {
        SystemEvents.TimeChanged -= OnSystemTimeChangedForCollapsedClock;
        SystemEvents.PowerModeChanged -= OnPowerModeChangedForCollapsedClock;
        _collapsedClockTimer?.Stop();
        _collapsedClockTimer = null;
    }
}

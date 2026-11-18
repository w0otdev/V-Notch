using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using VNotch.Models;
using VNotch.Services;

namespace VNotch;

public partial class DebugWindow
{
    private PerformanceDebugSnapshot? _lastSnapshot;
    private bool _localizationClosed;

    private void OnLanguageChanged()
    {
        if (_localizationClosed || Dispatcher.HasShutdownStarted) return;
        if (Dispatcher.CheckAccess()) RefreshLocalization();
        else Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_localizationClosed) RefreshLocalization();
        }));
    }

    private void RefreshLocalization()
    {
        LocalizedPresentation.Apply(this);
        Title = InspectorTitleText.Text = Loc.Get("debug.title");
        ViewStateLabel.Text = Loc.Get("debug.viewState");
        DragNotchCheckBox.Content = Loc.Get("debug.moveWithMouse");
        LockStateCheckBox.Content = Loc.Get("debug.lockView");
        ResetPositionBtn.Content = Loc.Get("tray.reset");
        ProcessUsageHeader.Text = Loc.Get("debug.processUsage");
        GlobalUsageHeader.Text = Loc.Get("debug.globalUsage");
        GlobalCpuLabel.Text = Loc.Get("debug.globalCpu");
        NetworkLabel.Text = Loc.Get("debug.network");
        AlertsHeader.Text = Loc.Get("debug.alerts");
        ServiceLogsHeader.Text = Loc.Get("debug.runtimeLogs");
        CopyAllLogsBtn.Content = Loc.Get(CopyAllLogsBtn.Tag is UiScheduledTimer ? "debug.copied" : "clipboard.copy");
        CopyServiceLogsBtn.Content = Loc.Get(CopyServiceLogsBtn.Tag is UiScheduledTimer ? "debug.copied" : "debug.copyAll");
        ClearLogsBtn.Content = ClearServiceLogsBtn.Content = Loc.Get("spotlight.clear");
        CloseButton.ToolTip = Loc.Get("common.close");
        AutomationProperties.SetName(CloseButton, Loc.Get("common.close"));
        AutomationProperties.SetName(ViewStateComboBox, Loc.Get("debug.viewState"));
        AutomationProperties.SetName(ServiceCategoryFilterComboBox, Loc.Get("debug.allCategories"));
        FpsSummaryText.ToolTip = Loc.Get("debug.fpsHint");
        GlassSummaryText.ToolTip = Loc.Get("debug.glassHint");

        LocalizeChoices(ViewStateComboBox,
        [
            "debug.currentView", "debug.mediaPlayer", "nav.clipboard", "debug.timerClock",
            "debug.audioRouter", "debug.compactPill", "debug.collapsed"
        ]);
        LocalizeChoices(ServiceCategoryFilterComboBox,
        [
            "debug.allCategories", "debug.mediaFilter", "nav.audio", "debug.batteryPower",
            "spotlight.system.bluetooth", null, "settings.widget.weather", "settings.spotlight",
            "debug.subtitlesLyrics", "settings.skin.liquidglass", "debug.memoryGc"
        ]);
        RefreshServiceLogsToggle();
        ServiceLogCountText.Text = Loc.Get("debug.serviceCount", _serviceLogs.Count);

        // A language switch must refresh text even if the measured values are unchanged.
        _prevVNotchCpuThreads = null;
        _prevVNotchPrivateRam = null;
        _prevGcHeap = null;
        _prevGlobalRamAvail = null;
        _prevVNotchRam = null;
        _prevGlobalRam = null;
        _prevGpuName = null;
        _prevHealthLevel = (PerformanceHealthLevel)(-1);
        UpdateSnapshot(_lastSnapshot ?? new PerformanceDebugSnapshot());
        GlassSummaryText.Text = _prevGlassMetrics is { } glass
            ? FormatGlassMetrics(glass.Fps, glass.Latency)
            : Loc.Get("debug.glassWaiting");
    }

    private static void LocalizeChoices(ComboBox combo, string?[] keys)
    {
        for (int i = 0; i < keys.Length; i++)
            if (keys[i] is { } key && combo.Items[i] is ComboBoxItem item)
                item.Content = Loc.Get(key);
    }

    private void RefreshServiceLogsToggle() => ToggleServiceLogsBtn.Content =
        Loc.Get("debug.serviceLogs") + (ServiceLogsPanel.Visibility == Visibility.Visible ? " ◀" : " ▶");

    private static string FormatGlassMetrics(double fps, double latency) => fps > 0
        ? Loc.Get("debug.glassMetrics", fps, latency.ToString("0.0", Loc.GetCulture()))
        : Loc.Get("debug.glassIdle");
}

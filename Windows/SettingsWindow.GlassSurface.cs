using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using VNotch.Controllers;
using VNotch.Controls;
using VNotch.Models;
using VNotch.Modules;
using VNotch.Presenters;
using VNotch.Services;

namespace VNotch;

public partial class SettingsWindow
{
    #region Liquid Glass UI Support

    private void ApplyLiquidGlassSkin()
    {
        if (GlassBackdropHost == null) return;
        if (GlassBackdropHost.Visibility == Visibility.Collapsed) return;

        // Liquid Glass is a notch skin only.
        MainShell.Background = (Brush)FindResource("WindowGlow");
        GlassBackdropHost.Background = null;
        GlassBackdropHost.Visibility = Visibility.Collapsed;
        GlassTintOverlay.Visibility = Visibility.Collapsed;
        GlassDarkOverlay.Visibility = Visibility.Collapsed;
        if (GlassGrainOverlay != null) GlassGrainOverlay.Visibility = Visibility.Collapsed;

        DetachGpuRefraction();
    }

    private void DetachGpuRefraction()
    {
        if (GlassBackdropImage != null)
        {
            GlassBackdropImage.Effect = null;
            GlassBackdropImage.Width = double.NaN;
            GlassBackdropImage.Height = double.NaN;
        }
        if (GlassBackdropHost != null)
            GlassBackdropHost.Effect = null;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // WS_EX_TOOLWINDOW keeps third-party dock/animation tools (e.g. MyDockFinder)
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            int exStyle = Win32Interop.GetWindowLong(hwnd, Win32Interop.GWL_EXSTYLE);
            Win32Interop.SetWindowLong(hwnd, Win32Interop.GWL_EXSTYLE, exStyle | Win32Interop.WS_EX_TOOLWINDOW);
        }
    }

    private bool _monitorChoicesClosed;
    private bool _displaySettingsSubscribed;

    private void SubscribeDisplaySettingsChanged()
    {
        if (_displaySettingsSubscribed || _monitorChoicesClosed) return;
        _displaySettingsSubscribed = true;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnMonitorConfigurationChanged;
    }

    private void UnsubscribeDisplaySettingsChanged()
    {
        if (!_displaySettingsSubscribed) return;
        _displaySettingsSubscribed = false;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnMonitorConfigurationChanged;
    }

    private void OnMonitorConfigurationChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_monitorChoicesClosed) RefreshMonitorChoices();
        }));
    }

    private void RefreshMonitorChoices(bool preserveSelection = true)
    {
        var previous = preserveSelection ? MonitorCombo.SelectedItem as MonitorSelection.Choice : null;
        string id = previous?.Id ?? _settings.MonitorDeviceId;
        int index = previous?.Index ?? _settings.MonitorIndex;
        var choices = MonitorSelection.GetChoices().ToList();
        var selected = string.IsNullOrEmpty(id)
            ? choices.FirstOrDefault(c => c.Index == index) ?? choices.FirstOrDefault()
            : choices.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));
        if (selected == null && !string.IsNullOrEmpty(id))
        {
            selected = new MonitorSelection.Choice(id,
                Loc.Get("settings.display.disconnected"), index, null);
            choices.Add(selected);
        }
        bool wasLoading = _isLoadingSettings;
        _isLoadingSettings = true;
        try
        {
            MonitorCombo.ItemsSource = choices;
            MonitorCombo.SelectedItem = selected;
        }
        finally { _isLoadingSettings = wasLoading; }
    }

    protected override void OnClosed(EventArgs e)
    {
        CompositionTarget.Rendering -= SmoothScroll_Tick;
        _isScrollAnimating = false;
        _livePreviewDebounce?.Dispose();
        _searchDebounce?.Dispose();
        _updatePresenter.Dispose();
        Loc.ReleaseTranslationIndex();
        _monitorChoicesClosed = true;
        UnsubscribeDisplaySettingsChanged();
        base.OnClosed(e);
        DetachGpuRefraction();
    }

    #endregion
}

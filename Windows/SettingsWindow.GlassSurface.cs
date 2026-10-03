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
using NAudio.CoreAudioApi;
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
        if (GlassBackdropHost.Visibility == Visibility.Collapsed && _liquidGlass == null) return;

        // Liquid Glass is a notch skin only. Keeping a second full-window
        MainShell.Background = (Brush)FindResource("WindowGlow");
        GlassBackdropHost.Background = null;
        GlassBackdropHost.Visibility = Visibility.Collapsed;
        GlassTintOverlay.Visibility = Visibility.Collapsed;
        GlassDarkOverlay.Visibility = Visibility.Collapsed;
        if (GlassGrainOverlay != null) GlassGrainOverlay.Visibility = Visibility.Collapsed;

        CompositionTarget.Rendering -= OnGlassRegionRendering;
        _liquidGlass?.ClearLiveRegion();
        _liquidGlass?.SetAnimating(false);
        _liquidGlass?.Stop();
        DetachGpuRefraction();
    }

    private (int Width, int Height) GetGlassSurfaceEnvelope()
    {
        double wDip = ActualWidth > 0 ? ActualWidth : Width;
        double hDip = ActualHeight > 0 ? ActualHeight : Height;
        if (!double.IsFinite(wDip) || wDip <= 0) wDip = 860;
        if (!double.IsFinite(hDip) || hDip <= 0) hDip = 620;

        double dpiScale = GetGlassDpiScale();
        return ((int)Math.Ceiling(wDip * dpiScale), (int)Math.Ceiling(hDip * dpiScale));
    }

    private bool _glassRebuildQueued;

    private void QueueGlassRendererRebuildIfTooSmall()
    {
        var lg = _liquidGlass;
        if (lg == null || _glassRebuildQueued) return;

        var (needW, needH) = GetGlassSurfaceEnvelope();
        if (needW <= lg.MaxRegionWidth && needH <= lg.MaxRegionHeight) return;

        _glassRebuildQueued = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _glassRebuildQueued = false;
            if (_liquidGlass == null) return;
            CompositionTarget.Rendering -= OnGlassRegionRendering;
            _liquidGlass.ClearLiveRegion();
            _liquidGlass.Stop();
            DetachGpuRefraction();
            _liquidGlass = null;
            ApplyLiquidGlassSkin();
        }));
    }

    private void OnGlassRegionRendering(object? sender, EventArgs e)
    {
        _liquidGlass?.SetLiveRegion(GetGlassCaptureRegion());
    }

    private LiquidGlassController.CaptureRegion? GetGlassCaptureRegion()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || MainShell == null || GlassBackdropHost == null) return null;

        double shellW = GlassBackdropHost.ActualWidth;
        double shellH = GlassBackdropHost.ActualHeight;
        if (shellW <= 0 || shellH <= 0) return null;

        double dpiScale = GetGlassDpiScale();
        UpdateGlassDpiIfChanged(dpiScale);

        int physW = (int)Math.Round(shellW * dpiScale);
        int physH = (int)Math.Round(shellH * dpiScale);

        try
        {
            // Project both corners so the open/close ShellScale animation is baked
            var tl = GlassBackdropHost.PointToScreen(new Point(0, 0));
            var br = GlassBackdropHost.PointToScreen(new Point(shellW, shellH));

            int physLeft = (int)Math.Round(tl.X, MidpointRounding.AwayFromZero);
            int physTop = (int)Math.Round(tl.Y, MidpointRounding.AwayFromZero);
            int scaledW = (int)Math.Round(br.X, MidpointRounding.AwayFromZero) - physLeft;
            int scaledH = (int)Math.Round(br.Y, MidpointRounding.AwayFromZero) - physTop;
            if (scaledW > 1) physW = scaledW;
            if (scaledH > 1) physH = scaledH;

            // Carry the fractional screen position so the present can compensate
            double subX = tl.X - Math.Round(tl.X);
            double subY = tl.Y - Math.Round(tl.Y);

            if (physTop < 0) { physH += physTop; physTop = 0; }
            if (physLeft < 0) { physW += physLeft; physLeft = 0; }
            if (physW <= 1 || physH <= 1) return null;

            return new LiquidGlassController.CaptureRegion(
                physLeft, physTop, physW, physH,
                MainShell.CornerRadius.TopLeft,
                MainShell.CornerRadius.BottomLeft,
                subX, subY);
        }
        catch
        {
            return null;
        }
    }

    private void UpdateGlassDpiIfChanged(double dpiScale)
    {
        if (Math.Abs(dpiScale - _lastAppliedDpiScale) <= 0.01) return;

        _lastAppliedDpiScale = dpiScale;
        QueueGlassRendererRebuildIfTooSmall();
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

    private double GetGlassDpiScale()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return 1.0;
        uint dpi = Win32Interop.GetDpiForWindow(hwnd);
        return dpi / 96.0;
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
        _updatePresenter.Dispose();
        _monitorChoicesClosed = true;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnMonitorConfigurationChanged;
        base.OnClosed(e);
        CompositionTarget.Rendering -= OnGlassRegionRendering;
        _liquidGlass?.Stop();
        DetachGpuRefraction();
        _liquidGlass = null;
    }

    #endregion
}

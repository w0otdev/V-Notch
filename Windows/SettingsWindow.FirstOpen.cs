using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Threading;
using VNotch.Models;
using VNotch.Services;

namespace VNotch;

public partial class SettingsWindow
{
    private enum FirstShowState
    {
        ReadyForRealShow,
        StandbyPrewarm,
        Shown
    }

    private FirstShowState _firstShowState;
    private bool _backgroundInitializationStarted;

    private bool IsDeferringBackgroundInitialization => _firstShowState == FirstShowState.StandbyPrewarm;

    protected override AutomationPeer OnCreateAutomationPeer() =>
        new SettingsWindowAutomationPeer(this);

    private sealed class SettingsWindowAutomationPeer(SettingsWindow owner) : WindowAutomationPeer(owner)
    {
        protected override List<AutomationPeer>? GetChildrenCore() => null;
    }

    private bool _preparingForFirstShow;
    private double _prewarmOpacity;
    private bool _prewarmShowActivated;

    internal async Task PrepareForFirstShowAsync()
    {
        Dispatcher.VerifyAccess();
        if (_monitorChoicesClosed || _firstShowState != FirstShowState.StandbyPrewarm) return;
        _preparingForFirstShow = true;
        _prewarmOpacity = Opacity;
        _prewarmShowActivated = ShowActivated;
        _firstShowState = FirstShowState.StandbyPrewarm;
        try
        {
            // A hidden Measure does not realize all WPF templates. Attach the
            // content to its native surface once without exposing or activating it.
            ShowActivated = false;
            Opacity = 0;
            MainShell.Opacity = 1;
            SettingsHeader.Opacity = 1;
            NavPanel.Opacity = 1;
            Show();
            UpdateLayout();
            await Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.Render);
        }
        finally
        {
            if (_firstShowState == FirstShowState.StandbyPrewarm && !_monitorChoicesClosed)
            {
                if (IsVisible) Hide();
                RestorePrewarmPresentation();
            }
            _preparingForFirstShow = false;
        }
    }

    private void RestorePrewarmPresentation()
    {
        MainShell.Opacity = 0;
        SettingsHeader.Opacity = 0;
        NavPanel.Opacity = 0;
        Opacity = _prewarmOpacity;
        ShowActivated = _prewarmShowActivated;
    }

    internal void ActivatePreparedWindow(NotchSettings settings, bool snapshotMatches, bool isHotkeyRegistered)
    {
        Dispatcher.VerifyAccess();
        if (_preparingForFirstShow) RestorePrewarmPresentation();
        if (!snapshotMatches)
        {
            RefreshPreparedSettings(settings, isHotkeyRegistered);
        }
        else
        {
            SetSpotlightHotkeyStatus(isHotkeyRegistered);
            RefreshMonitorChoices(preserveSelection: true);
        }

        SubscribeDisplaySettingsChanged();
        _firstShowState = FirstShowState.ReadyForRealShow;
        if (IsVisible) CompleteFirstRealShow();
    }

    internal void RefreshPreparedSettings(NotchSettings settings, bool isHotkeyRegistered)
    {
        Dispatcher.VerifyAccess();
        _settings = settings.Clone();
        _editSession.Reset(settings);
        _isSpotlightHotkeyRegistered = isHotkeyRegistered;
        LoadSettings();
        _editSession.Preview(ReadSettingsFromUi());
        _settings = _editSession.Applied;
    }

    private void OnFirstShowVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible || _firstShowState != FirstShowState.ReadyForRealShow) return;
        if (IsLoaded)
        {
            CompleteFirstRealShow();
        }
        else
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, CompleteFirstRealShow);
        }
    }

    private void CompleteFirstRealShow()
    {
        if (!IsVisible || _firstShowState != FirstShowState.ReadyForRealShow) return;
        _firstShowState = FirstShowState.Shown;
        var firstOpenProbe = Stopwatch.StartNew();
        PlayEntranceAnimation();
        ShowCpp26BannerIfNeeded();
        FirstOpenProbe?.Invoke($"DEBUG-FIRST-OPEN Settings entrance {firstOpenProbe.Elapsed.TotalMilliseconds:F1} ms");
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
        {
            if (_isClosing || Dispatcher.HasShutdownStarted) return;
            EnsureBackgroundInitializationStarted();
            LoadVisualizerAudioDevices().SafeFireAndForget("SETTINGS-AUDIO-DEVICES");
        });
    }

    private void EnsureBackgroundInitializationStarted()
    {
        if (_backgroundInitializationStarted || IsDeferringBackgroundInitialization) return;
        _backgroundInitializationStarted = true;

        LoadPerformancePresetsUi();
        LoadCameraDevices().SafeFireAndForget("SETTINGS-CAMERA-DEVICES");
        if (!_translationHardwareStarted)
        {
            _translationHardwareStarted = true;
            DetectTranslationHardwareAsync().SafeFireAndForget("SETTINGS-TRANSLATION-HW");
        }
        if (_settings.AutoCheckUpdates)
        {
            _updatePresenter.CheckForUpdatesAsync().SafeFireAndForget("SETTINGS-UPDATE-CHECK");
        }
    }
}

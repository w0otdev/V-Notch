using System.Diagnostics;
using System.Windows;
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

    internal void PrepareForFirstShow()
    {
        Dispatcher.VerifyAccess();
        double opacity = Opacity;
        bool showActivated = ShowActivated;
        _firstShowState = FirstShowState.StandbyPrewarm;
        try
        {
            // A hidden Measure does not realize all WPF templates. Attach the
            // content to its native surface once without exposing or activating it.
            ShowActivated = false;
            Opacity = 0;
            Show();
            UpdateLayout();
            Hide();
        }
        finally
        {
            if (IsVisible) Hide();
            Opacity = opacity;
            ShowActivated = showActivated;
        }
    }

    internal void ActivatePreparedWindow(NotchSettings settings, bool snapshotMatches, bool isHotkeyRegistered)
    {
        Dispatcher.VerifyAccess();
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
    }

    internal void RefreshPreparedSettings(NotchSettings settings, bool isHotkeyRegistered)
    {
        Dispatcher.VerifyAccess();
        _settings = settings.Clone();
        _originalSettings = settings.Clone();
        _appliedSettings = settings.Clone();
        _isSpotlightHotkeyRegistered = isHotkeyRegistered;
        LoadSettings();
        _appliedSettings = ReadSettingsFromUi();
        _settings = _appliedSettings.Clone();
    }

    private void OnFirstShowVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible || _firstShowState != FirstShowState.ReadyForRealShow) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, CompleteFirstRealShow);
    }

    private void CompleteFirstRealShow()
    {
        if (!IsVisible || _firstShowState != FirstShowState.ReadyForRealShow) return;
        _firstShowState = FirstShowState.Shown;
        EnsureBackgroundInitializationStarted();

        var firstOpenProbe = Stopwatch.StartNew();
        PlayEntranceAnimation();
        FirstOpenProbe?.Invoke($"DEBUG-FIRST-OPEN Settings entrance {firstOpenProbe.Elapsed.TotalMilliseconds:F1} ms");
        firstOpenProbe.Restart();
        LoadVisualizerAudioDevices().SafeFireAndForget("SETTINGS-VIS-AUDIO");
        FirstOpenProbe?.Invoke($"DEBUG-FIRST-OPEN Settings audio load dispatch {firstOpenProbe.Elapsed.TotalMilliseconds:F1} ms");
    }

    private void EnsureBackgroundInitializationStarted()
    {
        if (_backgroundInitializationStarted || IsDeferringBackgroundInitialization) return;
        _backgroundInitializationStarted = true;

        LoadCameraDevices().SafeFireAndForget("SETTINGS-CAMERA-DEVICES");
        if (!_translationHardwareStarted)
        {
            _translationHardwareStarted = true;
            DetectTranslationHardwareAsync().SafeFireAndForget("SETTINGS-TRANSLATION-HARDWARE");
        }
        if (_settings.AutoCheckUpdates)
        {
            _updatePresenter.CheckForUpdatesAsync().SafeFireAndForget("SETTINGS-UPDATE-CHECK");
        }
    }
}

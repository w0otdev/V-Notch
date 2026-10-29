using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
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
            MainShell.Opacity = 1;
            SettingsHeader.Opacity = 1;
            NavPanel.Opacity = 1;
            Show();
            UpdateLayout();
            Dispatcher.Invoke(static () => { }, DispatcherPriority.Render, CancellationToken.None);
            Hide();
        }
        finally
        {
            if (IsVisible) Hide();
            MainShell.Opacity = 0;
            SettingsHeader.Opacity = 0;
            NavPanel.Opacity = 0;
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

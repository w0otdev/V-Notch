using System.Windows;
using VNotch.Models;
using VNotch.Services;

namespace VNotch;

public partial class SettingsWindow
{
    private bool _preparingFirstShow;
    private bool _firstRealShowStarted;

    internal void PrepareForFirstShow()
    {
        Dispatcher.VerifyAccess();
        double opacity = Opacity;
        bool showActivated = ShowActivated;
        _preparingFirstShow = true;
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
            _preparingFirstShow = false;
        }
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

    private void StartDeferredBackgroundInitialization()
    {
        if (!_deferBackgroundInitialization) return;
        _deferBackgroundInitialization = false;
        LoadCameraDevices().SafeFireAndForget("SETTINGS-CAMERA-DEVICES");
        if (!_translationHardwareStarted)
        {
            _translationHardwareStarted = true;
            DetectTranslationHardwareAsync().SafeFireAndForget("SETTINGS-TRANSLATION-HARDWARE");
        }
        if (_settings.AutoCheckUpdates)
            _updatePresenter.CheckForUpdatesAsync().SafeFireAndForget("SETTINGS-UPDATE-CHECK");
    }
}

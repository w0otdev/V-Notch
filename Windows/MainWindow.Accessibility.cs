using System.Windows;
using System.Windows.Automation;
using VNotch.Services;

namespace VNotch;

public partial class MainWindow
{
    private bool _focusNotchAfterExpand;

    private void OpenNotchForKeyboard_Click(object sender, RoutedEventArgs e)
    {
        if (_cleanedUp || _isGreetingActive || _isAnimating) return;
        if (!_isNotchVisible) ToggleNotch_Click(sender, e);
        if (_isExpanded) FocusNotchForKeyboard();
        else
        {
            _focusNotchAfterExpand = true;
            ExpandNotch();
        }
    }

    private void FocusNotchForKeyboard()
    {
        _focusNotchAfterExpand = false;
        EnableKeyboardInput();
        HomeIconButton.Focus();
    }

    private void RefreshAccessibleNames()
    {
        foreach (var (element, key) in new (DependencyObject, string)[]
        {
            (SettingsButton, "tooltip.settings"), (BatterySection, "settings.batteryDevice.system"),
            (HomeIconButton, "nav.media"), (ClipboardIconButton, "nav.clipboard"), (CameraIconButton, "nav.camera"),
            (TimerIconButton, "nav.timer"), (AudioIconButton, "nav.audio"),
            (CameraSection, "settings.camera"), (UpdateNotificationButton, "tooltip.downloadUpdate"),
            (MediaPlayback.PrevButton, "media.previous"), (InlinePrevButton, "media.previous"),
            (MediaPlayback.NextButton, "media.next"), (InlineNextButton, "media.next"),
            (VolumeBarContainer, "media.volume"), (VolumeMuteButton, "media.mute"),
            (CountdownWidget.CountdownResetBtn, "timer.reset"), (CountdownWidget.CountdownStartBtn, "timer.start"),
            (CountdownWidget.CountdownPlusBtn, "timer.increase"), (CountdownWidget.CountdownMinusBtn, "timer.decrease"),
            (CountdownRestartBtn, "timer.restart"), (CountdownDismissBtn, "timer.dismiss")
        }) AutomationProperties.SetName(element, Loc.Get(key));
        RefreshPlaybackAccessibleNames();
    }

    private void RefreshPlaybackAccessibleNames()
    {
        string name = Loc.Get(_isPlaying ? "media.pause" : "media.play");
        AutomationProperties.SetName(MediaPlayback.PlayPauseButton, name);
        AutomationProperties.SetName(InlinePlayPauseButton, name);
    }

    private void AccessibleVolume_ValueRequested(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        ++_volumeInteractionVersion;
        _currentVolume = (float)e.NewValue;
        SetVolumeFill(VolumeBarScale, _currentVolume);
        UpdateVolumeIcon(_currentVolume, false);
        float volume = _currentVolume;
        _mediaVolumeWrites.Post("accessible-volume", () => _mediaService.TrySetCurrentSessionVolume(volume));
    }
}

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
            (HomeIconButton, "nav.media"), (FileShelfIconButton, "nav.shelf"),
            (TimerIconButton, "nav.timer"), (AudioIconButton, "nav.audio"),
            (CameraSection, "settings.camera"), (UpdateNotificationButton, "tooltip.downloadUpdate"),
            (PrevButton, "media.previous"), (InlinePrevButton, "media.previous"),
            (NextButton, "media.next"), (InlineNextButton, "media.next"),
            (VolumeBarContainer, "media.volume"), (VolumeMuteButton, "media.mute"),
            (CountdownResetBtn, "timer.reset"), (CountdownStartBtn, "timer.start"),
            (CountdownPlusBtn, "timer.increase"), (CountdownMinusBtn, "timer.decrease"),
            (CountdownRestartBtn, "timer.restart"), (CountdownDismissBtn, "timer.dismiss")
        }) AutomationProperties.SetName(element, Loc.Get(key));
        RefreshPlaybackAccessibleNames();
    }

    private void RefreshPlaybackAccessibleNames()
    {
        string name = Loc.Get(_isPlaying ? "media.pause" : "media.play");
        AutomationProperties.SetName(PlayPauseButton, name);
        AutomationProperties.SetName(InlinePlayPauseButton, name);
    }

    private void AccessibleVolume_ValueRequested(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        ++_volumeInteractionVersion;
        _currentVolume = (float)e.NewValue;
        VolumeBarScale.ScaleX = _currentVolume;
        UpdateVolumeIcon(_currentVolume, false);
        float volume = _currentVolume;
        _audioWrites.Post("accessible-volume", () => _mediaService.TrySetCurrentSessionVolume(volume));
    }
}

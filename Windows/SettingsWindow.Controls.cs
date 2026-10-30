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
    private readonly Func<string?>? _clipboardHotkeyError;
    private readonly Func<bool>? _invisibleHotkeyRegistered;
    private void InteractionSounds_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings) return;
        UpdateInteractionSoundControls();
        InteractionSounds.Configure(InteractionSoundsCheck.IsChecked == true, InteractionSoundVolumeSlider.Value / 100);
        PushLivePreview();
    }

    private void UpdateInteractionSoundControls()
    {
        if (InteractionSoundVolumeSlider == null) return;
        bool enabled = InteractionSoundsCheck.IsChecked == true;
        InteractionSoundVolumeSlider.IsEnabled = enabled;
        InteractionSoundVolumeSlider.Opacity = enabled ? 1 : .4;
    }

    private void InteractionSoundVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isLoadingSettings || InteractionSoundsCheck == null) return;
        InteractionSounds.Configure(InteractionSoundsCheck.IsChecked == true, e.NewValue / 100);
        PushLivePreview();
    }

    private void InteractionSoundVolume_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) InteractionSounds.Preview();
    }

    private void InteractionSoundVolume_KeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown)
            InteractionSounds.Preview();
    }
    private void ClipboardSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings || _isSyncingCameraVisibility) return;
        _settings.HideCamera = HideCameraCheck.IsChecked == true;
        PopulateNavTabsSettings();
        PushLivePreview();
    }

    private void ClipboardHotkey_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings) return;
        string gesture = ClipboardHotkeyBox.Text.Trim();
        if (gesture.Length > 0 && !ClipboardHotkeyController.TryParse(gesture, out _, out _))
        {
            ClipboardHotkeyHint.Text = Loc.Get("clipboard.invalidHotkey");
            return;
        }
        ApplyPreview(ReadSettingsFromUi());
        ClipboardHotkeyHint.Text = _clipboardHotkeyError?.Invoke() ?? Loc.Get("settings.clipboardHotkey.hint");
    }

    #region Slider Value Changed Handlers

    private void WidthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (WidthValue != null)
            WidthValue.Text = ((int)e.NewValue).ToString();
        PushLivePreview();
    }

    private void DynamicIslandWidthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (DynamicIslandWidthValue != null)
            DynamicIslandWidthValue.Text = ((int)e.NewValue).ToString();
        PushLivePreview();
    }

    private void DynamicIslandHeightSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (DynamicIslandHeightValue != null)
            DynamicIslandHeightValue.Text = ((int)e.NewValue).ToString();
        PushLivePreview();
    }

    private void HeightSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (HeightValue != null)
            HeightValue.Text = ((int)e.NewValue).ToString();
        PushLivePreview();
    }

    private void RadiusSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (RadiusValue != null)
            RadiusValue.Text = ((int)e.NewValue).ToString();
        PushLivePreview();
    }

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (OpacityValue != null)
            OpacityValue.Text = ((int)e.NewValue).ToString();
        PushLivePreview();
    }

    private void BlurBrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (BlurBrightnessValue != null)
            BlurBrightnessValue.Text = ((int)e.NewValue).ToString();
        PushLivePreview();
    }

    private void BlurDarkOverlaySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (BlurDarkOverlayValue != null)
            BlurDarkOverlayValue.Text = ((int)e.NewValue).ToString();
        PushLivePreview();
    }

    private void AutoAnimationFps_Changed(object sender, RoutedEventArgs e)
    {
        if (AnimationFpsSlider != null)
            AnimationFpsSlider.IsEnabled = AutoAnimationFpsCheck.IsChecked != true;
        if (_isLoadingSettings) return;
        PushLivePreview();
    }

    private void AnimationFpsSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (AnimationFpsValue != null)
            AnimationFpsValue.Text = ((int)Math.Round(e.NewValue)).ToString();
        PushLivePreview();
    }

    private void EnableSpotifyLyricsCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings) return;
        bool enabled = EnableSpotifyLyricsCheck.IsChecked ?? true;
        UpdateLyricsDependentControls(enabled, animate: true);
        UpdateSpotifyCanvasDependentControls(animate: true);
        PushLivePreview();
    }

    private void BrightenDarkLyricsBackgroundCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings) return;
        PushLivePreview();
    }

    private void EnableSpotifyCanvasCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings || _isUpdatingSpotifyCanvasOptIn) return;
        SetSpotifyCanvasOptIn(EnableSpotifyCanvasCheck.IsChecked == true, ConfirmSpotifyCanvasOptIn);
    }

    private bool _isUpdatingSpotifyCanvasOptIn;

    private bool ConfirmSpotifyCanvasOptIn() =>
        VNotch.Windows.ConfirmationDialog.Show(
            this,
            Loc.Get("settings.enableSpotifyCanvas.hint"),
            new VNotch.Windows.ConfirmationDialog.DialogOptions(
                Title: Loc.Get("settings.enableSpotifyCanvas"),
                BadgeText: Loc.Get(LocKeyBadgeExperimental),
                ConfirmText: Loc.Get("dialog.confirm"),
                CancelText: Loc.Get("dialog.cancel"),
                Icon: VNotch.Windows.ConfirmationDialog.DialogIcon.Warning,
                Style: VNotch.Windows.ConfirmationDialog.DialogStyle.Normal,
                DetailText: "https://www.spotify.com/us/legal/user-guidelines/"));

    internal bool SetSpotifyCanvasOptIn(bool enabled, Func<bool> confirm)
    {
        bool accepted = enabled && SpotifyCanvasConsent.TryEnable(_settings, confirm);
        if (!accepted) SpotifyCanvasConsent.Revoke(_settings);
        _isUpdatingSpotifyCanvasOptIn = true;
        try
        {
            EnableSpotifyCanvasCheck.IsChecked = accepted;
            if (_privacyOptions.TryGetValue("canvas", out var option)) option.Check.IsChecked = accepted;
        }
        finally { _isUpdatingSpotifyCanvasOptIn = false; }
        UpdateSpotifyCanvasDependentControls(animate: true);
        // Consent must take effect before sign-in, and revocation must cancel immediately.
        ApplyPreview(ReadSettingsFromUi());
        return accepted;
    }

    private void SpotifyCanvasBrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        PushLivePreview();
    }

    private void SpotifyConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (!NetworkPrivacy.Allows(ReadSettingsFromUi(), NetworkFeature.Canvas)) return;
        var loginWindow = new SpotifyLoginWindow
        {
            Owner = this
        };

        if (loginWindow.ShowDialog() == true && !string.IsNullOrWhiteSpace(loginWindow.SpotifySpDc))
        {
            _settings.SpotifySpDc = loginWindow.SpotifySpDc;
            UpdateSpotifyCanvasConnectionStatus();
            PushLivePreview();
        }
    }

    private void SpotifyDisconnectButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.SpotifySpDc = "";
        UpdateSpotifyCanvasConnectionStatus();
        PushLivePreview();
    }

    private void UpdateSpotifyCanvasDependentControls(bool animate = false)
    {
        if (SpotifyCanvasAccountPanel == null || EnableSpotifyCanvasCheck == null)
            return;

        bool lyricsEnabled = EnableSpotifyLyricsCheck?.IsChecked ?? true;
        bool canvasEnabled = EnableSpotifyCanvasCheck.IsChecked == true && SpotifyCanvasConsent.HasAccepted(_settings);

        AnimateDependentElement(EnableSpotifyCanvasCheck, lyricsEnabled, 0.45, animate);
        AnimateDependentElement(EnableSpotifyCanvasHint, lyricsEnabled, 0.45, animate);
        AnimateDependentElement(SpotifyCanvasAccountPanel, lyricsEnabled && canvasEnabled, 0.45, animate);
        AnimateDependentElement(SpotifyCanvasBrightnessSlider, lyricsEnabled && canvasEnabled, 0.45, animate);
    }

    private void UpdateSpotifyCanvasConnectionStatus()
    {
        if (SpotifyCanvasAccountStatus == null || SpotifyConnectButton == null || SpotifyDisconnectButton == null)
            return;

        bool connected = !string.IsNullOrWhiteSpace(_settings.SpotifySpDc);
        SpotifyCanvasAccountStatus.Text = connected
            ? Loc.Get("settings.spotifyCanvas.connected")
            : Loc.Get("settings.spotifyCanvas.notConnected");
        SpotifyCanvasAccountStatus.Foreground = new SolidColorBrush(
            connected ? Color.FromRgb(74, 222, 128) : Color.FromRgb(234, 179, 8));
        SpotifyConnectButton.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
        SpotifyDisconnectButton.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
    }

    private void EnableYouTubeSubtitlesCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings) return;
        UpdateYouTubeSubtitlesDependentControls(EnableYouTubeSubtitlesCheck.IsChecked ?? true, animate: true);
        PushLivePreview();
    }

    private void IgnoreYouTubeAutoSubtitlesCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings) return;
        _settings.IgnoreYouTubeAutoSubtitles = IgnoreYouTubeAutoSubtitlesCheck.IsChecked ?? false;
        PushLivePreview();
    }



    private void DynamicIslandModeCheck_Changed(object sender, RoutedEventArgs e)
    {
        UpdateDynamicIslandDependentControls(DynamicIslandModeCheck.IsChecked ?? false, animate: true);
        if (_isLoadingSettings) return;
        PushLivePreview();
    }

    private void UpdateDynamicIslandDependentControls(bool islandEnabled, bool animate = false)
    {
        AnimateDependentElement(DynamicIslandWidthSlider, islandEnabled, 0.4, animate);
        AnimateDependentElement(DynamicIslandHeightSlider, islandEnabled, 0.4, animate);
        UpdateLiquidGlassAvailability(animate);
    }

    private void UpdateLyricsDependentControls(bool lyricsEnabled, bool animate = false)
    {
        AnimateDependentElement(BrightenDarkLyricsBackgroundCheck, lyricsEnabled, 0.45, animate);
        AnimateDependentElement(DarkOverlayLabel, lyricsEnabled, 0.45, animate);
        AnimateDependentElement(DarkOverlayHint, lyricsEnabled, 0.45, animate);
        AnimateDependentElement(BlurDarkOverlaySlider, lyricsEnabled, 0.45, animate);
    }

    private void UpdateYouTubeSubtitlesDependentControls(bool subtitlesEnabled, bool animate = false)
    {
        AnimateDependentElement(IgnoreYouTubeAutoSubtitlesCheck, subtitlesEnabled, 0.45, animate);
        AnimateDependentElement(IgnoreYouTubeAutoSubtitlesHint, subtitlesEnabled, 0.45, animate);
        AnimateDependentElement(SubtitlePriorityRow, subtitlesEnabled, 0.45, animate);
    }

    private void PerformanceSetting_Changed(object sender, RoutedEventArgs e)
    {
        bool blurEnabled = EnableBlurEffectsCheck.IsChecked ?? true;
        UpdatePerformanceDependentControls(blurEnabled, animate: true);
        if (_isLoadingSettings) return;
        PushLivePreview();
    }

    private void UpdatePerformanceDependentControls(bool blurEnabled, bool animate = false)
    {
        AnimateDependentElement(SubjectBlurRow, blurEnabled, 0.45, animate);
        AnimateDependentElement(BlurBrightnessSlider, blurEnabled, 0.45, animate);
    }

    private void HoverDelaySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (HoverDelayValue != null)
            HoverDelayValue.Text = ((int)e.NewValue).ToString();
        PushLivePreview();
    }

    private void HoverExpandCheck_Changed(object sender, RoutedEventArgs e)
    {
        bool enabled = HoverExpandCheck.IsChecked ?? false;
        AnimateDependentElement(HoverDelaySlider, enabled, 0.4, animate: true);
    }

    private void IdleAutoHideCheck_Changed(object sender, RoutedEventArgs e)
    {
        bool enabled = IdleAutoHideCheck.IsChecked ?? false;
        AnimateDependentElement(IdleAutoHideDelaySlider, enabled, 0.4, animate: true);
    }

    private void LanguageCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_isLoadingSettings) return;
        if (LanguageCombo.SelectedItem is System.Windows.Controls.ComboBoxItem item && item.Tag is string lang)
        {
            if (lang == _settings.Language) return;

            _settings.Language = lang;
            Loc.SetLanguage(lang);
            _settingsAppService.ApplyAsync(_settings, _translationDownload?.Token ?? CancellationToken.None).SafeFireAndForget("SETTINGS-LANG");
            _editSession.Commit(_settings);
            AnimateLocalizationChange();
            SettingsChanged?.Invoke(this, _settings);
        }
    }

    private void PopulateWidgetCombo()
    {
        WidgetCombo.Items.Clear();
        WidgetCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = Loc.Get("settings.widget.calendar"), Tag = "calendar" });
        WidgetCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = Loc.Get("settings.widget.clock"), Tag = WidgetClock });
        WidgetCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = Loc.Get("settings.widget.wordclock"), Tag = WidgetWordClock });
        WidgetCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = Loc.Get("settings.widget.digitalclock"), Tag = "digitalclock" });
        WidgetCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = Loc.Get("settings.widget.weather"), Tag = WidgetWeather });
        WidgetCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = Loc.Get("settings.widget.sysmon"), Tag = WidgetSysMon });
        WidgetCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = Loc.Get("settings.widget.none"), Tag = "none" });
        WidgetCombo.SelectedIndex = _settings.ExpandedWidget switch
        {
            WidgetClock => 1,
            WidgetWordClock => 2,
            "digitalclock" => 3,
            WidgetWeather => 4,
            WidgetSysMon => 5,
            "none" => 6,
            _ => 0
        };
    }

    private void RepopulateWidgetComboPreservingSelection()
    {
        if (WidgetCombo == null) return;

        bool wasLoading = _isLoadingSettings;
        _isLoadingSettings = true;
        PopulateWidgetCombo();
        _isLoadingSettings = wasLoading;
    }

    private void WidgetCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_isLoadingSettings) return;
        if (WidgetCombo.SelectedItem is System.Windows.Controls.ComboBoxItem item && item.Tag is string widget)
        {
            if (widget == _settings.ExpandedWidget) return;

            _settings.ExpandedWidget = widget;
            PushLivePreview();
        }
    }



    private void PopulateClockPageStyleCombo()
    {
        if (ClockPageStyleCombo == null) return;
        ClockPageStyleCombo.Items.Clear();
        ClockPageStyleCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = Loc.Get("settings.clockPageStyle.analog"), Tag = "analog" });
        ClockPageStyleCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = Loc.Get("settings.clockPageStyle.digital"), Tag = "digital" });
        ClockPageStyleCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = Loc.Get("settings.clockPageStyle.wordclock"), Tag = WidgetWordClock });

        ClockPageStyleCombo.SelectedIndex = (_settings.ClockPageStyle ?? "analog").ToLowerInvariant() switch
        {
            "digital" => 1,
            WidgetWordClock => 2,
            _ => 0
        };
    }

    private void RepopulateClockPageStyleComboPreservingSelection()
    {
        if (ClockPageStyleCombo == null) return;
        bool wasLoading = _isLoadingSettings;
        _isLoadingSettings = true;
        PopulateClockPageStyleCombo();
        _isLoadingSettings = wasLoading;
    }

    private void ClockPageStyleCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_isLoadingSettings) return;
        if (ClockPageStyleCombo.SelectedItem is System.Windows.Controls.ComboBoxItem item && item.Tag is string style)
        {
            if (style == _settings.ClockPageStyle) return;
            _settings.ClockPageStyle = style;
            PushLivePreview();
        }
    }



    private void ResetTabOrderButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.NavTabOrder = DefaultNavTabs;
        _settings.VisibleNavTabs = DefaultNavTabs;
        PopulateNavTabsSettings();
        ApplySettingsFromUi(persist: true);
    }



    private void EnableWeatherCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings) return;
        bool enabled = EnableWeatherCheck.IsChecked ?? false;
        UpdateWeatherDependentControls(enabled, animate: true);
        PushLivePreview();
    }

    private void UpdateWeatherDependentControls(bool enabled, bool animate = false)
    {
        AnimateDependentElement(ManualCityLabel, enabled, 0.45, animate);
        AnimateDependentElement(ManualCityHint, enabled, 0.45, animate);
        AnimateDependentElement(ManualCityTextBox, enabled, 0.45, animate);
    }

    private void YouTubeApiCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings) return;
        bool enabled = YouTubeApiCheck.IsChecked ?? false;
        AnimateCollapsibleRow(YouTubeApiKeyRow, enabled, animate: true);
    }

    private void YouTubeApiKeyPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings) return;
        if (YouTubeApiKeyTextBox.Visibility == Visibility.Collapsed)
            YouTubeApiKeyTextBox.Text = YouTubeApiKeyPasswordBox.Password;
        UpdateYouTubeApiKeyStatus();
    }

    private void YouTubeApiKeyTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_isLoadingSettings) return;
        if (YouTubeApiKeyTextBox.Visibility == Visibility.Visible)
            YouTubeApiKeyPasswordBox.Password = YouTubeApiKeyTextBox.Text;
        UpdateYouTubeApiKeyStatus();
    }

    private void ProcessPriorityCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_isLoadingSettings) return;
        if (ProcessPriorityCombo.SelectedItem is System.Windows.Controls.ComboBoxItem item && item.Tag is string tag)
        {
            _settings.ProcessPriority = ProcessPriorityPolicy.Normalize(tag);
            ProcessPriorityPolicy.Apply(tag);

            if (!string.Equals(_settings.ProcessPriority, _editSession.Baseline.ProcessPriority, StringComparison.OrdinalIgnoreCase))
            {
                ShowRestartBanner();
            }
        }
    }

    private void GpuPreferenceCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_isLoadingSettings) return;
        if (GpuPreferenceCombo.SelectedItem is System.Windows.Controls.ComboBoxItem item && item.Tag is string tag && int.TryParse(tag, out int val))
        {
            _settings.GpuPreference = val;
            ApplyEffectiveGpuPreference();
        }
    }

    private void ExportSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "V-Notch Settings (*.vns)|*.vns|All Files (*.*)|*.*",
                DefaultExt = ".vns",
                FileName = $"VNotch-Settings-{DateTime.Now:yyyyMMdd-HHmmss}.vns",
                Title = Loc.Get("settings.exportSettings")
            };

            if (dialog.ShowDialog(this) == true)
            {
                NotchSettings snapshot = ReadSettingsFromUi();
                _settingsAppService.Export(dialog.FileName, snapshot);

                if (BackupStatusText != null)
                {
                    BackupStatusText.Text = Loc.Get("settings.export.success");
                    BackupStatusText.Foreground = BackupSuccessBrush;
                    BackupStatusText.Visibility = Visibility.Visible;

                    var timer = new UiScheduledTimer { Interval = TimeSpan.FromSeconds(3) };
                    timer.Tick += (s, args) =>
                    {
                        timer.Stop();
                        timer.Dispose();
                        BackupStatusText.Visibility = Visibility.Collapsed;
                    };
                    timer.Start();
                }
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("SETTINGS-EXPORT", ex, "Failed to export settings");
            MessageBox.Show(
                ex.Message,
                Loc.Get("error.title"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async void ImportSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "V-Notch Settings (*.vns;*.json)|*.vns;*.json|V-Notch Settings (*.vns)|*.vns|JSON Files (*.json)|*.json|All Files (*.*)|*.*",
                DefaultExt = ".vns",
                Title = Loc.Get("settings.importSettings")
            };

            if (dialog.ShowDialog(this) == true)
            {
                var (imported, _) = await _settingsAppService.ImportAsync(dialog.FileName, _settings, _translationDownload?.Token ?? CancellationToken.None);

                _settings = imported.Clone();
                _editSession.Reset(imported);

                LoadSettings();
                _editSession.Preview(ReadSettingsFromUi());
                _settings = _editSession.Applied;
                SettingsChanged?.Invoke(this, _settings);

                if (BackupStatusText != null)
                {
                    BackupStatusText.Text = Loc.Get("settings.import.success");
                    BackupStatusText.Foreground = BackupSuccessBrush;
                    BackupStatusText.Visibility = Visibility.Visible;

                    var timer = new UiScheduledTimer { Interval = TimeSpan.FromSeconds(3) };
                    timer.Tick += (s, args) =>
                    {
                        timer.Stop();
                        timer.Dispose();
                        BackupStatusText.Visibility = Visibility.Collapsed;
                    };
                    timer.Start();
                }

                // Show the restart banner so the user can immediately restart and refresh all subsystems
                ShowRestartBanner();
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("SETTINGS-IMPORT", ex, "Failed to import settings");
            MessageBox.Show(
                Loc.Get("settings.import.error", ex.Message),
                Loc.Get("error.title"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private bool _isRestartBannerVisible;

    public void ShowRestartBanner(string? title = null, string? message = null)
    {
        if (RestartPromptBanner == null) return;

        if (!string.IsNullOrEmpty(title) && RestartPromptTitle != null)
            RestartPromptTitle.Text = title;
        if (!string.IsNullOrEmpty(message) && RestartPromptMessage != null)
            RestartPromptMessage.Text = message;

        if (_isRestartBannerVisible && RestartPromptBanner.Visibility == Visibility.Visible)
            return;

        _isRestartBannerVisible = true;
        RestartPromptBanner.Visibility = Visibility.Visible;

        int fps = VNotch.Services.AnimationConfig.TargetFps;
        var ease = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 6 };

        RestartPromptBanner.BeginAnimation(OpacityProperty, null);
        RestartPromptTranslate.BeginAnimation(TranslateTransform.YProperty, null);

        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(350)) { EasingFunction = ease };
        var slide = new DoubleAnimation(20, 0, TimeSpan.FromMilliseconds(350)) { EasingFunction = ease };

        Timeline.SetDesiredFrameRate(fade, fps);
        Timeline.SetDesiredFrameRate(slide, fps);

        RestartPromptBanner.BeginAnimation(OpacityProperty, fade);
        RestartPromptTranslate.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    public void HideRestartBanner()
    {
        if (RestartPromptBanner == null || !_isRestartBannerVisible) return;

        _isRestartBannerVisible = false;
        int fps = VNotch.Services.AnimationConfig.TargetFps;
        var ease = new ExponentialEase { EasingMode = EasingMode.EaseIn, Exponent = 5 };

        var fade = new DoubleAnimation(RestartPromptBanner.Opacity, 0, TimeSpan.FromMilliseconds(250)) { EasingFunction = ease };
        var slide = new DoubleAnimation(RestartPromptTranslate.Y, 20, TimeSpan.FromMilliseconds(250)) { EasingFunction = ease };

        Timeline.SetDesiredFrameRate(fade, fps);
        Timeline.SetDesiredFrameRate(slide, fps);

        fade.Completed += (s, e) =>
        {
            if (!_isRestartBannerVisible)
            {
                RestartPromptBanner.BeginAnimation(OpacityProperty, null);
                RestartPromptTranslate.BeginAnimation(TranslateTransform.YProperty, null);
                RestartPromptBanner.Opacity = 0;
                RestartPromptTranslate.Y = 20;
                RestartPromptBanner.Visibility = Visibility.Collapsed;
            }
        };

        RestartPromptBanner.BeginAnimation(OpacityProperty, fade);
        RestartPromptTranslate.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    private void RestartNow_Click(object sender, RoutedEventArgs e)
    {
        ApplySettingsFromUi(persist: true);
        App.RestartApplication();
    }

    private void RestartLater_Click(object sender, RoutedEventArgs e)
    {
        HideRestartBanner();
    }

    private static readonly string Cpp26DiscussionUrl = string.Concat("https://", "github.com/w0otdev/V-Notch/discussions/32");
    private bool _isCpp26BannerVisible;

    public void ShowCpp26BannerIfNeeded()
    {
        if (Cpp26PromptBanner == null || _settings.HasSeenCpp26Notice) return;
        if (_isCpp26BannerVisible && Cpp26PromptBanner.Visibility == Visibility.Visible) return;


        _isCpp26BannerVisible = true;
        Cpp26PromptBanner.Visibility = Visibility.Visible;

        int fps = VNotch.Services.AnimationConfig.TargetFps;
        var ease = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 6 };

        Cpp26PromptBanner.BeginAnimation(OpacityProperty, null);
        Cpp26PromptTranslate.BeginAnimation(TranslateTransform.YProperty, null);

        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(350)) { EasingFunction = ease };
        var slide = new DoubleAnimation(20, 0, TimeSpan.FromMilliseconds(350)) { EasingFunction = ease };

        Timeline.SetDesiredFrameRate(fade, fps);
        Timeline.SetDesiredFrameRate(slide, fps);

        Cpp26PromptBanner.BeginAnimation(OpacityProperty, fade);
        Cpp26PromptTranslate.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    private void MarkCpp26NoticeSeen()
    {
        if (_settings.HasSeenCpp26Notice) return;
        _settings.HasSeenCpp26Notice = true;
        var baseline = _editSession.CommitCpp26NoticeSeen();
        SettingsChanged?.Invoke(this, _editSession.Applied);
        _settingsAppService.ApplyAsync(baseline, _translationDownload?.Token ?? CancellationToken.None).SafeFireAndForget("SETTINGS-CPP26-NOTICE");
    }

    public void HideCpp26Banner(bool markSeen = true)
    {
        if (markSeen) MarkCpp26NoticeSeen();

        if (Cpp26PromptBanner == null || !_isCpp26BannerVisible) return;

        _isCpp26BannerVisible = false;
        int fps = VNotch.Services.AnimationConfig.TargetFps;
        var ease = new ExponentialEase { EasingMode = EasingMode.EaseIn, Exponent = 5 };

        var fade = new DoubleAnimation(Cpp26PromptBanner.Opacity, 0, TimeSpan.FromMilliseconds(250)) { EasingFunction = ease };
        var slide = new DoubleAnimation(Cpp26PromptTranslate.Y, 20, TimeSpan.FromMilliseconds(250)) { EasingFunction = ease };

        Timeline.SetDesiredFrameRate(fade, fps);
        Timeline.SetDesiredFrameRate(slide, fps);

        fade.Completed += (s, e) =>
        {
            if (!_isCpp26BannerVisible)
            {
                Cpp26PromptBanner.BeginAnimation(OpacityProperty, null);
                Cpp26PromptTranslate.BeginAnimation(TranslateTransform.YProperty, null);
                Cpp26PromptBanner.Opacity = 0;
                Cpp26PromptTranslate.Y = 20;
                Cpp26PromptBanner.Visibility = Visibility.Collapsed;
            }
        };

        Cpp26PromptBanner.BeginAnimation(OpacityProperty, fade);
        Cpp26PromptTranslate.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    private void Cpp26Details_Click(object sender, RoutedEventArgs e)
    {
        HideCpp26Banner(markSeen: true);
        SafeLauncher.TryOpenUrl(Cpp26DiscussionUrl);
    }

    private void Cpp26Dismiss_Click(object sender, RoutedEventArgs e)
    {
        HideCpp26Banner(markSeen: true);
    }

    internal static int GetEffectiveGpuPreference(NotchSettings settings)
    {
        if (string.Equals(settings.NotchStyle, SkinLiquidGlass, StringComparison.OrdinalIgnoreCase) &&
            settings.LiquidGlass is { UseGpuRefraction: true, UseUltimateGpu: true })
        {
            return 2;
        }

        return settings.GpuPreference;
    }

    private void ApplyEffectiveGpuPreference()
    {
        int effective = GetEffectiveGpuPreference(_settings);
        ApplyGpuPreference(effective);

        int baselineEffective = GetEffectiveGpuPreference(_editSession.Baseline);
        if (effective != baselineEffective)
        {
            ShowRestartBanner();
        }
    }

    internal static void ApplyGpuPreference(int preference)
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath)) return;

            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\DirectX\UserGpuPreferences");
            if (key != null)
            {
                if (preference == 0)
                {
                    key.DeleteValue(exePath, false);
                }
                else
                {
                    key.SetValue(exePath, $"GpuPreference={preference};");
                }
            }
        }
        catch (Exception ex)
        {
            VNotch.Services.RuntimeLog.Log(LogCategory, $"Failed to set GPU preference: {ex.Message}");
        }
    }

    private bool _isKeyVisible = false;

    private void ToggleKeyVisibility_Click(object sender, RoutedEventArgs e)
    {
        _isKeyVisible = !_isKeyVisible;

        var duration = TimeSpan.FromMilliseconds(200);
        var easeOut = new QuadraticEase { EasingMode = EasingMode.EaseOut };

        if (_isKeyVisible)
        {
            YouTubeApiKeyTextBox.Text = YouTubeApiKeyPasswordBox.Password;
            YouTubeApiKeyPasswordBox.Visibility = Visibility.Collapsed;
            YouTubeApiKeyTextBox.Visibility = Visibility.Visible;

            var fadeOutOpen = new DoubleAnimation(1, 0, duration) { EasingFunction = easeOut };
            var fadeInClosed = new DoubleAnimation(0, 1, duration) { EasingFunction = easeOut, BeginTime = TimeSpan.FromMilliseconds(100) };
            System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(fadeOutOpen, VNotch.Services.AnimationConfig.TargetFps);
            EyeOpenIcon.BeginAnimation(OpacityProperty, fadeOutOpen);
            System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(fadeInClosed, VNotch.Services.AnimationConfig.TargetFps);
            EyeClosedIcon.BeginAnimation(OpacityProperty, fadeInClosed);
        }
        else
        {
            YouTubeApiKeyPasswordBox.Password = YouTubeApiKeyTextBox.Text;
            YouTubeApiKeyTextBox.Visibility = Visibility.Collapsed;
            YouTubeApiKeyPasswordBox.Visibility = Visibility.Visible;

            var fadeOutClosed = new DoubleAnimation(1, 0, duration) { EasingFunction = easeOut };
            var fadeInOpen = new DoubleAnimation(0, 1, duration) { EasingFunction = easeOut, BeginTime = TimeSpan.FromMilliseconds(100) };
            System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(fadeOutClosed, VNotch.Services.AnimationConfig.TargetFps);
            EyeClosedIcon.BeginAnimation(OpacityProperty, fadeOutClosed);
            System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(fadeInOpen, VNotch.Services.AnimationConfig.TargetFps);
            EyeOpenIcon.BeginAnimation(OpacityProperty, fadeInOpen);
        }
    }

    private void UpdateYouTubeApiKeyStatus()
    {
        string key = YouTubeApiKeyPasswordBox.Password?.Trim() ?? "";
        if (string.IsNullOrEmpty(key))
        {
            YouTubeApiKeyStatus.Text = "";
            YouTubeApiKeyStatus.Foreground = StatusMutedBrush;
        }
        else if (key.Length < 30)
        {
            YouTubeApiKeyStatus.Text = Loc.Get("settings.youtubeApi.statusTooShort");
            YouTubeApiKeyStatus.Foreground = StatusErrorBrush;
        }
        else if (!key.StartsWith("AIza", StringComparison.Ordinal))
        {
            YouTubeApiKeyStatus.Text = Loc.Get("settings.youtubeApi.statusMustStart");
            YouTubeApiKeyStatus.Foreground = StatusErrorBrush;
        }
        else if (key.Length >= 35 && key.Length <= 45)
        {
            YouTubeApiKeyStatus.Text = Loc.Get("settings.youtubeApi.statusValid");
            YouTubeApiKeyStatus.Foreground = StatusSuccessBrush;
        }
        else
        {
            YouTubeApiKeyStatus.Text = Loc.Get("settings.youtubeApi.statusUnexpectedLength");
            YouTubeApiKeyStatus.Foreground = StatusWarningBrush;
        }
    }

    private void AnimateLocalizationChange()
    {
        var (activeCard, activeTranslate) = GetSectionCardParts(_activeNav);
        if (activeCard == null || activeTranslate == null || VNotch.Services.AnimationConfig.ReduceMotion)
        {
            ApplyLocalization();
            if (IsVisible) LoadCameraDevices().SafeFireAndForget("SETTINGS-CAMERA-DEVICES");
            return;
        }

        var easeOut = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        int fps = VNotch.Services.AnimationConfig.TargetFps;
        const double slideDist = 6.0;

        activeCard.BeginAnimation(OpacityProperty, null);
        activeTranslate.BeginAnimation(TranslateTransform.YProperty, null);

        var fadeOut = new DoubleAnimation(activeCard.Opacity, 0.0, TimeSpan.FromMilliseconds(100)) { EasingFunction = easeOut };
        var slideOut = new DoubleAnimation(activeTranslate.Y, -slideDist, TimeSpan.FromMilliseconds(100)) { EasingFunction = easeOut };
        Timeline.SetDesiredFrameRate(fadeOut, fps);
        Timeline.SetDesiredFrameRate(slideOut, fps);

        fadeOut.Completed += (_, _) =>
        {
            ApplyLocalization();
            if (IsVisible) LoadCameraDevices().SafeFireAndForget("SETTINGS-CAMERA-DEVICES");

            activeTranslate.Y = slideDist;
            var fadeIn = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(220)) { EasingFunction = easeOut };
            var slideIn = new DoubleAnimation(slideDist, 0.0, TimeSpan.FromMilliseconds(260)) { EasingFunction = easeOut };
            Timeline.SetDesiredFrameRate(fadeIn, fps);
            Timeline.SetDesiredFrameRate(slideIn, fps);

            slideIn.Completed += (_, _) =>
            {
                activeTranslate.BeginAnimation(TranslateTransform.YProperty, null);
                activeTranslate.Y = 0;
            };

            activeCard.BeginAnimation(OpacityProperty, fadeIn);
            activeTranslate.BeginAnimation(TranslateTransform.YProperty, slideIn);
        };

        activeCard.BeginAnimation(OpacityProperty, fadeOut);
        activeTranslate.BeginAnimation(TranslateTransform.YProperty, slideOut);
    }

    private void PushLivePreview()
    {
        if (_isLoadingSettings) return;
        if (!IsLoaded) return;

        if (_livePreviewDebounce == null)
        {
            _livePreviewDebounce = new UiScheduledTimer(DispatcherPriority.Normal)
            {
                Interval = TimeSpan.FromMilliseconds(32)
            };
            _livePreviewDebounce.Tick += (s, e) =>
            {
                _livePreviewDebounce.Stop();
                var snapshot = ReadSettingsFromUi();
                ApplyPreview(snapshot);
            };
        }

        _livePreviewDebounce.Stop();
        _livePreviewDebounce.Start();
    }

    #endregion
}

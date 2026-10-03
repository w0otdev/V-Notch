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

    private bool ConfirmSpotifyCanvasOptIn() => MessageBox.Show(this,
        Loc.Get("settings.enableSpotifyCanvas.hint") + "\n\nhttps://www.spotify.com/us/legal/user-guidelines/",
        Loc.Get("settings.enableSpotifyCanvas"), MessageBoxButton.YesNo, MessageBoxImage.Warning,
        MessageBoxResult.No, Loc.GetCulture().TextInfo.IsRightToLeft
            ? MessageBoxOptions.RightAlign | MessageBoxOptions.RtlReading
            : MessageBoxOptions.None) == MessageBoxResult.Yes;

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
            _settingsAppService.ApplyAsync(_settings).SafeFireAndForget("SETTINGS-LANG");
            _originalSettings = _settings.Clone();
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

    private void PopulateShelfWidgetCombo()
    {
        if (ShelfWidgetCombo == null) return;
        ShelfWidgetCombo.Items.Clear();
        ShelfWidgetCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = Loc.Get("settings.shelfWidget.camera"), Tag = "camera" });
        ShelfWidgetCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = Loc.Get("settings.shelfWidget.sysmon"), Tag = WidgetSysMon });
        ShelfWidgetCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = Loc.Get("settings.shelfWidget.weather"), Tag = WidgetWeather });
        ShelfWidgetCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = Loc.Get("settings.shelfWidget.clock"), Tag = WidgetClock });
        ShelfWidgetCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = Loc.Get("settings.shelfWidget.none"), Tag = "none" });

        ShelfWidgetCombo.SelectedIndex = (_settings.ShelfWidget ?? "camera").ToLowerInvariant() switch
        {
            WidgetSysMon => 1,
            WidgetWeather => 2,
            WidgetClock => 3,
            "none" => 4,
            _ => 0
        };
    }

    private void RepopulateShelfWidgetComboPreservingSelection()
    {
        if (ShelfWidgetCombo == null) return;
        bool wasLoading = _isLoadingSettings;
        _isLoadingSettings = true;
        PopulateShelfWidgetCombo();
        _isLoadingSettings = wasLoading;
    }

    private void ShelfWidgetCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_isLoadingSettings) return;
        if (ShelfWidgetCombo.SelectedItem is System.Windows.Controls.ComboBoxItem item && item.Tag is string widget)
        {
            if (widget == _settings.ShelfWidget) return;
            _settings.ShelfWidget = widget;
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
            _settings.ProcessPriority = tag;
            ApplyProcessPriority(tag);

            if (!string.Equals(_settings.ProcessPriority, _originalSettings.ProcessPriority, StringComparison.OrdinalIgnoreCase))
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
            ApplyGpuPreference(val);

            if (_settings.GpuPreference != _originalSettings.GpuPreference)
            {
                ShowRestartBanner();
            }
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

                    var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                    timer.Tick += (s, args) =>
                    {
                        timer.Stop();
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
                var (imported, _) = await _settingsAppService.ImportAsync(dialog.FileName, _settings);

                _settings = imported.Clone();
                _originalSettings = imported.Clone();

                LoadSettings();
                _appliedSettings = ReadSettingsFromUi();
                _settings = _appliedSettings.Clone();
                SettingsChanged?.Invoke(this, _settings);

                if (BackupStatusText != null)
                {
                    BackupStatusText.Text = Loc.Get("settings.import.success");
                    BackupStatusText.Foreground = BackupSuccessBrush;
                    BackupStatusText.Visibility = Visibility.Visible;

                    var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                    timer.Tick += (s, args) =>
                    {
                        timer.Stop();
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

    private static void ApplyProcessPriority(string priority)
    {
        try
        {
            var p = System.Diagnostics.Process.GetCurrentProcess();
            p.PriorityClass = priority switch
            {
                "High" => System.Diagnostics.ProcessPriorityClass.High,
                "RealTime" => System.Diagnostics.ProcessPriorityClass.RealTime,
                _ => System.Diagnostics.ProcessPriorityClass.Normal
            };
        }
        catch (Exception ex)
        {
            RuntimeLog.Log(LogCategory, $"Failed to set process priority: {ex.Message}");
        }
    }

    private static void ApplyGpuPreference(int preference)
    {
        try
        {
            var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
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
        Language = System.Windows.Markup.XmlLanguage.GetLanguage(Loc.GetCulture().IetfLanguageTag);
        Title = Loc.Get("settings.windowTitle");
        ApplyTooltips();
        var easeOut = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        int fps = VNotch.Services.AnimationConfig.TargetFps;
        const double slideDist = 3.0;
        int staggerMs = 0;
        const int staggerStep = 12;

        var textUpdates = new (FrameworkElement element, Action update)[]
        {
            (SidebarBuildVersionText, () => { if (SidebarBuildVersionText != null) SidebarBuildVersionText.Text = $"Build {GetAppVersion()}"; }),
            (SearchPlaceholder, () => SearchPlaceholder.Text = Loc.Get("settings.searchPlaceholder")),

            (AppearanceHeader, () => AppearanceHeader.Text = Loc.Get("settings.appearance")),
            (BehaviorHeader, () => BehaviorHeader.Text = Loc.Get("settings.behavior")),
            (UpdatesHeader, () => UpdatesHeader.Text = Loc.Get("settings.updates")),
            (DonatingHeader, () => DonatingHeader.Text = Loc.Get("settings.donating")),
            (PerformanceHeader, () => PerformanceHeader.Text = Loc.Get("settings.performance")),
            (DisplayHeader, () => DisplayHeader.Text = Loc.Get("settings.display")),
            (SystemHeader, () => SystemHeader.Text = Loc.Get("settings.system")),
            (SpotlightHeader, () => SpotlightHeader.Text = Loc.Get("settings.spotlight")),
            (SearchingHeader, () => SearchingHeader.Text = Loc.Get("settings.searching")),
            (SearchingEmptyText, () => SearchingEmptyText.Text = Loc.Get("settings.search.noResults")),
            (SearchingEmptyHint, () => SearchingEmptyHint.Text = Loc.Get("settings.search.emptyHint")),
            (SearchingClearButton, () => SearchingClearButton.Content = Loc.Get("settings.search.clear")),

            (NavSearchingText, () => NavSearchingText.Text = Loc.Get("settings.searching")),
            (NavAppearanceText, () => NavAppearanceText.Text = Loc.Get("settings.nav.appearance")),
            (NavSkinsText, () => NavSkinsText.Text = Loc.Get("settings.nav.skins")),
            (NavBehaviorText, () => NavBehaviorText.Text = Loc.Get("settings.nav.behavior")),
            (NavDevicesText, () => NavDevicesText.Text = Loc.Get("settings.nav.devices")),
            (NavSystemText, () => NavSystemText.Text = Loc.Get("settings.nav.system")),
            (NavPrivacyText, () => NavPrivacyText.Text = Loc.Get("settings.nav.privacy")),
            (NavSpotlightText, () => NavSpotlightText.Text = Loc.Get("settings.nav.spotlight")),
            (NavAdvancedText, () => NavAdvancedText.Text = Loc.Get("settings.nav.advanced")),
            (NavPerformanceText, () => NavPerformanceText.Text = Loc.Get("settings.nav.performance")),
            (NavDonatingText, () => NavDonatingText.Text = Loc.Get("settings.nav.donating")),
            (NavUpdatesText, () => NavUpdatesText.Text = Loc.Get("settings.nav.updates")),

            (PrivacyHeader, () => PrivacyHeader.Text = Loc.Get("settings.header.privacy")),
            (LocalOnlyModeCheck, () => LocalOnlyModeCheck.Content = Loc.Get("settings.privacy.localOnly")),
            (LocalOnlyBadgeText, () => LocalOnlyBadgeText.Text = Loc.Get("settings.privacy.localOnly.badge")),
            (LocalOnlyModeHint, () => LocalOnlyModeHint.Text = Loc.Get("settings.privacy.localOnly.hint")),
            (PrivacyNetworkHeader, () => PrivacyNetworkHeader.Text = Loc.Get("settings.privacy.section.network")),
            (AutoCheckUpdatesCheck, () => AutoCheckUpdatesCheck.Content = Loc.Get("settings.privacy.autoUpdates")),
            (AutoCheckUpdatesHint, () => AutoCheckUpdatesHint.Text = Loc.Get("settings.privacy.autoUpdates.hint")),
            (EnableOnlineArtworkCheck, () => EnableOnlineArtworkCheck.Content = Loc.Get("settings.privacy.onlineArtwork")),
            (EnableOnlineArtworkHint, () => EnableOnlineArtworkHint.Text = Loc.Get("settings.privacy.onlineArtwork.hint")),
            (EnableOnlineLyricsCheck, () => EnableOnlineLyricsCheck.Content = Loc.Get("settings.privacy.onlineLyrics")),
            (EnableOnlineLyricsHint, () => EnableOnlineLyricsHint.Text = Loc.Get("settings.privacy.onlineLyrics.hint")),
            (PrivacySensorsHeader, () => PrivacySensorsHeader.Text = Loc.Get("settings.privacy.section.sensors")),
            (EnablePrivacyIndicatorsCheck, () => EnablePrivacyIndicatorsCheck.Content = Loc.Get("settings.privacy.indicators")),
            (EnablePrivacyIndicatorsHint, () => EnablePrivacyIndicatorsHint.Text = Loc.Get("settings.privacy.indicators.hint")),
            (EnableBrowserUrlInspectionCheck, () => EnableBrowserUrlInspectionCheck.Content = Loc.Get("settings.privacy.browserUrl")),
            (EnableBrowserUrlInspectionHint, () => EnableBrowserUrlInspectionHint.Text = Loc.Get("settings.privacy.browserUrl.hint")),
            (PrivacyStorageHeader, () => PrivacyStorageHeader.Text = Loc.Get("settings.privacy.section.storage")),
            (EnableDiagnosticLoggingCheck, () => EnableDiagnosticLoggingCheck.Content = Loc.Get("settings.privacy.logging")),
            (EnableDiagnosticLoggingHint, () => EnableDiagnosticLoggingHint.Text = Loc.Get("settings.privacy.logging.hint")),
            (EnableSpotlightHistoryCheck, () => EnableSpotlightHistoryCheck.Content = Loc.Get("settings.privacy.spotlightHistory")),
            (EnableSpotlightHistoryHint, () => EnableSpotlightHistoryHint.Text = Loc.Get("settings.privacy.spotlightHistory.hint")),
            (ClearSpotlightHistoryButton, () => ClearSpotlightHistoryButton.Content = Loc.Get("settings.privacy.clearSpotlight")),
            (ClearLogButton, () => ClearLogButton.Content = Loc.Get("settings.privacy.clearLog")),

            (NavTabsLabel, () => { NavTabsLabel.Text = Loc.Get("settings.navTabs"); NavTabsHint.Text = Loc.Get("settings.navTabs.hint"); ResetTabOrderButton.Content = Loc.Get("settings.tab.reset"); PopulateNavTabsSettings(); }),
            (ExpandedWidgetLabel, () => { ExpandedWidgetLabel.Text = Loc.Get("settings.expandedWidget"); ExpandedWidgetHint.Text = Loc.Get("settings.expandedWidget.hint"); RepopulateWidgetComboPreservingSelection(); }),
            (ShelfWidgetLabel, () => { ShelfWidgetLabel.Text = Loc.Get("settings.shelfWidget"); ShelfWidgetHint.Text = Loc.Get("settings.shelfWidget.hint"); RepopulateShelfWidgetComboPreservingSelection(); }),
            (ClockPageStyleLabel, () => { ClockPageStyleLabel.Text = Loc.Get("settings.clockPageStyle"); ClockPageStyleHint.Text = Loc.Get("settings.clockPageStyle.hint"); RepopulateClockPageStyleComboPreservingSelection(); }),
            (WidthLabel, () => { WidthLabel.Text = Loc.Get("settings.width"); WidthSlider.Label = Loc.Get("settings.width"); WidthSlider.Description = Loc.Get("settings.width.hint"); }),
            (DynamicIslandWidthLabel, () => { DynamicIslandWidthLabel.Text = Loc.Get("settings.dynamicIslandWidth"); DynamicIslandWidthSlider.Label = Loc.Get("settings.dynamicIslandWidth"); DynamicIslandWidthSlider.Description = Loc.Get("settings.dynamicIslandWidth.hint"); }),
            (DynamicIslandHeightLabel, () => { DynamicIslandHeightLabel.Text = Loc.Get("settings.dynamicIslandHeight"); DynamicIslandHeightSlider.Label = Loc.Get("settings.dynamicIslandHeight"); DynamicIslandHeightSlider.Description = Loc.Get("settings.dynamicIslandHeight.hint"); }),
            (HeightLabel, () => { HeightLabel.Text = Loc.Get("settings.height"); HeightSlider.Label = Loc.Get("settings.height"); HeightSlider.Description = Loc.Get("settings.height.hint"); }),
            (RadiusLabel, () => { RadiusLabel.Text = Loc.Get("settings.cornerRadius"); RadiusSlider.Label = Loc.Get("settings.cornerRadius"); RadiusSlider.Description = Loc.Get("settings.cornerRadius.hint"); }),
            (OpacityLabel, () => { OpacityLabel.Text = Loc.Get("settings.opacity"); OpacitySlider.Label = Loc.Get("settings.opacity"); OpacitySlider.Description = Loc.Get("settings.opacity.hint"); }),
            (BlurLabel, () => { BlurLabel.Text = Loc.Get("settings.blurBrightness"); BlurBrightnessSlider.Label = Loc.Get("settings.blurBrightness"); BlurBrightnessSlider.Description = Loc.Get("settings.blurBrightness.hint"); }),
            (DarkOverlayLabel, () => { DarkOverlayLabel.Text = Loc.Get("settings.lyricsDarkOverlay"); BlurDarkOverlaySlider.Label = Loc.Get("settings.lyricsDarkOverlay"); BlurDarkOverlaySlider.Description = Loc.Get("settings.lyricsDarkOverlay.hint"); }),
            (SpotifyCanvasBrightnessSlider, () => { SpotifyCanvasBrightnessSlider.Label = Loc.Get("settings.spotifyCanvasBrightness"); SpotifyCanvasBrightnessSlider.Description = Loc.Get("settings.spotifyCanvasBrightness.hint"); }),
            (EnableSpotifyLyricsHint, () => EnableSpotifyLyricsHint.Text = Loc.Get("settings.enableSpotifyLyrics.hint")),
            (EnableSpotifyCanvasHint, () => EnableSpotifyCanvasHint.Text = Loc.Get("settings.enableSpotifyCanvas.hint")),
            (EnableYouTubeSubtitlesHint, () => EnableYouTubeSubtitlesHint.Text = Loc.Get("settings.enableYouTubeSubtitles.hint")),
            (IgnoreYouTubeAutoSubtitlesHint, () => IgnoreYouTubeAutoSubtitlesHint.Text = Loc.Get("settings.ignoreYouTubeAutoSubtitles.hint")),
            (YouTubeSubtitlesAlphaBadge, () => YouTubeSubtitlesAlphaBadge.Text = Loc.Get("settings.badge.alpha")),
            (SubtitlePriorityLabel, () => SubtitlePriorityLabel.Text = Loc.Get("settings.subtitlePriority")),
            (SubtitlePriorityHint, () =>
            {
                SubtitlePriorityHint.Text = Loc.Get("settings.subtitlePriority.hint");
                LoadSubtitlePriority();
            }),
            (SpotifyCanvasAccountLabel, () => SpotifyCanvasAccountLabel.Text = Loc.Get("settings.spotifyCanvasAccount")),
            (SpotifyCanvasAccountHint, () => SpotifyCanvasAccountHint.Text = Loc.Get("settings.spotifyCanvasAccount.hint")),
            (SpotifyCanvasAccountStatus, UpdateSpotifyCanvasConnectionStatus),
            (DynamicIslandModeCheck, () => DynamicIslandModeCheck.Content = Loc.Get("settings.dynamicIslandMode")),
            (DynamicIslandModeHint, () => DynamicIslandModeHint.Text = Loc.Get("settings.dynamicIslandMode.hint")),

            (HoverExpandHint, () => HoverExpandHint.Text = Loc.Get("settings.hoverExpand.hint")),
            (ExpandDelayLabel, () => { ExpandDelayLabel.Text = Loc.Get("settings.expandDelay"); HoverDelaySlider.Label = Loc.Get("settings.expandDelay"); HoverDelaySlider.Description = Loc.Get("settings.expandDelay.hint"); }),
            (DisableMouseLeaveAutoCloseHint, () => DisableMouseLeaveAutoCloseHint.Text = Loc.Get("settings.disableAutoClose.hint")),
            (KeepMediaPinnedHint, () => KeepMediaPinnedHint.Text = Loc.Get("settings.keepMediaPinned.hint")),
            (ReopenLastViewHint, () => ReopenLastViewHint.Text = Loc.Get("settings.reopenLastView.hint")),
            (IdleAutoHideHint, () => IdleAutoHideHint.Text = Loc.Get("settings.idleAutoHide.hint")),
            (IdleAutoHideDelaySlider, () => { IdleAutoHideDelaySlider.Label = Loc.Get("settings.idleAutoHideDelay"); IdleAutoHideDelaySlider.Description = Loc.Get("settings.idleAutoHideDelay.hint"); }),

            (UpdateStatusText, () => UpdateStatusText.Text = Loc.Get("settings.upToDate")),
            (CurrentVersionText, () => { CurrentVersionText.Text = Loc.Get("settings.currentVersion", GetAppVersion()); ViewChangelogButton.Content = Loc.Get("settings.btn.changelog"); }),
            (ReportBugLabel, () => ReportBugLabel.Text = Loc.Get("settings.reportBug")),
            (ReportBugHint, () => ReportBugHint.Text = Loc.Get("settings.reportBug.hint")),
            (RequestFeatureLabel, () => RequestFeatureLabel.Text = Loc.Get("settings.requestFeature")),
            (RequestFeatureHint, () => RequestFeatureHint.Text = Loc.Get("settings.requestFeature.hint")),
            (ClearCacheLabel, () => ClearCacheLabel.Text = Loc.Get("settings.clearCache")),
            (ClearCacheHint, () => ClearCacheHint.Text = Loc.Get("settings.clearCache.hint")),

            (MonitorLabel, () => MonitorLabel.Text = Loc.Get("settings.activeMonitor")),
            (MonitorHint, () =>
            {
                MonitorHint.Text = Loc.Get("settings.activeMonitor.hint");
                RefreshMonitorChoices();
            }),
            (CameraLabel, () => CameraLabel.Text = Loc.Get("settings.camera")),
            (CameraHint, () =>
            {
                CameraHint.Text = Loc.Get("settings.camera.hint");
                LoadCameraDevices().SafeFireAndForget("SETTINGS-CAMERA-DEVICES");
            }),
            (VisualizerAudioLabel, () => VisualizerAudioLabel.Text = Loc.Get("settings.visualizerAudio")),
            (VisualizerAudioHint, () =>
            {
                VisualizerAudioHint.Text = Loc.Get("settings.visualizerAudio.hint");
                LoadVisualizerAudioDevices().SafeFireAndForget("SETTINGS-VIS-AUDIO");
            }),

            (AutoStartHint, () => AutoStartHint.Text = Loc.Get("settings.autoStart.hint")),
            (StayBehindWindowsHint, () => StayBehindWindowsHint.Text = Loc.Get("settings.stayBehindWindows.hint")),
            (HelloGreetingHint, () => HelloGreetingHint.Text = Loc.Get("settings.helloGreeting.hint")),
            (HideOnExclusiveFullscreenHint, () => HideOnExclusiveFullscreenHint.Text = Loc.Get("settings.hideExclusiveFs.hint")),
            (HideOnWindowedFullscreenHint, () => HideOnWindowedFullscreenHint.Text = Loc.Get("settings.hideWindowedFs.hint")),
            (MusicNotifyHint, () => MusicNotifyHint.Text = Loc.Get("settings.musicNotify.hint")),
            (SystemNotifyHint, () => SystemNotifyHint.Text = Loc.Get("settings.systemNotify.hint")),
            (ShelfUnlockHint, () => ShelfUnlockHint.Text = Loc.Get("settings.shelfUnlock.hint")),
            (CopyShelfClipboardHint, () => CopyShelfClipboardHint.Text = Loc.Get("settings.copyShelfClipboard.hint")),
            (ShowBatteryHint, () => ShowBatteryHint.Text = Loc.Get("settings.showBattery.hint")),
            (EnableSpotlightHint, () => EnableSpotlightHint.Text = Loc.Get("settings.enableSpotlight.hint")),
            (SpotlightAiHint, LocalizeSpotlightAiSettings),
            (SpotlightHotkeyWarning, () => SpotlightHotkeyWarning.Text = Loc.Get("settings.enableSpotlight.conflict")),
            (LanguageLabel, () => LanguageLabel.Text = Loc.Get("settings.language")),
            (LanguageHint, () => LanguageHint.Text = Loc.Get("settings.language.hint")),

            (EnableWeatherHint, () => EnableWeatherHint.Text = Loc.Get("settings.enableWeather.hint")),
            (ManualCityLabel, () => ManualCityLabel.Text = Loc.Get("settings.manualCity")),
            (ManualCityHint, () => ManualCityHint.Text = Loc.Get("settings.manualCity.hint")),

            (AdvancedHeader, () => AdvancedHeader.Text = Loc.Get("settings.advanced")),
            (YouTubeApiHint, () => YouTubeApiHint.Text = Loc.Get("settings.youtubeApi.hint")),
            (YouTubeApiKeyLabel, () => YouTubeApiKeyLabel.Text = Loc.Get("settings.youtubeApiKey")),
            (YouTubeApiKeyHint, () => YouTubeApiKeyHint.Text = Loc.Get("settings.youtubeApiKey.hint")),
            (YouTubeApiKeyStatus, UpdateYouTubeApiKeyStatus),
            (EnableDebugModeHint, () => EnableDebugModeHint.Text = Loc.Get("settings.enableDebugMode.hint")),
            (GlassFpsSlider, () => GlassFpsSlider.Label = Loc.Get("settings.glass.targetFps")),

            (AnimationFpsLabel, () => { AnimationFpsLabel.Text = Loc.Get("settings.animationFps"); AutoAnimationFpsCheck.Content = Loc.Get("settings.animationFps.auto"); AnimationFpsSlider.Label = Loc.Get("settings.animationFps"); AnimationFpsSlider.Description = Loc.Get("settings.animationFps.hint"); }),
            (EnableBlurEffectsHint, () => EnableBlurEffectsHint.Text = Loc.Get("settings.enableBlurEffects.hint")),
            (EnableSubjectBlurHint, () => EnableSubjectBlurHint.Text = Loc.Get("settings.enableSubjectBlur.hint")),
            (EnableSmartCropHint, () => EnableSmartCropHint.Text = Loc.Get("settings.enableSmartCrop.hint")),
            (MediaArtBackgroundHint, () => { MediaArtBackgroundCheck.Content = Loc.Get("settings.mediaArtBackground"); MediaArtBackgroundHint.Text = Loc.Get("settings.mediaArtBackground.hint"); }),

            (DonatingTitle, () => DonatingTitle.Text = Loc.Get("settings.donating.title")),
            (DonatingDescription, () => DonatingDescription.Text = Loc.Get("settings.donating.description")),
            (DonatingBankTitle, () => DonatingBankTitle.Text = Loc.Get("settings.donating.bank")),
            (DonatingBankHint, () => DonatingBankHint.Text = Loc.Get("settings.donating.bank.hint")),
            (StarRepoTitle, () => StarRepoTitle.Text = Loc.Get("settings.donating.star.title")),
            (StarRepoDescription, () => StarRepoDescription.Text = Loc.Get("settings.donating.star.description")),
            (StarRepoButtonText, () => StarRepoButtonText.Text = Loc.Get("settings.donating.star.button")),
            (OtherProjectsHeader, () => OtherProjectsHeader.Text = Loc.Get("settings.donating.otherProjects.title")),
            (OtherProjectsSubtext, () => OtherProjectsSubtext.Text = Loc.Get("settings.donating.otherProjects.subtitle")),
            (ProjectVertexDesc, () => ProjectVertexDesc.Text = Loc.Get("settings.donating.otherProjects.vertex.desc")),
            (ProjectScrollVDesc, () => ProjectScrollVDesc.Text = Loc.Get("settings.donating.otherProjects.scrollv.desc")),

            (BackupHeader, () => BackupHeader.Text = Loc.Get("settings.section.backup")),
            (ExportSettingsLabel, () => ExportSettingsLabel.Text = Loc.Get("settings.exportSettings")),
            (ExportSettingsHint, () => ExportSettingsHint.Text = Loc.Get("settings.exportSettings.hint")),
            (ImportSettingsLabel, () => ImportSettingsLabel.Text = Loc.Get("settings.importSettings")),
            (ImportSettingsHint, () => ImportSettingsHint.Text = Loc.Get("settings.importSettings.hint")),
            (RestartPromptTitle, () => RestartPromptTitle.Text = Loc.Get("settings.restartBanner.title")),
            (RestartPromptMessage, () => RestartPromptMessage.Text = Loc.Get("settings.restartBanner.message")),

            (ProcessPriorityLabel, () => ProcessPriorityLabel.Text = Loc.Get("settings.processPriority")),
            (ProcessPriorityHint, () => ProcessPriorityHint.Text = Loc.Get("settings.processPriority.hint")),
            (GpuPreferenceLabel, () => GpuPreferenceLabel.Text = Loc.Get("settings.gpuPreference")),
            (GpuPreferenceHint, () => GpuPreferenceHint.Text = Loc.Get("settings.gpuPreference.hint")),
            (GpuPreferenceRestartBadge, () => GpuPreferenceRestartBadge.Text = Loc.Get("settings.badge.restartRequired")),
            (GpuPreferenceRestartNote, () => GpuPreferenceRestartNote.Text = Loc.Get("settings.gpuPreference.restartNote")),
            (ProcessPriorityCombo, () =>
            {
                if (ProcessPriorityCombo.Items.Count >= 3)
                {
                    ((ComboBoxItem)ProcessPriorityCombo.Items[0]).Content = Loc.Get("settings.processPriority.normal");
                    ((ComboBoxItem)ProcessPriorityCombo.Items[1]).Content = Loc.Get("settings.processPriority.high");
                    ((ComboBoxItem)ProcessPriorityCombo.Items[2]).Content = Loc.Get("settings.processPriority.realtime");
                }
            }),
            (GpuPreferenceCombo, () =>
            {
                if (GpuPreferenceCombo.Items.Count >= 3)
                {
                    ((ComboBoxItem)GpuPreferenceCombo.Items[0]).Content = Loc.Get("settings.gpuPreference.auto");
                    ((ComboBoxItem)GpuPreferenceCombo.Items[1]).Content = Loc.Get("settings.gpuPreference.igpu");
                    ((ComboBoxItem)GpuPreferenceCombo.Items[2]).Content = Loc.Get("settings.gpuPreference.dgpu");
                }
            }),

            (SkinCard, () =>
            {
                ApplyLiquidGlassLocalization();
                GpuRefractionCheck.Content = Loc.Get("settings.gpuRefraction");
                GpuRefractionHint.Text = Loc.Get("settings.gpuRefraction.hint");
            }),
        };

        AnimateContentChange(ExportSettingsButton, () => ExportSettingsButton.Content = Loc.Get("settings.exportSettings.btn"), staggerMs, easeOut, fps);
        staggerMs += staggerStep;
        AnimateContentChange(ImportSettingsButton, () => ImportSettingsButton.Content = Loc.Get("settings.importSettings.btn"), staggerMs, easeOut, fps);
        staggerMs += staggerStep;
        AnimateContentChange(RestartNowButton, () => RestartNowButton.Content = Loc.Get("settings.restartBanner.restartNow"), staggerMs, easeOut, fps);
        staggerMs += staggerStep;
        AnimateContentChange(RestartLaterButton, () => RestartLaterButton.Content = Loc.Get("settings.restartBanner.later"), staggerMs, easeOut, fps);
        staggerMs += staggerStep;
        TooltipHelper.SetLocalizedTooltip(ExportSettingsButton, "tooltip.exportSettings");
        TooltipHelper.SetLocalizedTooltip(ImportSettingsButton, "tooltip.importSettings");

        AnimateContentChange(CheckUpdateButton, () => CheckUpdateButton.Content = Loc.Get("settings.checkUpdate"), staggerMs, easeOut, fps);
        staggerMs += staggerStep;
        AnimateContentChange(DownloadUpdateButton, () => DownloadUpdateButton.Content = Loc.Get("settings.downloadInstall"), staggerMs, easeOut, fps);
        staggerMs += staggerStep;
        AnimateContentChange(DonatePaypalButton, () => DonatePaypalButton.Content = Loc.Get("settings.donating.paypal"), staggerMs, easeOut, fps);
        staggerMs += staggerStep;
        AnimateContentChange(ResetButton, () => ResetButton.Content = Loc.Get("settings.btn.reset"), staggerMs, easeOut, fps);
        staggerMs += staggerStep;
        AnimateContentChange(ApplyButton, () => ApplyButton.Content = Loc.Get("settings.btn.apply"), staggerMs, easeOut, fps);
        staggerMs += staggerStep;
        AnimateContentChange(SaveButton, () => SaveButton.Content = Loc.Get("settings.btn.save"), staggerMs, easeOut, fps);
        staggerMs += staggerStep;

        AnimateContentChange(AutoStartCheck, () => AutoStartCheck.Content = Loc.Get("settings.autoStart"), staggerMs, easeOut, fps);
        staggerMs += staggerStep;
        AnimateContentChange(StayBehindWindowsCheck, () => StayBehindWindowsCheck.Content = Loc.Get("settings.stayBehindWindows"), staggerMs, easeOut, fps);
        staggerMs += staggerStep;
        AnimateContentChange(HelloGreetingCheck, () => HelloGreetingCheck.Content = Loc.Get("settings.helloGreeting"), staggerMs, easeOut, fps);
        staggerMs += staggerStep;
        AnimateContentChange(HideOnExclusiveFullscreenCheck, () => HideOnExclusiveFullscreenCheck.Content = Loc.Get("settings.hideExclusiveFs"), staggerMs, easeOut, fps);
        staggerMs += staggerStep;
        AnimateContentChange(HideOnWindowedFullscreenCheck, () => HideOnWindowedFullscreenCheck.Content = Loc.Get("settings.hideWindowedFs"), staggerMs, easeOut, fps);
        staggerMs += staggerStep;
        AnimateContentChange(MusicNotifyCheck, () => MusicNotifyCheck.Content = Loc.Get("settings.musicNotify"), staggerMs, easeOut, fps);
        staggerMs += staggerStep;
        AnimateContentChange(SystemNotifyCheck, () => SystemNotifyCheck.Content = Loc.Get("settings.systemNotify"), staggerMs, easeOut, fps);
        staggerMs += staggerStep;
        AnimateContentChange(ShelfUnlockCheck, () => ShelfUnlockCheck.Content = Loc.Get("settings.shelfUnlock"), staggerMs, easeOut, fps);
        staggerMs += staggerStep;
        ScreenshotTrayCheck.Content = Loc.Get("settings.screenshotTray");
        ScreenshotTrayHint.Text = Loc.Get("settings.screenshotTray.hint");
        ScreenshotTrayDurationSlider.Label = Loc.Get("settings.screenshotTrayDuration");
        ScreenshotTrayDurationSlider.Description = Loc.Get("settings.screenshotTrayDuration.hint");
        AnimateContentChange(CopyShelfClipboardCheck, () => CopyShelfClipboardCheck.Content = Loc.Get("settings.copyShelfClipboard"), staggerMs, easeOut, fps);
        staggerMs += staggerStep;
        AnimateContentChange(ShowBatteryCheck, () => ShowBatteryCheck.Content = Loc.Get("settings.showBattery"), staggerMs, easeOut, fps);
        staggerMs += staggerStep;
        AnimateContentChange(EnableSpotlightCheck, () => EnableSpotlightCheck.Content = Loc.Get("settings.enableSpotlight"), staggerMs, easeOut, fps);
        staggerMs += staggerStep;
        AnimateContentChange(YouTubeApiCheck, () => YouTubeApiCheck.Content = Loc.Get("settings.youtubeApi"), staggerMs, easeOut, fps);
        staggerMs += staggerStep;
        AnimateContentChange(HoverExpandCheck, () => HoverExpandCheck.Content = Loc.Get("settings.hoverExpand"), staggerMs, easeOut, fps);
        staggerMs += staggerStep;
        AnimateContentChange(DisableMouseLeaveAutoCloseCheck, () => DisableMouseLeaveAutoCloseCheck.Content = Loc.Get("settings.disableAutoClose"), staggerMs, easeOut, fps);
        staggerMs += staggerStep;
        AnimateContentChange(KeepMediaPinnedCheck, () => KeepMediaPinnedCheck.Content = Loc.Get("settings.keepMediaPinned"), staggerMs, easeOut, fps);
        AnimateContentChange(ReopenLastViewCheck, () => ReopenLastViewCheck.Content = Loc.Get("settings.reopenLastView"), staggerMs, easeOut, fps);
        staggerMs += staggerStep;
        AnimateContentChange(IdleAutoHideCheck, () => IdleAutoHideCheck.Content = Loc.Get("settings.idleAutoHide"), staggerMs, easeOut, fps);
        staggerMs += staggerStep;
        AnimateContentChange(EnableDebugModeCheck, () => EnableDebugModeCheck.Content = Loc.Get("settings.enableDebugMode"), staggerMs, easeOut, fps);
        staggerMs += staggerStep;
        AnimateContentChange(EnableSpotifyLyricsCheck, () => EnableSpotifyLyricsCheck.Content = Loc.Get("settings.enableSpotifyLyrics"), staggerMs, easeOut, fps);
        AnimateContentChange(BrightenDarkLyricsBackgroundCheck, () => BrightenDarkLyricsBackgroundCheck.Content = Loc.Get("settings.brightenDarkLyricsBackground"), staggerMs, easeOut, fps);
        staggerMs += staggerStep;
        AnimateContentChange(EnableSpotifyCanvasCheck, () => EnableSpotifyCanvasCheck.Content = Loc.Get("settings.enableSpotifyCanvas"), staggerMs, easeOut, fps);
        staggerMs += staggerStep;
        AnimateContentChange(SpotifyConnectButton, () => SpotifyConnectButton.Content = Loc.Get("settings.spotifyCanvas.connect"), staggerMs, easeOut, fps);
        staggerMs += staggerStep;
        AnimateContentChange(SpotifyDisconnectButton, () => SpotifyDisconnectButton.Content = Loc.Get("settings.spotifyCanvas.disconnect"), staggerMs, easeOut, fps);
        staggerMs += staggerStep;
        AnimateContentChange(EnableYouTubeSubtitlesCheck, () => EnableYouTubeSubtitlesLabel.Text = Loc.Get("settings.enableYouTubeSubtitles"), staggerMs, easeOut, fps);
        staggerMs += staggerStep;
        AnimateContentChange(IgnoreYouTubeAutoSubtitlesCheck, () => IgnoreYouTubeAutoSubtitlesLabel.Text = Loc.Get("settings.ignoreYouTubeAutoSubtitles"), staggerMs, easeOut, fps);
        staggerMs += staggerStep;
        AnimateContentChange(EnableBlurEffectsCheck, () => EnableBlurEffectsCheck.Content = Loc.Get("settings.enableBlurEffects"), staggerMs, easeOut, fps);
        staggerMs += staggerStep;
        AnimateContentChange(EnableSubjectBlurCheck, () => EnableSubjectBlurCheck.Content = Loc.Get("settings.enableSubjectBlur"), staggerMs, easeOut, fps);
        staggerMs += staggerStep;
        AnimateContentChange(EnableSmartCropCheck, () => EnableSmartCropCheck.Content = Loc.Get("settings.enableSmartCrop"), staggerMs, easeOut, fps);
        staggerMs += staggerStep;
        AnimateContentChange(EnableWeatherCheck, () => EnableWeatherCheck.Content = Loc.Get("settings.enableWeather"), staggerMs, easeOut, fps);
        staggerMs += staggerStep;

        foreach (var (element, update) in textUpdates)
        {
            if (element == null) continue;
            AnimateTextSwap(element, update, staggerMs, easeOut, fps, slideDist);
            staggerMs += staggerStep;
        }

    }

    private static void AnimateTextSwap(FrameworkElement element, Action updateText, int delayMs, IEasingFunction easing, int fps, double slideDist)
    {
        var translate = element.RenderTransform as TranslateTransform;
        if (translate == null)
        {
            translate = new TranslateTransform(0, 0);
            element.RenderTransform = translate;
        }

        element.BeginAnimation(OpacityProperty, null);
        translate.BeginAnimation(TranslateTransform.XProperty, null);

        var fadeOut = new DoubleAnimation
        {
            To = 0,
            Duration = TimeSpan.FromMilliseconds(100),
            EasingFunction = easing,
            BeginTime = TimeSpan.FromMilliseconds(delayMs)
        };
        Timeline.SetDesiredFrameRate(fadeOut, fps);

        var slideOut = new DoubleAnimation
        {
            To = -slideDist,
            Duration = TimeSpan.FromMilliseconds(100),
            EasingFunction = easing,
            BeginTime = TimeSpan.FromMilliseconds(delayMs)
        };
        Timeline.SetDesiredFrameRate(slideOut, fps);

        fadeOut.Completed += (s, e) =>
        {
            updateText();

            translate.X = slideDist;

            var fadeIn = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(220),
                EasingFunction = easing
            };
            Timeline.SetDesiredFrameRate(fadeIn, fps);

            var slideIn = new DoubleAnimation
            {
                From = slideDist,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(300),
                EasingFunction = easing
            };
            Timeline.SetDesiredFrameRate(slideIn, fps);

            slideIn.Completed += (s2, e2) =>
            {
                translate.BeginAnimation(TranslateTransform.XProperty, null);
                translate.X = 0;
            };

            element.BeginAnimation(OpacityProperty, fadeIn);
            translate.BeginAnimation(TranslateTransform.XProperty, slideIn);
        };

        element.BeginAnimation(OpacityProperty, fadeOut);
        translate.BeginAnimation(TranslateTransform.XProperty, slideOut);
    }

    private static void AnimateContentChange(FrameworkElement element, Action updateContent, int delayMs, IEasingFunction easing, int fps)
    {
        element.BeginAnimation(OpacityProperty, null);

        var fadeOut = new DoubleAnimation
        {
            To = 0,
            Duration = TimeSpan.FromMilliseconds(120),
            EasingFunction = easing,
            BeginTime = TimeSpan.FromMilliseconds(delayMs)
        };
        Timeline.SetDesiredFrameRate(fadeOut, fps);

        fadeOut.Completed += (s, e) =>
        {
            updateContent();

            var fadeIn = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(280),
                EasingFunction = easing
            };
            Timeline.SetDesiredFrameRate(fadeIn, fps);

            element.BeginAnimation(OpacityProperty, fadeIn);
        };

        element.BeginAnimation(OpacityProperty, fadeOut);
    }

    private void PushLivePreview()
    {
        if (_isLoadingSettings) return;
        if (!IsLoaded) return;

        if (_livePreviewDebounce == null)
        {
            _livePreviewDebounce = new DispatcherTimer(DispatcherPriority.Normal)
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

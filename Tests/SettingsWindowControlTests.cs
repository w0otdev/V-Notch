using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using VNotch.Models;
using VNotch.Services;
using VNotch.Services.Translation;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class SettingsWindowControlTests
{
    [Fact]
    public void BatterySourceIsEditableAndSurvivesLocalizationAndApply() => SharedStaTestRunner.RunAsync(async _ =>
    {
        using var fixture = new Fixture();
        var window = fixture.Window;
        var combo = Assert.IsType<ComboBox>(window.FindName("BatteryDeviceCombo"));
        combo.SelectedValue = NotchSettings.AutoBluetoothBatteryDeviceId;
        Assert.Equal(NotchSettings.AutoBluetoothBatteryDeviceId, window.ReadSettingsFromUi().BatteryDeviceId);
        Loc.SetLanguage("vi");
        Invoke(window, "ApplyLocalization");
        Assert.Equal(NotchSettings.AutoBluetoothBatteryDeviceId, combo.SelectedValue);
        await window.ApplyWithoutClosingAsync();
        Assert.Equal(NotchSettings.AutoBluetoothBatteryDeviceId, fixture.Service.Applied!.BatteryDeviceId);
        combo.SelectedValue = NotchSettings.SystemBatteryDeviceId;
        Assert.Equal(NotchSettings.SystemBatteryDeviceId, window.ReadSettingsFromUi().BatteryDeviceId);
    });

    [Fact]
    public void LegacyGlassCapturePreferenceIsRetiredInFavorOfGhostMode() => SharedStaTestRunner.Run(() =>
    {
        using var fixture = new Fixture();
        var window = fixture.Window;
        window.NavigateToSection("Skins");
        window.SkinCombo.SelectedIndex = 1;
        Assert.Null(window.FindName("GlassHideCaptureCheck"));
        window.GpuRefractionCheck.IsChecked = true;
        Assert.False(window.ReadSettingsFromUi().InvisibleMode);
        window.GpuRefractionCheck.IsChecked = false;
        Assert.False(window.ReadSettingsFromUi().InvisibleMode);
        window.GlassPresetCombo.SelectedIndex = 1;
        Assert.False(window.ReadSettingsFromUi().InvisibleMode);
    });

    [Fact]
    public void MissingBatteryChoiceIsRetainedWhenDevicesRefresh() => SharedStaTestRunner.Run(() =>
    {
        using var fixture = new Fixture();
        var window = fixture.Window;
        window.PopulateBatterySources([new("headset", "Headphones", 42, true)]);
        window.BatteryDeviceCombo.SelectedValue = "headset";
        window.PopulateBatterySources([new("headset", "Headphones", 42, false)]);
        Assert.Equal(Loc.Get("settings.batteryDevice.unavailable", "Headphones"), ((ComboBoxItem)window.BatteryDeviceCombo.SelectedItem).Content);
        window.PopulateBatterySources(Array.Empty<BatteryDevice>());
        Assert.Equal("headset", window.ReadSettingsFromUi().BatteryDeviceId);
        Assert.Equal("headset", window.BatteryDeviceCombo.SelectedValue);
    });

    [Fact]
    public void LyricsProvidersReorderPersistAndStopAtTheListEdges() => SharedStaTestRunner.Run(() =>
    {
        using var fixture = new Fixture();
        var window = fixture.Window;
        window.EnableSpotifyLyricsCheck.IsChecked = true;
        Assert.Equal(LyricsProviderOrder.Default, window.ReadSettingsFromUi().LyricsProviderPriority);
        window.LyricsProviderList.SelectedIndex = 0;
        window.LyricsProviderUp.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(0, window.LyricsProviderList.SelectedIndex);
        window.LyricsProviderDown.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("NetEase,AMLL TTML DB,Kugou,QQ Music,LRCLIB,lrc mux", window.ReadSettingsFromUi().LyricsProviderPriority);
        Assert.Equal(1, window.LyricsProviderList.SelectedIndex);
        Assert.Equal(1.0, window.LyricsProviderUp.Opacity, 2);
        Assert.Equal(1.0, window.LyricsProviderDown.Opacity, 2);
        int last = window.LyricsProviderList.Items.Count - 1;
        window.LyricsProviderList.SelectedIndex = last;
        Assert.Equal(0.38, window.LyricsProviderDown.Opacity, 2);
        window.LyricsProviderDown.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(last, window.LyricsProviderList.SelectedIndex);
        Assert.False(string.IsNullOrWhiteSpace(window.LyricsProviderList.Tag as string));
        Assert.False(string.IsNullOrWhiteSpace(window.LyricsProviderLabel.Tag as string));
        Assert.False(string.IsNullOrWhiteSpace(window.LyricsProviderHeaderGrid.Tag as string));
        Assert.False(string.IsNullOrWhiteSpace(window.LyricsProviderTextPanel.Tag as string));
        Assert.False(string.IsNullOrWhiteSpace(window.LyricsProviderPanel.Tag as string));
        Assert.False(string.IsNullOrWhiteSpace(window.LyricsProviderHint.Tag as string));
        Assert.DoesNotContain("API key", window.LyricsProviderHint.Tag as string, StringComparison.OrdinalIgnoreCase);
    });
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    [Fact]
    public void InvisibleShortcutPersistsOnlyItsOwnChange() => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = new Fixture();
        var window = fixture.Window;
        double originalWidth = window.ReadSettingsFromUi().Width;
        window.WidthSlider.Value = originalWidth + 30;
        await window.ToggleInvisibleFromShortcutAsync();
        Assert.True(window.ReadSettingsFromUi().InvisibleMode);
        Assert.True(fixture.Service.Applied!.InvisibleMode);
        Assert.Equal(originalWidth, fixture.Service.Applied.Width);
        Assert.Equal(originalWidth + 30, window.ReadSettingsFromUi().Width);
        await window.ToggleInvisibleFromShortcutAsync();
        Assert.False(window.ReadSettingsFromUi().InvisibleMode);
        Assert.False(fixture.Service.Applied.InvisibleMode);
    });

    [Fact]
    public void TranslationModelsAreGroupedAndSelectionSurvivesHardwareRefresh() => SharedStaTestRunner.Run(() =>
    {
        using var fixture = new Fixture();
        var window = fixture.Window;
        Assert.Equal(9, window.TranslationModelCombo.Items.Count);
        Assert.Equal(2, window.TranslationModelCombo.Items.Groups!.Count);
        foreach (var model in TranslationModelCatalog.All)
        {
            window.TranslationModelCombo.SelectedValue = model.Id;
            Assert.Equal(model.Id, window.ReadSettingsFromUi().TranslationModelId);
            Assert.Equal(model.Id, Field<TranslationModelStore>(window, "_translationStore").Profile.Id);
            Assert.Contains(model.DisplayName, window.TranslationModelHint.Text);
        }
        typeof(SettingsWindow).GetField("_translationHardware", Private)!.SetValue(window, new TranslationHardware(16, 8, 8, "GPU"));
        typeof(SettingsWindow).GetMethod("RefreshTranslationModelDetails", Private)!.Invoke(window, null);
        Assert.Equal("gemma4-31b", window.ReadSettingsFromUi().TranslationModelId);
        Assert.Single(window.TranslationModelCombo.Items.Cast<object>().Where(x => x.ToString()!.Contains(Loc.Get("translation.recommended"))));
    });

    [Fact]
    public void AdvancedTranslationControlsClampPersistAndResetOnlyAdvancedValues() => SharedStaTestRunner.Run(() =>
    {
        using var fixture = new Fixture();
        var window = fixture.Window;
        window.EnableTranslationCheck.IsChecked = true;
        var inputs = Field<Dictionary<string, (TextBlock Label, TextBox Input)>>(window, "_translationAdvancedInputs");
        Assert.Equal(13, inputs.Count);
        inputs["GpuLayers"].Input.Text = "-10";
        inputs["CacheEntries"].Input.Text = "300";
        inputs["TimeoutSeconds"].Input.Text = "invalid";
        var snapshot = window.ReadSettingsFromUi();
        Assert.Equal(0, snapshot.TranslationGpuLayers);
        Assert.Equal(300, snapshot.TranslationCacheEntries);
        Assert.Equal(90, snapshot.TranslationTimeoutSeconds);
        window.ApplyPreview(snapshot);
        Assert.Equal("300", inputs["CacheEntries"].Input.Text);
        window.TranslationAdvancedReset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        snapshot = window.ReadSettingsFromUi();
        Assert.True(snapshot.EnableLiveTranslation);
        Assert.Equal(TranslationOptions.From(new NotchSettings()), TranslationOptions.From(snapshot));
    });


    [Fact]
    public void InstalledModelKeepsAVisibleConfirmationAndUpdatesUsageHint() => SharedStaTestRunner.Run(() =>
    {
        using var fixture = new Fixture();
        var window = fixture.Window;
        window.PresentTranslationModelAvailability(true);
        Assert.Equal(Visibility.Visible, window.TranslationReadyCard.Visibility);
        Assert.Equal(Loc.Get("translation.installComplete"), window.TranslationReadyTitle.Text);
        Assert.Equal(Visibility.Collapsed, window.TranslationDownloadButton.Visibility);
        Assert.Equal(Visibility.Collapsed, window.TranslationModelStatus.Visibility);
        Assert.Equal(Loc.Get("translation.readyEnableHint"), window.TranslationReadyHint.Text);
        window.EnableTranslationCheck.IsChecked = true;
        Assert.True(window.ReadSettingsFromUi().EnableLiveTranslation);
        Assert.Equal(Loc.Get("translation.readyManualHint"), window.TranslationReadyHint.Text);
        window.AutoTranslationCheck.IsChecked = true;
        Assert.Equal(Loc.Get("translation.readyAutoHint"), window.TranslationReadyHint.Text);
        window.PresentTranslationModelAvailability(true); // refresh preserves confirmation
        Assert.Equal(Visibility.Visible, window.TranslationReadyCard.Visibility);
        window.PresentTranslationModelAvailability(false);
        Assert.Equal(Visibility.Collapsed, window.TranslationReadyCard.Visibility);
        Assert.Equal(Visibility.Visible, window.TranslationDownloadButton.Visibility);
    });

    [Fact]
    public void DownloadCaptionAndPercentageUseTheSameRoundingAndAppPalette() => SharedStaTestRunner.Run(() =>
    {
        using var fixture = new Fixture();
        var window = fixture.Window;
        window.ShowTranslationDownloadProgress(new(TranslationDownloadStage.Downloading, 321_600_000, 2_500_000_000));
        Assert.Equal("12%", window.TranslationProgressPercent.Text);
        Assert.Equal(Loc.Get("translation.downloadingTitle"), window.TranslationProgressCaption.Text);
        Assert.Equal(Loc.Get("translation.downloading", 12), window.TranslationModelStatus.Text);
        Assert.Same(window.Resources["SurfaceBrush"], window.TranslationProgressCard.Background);
        Assert.Same(window.FindResource("AccentBrush"), window.TranslationModelProgress.Foreground);
        Assert.Same(window.Resources["TrackBg"], window.TranslationModelProgress.Background);
    });

    [Theory]
    [InlineData("http", "translation.downloadHttpError")]
    [InlineData("network", "translation.downloadConnectionError")]
    [InlineData("access", "translation.modelAccessDenied")]
    [InlineData("io", "translation.modelIoFailed")]
    [InlineData("unexpected", "translation.installUnexpected")]
    public void ModelInstallationErrorsExplainTheActualFailureCategory(string category, string key) => SharedStaTestRunner.RunAsync(async _ =>
    {
        using var fixture = new Fixture();
        fixture.Window.EnableTranslationCheck.IsChecked = true;
        Exception error = category switch
        {
            "http" => new System.Net.Http.HttpRequestException("message must not be shown", null, System.Net.HttpStatusCode.Forbidden),
            "network" => new System.Net.Http.HttpRequestException("message must not be shown"),
            "access" => new UnauthorizedAccessException("private path"),
            "io" => new System.IO.IOException("private path"),
            _ => new NullReferenceException("internal details")
        };
        await fixture.Window.RunTranslationModelActionAsync(_ => throw error);
        Assert.Equal(category == "http" ? Loc.Get(key, 403) : Loc.Get(key), fixture.Window.TranslationModelStatus.Text);
        Assert.Equal(Visibility.Collapsed, fixture.Window.TranslationProgressCard.Visibility);
        Assert.True(fixture.Window.TranslationImportButton.IsEnabled);
    });

    [Fact]
    public void ModelDownloadShowsFeedbackBeforeNetworkRepliesAndStopsAnimationOnCancel() => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool reducedMotion = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(false);
        using var fixture = new Fixture();
        var window = fixture.Window;
        var card = window.TranslationProgressCard;
        window.EnableTranslationCheck.IsChecked = true;
        var parent = (Panel)card.Parent;
        int index = parent.Children.IndexOf(card);
        parent.Children.Remove(card);
        var host = new BackgroundWindow { Width = 480, Height = 150, Content = card };
        host.Show();
        Task? download = null;
        try
        {
            download = window.RunTranslationModelActionAsync(token => Task.Delay(Timeout.Infinite, token), "translation.connecting");
            Assert.Equal(Visibility.Visible, card.Visibility);
            Assert.Equal(Loc.Get("translation.connecting"), window.TranslationProgressCaption.Text);
            Assert.Equal(Loc.Get("translation.cancel"), window.TranslationDownloadButton.Content);
            Assert.False(window.TranslationImportButton.IsEnabled);
            await WpfFrameWaiter.UntilAsync(() => window.TranslationProgressPulse.IsVisible, "download connecting animation", ct);
            Assert.True(window.TranslationPulseOffset.HasAnimatedProperties);
            window.ShowTranslationDownloadProgress(new(TranslationDownloadStage.Downloading, 1_000_000_000, 2_500_000_000, 10_000_000));
            Assert.Equal("40%", window.TranslationProgressPercent.Text);
            Assert.Contains("1.00 GB / 2.50 GB", window.TranslationProgressDetail.Text);
            Assert.Contains("10.0 MB/s", window.TranslationProgressDetail.Text);
            Assert.Equal(Visibility.Collapsed, window.TranslationProgressPulse.Visibility);
            await WpfFrameWaiter.UntilAsync(() => Math.Abs(window.TranslationModelProgress.Value - 40) < .01, "animated download percentage", ct);
            window.TranslationModelProgress.ApplyTemplate();
            window.TranslationModelProgress.UpdateLayout();
            var fill = (FrameworkElement)window.TranslationModelProgress.Template.FindName("PART_Indicator", window.TranslationModelProgress);
            Assert.InRange(fill.ActualWidth / window.TranslationModelProgress.ActualWidth, .39, .41);
            window.ShowTranslationDownloadProgress(new(TranslationDownloadStage.Verifying, 2_500_000_000, 2_500_000_000));
            Assert.Equal(Loc.Get("translation.verifying"), window.TranslationProgressCaption.Text);
            Assert.Equal(Visibility.Visible, window.TranslationProgressPulse.Visibility);
            AnimationConfig.SetReduceMotion(true);
            await WpfFrameWaiter.UntilAsync(() => !window.TranslationPulseOffset.HasAnimatedProperties, "reduced motion stops download sweep", ct);
            window.TranslationDownloadButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await download;
            Assert.Equal(Visibility.Collapsed, card.Visibility);
            Assert.Equal(Loc.Get("translation.downloadCancelled"), window.TranslationModelStatus.Text);
            Assert.True(window.TranslationImportButton.IsEnabled);
            Assert.False(window.TranslationPulseOffset.HasAnimatedProperties);
            await window.RunTranslationModelActionAsync(_ =>
            {
                Invoke(window, "TranslationModelChanged");
                throw new TranslationException("translation.downloadTimeout");
            });
            await window.Dispatcher.InvokeAsync(() => { });
            Assert.Equal(Loc.Get("translation.downloadTimeout"), window.TranslationModelStatus.Text);
        }
        finally
        {
            if (download is { IsCompleted: false })
            {
                window.TranslationDownloadButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await download;
            }
            host.Content = null;
            host.Close();
            parent.Children.Insert(index, card);
            AnimationConfig.SetReduceMotion(reducedMotion);
        }
    });

    [Theory]
    [InlineData("EnableBlurEffectsCheck", "EnableBlurEffects")]
    [InlineData("MediaArtBackgroundCheck", "ShowMediaArtBackground")]
    [InlineData("EnableSubjectBlurCheck", "EnableSubjectBlur")]
    [InlineData("EnableSmartCropCheck", "EnableSmartCrop")]
    [InlineData("EnableSpotifyLyricsCheck", "EnableSpotifyLyrics")]
    [InlineData("BrightenDarkLyricsBackgroundCheck", "BrightenDarkLyricsBackground")]
    [InlineData("EnableYouTubeSubtitlesCheck", "EnableYouTubeSubtitles")]
    [InlineData("IgnoreYouTubeAutoSubtitlesCheck", "IgnoreYouTubeAutoSubtitles")]
    [InlineData("DynamicIslandModeCheck", "EnableDynamicIslandMode")]
    [InlineData("AutoAnimationFpsCheck", "AutoAnimationFps")]
    [InlineData("HoverExpandCheck", "EnableHoverExpand")]
    [InlineData("DisableMouseLeaveAutoCloseCheck", "DisableMouseLeaveAutoClose")]
    [InlineData("KeepMediaPinnedCheck", "KeepMediaPinnedOnTrackChange")]
    [InlineData("ReopenLastViewCheck", "ReopenLastViewOnExpand")]
    [InlineData("StayBehindWindowsCheck", "StayBehindWindows")]
    [InlineData("HelloGreetingCheck", "EnableHelloGreeting")]
    [InlineData("HideOnExclusiveFullscreenCheck", "HideOnExclusiveFullscreen")]
    [InlineData("HideOnWindowedFullscreenCheck", "HideOnWindowedFullscreen")]
    [InlineData("IdleAutoHideCheck", "EnableIdleAutoHide")]
    [InlineData("MusicNotifyCheck", "ShowMusicNotifications")]
    [InlineData("SystemNotifyCheck", "ShowSystemNotifications")]
    [InlineData("HideCameraCheck", "HideCamera")]
    [InlineData("TrayTextCheck", "ClipboardCaptureText")]
    [InlineData("TrayImagesCheck", "ClipboardCaptureImages")]
    [InlineData("TrayFilesCheck", "ClipboardCaptureFiles")]
    [InlineData("TraySensitiveCheck", "ClipboardAutoDeleteSensitive")]
    [InlineData("ShowBatteryCheck", "ShowBatteryIndicator")]
    [InlineData("EnableWeatherCheck", "EnableWeather")]
    [InlineData("AutoCheckUpdatesCheck", "AutoCheckUpdates")]
    [InlineData("EnableOnlineArtworkCheck", "EnableOnlineArtworkLookup")]
    [InlineData("EnableOnlineLyricsCheck", "EnableOnlineLyrics")]
    [InlineData("EnablePrivacyIndicatorsCheck", "EnablePrivacyIndicators")]
    [InlineData("EnableBrowserUrlInspectionCheck", "EnableBrowserUrlInspection")]
    [InlineData("EnableDiagnosticLoggingCheck", "EnableDiagnosticLogging")]
    [InlineData("EnableSpotlightHistoryCheck", "EnableSpotlightHistory")]
    [InlineData("EnableTranslationCheck", "EnableLiveTranslation")]
    [InlineData("AutoTranslationCheck", "AutoLiveTranslation")]
    public void ToggleValuesRoundTripThroughPreviewAndPersistence(string controlName, string settingName) => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = new Fixture();
        var toggle = Field<ToggleButton>(fixture.Window, controlName);
        var property = typeof(NotchSettings).GetProperty(settingName)!;
        foreach (bool enabled in new[] { false, true, false })
        {
            toggle.IsChecked = enabled;
            var snapshot = fixture.Window.ReadSettingsFromUi();
            Assert.Equal(enabled, property.GetValue(snapshot));
            fixture.Window.ApplyPreview(snapshot);
            await fixture.Window.SaveAsync(snapshot, ct);
            Assert.Equal(enabled, property.GetValue(fixture.Service.Applied!));
        }
    });

    [Theory]
    [InlineData("WidthSlider", "Width", 350, 350)]
    [InlineData("HeightSlider", "Height", 48, 48)]
    [InlineData("RadiusSlider", "CornerRadius", 18, 18)]
    [InlineData("DynamicIslandWidthSlider", "DynamicIslandWidth", 250, 250)]
    [InlineData("DynamicIslandHeightSlider", "DynamicIslandHeight", 48, 48)]
    [InlineData("OpacitySlider", "Opacity", 70, 0.7)]
    [InlineData("BlurBrightnessSlider", "MediaBlurBrightnessBoost", 150, 1.5)]
    [InlineData("BlurDarkOverlaySlider", "MediaBlurDarkOverlay", 35, 0.35)]
    [InlineData("SpotifyCanvasBrightnessSlider", "SpotifyCanvasBrightness", 60, 0.6)]
    [InlineData("AnimationFpsSlider", "AnimationFps", 60, 60)]
    [InlineData("HoverDelaySlider", "HoverExpandDelay", 500, 500)]
    [InlineData("IdleAutoHideDelaySlider", "IdleAutoHideDelay", 5, 5000)]
    [InlineData("IdleAutoHideDelaySlider", "IdleAutoHideDelay", 0.5, 500)]
    [InlineData("IdleAutoHideDelaySlider", "IdleAutoHideDelay", 0.3, 300)]
    [InlineData("IdleAutoHideDelaySlider", "IdleAutoHideDelay", 0.1, 100)]
    [InlineData("TrayDelaySlider", "ClipboardCaptureDelay", 700, 700)]
    public void SlidersConvertDisplayUnitsToSettings(string controlName, string settingName, double value, double expected) => SharedStaTestRunner.Run(() =>
    {
        using var fixture = new Fixture();
        Field<RangeBase>(fixture.Window, controlName).Value = value;
        var snapshot = fixture.Window.ReadSettingsFromUi();
        Assert.Equal(expected, Convert.ToDouble(typeof(NotchSettings).GetProperty(settingName)!.GetValue(snapshot)), 5);
        fixture.Window.ApplyPreview(snapshot);
        Assert.Equal(value, Field<RangeBase>(fixture.Window, controlName).Value);
    });

    [Theory]
    [InlineData("WidgetCombo", "ExpandedWidget")]
    [InlineData("ClockPageStyleCombo", "ClockPageStyle")]
    public void EveryWidgetOptionSurvivesPreviewAndLocalization(string controlName, string settingName) => SharedStaTestRunner.Run(() =>
    {
        using var fixture = new Fixture();
        var combo = Field<ComboBox>(fixture.Window, controlName);
        var tags = combo.Items.Cast<ComboBoxItem>().Select(item => (string)item.Tag).ToArray();
        foreach (string tag in tags)
        {
            combo.SelectedItem = combo.Items.Cast<ComboBoxItem>().Single(item => (string)item.Tag == tag);
            var snapshot = fixture.Window.ReadSettingsFromUi();
            Assert.Equal(tag, typeof(NotchSettings).GetProperty(settingName)!.GetValue(snapshot));
            fixture.Window.ApplyPreview(snapshot);
            Invoke(fixture.Window, controlName switch
            {
                "WidgetCombo" => "RepopulateWidgetComboPreservingSelection",
                _ => "RepopulateClockPageStyleComboPreservingSelection"
            });
            Assert.Equal(tag, Assert.IsType<ComboBoxItem>(combo.SelectedItem).Tag);
        }
    });

    [Fact]
    public void FeatureTogglesUpdateDependentControls() => SharedStaTestRunner.Run(() =>
    {
        using var fixture = new Fixture();
        var window = fixture.Window;
        foreach (bool enabled in new[] { false, true })
        {
            window.EnableSpotifyLyricsCheck.IsChecked = enabled;
            Assert.Equal(enabled, window.BlurDarkOverlaySlider.IsEnabled);
            window.EnableYouTubeSubtitlesCheck.IsChecked = enabled;
            Assert.Equal(enabled, window.IgnoreYouTubeAutoSubtitlesCheck.IsEnabled);
            window.EnableBlurEffectsCheck.IsChecked = enabled;
            Assert.Equal(enabled, window.BlurBrightnessSlider.IsEnabled);
            window.HoverExpandCheck.IsChecked = enabled;
            Assert.Equal(enabled, window.HoverDelaySlider.IsEnabled);
            window.IdleAutoHideCheck.IsChecked = enabled;
            Assert.Equal(enabled, window.IdleAutoHideDelaySlider.IsEnabled);
            window.EnableWeatherCheck.IsChecked = enabled;
            Assert.Equal(enabled, window.ManualCityTextBox.IsEnabled);
            window.DynamicIslandModeCheck.IsChecked = enabled;
            Assert.Equal(enabled, window.DynamicIslandWidthSlider.IsEnabled);
            Assert.Equal(enabled, window.DynamicIslandHeightSlider.IsEnabled);
        }
        window.AutoAnimationFpsCheck.IsChecked = true;
        Assert.False(window.AnimationFpsSlider.IsEnabled);
        window.AutoAnimationFpsCheck.IsChecked = false;
        Assert.True(window.AnimationFpsSlider.IsEnabled);
    });

    [Fact]
    public void SearchShowsMatchesAndEmptyStateThenRestoresTheSettingsRows() => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = new Fixture();
        var window = fixture.Window;
        window.SettingsSearchBox.Text = "width";
        await WpfFrameWaiter.UntilAsync(() => window.SearchResultsStack.Children.Count > 0, "settings width search", ct);
        Assert.Equal(Visibility.Collapsed, window.SearchPlaceholder.Visibility);
        Assert.Equal(Visibility.Collapsed, window.SearchingEmptyState.Visibility);
        Assert.NotNull(window.WidthSlider.Parent);
        window.SettingsSearchBox.Text = "zzzzzz-no-setting-12345";
        await WpfFrameWaiter.UntilAsync(() => window.SearchingEmptyState.Visibility == Visibility.Visible, "empty settings search", ct);
        Assert.Empty(window.SearchResultsStack.Children);
        Assert.Contains("zzzzzz-no-setting-12345", window.SearchingEmptyQuery.Text);
        window.SettingsSearchBox.Clear();
        Assert.Empty(window.SearchResultsStack.Children);
        Assert.NotNull(window.WidthSlider.Parent);
        Assert.False(Field<bool>(window, "_isSearchMode"));
        Assert.Equal(Visibility.Visible, window.SearchPlaceholder.Visibility);
        window.SettingsSearchBox.Text = "opacity";
        await WpfFrameWaiter.UntilAsync(() => window.SearchResultsStack.Children.Count > 0, "a second settings search", ct);
        Invoke(window, "SearchingClearButton_Click", window.SearchingClearButton, new RoutedEventArgs());
        Assert.Equal("", window.SettingsSearchBox.Text);
        Assert.False(Field<bool>(window, "_isSearchMode"));
    });

    [Fact]
    public void LanguageChangeUpdatesLabelsWithoutLosingWidgetChoices() => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = new Fixture();
        var window = fixture.Window;
        NotchSettings? previewed = null;
        window.SettingsChanged += (_, s) => previewed = s;
        window.WidgetCombo.SelectedIndex = 2;
        foreach (string language in new[] { "vi", "ja", "en" })
        {
            window.LanguageCombo.SelectedItem = window.LanguageCombo.Items.Cast<ComboBoxItem>().Single(item => (string)item.Tag == language);
            await WpfFrameWaiter.UntilAsync(() => window.WidthLabel.Text == Loc.Get("settings.width") && previewed?.Language == language, "localized width label", ct);
            Assert.Equal(language, window.ReadSettingsFromUi().Language);
            Assert.Equal("wordclock", window.ReadSettingsFromUi().ExpandedWidget);
            Assert.Equal(Loc.Get("settings.windowTitle"), window.Title);
            Assert.Equal(language, previewed!.Language);
            Assert.Null(fixture.Service.Applied);
        }
    });

    [Theory]
    [InlineData("", "")]
    [InlineData("short", "settings.youtubeApi.statusTooShort")]
    [InlineData("wrong", "settings.youtubeApi.statusMustStart")]
    [InlineData("valid", "settings.youtubeApi.statusValid")]
    [InlineData("long", "settings.youtubeApi.statusUnexpectedLength")]
    public void ApiKeyValidationAndVisibilityKeepTheTwoEditorsInSync(string shape, string statusKey) => SharedStaTestRunner.Run(() =>
    {
        using var fixture = new Fixture();
        var window = fixture.Window;
        string value = shape switch
        {
            "wrong" => new string('x', 39),
            "valid" => "AIza" + new string('x', 35),
            "long" => "AIza" + new string('x', 50),
            _ => shape
        };
        window.YouTubeApiKeyPasswordBox.Password = value;
        Assert.Equal(statusKey == "" ? "" : Loc.Get(statusKey), window.YouTubeApiKeyStatus.Text);
        Invoke(window, "ToggleKeyVisibility_Click", window, new RoutedEventArgs());
        Assert.Equal(Visibility.Visible, window.YouTubeApiKeyTextBox.Visibility);
        Assert.Equal(value, window.YouTubeApiKeyTextBox.Text);
        window.YouTubeApiKeyTextBox.Text = value + "x";
        Assert.Equal(value + "x", window.YouTubeApiKeyPasswordBox.Password);
        Invoke(window, "ToggleKeyVisibility_Click", window, new RoutedEventArgs());
        Assert.Equal(Visibility.Collapsed, window.YouTubeApiKeyTextBox.Visibility);
        Assert.Equal(value + "x", window.ReadSettingsFromUi().YouTubeApiKey);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NavigationHoverPreservesSelectionAndReturnsToRestAfterRapidMoves(bool reducedMotion) => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(reducedMotion);
        try
        {
            using var fixture = new Fixture();
            var window = fixture.Window;
            window.Show();
            await WpfFrameWaiter.UntilAsync(() => window.IsLoaded && window.NavPanel.Opacity >= .999,
                "settings window loads before pointer hover", ct);
            var row = (StackPanel)window.NavAppearance.Child;
            var pointer = new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount);
            Invoke(window, "NavButton_MouseEnter", window.NavAppearance, pointer);
            await WpfFrameWaiter.UntilAsync(() => window.NavActiveHoverOverlay.Opacity >= .099,
                "selected settings pill lights up on hover", ct);
            Assert.Same(window.FindResource("AccentForegroundBrush"), window.NavAppearanceText.Foreground);
            Assert.Same(window.FindResource("AccentBrush"), window.NavActiveIndicator.Background);
            Assert.False(window.NavActiveHoverOverlay.IsHitTestVisible);
            bool motion = !reducedMotion && SystemParameters.ClientAreaAnimation;
            Assert.Equal(0, row.RenderTransform.Value.OffsetX);
            Assert.Equal(1, row.Children[0].RenderTransform.Value.M11, 3);

            Invoke(window, "NavButton_MouseEnter", window.NavSkins, pointer);
            await WpfFrameWaiter.UntilAsync(() => window.NavHoverIndicator.Opacity == 1,
                "unselected settings row reveals its hover surface", ct);
            Assert.Same(window.FindResource("AccentNavHoverBrush"), window.NavHoverIndicator.Background);
            Assert.Same(window.FindResource("AccentNavHoverBorderBrush"), window.NavHoverIndicator.BorderBrush);
            double displayedY = window.NavHoverIndicatorTranslate.Y;
            window.NavSkins.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseLeaveEvent });
            Assert.Equal("Skins", Field<string>(window, "_hoveredNav"));
            Assert.True(window.NavHoverIndicator.Opacity > .99);
            Invoke(window, "NavButton_MouseEnter", window.NavPrivacy, pointer);
            if (motion) Assert.Equal(displayedY, window.NavHoverIndicatorTranslate.Y, 3);

            Invoke(window, "NavButton_MouseEnter", window.NavBehavior, pointer);
            window.NavigateToSection("Behavior");
            await WpfFrameWaiter.UntilAsync(() => window.NavActiveHoverOverlay.Opacity >= .099,
                "newly selected settings pill keeps its hover feedback", ct);
            Assert.Same(window.FindResource("AccentForegroundBrush"), window.NavBehaviorText.Foreground);
            Invoke(window, "NavItemsHost_MouseLeave", window.NavItemsHost, pointer);
            var nextRow = (StackPanel)window.NavBehavior.Child;
            await WpfFrameWaiter.UntilAsync(() => window.NavActiveHoverOverlay.Opacity == 0 && window.NavHoverIndicator.Opacity == 0 &&
                nextRow.RenderTransform.Value.OffsetX == 0 && nextRow.Children[0].RenderTransform.Value.M11 == 1,
                "settings hover fully clears after leaving the navigation", ct);
            Assert.Same(window.FindResource("AccentForegroundBrush"), window.NavBehaviorText.Foreground);
            Assert.Equal("Behavior", Field<string>(window, "_activeNav"));
            Assert.False(Field<bool>(window, "_navHoverAnimating"));

            Invoke(window, "NavButton_MouseEnter", window.NavPrivacy, pointer);
            window.Hide();
            Assert.Equal(0, window.NavHoverIndicator.Opacity);
            Assert.False(Field<bool>(window, "_navHoverAnimating"));
        }
        finally { AnimationConfig.SetReduceMotion(previous); }
    });

    [Fact]
    public void SidebarHoverRetainsItsMomentumAcrossGapsAndWhenReenteringDuringFadeOut() => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(false);
        try
        {
            using var fixture = new Fixture();
            var window = fixture.Window;
            window.Show();
            await WpfFrameWaiter.UntilAsync(() => window.IsLoaded && window.NavPanel.Opacity >= .999,
                "sidebar loads before testing continuous hover", ct);
            if (!SystemParameters.ClientAreaAnimation) return;
            var pointer = new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount);
            Invoke(window, "NavButton_MouseEnter", window.NavSkins, pointer);
            Invoke(window, "AdvanceNavHoverMotion", .1);
            Invoke(window, "NavButton_MouseEnter", window.NavSpotlight, pointer);
            Invoke(window, "AdvanceNavHoverMotion", .016);
            object moving = Field<object>(window, "_navHoverState");
            Assert.True((double)moving.GetType().GetField("YVelocity")!.GetValue(moving)! > 0);
            window.NavSpotlight.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseLeaveEvent });
            Assert.Equal("Spotlight", Field<string>(window, "_hoveredNav"));
            Assert.Equal(moving, Field<object>(window, "_navHoverState"));
            Invoke(window, "NavButton_MouseEnter", window.NavBehavior, pointer);
            Assert.Equal(moving, Field<object>(window, "_navHoverState"));
            Invoke(window, "NavItemsHost_MouseLeave", window.NavItemsHost, pointer);
            Invoke(window, "AdvanceNavHoverMotion", .1);
            Invoke(window, "AdvanceNavHoverMotion", .1);
            Assert.InRange(window.NavHoverIndicator.Opacity, .001, .05);
            object fading = Field<object>(window, "_navHoverState");
            Invoke(window, "NavButton_MouseEnter", window.NavDevices, pointer);
            Assert.Equal(fading, Field<object>(window, "_navHoverState"));
            for (int frame = 0; frame < 10; frame++) Invoke(window, "AdvanceNavHoverMotion", .1);
            Assert.False(Field<bool>(window, "_navHoverAnimating"));
            Assert.Equal(1, window.NavHoverIndicator.Opacity);

            string? framesDirectory = Environment.GetEnvironmentVariable("VNOTCH_NAV_HOVER_FRAMES");
            if (!string.IsNullOrEmpty(framesDirectory))
            {
                System.IO.Directory.CreateDirectory(framesDirectory);
                AccentTheme.Apply("pink");
                Invoke(window, "ResetProgressiveBlurElements", true);
                var destinations = new[] { window.NavSkins, window.NavSpotlight, window.NavBehavior, window.NavFileTray, window.NavPrivacy };
                for (int frame = 0; frame < 100; frame++)
                {
                    if (frame % 20 == 0) Invoke(window, "NavButton_MouseEnter", destinations[frame / 20], pointer);
                    await WpfFrameWaiter.NextAsync(ct);
                    var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                        (int)Math.Ceiling(window.NavItemsHost.ActualWidth * 2),
                        (int)Math.Ceiling(window.NavItemsHost.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
                    var visual = new DrawingVisual();
                    using (var drawing = visual.RenderOpen())
                    {
                        var bounds = new Rect(0, 0, window.NavItemsHost.ActualWidth, window.NavItemsHost.ActualHeight);
                        drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(15, 15, 18)), null, bounds);
                        drawing.DrawRectangle(new VisualBrush(window.NavItemsHost), null, bounds);
                    }
                    bitmap.Render(visual);
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                    using var file = System.IO.File.Create(System.IO.Path.Combine(framesDirectory, $"frame-{frame:000}.png"));
                    encoder.Save(file);
                }
            }
            window.Hide();
            Assert.False(Field<bool>(window, "_navHoverAnimating"));
            Assert.Equal(0, window.NavHoverIndicator.Opacity);
        }
        finally { AccentTheme.Apply("default"); AnimationConfig.SetReduceMotion(previous); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SectionNavigationShowsOnlyTheLatestRequestedPanelAndResetsItsScroll(bool reducedMotion) => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(reducedMotion);
        try
        {
            using var fixture = new Fixture();
            var window = fixture.Window;
            var panels = Field<Dictionary<string, StackPanel>>(window, "_navPanels");
            int sectionIdx = 0;
            foreach (string section in panels.Keys.Reverse())
            {
                AnimationConfig.SetReduceMotion(reducedMotion || sectionIdx++ > 1);
                Invoke(window, "NavigateToSection", section);
                if (!reducedMotion && sectionIdx == 1 && window.IsLoaded && SystemParameters.ClientAreaAnimation)
                {
                    Assert.True(Field<bool>(window, "_navPillAnimating"));
                }
                await WpfFrameWaiter.UntilAsync(() => panels[section].Visibility == Visibility.Visible && panels.Values.Count(panel => panel.Visibility == Visibility.Visible) == 1, "settings section navigation", ct);
                Assert.Equal(section, Field<string>(window, "_activeNav"));
                Assert.Equal(0, window.SettingsScrollViewer.VerticalOffset);
                Assert.True(window.NavActiveIndicatorTranslate.Y >= 0);
            }
            string[] destinations = panels.Keys.Where(key => key != Field<string>(window, "_activeNav")).Take(3).ToArray();
            AnimationConfig.SetReduceMotion(reducedMotion);
            foreach (string destination in destinations) Invoke(window, "NavigateToSection", destination);
            string latest = destinations[^1];
            await WpfFrameWaiter.UntilAsync(() => panels[latest].Visibility == Visibility.Visible && panels.Values.Count(panel => panel.Visibility == Visibility.Visible) == 1, "latest settings section wins", ct);
            await WpfFrameWaiter.UntilAsync(() => window.NavActiveIndicatorScale.ScaleX == 1 && window.NavActiveIndicatorScale.ScaleY == 1,
                "settings selection pill settles after rapid selection", ct);
            Invoke(window, "NavigateToSection", latest);
            Assert.Equal(latest, Field<string>(window, "_activeNav"));
        }
        finally { AnimationConfig.SetReduceMotion(previous); }
    });

    [Fact]
    public void SidebarSelectionMovesAndStretchesBeforeSettlingAndCanReverseMidFlight() => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(false);
        try
        {
            using var fixture = new Fixture();
            var window = fixture.Window;
            string? framesDirectory = Environment.GetEnvironmentVariable("VNOTCH_NAV_MOTION_FRAMES");
            if (!string.IsNullOrEmpty(framesDirectory))
            {
                window.Resources["AccentBrush"] = new SolidColorBrush(Color.FromRgb(0xEC, 0x82, 0xC4));
                window.Resources["AccentForegroundBrush"] = Brushes.Black;
            }
            window.Show();
            await WpfFrameWaiter.UntilAsync(() => window.IsLoaded && window.NavPanel.Opacity >= .999,
                "settings sidebar settles before testing its selection pill", ct);
            if (!SystemParameters.ClientAreaAnimation) return;
            double start = window.NavActiveIndicator.TranslatePoint(new Point(0, 0), window.NavItemsStack).Y;
            double target = window.NavPrivacy.TranslatePoint(new Point(0, 0), window.NavItemsStack).Y;
            window.NavigateToSection("Privacy");
            Invoke(window, "AdvanceNavPillMotion", .08);
            double between = window.NavActiveIndicator.TranslatePoint(new Point(0, 0), window.NavItemsStack).Y;
            double stretched = window.NavActiveIndicatorScale.ScaleY;
            var state = Field<object>(window, "_navPillState");
            double topVelocity = (double)state.GetType().GetField("TopVelocity")!.GetValue(state)!;
            double bottomVelocity = (double)state.GetType().GetField("BottomVelocity")!.GetValue(state)!;
            Assert.InRange(between, start + .1, target - .1);
            Assert.True(stretched > 1.05);
            Assert.True(window.NavActiveIndicatorScale.ScaleX < 1);

            window.NavigateToSection("Appearance");
            Assert.Equal(between, window.NavActiveIndicator.TranslatePoint(new Point(0, 0), window.NavItemsStack).Y, 3);
            Assert.Equal(stretched, window.NavActiveIndicatorScale.ScaleY, 3);
            var retargeted = Field<object>(window, "_navPillState");
            Assert.Equal(topVelocity, (double)retargeted.GetType().GetField("TopVelocity")!.GetValue(retargeted)!);
            Assert.Equal(bottomVelocity, (double)retargeted.GetType().GetField("BottomVelocity")!.GetValue(retargeted)!);
            await WpfFrameWaiter.UntilAsync(() => !Field<bool>(window, "_navPillAnimating"),
                "reversed sidebar selection settles and detaches its frame handler", ct);
            Assert.Equal(window.NavAppearance.TranslatePoint(new Point(0, 0), window.NavItemsStack).Y,
                window.NavActiveIndicatorTranslate.Y, 3);
            Assert.Equal(1, window.NavActiveIndicatorScale.ScaleX);
            Assert.Equal(1, window.NavActiveIndicatorScale.ScaleY);
            if (!string.IsNullOrEmpty(framesDirectory))
            {
                window.ResetProgressiveBlurElements(isMenuList: true);
                System.IO.Directory.CreateDirectory(framesDirectory);
                window.NavigateToSection("Skins");
                window.SyncNavIndicators(animate: false);
                var iconBrushMethod = typeof(SettingsWindow).GetMethod("ApplyNavIconBrush", BindingFlags.Static | BindingFlags.NonPublic)!;
                int frame = 0;
                foreach (string section in new[] { "Devices", "Privacy", "Skins" })
                {
                    window.NavigateToSection(section);
                    foreach (var button in window.NavItemsStack.Children.OfType<Border>())
                    {
                        if (button.Child is not StackPanel row || row.Children.Count < 2) continue;
                        Brush brush = button.Tag as string == section ? Brushes.Black : Brushes.Silver;
                        iconBrushMethod.Invoke(null, new object[] { row.Children[0], brush, false });
                        if (row.Children[1] is TextBlock text) text.Foreground = brush;
                    }
                    for (int step = 0; step < 36; step++, frame++)
                    {
                        Invoke(window, "AdvanceNavPillMotion", .016);
                        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                            (int)Math.Ceiling(window.NavItemsHost.ActualWidth * 2),
                            (int)Math.Ceiling(window.NavItemsHost.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
                        bitmap.Render(window.NavItemsHost);
                        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                        using var file = System.IO.File.Create(System.IO.Path.Combine(framesDirectory, $"frame-{frame:000}.png"));
                        encoder.Save(file);
                    }
                }
            }
        }
        finally { AnimationConfig.SetReduceMotion(previous); }
    });

    [Fact]
    public void SidebarReducedMotionClearsAnInFlightSelectionAndUsesTheExactRow() => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(false);
        try
        {
            using var fixture = new Fixture();
            var window = fixture.Window;
            window.Show();
            await WpfFrameWaiter.UntilAsync(() => window.IsLoaded && window.NavPanel.Opacity >= .999,
                "settings sidebar loads before reducing motion", ct);
            window.NavigateToSection("System");
            AnimationConfig.SetReduceMotion(true);
            window.SyncNavIndicators(animate: false);
            Assert.False(Field<bool>(window, "_navPillAnimating"));
            Assert.False(window.NavActiveIndicatorTranslate.HasAnimatedProperties);
            Assert.False(window.NavActiveIndicatorScale.HasAnimatedProperties);
            Assert.Equal(System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(window.NavSystem).Y + window.NavSystem.Margin.Top,
                window.NavActiveIndicatorTranslate.Y, 3);
            Assert.Equal(1, window.NavActiveIndicatorScale.ScaleX);
            Assert.Equal(1, window.NavActiveIndicatorScale.ScaleY);
        }
        finally { AnimationConfig.SetReduceMotion(previous); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CascadeWithoutTailBlurPreservesOpacityAndReleasesEveryClock(bool menu) => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(false);
        try
        {
            using var fixture = new Fixture();
            var items = Enumerable.Range(0, 8).Select(index => (UIElement)new Border
            {
                Opacity = index == 7 ? .4 : 1,
                IsEnabled = index != 7
            }).ToArray();
            fixture.Window.ApplyProgressiveBlurCascade(items, 0, menu ? -1 : 1, menu, 60);
            Assert.All(items.Take(6), item => Assert.IsType<System.Windows.Media.Effects.BlurEffect>(item.Effect));
            Assert.All(items.Skip(6), item => Assert.Null(item.Effect));
            await WpfFrameWaiter.UntilAsync(() => items.All(item => !item.HasAnimatedProperties &&
                !item.RenderTransform.HasAnimatedProperties && item.Effect == null), "cascade releases blur and motion clocks", ct);
            Assert.All(items.Take(7), item => Assert.Equal(1, item.Opacity));
            Assert.Equal(.4, items[7].Opacity);
            fixture.Window.ResetProgressiveBlurElements(menu);
            Assert.All(items, item => Assert.Equal(0, ((TranslateTransform)item.RenderTransform).Y));
        }
        finally { AnimationConfig.SetReduceMotion(previous); }
    });

    [Fact]
    public void EntranceAnimationFinishesItsShellHeaderSocialIconsAndFooter() => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = new Fixture();
        var window = fixture.Window;
        window.Left = 80;
        window.Top = 60;
        Invoke(window, "PlayEntranceAnimation");
        Assert.True(window.ShellScale.ScaleX < 1);
        Assert.Equal(new Point(0.5, 0), window.MainShell.RenderTransformOrigin);
        await WpfFrameWaiter.UntilAsync(() => Math.Abs(window.ShellScale.ScaleX - 1) < 1e-6 && Math.Abs(window.ShellScale.ScaleY - 1) < 1e-6 && Math.Abs(window.FooterTranslate.Y) < 1e-6, "settings entrance shell and footer", ct);
        Assert.Equal(80, window.Left);
        Assert.Equal(60, window.Top);
        Assert.Equal(1, window.SettingsHeader.Opacity);
        Assert.Equal(1, window.SocialWebsite.Opacity);
        Assert.Equal(1, window.SocialGitHub.Opacity);
        Assert.Equal(1, window.SocialFacebook.Opacity);
        Assert.Equal(1, window.SocialDiscord.Opacity);
        Assert.Equal(new CornerRadius(24), window.MainShell.CornerRadius);
        await WpfFrameWaiter.UntilAsync(() => window.MainShell.Effect is System.Windows.Media.Effects.DropShadowEffect { Opacity: 0.42 }, "settings entrance shadow", ct);
    });

    [Fact]
    public void SmoothWheelScrollingReachesItsTargetAndOpenDropdownBlocksPageScrolling() => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = new Fixture();
        var window = fixture.Window;
        var content = new StackPanel();
        for (int i = 0; i < 20; i++) content.Children.Add(new Border { Height = 50 });
        window.SettingsScrollViewer.Content = content;
        var parent = Assert.IsAssignableFrom<Panel>(window.SettingsScrollViewer.Parent);
        int index = parent.Children.IndexOf(window.SettingsScrollViewer);
        parent.Children.Remove(window.SettingsScrollViewer);
        var host = new BackgroundWindow { Width = 300, Height = 200, Content = window.SettingsScrollViewer };
        try
        {
            host.Show();
            await WpfFrameWaiter.NextAsync(ct);
            Assert.True(window.SettingsScrollViewer.ScrollableHeight > 500);
            var wheel = new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, -120) { RoutedEvent = System.Windows.Input.Mouse.PreviewMouseWheelEvent };
            Invoke(window, "SettingsScrollViewer_PreviewMouseWheel", window.SettingsScrollViewer, wheel);
            Assert.True(wheel.Handled);
            Assert.True(Field<bool>(window, "_isScrollAnimating"));
            double target = Field<double>(window, "_scrollTarget");
            await WpfFrameWaiter.UntilAsync(() => !Field<bool>(window, "_isScrollAnimating"), "smooth settings wheel scroll settles", ct);
            Assert.Equal(target, window.SettingsScrollViewer.VerticalOffset);
            var originalCombo = window.WidgetCombo;
            window.WidgetCombo = new OpenDropDownComboBox();
            try
            {
                var blocked = new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, -120) { RoutedEvent = System.Windows.Input.Mouse.PreviewMouseWheelEvent };
                Invoke(window, "SettingsScrollViewer_PreviewMouseWheel", window.SettingsScrollViewer, blocked);
                Assert.True(blocked.Handled);
                Assert.False(Field<bool>(window, "_isScrollAnimating"));
                Assert.Equal(target, window.SettingsScrollViewer.VerticalOffset);
            }
            finally { window.WidgetCombo = originalCombo; }
            var reverse = new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, 1200) { RoutedEvent = System.Windows.Input.Mouse.PreviewMouseWheelEvent };
            Invoke(window, "SettingsScrollViewer_PreviewMouseWheel", window.SettingsScrollViewer, reverse);
            await WpfFrameWaiter.UntilAsync(() => { window.SettingsScrollViewer.UpdateLayout(); return !Field<bool>(window, "_isScrollAnimating"); }, "settings scroll clamps to top", ct);
            Assert.Equal(0, window.SettingsScrollViewer.VerticalOffset);
        }
        finally
        {
            host.Content = null;
            host.Close();
            parent.Children.Insert(index, window.SettingsScrollViewer);
        }
    });

    [Theory]
    [InlineData(null, "native,english,auto")]
    [InlineData("", "native,english,auto")]
    [InlineData("auto,english,native", "auto,english,native")]
    [InlineData("auto,invalid, english", "auto,english,native")]
    [InlineData("native,native,auto", "native,auto,english")]
    public void SubtitlePriorityRestoresAllSupportedModesExactlyOnce(string? input, string expected) => SharedStaTestRunner.Run(() =>
    {
        using var fixture = new Fixture();
        Field<NotchSettings>(fixture.Window, "_settings").SubtitlePriority = input!;
        Invoke(fixture.Window, "LoadSubtitlePriority");
        Assert.Equal(expected, typeof(SettingsWindow).GetMethod("GetSubtitlePriorityString", Private)!.Invoke(fixture.Window, null));
        var items = fixture.Window.SubtitlePriorityItems.Items;
        Assert.Equal(3, items.Count);
        foreach (object item in items) Assert.False(string.IsNullOrWhiteSpace(item.ToString()));
    });

    private static T Field<T>(SettingsWindow window, string name) => (T)typeof(SettingsWindow).GetField(name, Private)!.GetValue(window)!;

    // The settings window stays unshown. Supply the open state without creating
    // a native popup, while the scroll viewer renders in an input-safe host.
    private sealed class OpenDropDownComboBox : ComboBox
    {
        static OpenDropDownComboBox() => IsDropDownOpenProperty.OverrideMetadata(typeof(OpenDropDownComboBox),
            new FrameworkPropertyMetadata(true, null, (_, _) => true));
    }
    private static void Invoke(SettingsWindow window, string method, params object?[] arguments) =>
        typeof(SettingsWindow).GetMethod(method, Private)!.Invoke(window, arguments);

    private sealed class Fixture : IDisposable
    {
        internal RecordingService Service { get; } = new();
        internal SettingsWindow Window { get; }
        internal Fixture(IUpdateService? updates = null)
        {
            Loc.SetLanguage("en");
            BackgroundTestWindows.EnsureApplicationResources();
            Window = new SettingsWindow(new NotchSettings { Language = "en", AutoCheckUpdates = false, EnableWeather = false }, Service, updateService: updates);
            BackgroundTestWindows.ProtectInput(Window);
        }
        public void Dispose() { Window.Close(); Loc.SetLanguage("en"); }
    }

    [Fact]
    public void PrereleasePreferencePersistsAndTranslationShowsVietnameseExperimentalBadge() => SharedStaTestRunner.RunAsync(async ct =>
    {
        var updates = new PrereleasePreferenceUpdateService();
        using var fixture = new Fixture(updates);
        var window = fixture.Window;
        Assert.False(window.IncludePrereleaseUpdatesCheck.IsChecked);
        window.IncludePrereleaseUpdatesCheck.IsChecked = true;
        var snapshot = window.ReadSettingsFromUi();
        Assert.True(snapshot.IncludePrereleaseUpdates);
        await window.SaveAsync(snapshot, ct);
        Assert.True(fixture.Service.Applied!.IncludePrereleaseUpdates);
        Loc.SetLanguage("vi");
        Invoke(window, "ApplyLocalization");
        Assert.Equal("THỬ NGHIỆM", window.TranslationExperimentalBadge.Text);
        Assert.Contains("pre-release", window.IncludePrereleaseUpdatesCheck.Content.ToString());
        window.LocalOnlyModeCheck.IsChecked = true;
        Assert.False(window.IncludePrereleaseUpdatesCheck.IsEnabled);
    });

    private sealed class PrereleasePreferenceUpdateService : IUpdateService
    {
        public bool IncludePrereleases { get; private set; }
        public string CurrentVersion => "1.0.0";
        public UpdateInfo? LatestUpdateInfo => null;
        public event EventHandler<UpdateInfo?>? UpdateCheckCompleted { add { /* no-op */ } remove { /* no-op */ } }
        public Task<UpdateInfo?> CheckForUpdatesAsync(bool includePrereleases = false, bool forceRefresh = false)
        {
            IncludePrereleases = includePrereleases;
            return Task.FromResult<UpdateInfo?>(null);
        }
        public Task<IReadOnlyList<UpdateInfo>> GetAllReleasesAsync() => Task.FromResult<IReadOnlyList<UpdateInfo>>([]);
        public Task<bool> DownloadAndInstallUpdateAsync(UpdateInfo updateInfo, IProgress<double>? progress = null, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    [Theory]
    [InlineData("Appearance", false)]
    [InlineData("Behavior", true)]
    [InlineData("Devices", true)]
    [InlineData("System", true)]
    [InlineData("Privacy", true)]
    [InlineData("Spotlight", true)]
    [InlineData("Advanced", true)]
    [InlineData("Performance", true)]
    [InlineData("Donating", true)]
    [InlineData("Updates", true)]
    [InlineData("Searching", true)]
    [InlineData("Skins", true)]
    public void SettingsSaveClosesEverySectionOnceAndPersistsSnapshot(string section, bool fastClose) => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previousReducedMotion = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(fastClose);
        try
        {
            using var fixture = new Fixture();
            var window = fixture.Window;
            window.Top = 20;
            window.Left = 20;
            var flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic;
            string nav = (string)typeof(SettingsWindow).GetField("NavSection" + section, flags)!.GetRawConstantValue()!;
            typeof(SettingsWindow).GetField("_activeNav", flags)!.SetValue(window, nav);
            int closing = 0;
            bool closed = false;
            window.AnimatedClosing += (_, _) => closing++;
            window.Closed += (_, _) => closed = true;
            window.WidthSlider.Value = 400;
            typeof(SettingsWindow).GetMethod("Save_Click", flags)!.Invoke(window, [window, new RoutedEventArgs()]);
            typeof(SettingsWindow).GetMethod("CloseWithAnimation", flags)!.Invoke(window, null);
            await WpfFrameWaiter.UntilAsync(() => closed && fixture.Service.Applied != null, "settings save and close", ct);
            Assert.Equal(400, fixture.Service.Applied!.Width);
            Assert.Equal(1, closing);
            Assert.Equal(0, window.Opacity);
        }
        finally { AnimationConfig.SetReduceMotion(previousReducedMotion); }
    });

    [Fact]
    public void SettingsApplyClosesWindowEvenDuringEntranceTailAndPersistsSnapshot() => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = new Fixture();
        var window = fixture.Window;
        window.Top = 20;
        window.Left = 20;
        Invoke(window, "PlayEntranceAnimation");
        int closing = 0;
        bool closed = false;
        window.AnimatedClosing += (_, _) => closing++;
        window.Closed += (_, _) => closed = true;
        window.WidthSlider.Value = 380;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(SettingsWindow).GetMethod("Apply_Click", flags)!.Invoke(window, [window, new RoutedEventArgs()]);
        typeof(SettingsWindow).GetMethod("CloseWithAnimation", flags)!.Invoke(window, null);
        await WpfFrameWaiter.UntilAsync(() => closed && fixture.Service.Applied != null, "settings apply and close", ct);
        Assert.Equal(380, fixture.Service.Applied!.Width);
        Assert.Equal(1, closing);
        Assert.Equal(0, window.Opacity);
    });

    [Fact]
    public void SettingsCancelRevertsPreviewWithoutSaving() => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = new Fixture();
        var window = fixture.Window;
        window.Top = 20;
        window.Left = 20;
        int originalWidth = window.ReadSettingsFromUi().Width;
        NotchSettings? preview = null;
        bool closed = false;
        window.SettingsChanged += (_, settings) => preview = settings;
        window.Closed += (_, _) => closed = true;
        window.WidthSlider.Value = 400;
        window.ApplyPreview(window.ReadSettingsFromUi());
        typeof(SettingsWindow).GetMethod("Cancel_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [window, new RoutedEventArgs()]);
        await WpfFrameWaiter.UntilAsync(() => closed, "settings cancel and close", ct);
        Assert.Equal(originalWidth, preview!.Width);
        Assert.Null(fixture.Service.Applied);
    });

    [Fact]
    public void CacheCleanupDeletesOnlyKnownCacheAndPreservesRecoveryFiles() => SharedStaTestRunner.Run(() =>
    {
        string directory = System.IO.Directory.CreateTempSubdirectory("vnotch-cache-cleanup-test-").FullName;
        try
        {
            string appData = System.IO.Path.Combine(directory, "app-data");
            string binary = System.IO.Path.Combine(directory, "binary");
            System.IO.Directory.CreateDirectory(appData);
            System.IO.Directory.CreateDirectory(binary);
            foreach (string name in new[] { "cache", "canvas_cache", "lyrics_cache", "thumbnails", "temp" })
            {
                string nested = System.IO.Path.Combine(appData, name, "nested");
                System.IO.Directory.CreateDirectory(nested);
                System.IO.File.WriteAllText(System.IO.Path.Combine(nested, "cached.bin"), "test");
            }
            foreach (string name in new[] { "source_cache.json", "settings.corrupt-test.json", "settings.json", "keep.txt" })
                System.IO.File.WriteAllText(System.IO.Path.Combine(appData, name), "test");
            System.IO.File.WriteAllText(System.IO.Path.Combine(binary, "vnotch-debug.log.old"), "test");
            var flags = BindingFlags.Static | BindingFlags.NonPublic;
            Assert.Equal(5, typeof(SettingsWindow).GetMethod("DeleteCacheDirectories", flags)!.Invoke(null, [appData]));
            Assert.Equal(2, typeof(SettingsWindow).GetMethod("DeleteExplicitCacheFiles", flags)!.Invoke(null, [appData, binary]));
            Assert.True(System.IO.File.Exists(System.IO.Path.Combine(appData, "settings.corrupt-test.json")));
            Assert.True(System.IO.File.Exists(System.IO.Path.Combine(appData, "settings.json")));
            Assert.True(System.IO.File.Exists(System.IO.Path.Combine(appData, "keep.txt")));
            Assert.Equal(0, typeof(SettingsWindow).GetMethod("DeleteCacheDirectories", flags)!.Invoke(null, [appData]));
        }
        finally
        {
            string root = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath())) + System.IO.Path.DirectorySeparatorChar;
            if (!System.IO.Path.GetFullPath(directory).StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Cleanup directory is outside the temporary root.");
            System.IO.Directory.Delete(directory, recursive: true);
        }
    });

    [Theory]
    [InlineData("frosted", 0.20)]
    [InlineData("dark", 0.25)]
    [InlineData("ultrathin", 0.10)]
    [InlineData("thin", 0.20)]
    [InlineData("regular", 0.20)]
    [InlineData("thick", 0.35)]
    [InlineData("ultrathick", 0.50)]
    [InlineData("clear", 0.05)]
    public void GlassPresetsSurviveLocalizationAndPreserveTheCustomSlot(string preset, double blur) => SharedStaTestRunner.Run(() =>
    {
        using var fixture = new Fixture();
        var window = fixture.Window;
        Assert.Equal(9, window.GlassPresetCombo.Items.Count);
        window.SkinCombo.SelectedIndex = 1;
        window.GlassBlurSlider.Value = 40;
        window.GlassRefractionSlider.Value = 70;
        window.GlassFpsSlider.Value = 120;
        window.UltimateGpuCheck.IsChecked = true;
        window.GpuRefractionCheck.IsChecked = false;
        window.GlassPresetCombo.SelectedItem = window.GlassPresetCombo.Items.Cast<ComboBoxItem>().Single(item => (string)item.Tag == preset);
        var snapshot = window.ReadSettingsFromUi();
        Assert.Equal("liquidglass", snapshot.NotchStyle);
        Assert.Equal(preset, snapshot.LiquidGlassPreset);
        Assert.Equal(blur, snapshot.LiquidGlass.BlurAmount, 5);
        Assert.Equal(120, snapshot.LiquidGlass.TargetFps);
        Assert.False(snapshot.LiquidGlass.UseGpuRefraction);
        Assert.True(snapshot.LiquidGlass.UseUltimateGpu);
        Assert.Equal(Visibility.Visible, window.UltimateGpuWarning.Visibility);
        foreach (string language in new[] { "vi", "ja", "en" })
        {
            Loc.SetLanguage(language);
            Invoke(window, "ApplyLiquidGlassLocalization");
            Assert.Equal(9, window.GlassPresetCombo.Items.Count);
            Assert.Equal(preset, ((ComboBoxItem)window.GlassPresetCombo.SelectedItem).Tag);
            Assert.Equal(blur, window.ReadSettingsFromUi().LiquidGlass.BlurAmount, 5);
        }
        window.GlassPresetCombo.SelectedItem = window.GlassPresetCombo.Items.Cast<ComboBoxItem>().Single(item => (string)item.Tag == "custom");
        Assert.Equal(40, window.GlassBlurSlider.Value);
        Assert.Equal(70, window.GlassRefractionSlider.Value);
        Assert.Equal(0.4, window.ReadSettingsFromUi().LiquidGlassCustom!.BlurAmount, 5);
        window.GlassBlurSlider.Value = 55;
        Assert.Equal("custom", ((ComboBoxItem)window.GlassPresetCombo.SelectedItem).Tag);
        window.SkinCombo.SelectedIndex = 0;
        Assert.Equal("default", window.ReadSettingsFromUi().NotchStyle);
    });

    [Theory]
    [InlineData("GlassBlurSlider", "BlurAmount", 40, 0.4)]
    [InlineData("GlassRefractionSlider", "Refraction", 70, 0.7)]
    [InlineData("GlassEdgeBendSlider", "EdgeBend", 40, 0.4)]
    [InlineData("GlassChromSlider", "ChromaticAberration", 50, 0.5)]
    [InlineData("GlassEdgeHighlightSlider", "EdgeHighlight", 50, 0.5)]
    [InlineData("GlassTouchLightSlider", "TouchLight", 50, 0.5)]
    [InlineData("GlassSpecularSlider", "Specular", 50, 0.5)]
    [InlineData("GlassFresnelSlider", "Fresnel", 50, 0.5)]
    [InlineData("GlassDistortionSlider", "Distortion", 30, 0.3)]
    [InlineData("GlassGrainSlider", "Noise", 20, 0.2)]
    [InlineData("GlassZRadiusSlider", "ZRadius", 40, 0.4)]
    [InlineData("GlassOpacitySlider", "Opacity", 60, 0.6)]
    [InlineData("GlassSaturationSlider", "Saturation", 30, 0.3)]
    [InlineData("GlassBrightnessSlider", "Brightness", 20, 0.2)]
    [InlineData("GlassShadowOpacitySlider", "ShadowOpacity", 40, 0.4)]
    [InlineData("GlassShadowSpreadSlider", "ShadowSpread", 20, 20)]
    [InlineData("GlassBevelModeSlider", "BevelMode", 1, 1)]
    [InlineData("GlassFpsSlider", "TargetFps", 120, 120)]
    public void GlassControlsRoundTripDisplayUnitsAndHiddenOptics(string control, string property, double value, double expected) => SharedStaTestRunner.Run(() =>
    {
        using var fixture = new Fixture();
        var window = fixture.Window;
        Field<RangeBase>(window, control).Value = value;
        var snapshot = window.ReadSettingsFromUi();
        Assert.Equal(expected, Convert.ToDouble(typeof(LiquidGlassConfig).GetProperty(property)!.GetValue(snapshot.LiquidGlass)), 5);
        Assert.Equal("custom", snapshot.LiquidGlassPreset);
        window.ApplyPreview(snapshot);
        Assert.Equal(value, Field<RangeBase>(window, control).Value);
    });

    [Theory]
    [InlineData(0, 3)]
    [InlineData(3, 0)]
    [InlineData(1, 2)]
    public void SettingsTabDragReordersAndVisibilityTogglePersists(int from, int to) => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = new Fixture();
        var window = fixture.Window;
        NotchSettings? previewed = null;
        window.SettingsChanged += (_, s) => previewed = s;
        var rows = window.NavTabsSettingsContainer.Children.OfType<Border>().ToList();
        var expected = rows.Select(row => row.Tag.ToString()!).ToList();
        string moved = expected[from];
        expected.RemoveAt(from);
        expected.Insert(to, moved);
        var row = rows[from];
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(SettingsWindow).GetField("_settingsNavDragRow", flags)!.SetValue(window, row);
        typeof(SettingsWindow).GetField("_isSettingsNavRowDragging", flags)!.SetValue(window, true);
        typeof(SettingsWindow).GetField("_settingsNavInitialSlot", flags)!.SetValue(window, from);
        typeof(SettingsWindow).GetField("_settingsNavTargetSlot", flags)!.SetValue(window, from);
        ((TransformGroup)row.RenderTransform).Children.OfType<TranslateTransform>().Single().Y = (to - from) * 44;
        Invoke(window, "UpdateSettingsNavNeighborDisplacements");
        Assert.Equal(to, Field<int>(window, "_settingsNavTargetSlot"));
        Invoke(window, "UpdateSettingsNavNeighborDisplacements");
        Invoke(window, "EndSettingsNavRowDrag");
        await WpfFrameWaiter.UntilAsync(() => previewed?.NavTabOrder == string.Join(',', expected), "settings tab order previewed", ct);
        Assert.Equal(expected, window.NavTabsSettingsContainer.Children.OfType<Border>().Select(child => child.Tag.ToString()!));
        foreach (var restored in window.NavTabsSettingsContainer.Children.OfType<Border>().ToList())
        {
            var check = ((StackPanel)((Grid)restored.Child).Children[0]).Children.OfType<CheckBox>().Single();
            if ((string)restored.Tag == "Media") { Assert.False(check.IsEnabled); Assert.True(check.IsChecked); continue; }
            check.IsChecked = false;
            Assert.DoesNotContain((string)restored.Tag, previewed!.VisibleNavTabs.Split(','));
            check.IsChecked = true;
            Assert.Contains((string)restored.Tag, previewed.VisibleNavTabs.Split(','));
            Assert.Null(restored.Effect);
            Assert.Equal(0, Panel.GetZIndex(restored));
        }
        Assert.Null(fixture.Service.Applied);
        await window.ApplyWithoutClosingAsync();
        Assert.Equal(string.Join(',', expected), fixture.Service.Applied!.NavTabOrder);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResetRequiresConfirmationAndRestoresEditableDefaults(bool confirm) => SharedStaTestRunner.Run(() =>
    {
        using var fixture = new Fixture();
        var window = fixture.Window;
        window.WidthSlider.Value = 350;
        window.HeightSlider.Value = 48;
        window.DynamicIslandModeCheck.IsChecked = true;
        window.HideCameraCheck.IsChecked = true;
        window.IgnoreYouTubeAutoSubtitlesCheck.IsChecked = true;
        window.Show();
        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        var before = window.ReadSettingsFromUi();
        bool answered = false;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        timer.Tick += (_, _) =>
        {
            var dialog = Application.Current.Windows.OfType<VNotch.Windows.ConfirmationDialog>().LastOrDefault();
            if (dialog == null) return;
            timer.Stop();
            BackgroundTestWindows.ProtectInput(dialog);
            answered = true;
            typeof(VNotch.Windows.ConfirmationDialog).GetMethod(confirm ? "ConfirmButton_Click" : "CancelButton_Click", Private)!
                .Invoke(dialog, new object[] { dialog, new RoutedEventArgs() });
        };
        timer.Start();
        try { Invoke(window, "Reset_Click", window, new RoutedEventArgs()); }
        finally { timer.Stop(); }
        Assert.True(answered);
        var result = window.ReadSettingsFromUi();
        if (!confirm)
        {
            Assert.True(before.ValueEquals(result));
            return;
        }
        var defaults = new NotchSettings();
        Assert.Equal(defaults.Width, result.Width);
        Assert.Equal(defaults.Height, result.Height);
        Assert.Equal(defaults.EnableDynamicIslandMode, result.EnableDynamicIslandMode);
        Assert.Equal(defaults.HideCamera, result.HideCamera);
        Assert.Equal(defaults.IgnoreYouTubeAutoSubtitles, result.IgnoreYouTubeAutoSubtitles);
        Assert.Equal(defaults.ExpandedWidget, result.ExpandedWidget);
        Assert.Equal(defaults.NavTabOrder, result.NavTabOrder);
        Assert.Equal(defaults.SubtitlePriority, result.SubtitlePriority);
        Assert.False(result.EnableSpotifyCanvas);
    });

    private sealed class RecordingService : ISettingsApplicationService
    {
        internal NotchSettings? Applied { get; private set; }
        public NotchSettings Load() => new();
        public Task ApplyAsync(NotchSettings settings, CancellationToken ct = default)
        {
            Applied = settings.Clone();
            SettingsApplied?.Invoke(this, Applied);
            return Task.CompletedTask;
        }
        public Task<(NotchSettings Settings, bool RequiresRestart)> ImportAsync(string filePath, NotchSettings? currentSettings = null, CancellationToken ct = default) =>
            Task.FromResult((new NotchSettings(), false));
        public void Export(string filePath, NotchSettings settings) { }
        public bool IsAutoStartEnabled() => false;
        public event EventHandler<NotchSettings>? SettingsApplied;
    }
}

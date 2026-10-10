using System.Reflection;
using System.Windows;
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
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

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
    public void InstalledModelKeepsAVisibleConfirmationAndOffersTheNextAction() => SharedStaTestRunner.Run(() =>
    {
        using var fixture = new Fixture();
        var window = fixture.Window;
        window.PresentTranslationModelAvailability(true);
        Assert.Equal(Visibility.Visible, window.TranslationReadyCard.Visibility);
        Assert.Equal(Loc.Get("translation.installComplete"), window.TranslationReadyTitle.Text);
        Assert.Equal(Visibility.Collapsed, window.TranslationDownloadButton.Visibility);
        Assert.Equal(Visibility.Collapsed, window.TranslationModelStatus.Visibility);
        Assert.Equal(Loc.Get("translation.enable"), window.TranslationReadyActionButton.Content);
        window.TranslationReadyActionButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(window.ReadSettingsFromUi().EnableLiveTranslation);
        Assert.Equal(Loc.Get("translation.tryClipboard"), window.TranslationReadyActionButton.Content);
        Assert.Equal(Loc.Get("translation.readyManualHint"), window.TranslationReadyHint.Text);
        bool preview = false;
        window.TranslationPreviewRequested += () => preview = true;
        window.TranslationReadyActionButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(preview);
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
        Assert.Same(window.Resources["AccentBrush"], window.TranslationModelProgress.Foreground);
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
        window.WidgetCombo.SelectedIndex = 2;
        foreach (string language in new[] { "vi", "ja", "en" })
        {
            window.LanguageCombo.SelectedItem = window.LanguageCombo.Items.Cast<ComboBoxItem>().Single(item => (string)item.Tag == language);
            await WpfFrameWaiter.UntilAsync(() => window.WidthLabel.Text == Loc.Get("settings.width"), "localized width label", ct);
            Assert.Equal(language, window.ReadSettingsFromUi().Language);
            Assert.Equal("wordclock", window.ReadSettingsFromUi().ExpandedWidget);
            Assert.Equal(Loc.Get("settings.windowTitle"), window.Title);
            Assert.Equal(language, fixture.Service.Applied!.Language);
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
    public void SectionNavigationShowsOnlyTheLatestRequestedPanelAndResetsItsScroll(bool reducedMotion) => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(reducedMotion);
        try
        {
            using var fixture = new Fixture();
            var window = fixture.Window;
            var panels = Field<Dictionary<string, StackPanel>>(window, "_navPanels");
            foreach (string section in panels.Keys.Reverse())
            {
                Invoke(window, "NavigateToSection", section);
                await WpfFrameWaiter.UntilAsync(() => panels[section].Visibility == Visibility.Visible && panels.Values.Count(panel => panel.Visibility == Visibility.Visible) == 1, "settings section navigation", ct);
                Assert.Equal(section, Field<string>(window, "_activeNav"));
                Assert.Equal(0, window.SettingsScrollViewer.VerticalOffset);
            }
            string[] destinations = panels.Keys.Where(key => key != Field<string>(window, "_activeNav")).Take(3).ToArray();
            foreach (string destination in destinations) Invoke(window, "NavigateToSection", destination);
            string latest = destinations.Last();
            await WpfFrameWaiter.UntilAsync(() => panels[latest].Visibility == Visibility.Visible && panels.Values.Count(panel => panel.Visibility == Visibility.Visible) == 1, "latest settings section wins", ct);
            Invoke(window, "NavigateToSection", latest);
            Assert.Equal(latest, Field<string>(window, "_activeNav"));
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
        await WpfFrameWaiter.UntilAsync(() => window.ShellScale.ScaleX == 1 && window.ShellScale.ScaleY == 1 && window.FooterTranslate.Y == 0, "settings entrance shell and footer", ct);
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
            var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources["SFProDisplay"] = new FontFamily("Segoe UI");
            app.Resources["SFProText"] = new FontFamily("Segoe UI");
            app.Resources["IconFont"] = new FontFamily("Segoe MDL2 Assets");
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
        public event EventHandler<UpdateInfo?>? UpdateCheckCompleted { add { } remove { } }
        public Task<UpdateInfo?> CheckForUpdatesAsync(bool includePrereleases = false)
        {
            IncludePrereleases = includePrereleases;
            return Task.FromResult<UpdateInfo?>(null);
        }
        public Task<IReadOnlyList<UpdateInfo>> GetAllReleasesAsync() => Task.FromResult<IReadOnlyList<UpdateInfo>>([]);
        public Task<bool> DownloadAndInstallUpdateAsync(UpdateInfo updateInfo, IProgress<double>? progress = null, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    [Theory]
    [InlineData("Appearance")]
    [InlineData("Behavior")]
    [InlineData("Devices")]
    [InlineData("System")]
    [InlineData("Privacy")]
    [InlineData("Spotlight")]
    [InlineData("Advanced")]
    [InlineData("Performance")]
    [InlineData("Donating")]
    [InlineData("Updates")]
    [InlineData("Searching")]
    [InlineData("Skins")]
    public void SettingsSaveClosesEverySectionOnceAndPersistsSnapshot(string section) => SharedStaTestRunner.RunAsync(async ct =>
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
    public void CacheCleanupDeletesOnlyKnownCacheAndCorruptBackupFiles() => SharedStaTestRunner.Run(() =>
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
            Assert.Equal(1, typeof(SettingsWindow).GetMethod("DeleteCorruptSettingsFiles", flags)!.Invoke(null, [appData]));
            Assert.True(System.IO.File.Exists(System.IO.Path.Combine(appData, "settings.json")));
            Assert.True(System.IO.File.Exists(System.IO.Path.Combine(appData, "keep.txt")));
            Assert.Equal(0, typeof(SettingsWindow).GetMethod("DeleteCacheDirectories", flags)!.Invoke(null, [appData]));
            Assert.Equal(0, typeof(SettingsWindow).GetMethod("DeleteCorruptSettingsFiles", flags)!.Invoke(null, [System.IO.Path.Combine(directory, "missing")]));
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
        window.GpuRefractionCheck.IsChecked = false;
        window.GlassPresetCombo.SelectedItem = window.GlassPresetCombo.Items.Cast<ComboBoxItem>().Single(item => (string)item.Tag == preset);
        var snapshot = window.ReadSettingsFromUi();
        Assert.Equal("liquidglass", snapshot.NotchStyle);
        Assert.Equal(preset, snapshot.LiquidGlassPreset);
        Assert.Equal(blur, snapshot.LiquidGlass!.BlurAmount, 5);
        Assert.Equal(120, snapshot.LiquidGlass.TargetFps);
        Assert.False(snapshot.LiquidGlass.UseGpuRefraction);
        foreach (string language in new[] { "vi", "ja", "en" })
        {
            Loc.SetLanguage(language);
            Invoke(window, "ApplyLiquidGlassLocalization");
            Assert.Equal(9, window.GlassPresetCombo.Items.Count);
            Assert.Equal(preset, ((ComboBoxItem)window.GlassPresetCombo.SelectedItem).Tag);
            Assert.Equal(blur, window.ReadSettingsFromUi().LiquidGlass!.BlurAmount, 5);
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
        await WpfFrameWaiter.UntilAsync(() => fixture.Service.Applied?.NavTabOrder == string.Join(',', expected), "settings tab order persisted", ct);
        Assert.Equal(expected, window.NavTabsSettingsContainer.Children.OfType<Border>().Select(child => child.Tag.ToString()!));
        foreach (var restored in window.NavTabsSettingsContainer.Children.OfType<Border>().ToList())
        {
            var check = ((StackPanel)((Grid)restored.Child).Children[0]).Children.OfType<CheckBox>().Single();
            if ((string)restored.Tag == "Media") { Assert.False(check.IsEnabled); Assert.True(check.IsChecked); continue; }
            check.IsChecked = false;
            Assert.DoesNotContain((string)restored.Tag, fixture.Service.Applied!.VisibleNavTabs.Split(','));
            check.IsChecked = true;
            Assert.Contains((string)restored.Tag, fixture.Service.Applied!.VisibleNavTabs.Split(','));
            Assert.Null(restored.Effect);
            Assert.Equal(0, Panel.GetZIndex(restored));
        }
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

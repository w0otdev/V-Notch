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
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using VNotch.Controllers;
using VNotch.Controls;
using VNotch.Models;
using VNotch.Modules;
using VNotch.Services;

namespace VNotch;

public partial class SettingsWindow : Window
{
    private NotchSettings _settings;
    private NotchSettings _originalSettings;
    private readonly SettingsService _settingsService;
    private readonly BluetoothModule? _bluetoothModule;
    private readonly IUpdateService _updateService;
    private UpdateInfo? _availableUpdate;
    private bool _isLoadingSettings = true;
    private DispatcherTimer? _livePreviewDebounce;
    private bool _isSpotlightHotkeyRegistered;

    // Liquid Glass UI components
    private LiquidGlassController? _liquidGlass;
    private LiquidGlassRefractionEffect? _glassRefractionEffect;
    private bool _gpuRefractionConfigured;
    private double _lastAppliedDpiScale = 1.0;
    private IntPtr _hwnd = IntPtr.Zero;

    public event EventHandler<NotchSettings>? SettingsChanged;
    public event EventHandler? AnimatedClosing;

    public SettingsWindow(
        NotchSettings settings,
        SettingsService settingsService,
        BluetoothModule? bluetoothModule = null,
        bool isSpotlightHotkeyRegistered = true)
    {
        InitializeComponent();
        AnimationPrimitives.ApplyFpsToTree(this);

        _settings = settings.Clone();
        _originalSettings = settings.Clone();
        _settingsService = settingsService;
        _bluetoothModule = bluetoothModule;
        _isSpotlightHotkeyRegistered = isSpotlightHotkeyRegistered;
        _updateService = new UpdateService();

        InitializeNavigation();
        LoadSettings();
        CheckForUpdatesAsync().SafeFireAndForget("SETTINGS-UPDATE-CHECK");
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        PlayEntranceAnimation();
        LoadVisualizerAudioDevices().SafeFireAndForget("SETTINGS-VIS-AUDIO");
    }

    private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void WindowSurface_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        if (IsInteractiveElement(e.OriginalSource as DependencyObject))
        {
            return;
        }

        DragMove();
    }

    private static bool IsInteractiveElement(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is ButtonBase or Slider or Thumb or ComboBox or ComboBoxItem or CheckBox or ScrollBar or TextBox or PasswordBox)
            {
                return true;
            }

            if (source is FrameworkElement fe)
            {
                if (fe.Name == "SubtitlePriorityItems")
                    return true;

                if (fe is Border border && border.Tag is string tag &&
                    (tag == "Searching" || tag == "Appearance" || tag == "Skins" || tag == "Behavior" || tag == "Devices" ||
                     tag == "System" || tag == "Privacy" || tag == "Spotlight" || tag == "Advanced" || tag == "Performance" ||
                     tag == "Donating" || tag == "Updates" || Array.IndexOf(_navOrder, tag) >= 0))
                {
                    return true;
                }
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private void LoadSettings()
    {
        _isLoadingSettings = true;

        // Apply tooltips to UI elements
        ApplyTooltips();

        WidthSlider.Value = _settings.Width;
        DynamicIslandWidthSlider.Value = _settings.DynamicIslandWidth;
        DynamicIslandHeightSlider.Value = _settings.DynamicIslandHeight;
        HeightSlider.Value = _settings.Height;
        RadiusSlider.Value = _settings.CornerRadius;
        OpacitySlider.Value = _settings.Opacity * 100;
        BlurBrightnessSlider.Value = _settings.MediaBlurBrightnessBoost * 100;
        BlurDarkOverlaySlider.Value = _settings.MediaBlurDarkOverlay * 100;
        SpotifyCanvasBrightnessSlider.Value = _settings.SpotifyCanvasBrightness * 100;
        AnimationFpsSlider.Value = _settings.AnimationFps;
        EnableBlurEffectsCheck.IsChecked = _settings.EnableBlurEffects;
        MediaArtBackgroundCheck.IsChecked = _settings.ShowMediaArtBackground;

        // Liquid Glass availability depends on this checkbox. Initialize the mode
        DynamicIslandModeCheck.IsChecked = _settings.EnableDynamicIslandMode;
        UpdateDynamicIslandDependentControls(_settings.EnableDynamicIslandMode);
        LoadLiquidGlassUi();
        EnableSubjectBlurCheck.IsChecked = _settings.EnableSubjectBlur;
        EnableSmartCropCheck.IsChecked = _settings.EnableSmartCrop;
        UpdatePerformanceDependentControls(_settings.EnableBlurEffects);
        EnableSpotifyLyricsCheck.IsChecked = _settings.EnableSpotifyLyrics;
        UpdateLyricsDependentControls(_settings.EnableSpotifyLyrics);
        EnableSpotifyCanvasCheck.IsChecked = _settings.EnableSpotifyCanvas;
        UpdateSpotifyCanvasDependentControls();
        UpdateSpotifyCanvasConnectionStatus();
        EnableYouTubeSubtitlesCheck.IsChecked = _settings.EnableYouTubeSubtitles;
        IgnoreYouTubeAutoSubtitlesCheck.IsChecked = _settings.IgnoreYouTubeAutoSubtitles;
        UpdateYouTubeSubtitlesDependentControls(_settings.EnableYouTubeSubtitles);

        LoadSubtitlePriority();

        HoverExpandCheck.IsChecked = _settings.EnableHoverExpand;
        HoverDelaySlider.Value = _settings.HoverExpandDelay;
        HoverDelaySlider.IsEnabled = _settings.EnableHoverExpand;
        HoverDelaySlider.Opacity = _settings.EnableHoverExpand ? 1.0 : 0.4;
        DisableMouseLeaveAutoCloseCheck.IsChecked = _settings.DisableMouseLeaveAutoClose;
        ReopenLastViewCheck.IsChecked = _settings.ReopenLastViewOnExpand;

        var monitors = NotchManager.GetMonitorNames();
        MonitorCombo.ItemsSource = monitors;
        MonitorCombo.SelectedIndex = Math.Min(_settings.MonitorIndex, monitors.Length - 1);

        LoadCameraDevices().SafeFireAndForget("SETTINGS-CAMERA-DEVICES");
        SetVisualizerAudioDevicePlaceholder();

        AutoStartCheck.IsChecked = StartupManager.IsAutoStartEnabled();
        StayBehindWindowsCheck.IsChecked = _settings.StayBehindWindows;
        HelloGreetingCheck.IsChecked = _settings.EnableHelloGreeting;
        HideOnExclusiveFullscreenCheck.IsChecked = _settings.HideOnExclusiveFullscreen;
        HideOnWindowedFullscreenCheck.IsChecked = _settings.HideOnWindowedFullscreen;
        IdleAutoHideCheck.IsChecked = _settings.EnableIdleAutoHide;
        IdleAutoHideDelaySlider.Value = Math.Max(2, _settings.IdleAutoHideDelay / 1000.0);
        IdleAutoHideDelaySlider.IsEnabled = _settings.EnableIdleAutoHide;
        IdleAutoHideDelaySlider.Opacity = _settings.EnableIdleAutoHide ? 1.0 : 0.4;
        MusicNotifyCheck.IsChecked = _settings.ShowMusicNotifications;
        SystemNotifyCheck.IsChecked = _settings.ShowSystemNotifications;
        ShelfUnlockCheck.IsChecked = _settings.IsShelfUploadLimitUnlocked;
        CopyShelfClipboardCheck.IsChecked = _settings.CopyShelfFilesToClipboard;
        EnableSpotlightCheck.IsChecked = _settings.EnableSpotlight;
        EnableDebugModeCheck.IsChecked = _settings.EnableDebugMode;
        UpdateSpotlightHotkeyWarning();
        ShowBatteryCheck.IsChecked = _settings.ShowBatteryIndicator;

        LanguageCombo.Items.Clear();
        var availableLanguages = Loc.GetAvailableLanguages();
        int selectedIndex = 0;
        for (int i = 0; i < availableLanguages.Count; i++)
        {
            var lang = availableLanguages[i];
            LanguageCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = lang.Name, Tag = lang.Code });
            if (lang.Code == _settings.Language)
            {
                selectedIndex = i;
            }
        }
        LanguageCombo.SelectedIndex = selectedIndex;

        PopulateWidgetCombo();
        PopulateShelfWidgetCombo();
        PopulateClockPageStyleCombo();
        PopulateNavTabsSettings();

        YouTubeApiCheck.IsChecked = _settings.EnableYouTubeApi;
        YouTubeApiKeyPasswordBox.Password = _settings.YouTubeApiKey;
        YouTubeApiKeyTextBox.Text = _settings.YouTubeApiKey;
        YouTubeApiKeyRow.Visibility = _settings.EnableYouTubeApi ? Visibility.Visible : Visibility.Collapsed;
        UpdateYouTubeApiKeyStatus();

        EnableWeatherCheck.IsChecked = _settings.EnableWeather;
        ManualCityTextBox.Text = _settings.ManualCity;
        UpdateWeatherDependentControls(_settings.EnableWeather);

        ProcessPriorityCombo.SelectedItem = ProcessPriorityCombo.Items.OfType<System.Windows.Controls.ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == _settings.ProcessPriority) ?? ProcessPriorityCombo.Items[0];
        GpuPreferenceCombo.SelectedItem = GpuPreferenceCombo.Items.OfType<System.Windows.Controls.ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == _settings.GpuPreference.ToString()) ?? GpuPreferenceCombo.Items[0];

        LocalOnlyModeCheck.IsChecked = _settings.EnableLocalOnlyMode;
        AutoCheckUpdatesCheck.IsChecked = _settings.AutoCheckUpdates;
        EnableOnlineArtworkCheck.IsChecked = _settings.EnableOnlineArtworkLookup;
        EnableOnlineLyricsCheck.IsChecked = _settings.EnableOnlineLyrics;
        EnablePrivacyIndicatorsCheck.IsChecked = _settings.EnablePrivacyIndicators;
        EnableBrowserUrlInspectionCheck.IsChecked = _settings.EnableBrowserUrlInspection;
        EnableDiagnosticLoggingCheck.IsChecked = _settings.EnableDiagnosticLogging;
        EnableSpotlightHistoryCheck.IsChecked = _settings.EnableSpotlightHistory;
        UpdateLocalOnlyDependentControls(_settings.EnableLocalOnlyMode);

        ApplyLiquidGlassSkin();
        _isLoadingSettings = false;
        ApplyLocalization();
    }

    private static string GetAppVersion()
    {
        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        return v != null ? FormatVersion(v) : "1.9.1";
    }

    private static string FormatVersion(Version v)
    {
        return v.Revision > 0
            ? $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}"
            : $"{v.Major}.{v.Minor}.{v.Build}";
    }
    private void ApplyTooltips()
    {
        // Header tooltips
        TooltipHelper.SetLocalizedTooltip(SocialWebsite, "tooltip.website");
        TooltipHelper.SetLocalizedTooltip(SocialGitHub, "tooltip.github");
        TooltipHelper.SetLocalizedTooltip(SocialFacebook, "tooltip.facebook");
        TooltipHelper.SetLocalizedTooltip(SocialDiscord, "tooltip.discord");

        // Navigation tooltips
        TooltipHelper.SetLocalizedTooltip(NavAppearance, "tooltip.nav.appearance");
        TooltipHelper.SetLocalizedTooltip(NavSkins, "tooltip.nav.skins");
        TooltipHelper.SetLocalizedTooltip(NavBehavior, "tooltip.nav.behavior");
        TooltipHelper.SetLocalizedTooltip(NavDevices, "tooltip.nav.devices");
        TooltipHelper.SetLocalizedTooltip(NavSystem, "tooltip.nav.system");
        TooltipHelper.SetLocalizedTooltip(NavPrivacy, "tooltip.nav.privacy");
        TooltipHelper.SetLocalizedTooltip(NavSpotlight, "tooltip.nav.spotlight");
        TooltipHelper.SetLocalizedTooltip(NavAdvanced, "tooltip.nav.advanced");
        TooltipHelper.SetLocalizedTooltip(NavPerformance, "tooltip.nav.performance");
        TooltipHelper.SetLocalizedTooltip(NavUpdates, "tooltip.nav.updates");
        TooltipHelper.SetLocalizedTooltip(NavDonating, "tooltip.nav.donating");

        // Button tooltips
        TooltipHelper.SetLocalizedTooltip(CheckUpdateButton, "tooltip.checkUpdates");
        TooltipHelper.SetLocalizedTooltip(DownloadUpdateButton, "tooltip.downloadUpdate");
        TooltipHelper.SetLocalizedTooltip(ViewChangelogButton, "tooltip.viewChangelog");
        TooltipHelper.SetLocalizedTooltip(ReportBugButton, "tooltip.reportBug");
        TooltipHelper.SetLocalizedTooltip(RequestFeatureButton, "tooltip.requestFeature");
        TooltipHelper.SetLocalizedTooltip(ClearCacheButton, "tooltip.clearCache");
        TooltipHelper.SetLocalizedTooltip(ToggleKeyVisibilityButton, "tooltip.showHideKey");
        TooltipHelper.SetLocalizedTooltip(CloseSettingsButton, "tooltip.close");
        TooltipHelper.SetLocalizedTooltip(ResetButton, "tooltip.resetSettings");
        TooltipHelper.SetLocalizedTooltip(ApplyButton, "tooltip.applySettings");
        TooltipHelper.SetLocalizedTooltip(SaveButton, "tooltip.saveSettings");
        TooltipHelper.SetLocalizedTooltip(ExportSettingsButton, "tooltip.exportSettings");
        TooltipHelper.SetLocalizedTooltip(ImportSettingsButton, "tooltip.importSettings");

        // Checkbox tooltips
        TooltipHelper.SetLocalizedTooltip(AutoStartCheck, "tooltip.autoStart");
        TooltipHelper.SetLocalizedTooltip(EnableBlurEffectsCheck, "tooltip.blurEffects");
        TooltipHelper.SetLocalizedTooltip(MediaArtBackgroundCheck, "tooltip.mediaArtBackground");
        TooltipHelper.SetLocalizedTooltip(EnableSubjectBlurCheck, "tooltip.subjectBlur");
        TooltipHelper.SetLocalizedTooltip(EnableSmartCropCheck, "tooltip.smartCrop");
        TooltipHelper.SetLocalizedTooltip(DynamicIslandModeCheck, "tooltip.dynamicIsland");
        TooltipHelper.SetLocalizedTooltip(HoverExpandCheck, "tooltip.hoverExpand");
        TooltipHelper.SetLocalizedTooltip(DisableMouseLeaveAutoCloseCheck, "tooltip.disableAutoClose");
        TooltipHelper.SetLocalizedTooltip(ReopenLastViewCheck, "tooltip.reopenLastView");
        TooltipHelper.SetLocalizedTooltip(IdleAutoHideCheck, "tooltip.idleAutoHide");
        TooltipHelper.SetLocalizedTooltip(EnableSpotifyLyricsCheck, "tooltip.spotifyLyrics");
        TooltipHelper.SetLocalizedTooltip(EnableYouTubeSubtitlesCheck, "tooltip.youtubeSubtitles");
        TooltipHelper.SetLocalizedTooltip(IgnoreYouTubeAutoSubtitlesCheck, "settings.ignoreYouTubeAutoSubtitles.hint");
        TooltipHelper.SetLocalizedTooltip(YouTubeApiCheck, "tooltip.youtubeApi");
        TooltipHelper.SetLocalizedTooltip(HideOnExclusiveFullscreenCheck, "tooltip.hideExclusiveFs");
        TooltipHelper.SetLocalizedTooltip(HideOnWindowedFullscreenCheck, "tooltip.hideWindowedFs");
        TooltipHelper.SetLocalizedTooltip(MusicNotifyCheck, "tooltip.musicNotify");
        TooltipHelper.SetLocalizedTooltip(SystemNotifyCheck, "tooltip.systemNotify");
        TooltipHelper.SetLocalizedTooltip(ShowBatteryCheck, "tooltip.showBattery");
        TooltipHelper.SetLocalizedTooltip(ShelfUnlockCheck, "tooltip.shelfUnlock");
        TooltipHelper.SetLocalizedTooltip(CopyShelfClipboardCheck, "tooltip.copyShelfClipboard");
        TooltipHelper.SetLocalizedTooltip(HelloGreetingCheck, "tooltip.helloGreeting");
        TooltipHelper.SetLocalizedTooltip(EnableSpotlightCheck, "settings.enableSpotlight.hint");
        TooltipHelper.SetLocalizedTooltip(EnableWeatherCheck, "tooltip.enableWeather");
        TooltipHelper.SetLocalizedTooltip(GpuRefractionCheck, "tooltip.gpuRefraction");
        TooltipHelper.SetLocalizedTooltip(EnableSpotifyCanvasCheck, "tooltip.spotifyCanvas");
        TooltipHelper.SetLocalizedTooltip(LocalOnlyModeCheck, "tooltip.privacy.localOnly");
        TooltipHelper.SetLocalizedTooltip(AutoCheckUpdatesCheck, "tooltip.privacy.autoUpdates");
        TooltipHelper.SetLocalizedTooltip(EnableOnlineArtworkCheck, "tooltip.privacy.onlineArtwork");
        TooltipHelper.SetLocalizedTooltip(EnableOnlineLyricsCheck, "tooltip.privacy.onlineLyrics");
        TooltipHelper.SetLocalizedTooltip(EnablePrivacyIndicatorsCheck, "tooltip.privacy.indicators");
        TooltipHelper.SetLocalizedTooltip(EnableBrowserUrlInspectionCheck, "tooltip.privacy.browserUrl");
        TooltipHelper.SetLocalizedTooltip(EnableDiagnosticLoggingCheck, "tooltip.privacy.logging");
        TooltipHelper.SetLocalizedTooltip(EnableSpotlightHistoryCheck, "tooltip.privacy.spotlightHistory");
        TooltipHelper.SetLocalizedTooltip(ClearSpotlightHistoryButton, "tooltip.privacy.clearSpotlight");
        TooltipHelper.SetLocalizedTooltip(ClearLogButton, "tooltip.privacy.clearLog");

        // Combo box tooltips
        TooltipHelper.SetLocalizedTooltip(WidgetCombo, "tooltip.widgetCombo");
        TooltipHelper.SetLocalizedTooltip(MonitorCombo, "tooltip.monitorCombo");
        TooltipHelper.SetLocalizedTooltip(CameraCombo, "tooltip.cameraCombo");
        TooltipHelper.SetLocalizedTooltip(VisualizerAudioCombo, "tooltip.visualizerAudioCombo");
        TooltipHelper.SetLocalizedTooltip(LanguageCombo, "tooltip.languageCombo");
        TooltipHelper.SetLocalizedTooltip(SkinCombo, "tooltip.skinCombo");
        TooltipHelper.SetLocalizedTooltip(GlassPresetCombo, "tooltip.glassPresetCombo");
    }

    private void ApplyLocalization()
    {
        ApplySupplementalLocalization();
        SettingsTitleText.Text = Loc.Get("settings.title");
        SettingsSubtitleText.Text = Loc.Get("settings.subtitle");
        string appVersion = GetAppVersion();
        if (SettingsVersionBadgeText != null) SettingsVersionBadgeText.Text = $"v{appVersion}";
        if (SidebarBuildVersionText != null) SidebarBuildVersionText.Text = $"Build {appVersion}";
        SearchPlaceholder.Text = Loc.Get("settings.searchPlaceholder");

        AppearanceHeader.Text = Loc.Get("settings.appearance");
        BehaviorHeader.Text = Loc.Get("settings.behavior");
        UpdatesHeader.Text = Loc.Get("settings.updates");
        DonatingHeader.Text = Loc.Get("settings.donating");
        PerformanceHeader.Text = Loc.Get("settings.performance");
        DisplayHeader.Text = Loc.Get("settings.display");
        SystemHeader.Text = Loc.Get("settings.system");
        SpotlightHeader.Text = Loc.Get("settings.spotlight");
        EnableSpotlightCheck.Content = Loc.Get("settings.enableSpotlight");
        EnableSpotlightHint.Text = Loc.Get("settings.enableSpotlight.hint");
        SpotlightHotkeyWarning.Text = Loc.Get("settings.enableSpotlight.conflict");
        SearchingHeader.Text = Loc.Get("settings.searching");
        SearchingEmptyText.Text = Loc.Get("settings.search.noResults");

        NavSearchingText.Text = Loc.Get("settings.searching");
        NavAppearanceText.Text = Loc.Get("settings.nav.appearance");
        NavSkinsText.Text = Loc.Get("settings.nav.skins");
        NavBehaviorText.Text = Loc.Get("settings.nav.behavior");
        NavDevicesText.Text = Loc.Get("settings.nav.devices");
        NavSystemText.Text = Loc.Get("settings.nav.system");
        NavPrivacyText.Text = Loc.Get("settings.nav.privacy");
        NavSpotlightText.Text = Loc.Get("settings.nav.spotlight");
        NavAdvancedText.Text = Loc.Get("settings.nav.advanced");
        NavPerformanceText.Text = Loc.Get("settings.nav.performance");
        NavDonatingText.Text = Loc.Get("settings.nav.donating");
        NavUpdatesText.Text = Loc.Get("settings.nav.updates");

        PrivacyHeader.Text = Loc.Get("settings.header.privacy");
        LocalOnlyModeCheck.Content = Loc.Get("settings.privacy.localOnly");
        LocalOnlyBadgeText.Text = Loc.Get("settings.privacy.localOnly.badge");
        LocalOnlyModeHint.Text = Loc.Get("settings.privacy.localOnly.hint");
        PrivacyNetworkHeader.Text = Loc.Get("settings.privacy.section.network");
        AutoCheckUpdatesCheck.Content = Loc.Get("settings.privacy.autoUpdates");
        AutoCheckUpdatesHint.Text = Loc.Get("settings.privacy.autoUpdates.hint");
        EnableOnlineArtworkCheck.Content = Loc.Get("settings.privacy.onlineArtwork");
        EnableOnlineArtworkHint.Text = Loc.Get("settings.privacy.onlineArtwork.hint");
        EnableOnlineLyricsCheck.Content = Loc.Get("settings.privacy.onlineLyrics");
        EnableOnlineLyricsHint.Text = Loc.Get("settings.privacy.onlineLyrics.hint");
        PrivacySensorsHeader.Text = Loc.Get("settings.privacy.section.sensors");
        EnablePrivacyIndicatorsCheck.Content = Loc.Get("settings.privacy.indicators");
        EnablePrivacyIndicatorsHint.Text = Loc.Get("settings.privacy.indicators.hint");
        EnableBrowserUrlInspectionCheck.Content = Loc.Get("settings.privacy.browserUrl");
        EnableBrowserUrlInspectionHint.Text = Loc.Get("settings.privacy.browserUrl.hint");
        PrivacyStorageHeader.Text = Loc.Get("settings.privacy.section.storage");
        EnableDiagnosticLoggingCheck.Content = Loc.Get("settings.privacy.logging");
        EnableDiagnosticLoggingHint.Text = Loc.Get("settings.privacy.logging.hint");
        EnableSpotlightHistoryCheck.Content = Loc.Get("settings.privacy.spotlightHistory");
        EnableSpotlightHistoryHint.Text = Loc.Get("settings.privacy.spotlightHistory.hint");
        ClearSpotlightHistoryButton.Content = Loc.Get("settings.privacy.clearSpotlight");
        ClearLogButton.Content = Loc.Get("settings.privacy.clearLog");

        NavTabsLabel.Text = Loc.Get("settings.navTabs");
        NavTabsHint.Text = Loc.Get("settings.navTabs.hint");
        ResetTabOrderButton.Content = Loc.Get("settings.tab.reset");
        ExpandedWidgetLabel.Text = Loc.Get("settings.expandedWidget");
        ExpandedWidgetHint.Text = Loc.Get("settings.expandedWidget.hint");
        ShelfWidgetLabel.Text = Loc.Get("settings.shelfWidget");
        ShelfWidgetHint.Text = Loc.Get("settings.shelfWidget.hint");
        ClockPageStyleLabel.Text = Loc.Get("settings.clockPageStyle");
        ClockPageStyleHint.Text = Loc.Get("settings.clockPageStyle.hint");
        RepopulateWidgetComboPreservingSelection();
        RepopulateShelfWidgetComboPreservingSelection();
        RepopulateClockPageStyleComboPreservingSelection();
        PopulateNavTabsSettings();
        WidthLabel.Text = Loc.Get("settings.width");
        WidthSlider.Label = Loc.Get("settings.width");
        WidthSlider.Description = Loc.Get("settings.width.hint");
        DynamicIslandWidthLabel.Text = Loc.Get("settings.dynamicIslandWidth");
        DynamicIslandWidthSlider.Label = Loc.Get("settings.dynamicIslandWidth");
        DynamicIslandWidthSlider.Description = Loc.Get("settings.dynamicIslandWidth.hint");
        DynamicIslandHeightLabel.Text = Loc.Get("settings.dynamicIslandHeight");
        DynamicIslandHeightSlider.Label = Loc.Get("settings.dynamicIslandHeight");
        DynamicIslandHeightSlider.Description = Loc.Get("settings.dynamicIslandHeight.hint");
        HeightLabel.Text = Loc.Get("settings.height");
        HeightSlider.Label = Loc.Get("settings.height");
        HeightSlider.Description = Loc.Get("settings.height.hint");
        RadiusLabel.Text = Loc.Get("settings.cornerRadius");
        RadiusSlider.Label = Loc.Get("settings.cornerRadius");
        RadiusSlider.Description = Loc.Get("settings.cornerRadius.hint");
        OpacityLabel.Text = Loc.Get("settings.opacity");
        OpacitySlider.Label = Loc.Get("settings.opacity");
        OpacitySlider.Description = Loc.Get("settings.opacity.hint");
        BlurLabel.Text = Loc.Get("settings.blurBrightness");
        BlurBrightnessSlider.Label = Loc.Get("settings.blurBrightness");
        BlurBrightnessSlider.Description = Loc.Get("settings.blurBrightness.hint");
        DarkOverlayLabel.Text = Loc.Get("settings.lyricsDarkOverlay");
        BlurDarkOverlaySlider.Label = Loc.Get("settings.lyricsDarkOverlay");
        BlurDarkOverlaySlider.Description = Loc.Get("settings.lyricsDarkOverlay.hint");
        EnableSpotifyLyricsCheck.Content = Loc.Get("settings.enableSpotifyLyrics");
        EnableSpotifyLyricsHint.Text = Loc.Get("settings.enableSpotifyLyrics.hint");
        EnableSpotifyCanvasCheck.Content = Loc.Get("settings.enableSpotifyCanvas");
        EnableSpotifyCanvasHint.Text = Loc.Get("settings.enableSpotifyCanvas.hint");
        SpotifyCanvasBrightnessSlider.Label = Loc.Get("settings.spotifyCanvasBrightness");
        SpotifyCanvasBrightnessSlider.Description = Loc.Get("settings.spotifyCanvasBrightness.hint");
        SpotifyCanvasAccountLabel.Text = Loc.Get("settings.spotifyCanvasAccount");
        SpotifyCanvasAccountHint.Text = Loc.Get("settings.spotifyCanvasAccount.hint");
        SpotifyConnectButton.Content = Loc.Get("settings.spotifyCanvas.connect");
        SpotifyDisconnectButton.Content = Loc.Get("settings.spotifyCanvas.disconnect");
        UpdateSpotifyCanvasConnectionStatus();
        EnableYouTubeSubtitlesLabel.Text = Loc.Get("settings.enableYouTubeSubtitles");
        if (YouTubeSubtitlesAlphaBadge != null) YouTubeSubtitlesAlphaBadge.Text = Loc.Get("settings.badge.alpha");
        EnableYouTubeSubtitlesHint.Text = Loc.Get("settings.enableYouTubeSubtitles.hint");
        IgnoreYouTubeAutoSubtitlesLabel.Text = Loc.Get("settings.ignoreYouTubeAutoSubtitles");
        IgnoreYouTubeAutoSubtitlesHint.Text = Loc.Get("settings.ignoreYouTubeAutoSubtitles.hint");

        SubtitlePriorityLabel.Text = Loc.Get("settings.subtitlePriority");
        SubtitlePriorityHint.Text = Loc.Get("settings.subtitlePriority.hint");
        LoadSubtitlePriority();

        DynamicIslandModeCheck.Content = Loc.Get("settings.dynamicIslandMode");
        DynamicIslandModeHint.Text = Loc.Get("settings.dynamicIslandMode.hint");

        HoverExpandCheck.Content = Loc.Get("settings.hoverExpand");
        HoverExpandHint.Text = Loc.Get("settings.hoverExpand.hint");
        ExpandDelayLabel.Text = Loc.Get("settings.expandDelay");
        HoverDelaySlider.Label = Loc.Get("settings.expandDelay");
        HoverDelaySlider.Description = Loc.Get("settings.expandDelay.hint");
        DisableMouseLeaveAutoCloseCheck.Content = Loc.Get("settings.disableAutoClose");
        DisableMouseLeaveAutoCloseHint.Text = Loc.Get("settings.disableAutoClose.hint");
        ReopenLastViewCheck.Content = Loc.Get("settings.reopenLastView");
        ReopenLastViewHint.Text = Loc.Get("settings.reopenLastView.hint");
        IdleAutoHideCheck.Content = Loc.Get("settings.idleAutoHide");
        IdleAutoHideHint.Text = Loc.Get("settings.idleAutoHide.hint");
        IdleAutoHideKeywords.Text = Loc.Get("settings.idleAutoHide.keywords");
        IdleAutoHideDelaySlider.Label = Loc.Get("settings.idleAutoHideDelay");
        IdleAutoHideDelaySlider.Description = Loc.Get("settings.idleAutoHideDelay.hint");
        IdleAutoHideDelayKeywords.Text = Loc.Get("settings.idleAutoHideDelay.keywords");

        CheckUpdateButton.Content = Loc.Get("settings.checkUpdate");
        DownloadUpdateButton.Content = Loc.Get("settings.downloadInstall");
        UpdateStatusText.Text = Loc.Get("settings.upToDate");
        CurrentVersionText.Text = Loc.Get("settings.currentVersion", GetAppVersion());
        ViewChangelogButton.Content = Loc.Get("settings.btn.changelog");
        ReportBugLabel.Text = Loc.Get("settings.reportBug");
        ReportBugHint.Text = Loc.Get("settings.reportBug.hint");
        RequestFeatureLabel.Text = Loc.Get("settings.requestFeature");
        RequestFeatureHint.Text = Loc.Get("settings.requestFeature.hint");
        ClearCacheLabel.Text = Loc.Get("settings.clearCache");
        ClearCacheHint.Text = Loc.Get("settings.clearCache.hint");

        MonitorLabel.Text = Loc.Get("settings.activeMonitor");
        MonitorHint.Text = Loc.Get("settings.activeMonitor.hint");
        int monitorIdx = MonitorCombo.SelectedIndex;
        var monitors = NotchManager.GetMonitorNames();
        MonitorCombo.ItemsSource = monitors;
        MonitorCombo.SelectedIndex = Math.Min(monitorIdx < 0 ? _settings.MonitorIndex : monitorIdx, monitors.Length - 1);

        CameraLabel.Text = Loc.Get("settings.camera");
        CameraHint.Text = Loc.Get("settings.camera.hint");
        VisualizerAudioLabel.Text = Loc.Get("settings.visualizerAudio");
        VisualizerAudioHint.Text = Loc.Get("settings.visualizerAudio.hint");
        if (IsLoaded)
        {
            Dispatcher.BeginInvoke(new Action(() => LoadVisualizerAudioDevices().SafeFireAndForget("SETTINGS-VIS-AUDIO")), DispatcherPriority.Background);
        }
        else
        {
            SetVisualizerAudioDevicePlaceholder();
        }

        ResetButton.Content = Loc.Get("settings.btn.reset");
        ApplyButton.Content = Loc.Get("settings.btn.apply");
        SaveButton.Content = Loc.Get("settings.btn.save");

        HelloGreetingCheck.Content = Loc.Get("settings.helloGreeting");
        HelloGreetingHint.Text = Loc.Get("settings.helloGreeting.hint");
        AutoStartCheck.Content = Loc.Get("settings.autoStart");
        AutoStartHint.Text = Loc.Get("settings.autoStart.hint");
        StayBehindWindowsCheck.Content = Loc.Get("settings.stayBehindWindows");
        StayBehindWindowsHint.Text = Loc.Get("settings.stayBehindWindows.hint");
        HideOnExclusiveFullscreenCheck.Content = Loc.Get("settings.hideExclusiveFs");
        HideOnExclusiveFullscreenHint.Text = Loc.Get("settings.hideExclusiveFs.hint");
        HideOnWindowedFullscreenCheck.Content = Loc.Get("settings.hideWindowedFs");
        HideOnWindowedFullscreenHint.Text = Loc.Get("settings.hideWindowedFs.hint");
        MusicNotifyCheck.Content = Loc.Get("settings.musicNotify");
        MusicNotifyHint.Text = Loc.Get("settings.musicNotify.hint");
        SystemNotifyCheck.Content = Loc.Get("settings.systemNotify");
        SystemNotifyHint.Text = Loc.Get("settings.systemNotify.hint");
        ShelfUnlockCheck.Content = Loc.Get("settings.shelfUnlock");
        ShelfUnlockHint.Text = Loc.Get("settings.shelfUnlock.hint");
        CopyShelfClipboardCheck.Content = Loc.Get("settings.copyShelfClipboard");
        CopyShelfClipboardHint.Text = Loc.Get("settings.copyShelfClipboard.hint");
        ShowBatteryCheck.Content = Loc.Get("settings.showBattery");
        ShowBatteryHint.Text = Loc.Get("settings.showBattery.hint");
        LanguageLabel.Text = Loc.Get("settings.language");
        LanguageHint.Text = Loc.Get("settings.language.hint");
        AdvancedHeader.Text = Loc.Get("settings.advanced");
        YouTubeApiCheck.Content = Loc.Get("settings.youtubeApi");
        YouTubeApiHint.Text = Loc.Get("settings.youtubeApi.hint");
        YouTubeApiKeyLabel.Text = Loc.Get("settings.youtubeApiKey");
        YouTubeApiKeyHint.Text = Loc.Get("settings.youtubeApiKey.hint");

        AnimationFpsLabel.Text = Loc.Get("settings.animationFps");
        AnimationFpsSlider.Label = Loc.Get("settings.animationFps");
        AnimationFpsSlider.Description = Loc.Get("settings.animationFps.hint");
        EnableBlurEffectsCheck.Content = Loc.Get("settings.enableBlurEffects");
        EnableBlurEffectsHint.Text = Loc.Get("settings.enableBlurEffects.hint");
        MediaArtBackgroundCheck.Content = Loc.Get("settings.mediaArtBackground");
        MediaArtBackgroundHint.Text = Loc.Get("settings.mediaArtBackground.hint");
        ApplyLiquidGlassLocalization();
        EnableSubjectBlurCheck.Content = Loc.Get("settings.enableSubjectBlur");
        EnableSubjectBlurHint.Text = Loc.Get("settings.enableSubjectBlur.hint");
        EnableSmartCropCheck.Content = Loc.Get("settings.enableSmartCrop");
        EnableSmartCropHint.Text = Loc.Get("settings.enableSmartCrop.hint");

        DonatingTitle.Text = Loc.Get("settings.donating.title");
        DonatingDescription.Text = Loc.Get("settings.donating.description");
        DonatePaypalButton.Content = Loc.Get("settings.donating.paypal");
        DonatingBankTitle.Text = Loc.Get("settings.donating.bank");
        DonatingBankHint.Text = Loc.Get("settings.donating.bank.hint");
    }

    internal void SetSpotlightHotkeyStatus(bool isRegistered)
    {
        _isSpotlightHotkeyRegistered = isRegistered;
        UpdateSpotlightHotkeyWarning();
    }

    private void UpdateSpotlightHotkeyWarning()
    {
        SpotlightHotkeyWarning.Visibility = EnableSpotlightCheck.IsChecked == true && !_isSpotlightHotkeyRegistered
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void EnableSpotlightCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings) return;

        UpdateSpotlightHotkeyWarning();
        PushLivePreview();
    }

    private void EnableDebugModeCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings) return;
        _settings.EnableDebugMode = EnableDebugModeCheck.IsChecked == true;

        if (Application.Current.MainWindow is MainWindow main)
        {
            main.ToggleDebugMode(_settings.EnableDebugMode);
        }

        PushLivePreview();
    }

    private void UpdateLocalOnlyDependentControls(bool isLocalOnly, bool animate = false)
    {
        if (PrivacyNetworkSection != null)
        {
            PrivacyNetworkSection.Visibility = Visibility.Visible;
            PrivacyNetworkSection.IsEnabled = !isLocalOnly;
            PrivacyNetworkSection.IsHitTestVisible = !isLocalOnly;

            double targetOpacity = isLocalOnly ? 0.35 : 1.0;
            if (animate && !AnimationConfig.ReduceMotion)
            {
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                var opacityAnim = new DoubleAnimation(PrivacyNetworkSection.Opacity, targetOpacity, TimeSpan.FromMilliseconds(260))
                {
                    EasingFunction = ease
                };
                Timeline.SetDesiredFrameRate(opacityAnim, AnimationConfig.TargetFps);
                PrivacyNetworkSection.BeginAnimation(OpacityProperty, opacityAnim);
            }
            else
            {
                PrivacyNetworkSection.BeginAnimation(OpacityProperty, null);
                PrivacyNetworkSection.Opacity = targetOpacity;
            }
        }

        if (LocalOnlyActiveBadge != null)
        {
            if (isLocalOnly)
            {
                LocalOnlyActiveBadge.Visibility = Visibility.Visible;
                if (animate && !AnimationConfig.ReduceMotion)
                {
                    var ease = new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut };
                    var fadeIn = new DoubleAnimation(LocalOnlyActiveBadge.Opacity, 1.0, TimeSpan.FromMilliseconds(240))
                    {
                        EasingFunction = ease
                    };
                    Timeline.SetDesiredFrameRate(fadeIn, AnimationConfig.TargetFps);
                    LocalOnlyActiveBadge.BeginAnimation(OpacityProperty, fadeIn);

                    if (LocalOnlyActiveBadge.RenderTransform is ScaleTransform scale)
                    {
                        var scaleAnim = new DoubleAnimation(0.85, 1.0, TimeSpan.FromMilliseconds(240))
                        {
                            EasingFunction = ease
                        };
                        Timeline.SetDesiredFrameRate(scaleAnim, AnimationConfig.TargetFps);
                        scale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
                        scale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);
                    }
                }
                else
                {
                    LocalOnlyActiveBadge.BeginAnimation(OpacityProperty, null);
                    LocalOnlyActiveBadge.Opacity = 1.0;
                    if (LocalOnlyActiveBadge.RenderTransform is ScaleTransform scale)
                    {
                        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                        scale.ScaleX = 1.0;
                        scale.ScaleY = 1.0;
                    }
                }
            }
            else
            {
                if (animate && !AnimationConfig.ReduceMotion && LocalOnlyActiveBadge.Visibility == Visibility.Visible)
                {
                    var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
                    var fadeOut = new DoubleAnimation(LocalOnlyActiveBadge.Opacity, 0.0, TimeSpan.FromMilliseconds(180))
                    {
                        EasingFunction = ease
                    };
                    fadeOut.Completed += (_, _) =>
                    {
                        if (!(LocalOnlyModeCheck?.IsChecked ?? false))
                        {
                            LocalOnlyActiveBadge.Visibility = Visibility.Collapsed;
                        }
                    };
                    Timeline.SetDesiredFrameRate(fadeOut, AnimationConfig.TargetFps);
                    LocalOnlyActiveBadge.BeginAnimation(OpacityProperty, fadeOut);
                }
                else
                {
                    LocalOnlyActiveBadge.BeginAnimation(OpacityProperty, null);
                    LocalOnlyActiveBadge.Opacity = 0.0;
                    LocalOnlyActiveBadge.Visibility = Visibility.Collapsed;
                }
            }
        }

        if (AutoCheckUpdatesCheck != null)
        {
            AutoCheckUpdatesCheck.IsEnabled = !isLocalOnly;
        }

        if (EnableOnlineArtworkCheck != null)
        {
            EnableOnlineArtworkCheck.IsEnabled = !isLocalOnly;
        }

        if (EnableOnlineLyricsCheck != null)
        {
            EnableOnlineLyricsCheck.IsEnabled = !isLocalOnly;
        }
    }

    private void LocalOnlyModeCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings) return;
        bool isLocalOnly = LocalOnlyModeCheck.IsChecked ?? false;
        _settings.EnableLocalOnlyMode = isLocalOnly;
        UpdateLocalOnlyDependentControls(isLocalOnly, animate: true);
        PushLivePreview();
    }

    /// <summary>
    /// Smoothly animates the opacity and interactive state of a UI element or panel
    /// when its parent setting is toggled on or off across all settings panels.
    /// </summary>
    private void AnimateDependentElement(UIElement? element, bool enabled, double disabledOpacity = 0.4, bool animate = false)
    {
        if (element == null) return;

        element.IsEnabled = enabled;
        element.IsHitTestVisible = enabled;

        double targetOpacity = enabled ? 1.0 : disabledOpacity;

        if (animate && !_isLoadingSettings && !AnimationConfig.ReduceMotion)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var anim = new DoubleAnimation(element.Opacity, targetOpacity, TimeSpan.FromMilliseconds(240))
            {
                EasingFunction = ease
            };
            Timeline.SetDesiredFrameRate(anim, AnimationConfig.TargetFps);
            element.BeginAnimation(OpacityProperty, anim);
        }
        else
        {
            element.BeginAnimation(OpacityProperty, null);
            element.Opacity = targetOpacity;
        }
    }

    /// <summary>
    /// Smoothly animates the expanding/collapsing and fade of a child row or panel
    /// when its parent toggle is enabled/disabled across all settings panels.
    /// </summary>
    private void AnimateCollapsibleRow(FrameworkElement? element, bool visible, bool animate = false)
    {
        if (element == null) return;

        if (visible)
        {
            element.Visibility = Visibility.Visible;
            element.IsEnabled = true;
            element.IsHitTestVisible = true;

            if (animate && !_isLoadingSettings && !AnimationConfig.ReduceMotion)
            {
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                var fadeIn = new DoubleAnimation(element.Opacity < 0.1 ? 0.0 : element.Opacity, 1.0, TimeSpan.FromMilliseconds(240))
                {
                    EasingFunction = ease
                };
                Timeline.SetDesiredFrameRate(fadeIn, AnimationConfig.TargetFps);
                element.BeginAnimation(OpacityProperty, fadeIn);

                if (element.RenderTransform is TranslateTransform tt)
                {
                    var slideIn = new DoubleAnimation(-6, 0, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease };
                    Timeline.SetDesiredFrameRate(slideIn, AnimationConfig.TargetFps);
                    tt.BeginAnimation(TranslateTransform.YProperty, slideIn);
                }
            }
            else
            {
                element.BeginAnimation(OpacityProperty, null);
                element.Opacity = 1.0;
                if (element.RenderTransform is TranslateTransform tt)
                {
                    tt.BeginAnimation(TranslateTransform.YProperty, null);
                    tt.Y = 0;
                }
            }
        }
        else
        {
            element.IsEnabled = false;
            element.IsHitTestVisible = false;

            if (animate && !_isLoadingSettings && !AnimationConfig.ReduceMotion && element.Visibility == Visibility.Visible)
            {
                var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
                var fadeOut = new DoubleAnimation(element.Opacity, 0.0, TimeSpan.FromMilliseconds(180))
                {
                    EasingFunction = ease
                };
                fadeOut.Completed += (_, _) =>
                {
                    element.Visibility = Visibility.Collapsed;
                };
                Timeline.SetDesiredFrameRate(fadeOut, AnimationConfig.TargetFps);
                element.BeginAnimation(OpacityProperty, fadeOut);
            }
            else
            {
                element.BeginAnimation(OpacityProperty, null);
                element.Opacity = 0.0;
                element.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void AutoCheckUpdatesCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings) return;
        _settings.AutoCheckUpdates = AutoCheckUpdatesCheck.IsChecked ?? true;
        PushLivePreview();
    }

    private void EnableOnlineArtworkCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings) return;
        _settings.EnableOnlineArtworkLookup = EnableOnlineArtworkCheck.IsChecked ?? true;
        PushLivePreview();
    }

    private void EnableOnlineLyricsCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings) return;
        _settings.EnableOnlineLyrics = EnableOnlineLyricsCheck.IsChecked ?? true;
        PushLivePreview();
    }

    private void EnablePrivacyIndicatorsCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings) return;
        _settings.EnablePrivacyIndicators = EnablePrivacyIndicatorsCheck.IsChecked ?? true;
        PushLivePreview();
    }

    private void EnableBrowserUrlInspectionCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings) return;
        _settings.EnableBrowserUrlInspection = EnableBrowserUrlInspectionCheck.IsChecked ?? true;
        PushLivePreview();
    }

    private void EnableDiagnosticLoggingCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings) return;
        bool enabled = EnableDiagnosticLoggingCheck.IsChecked ?? true;
        _settings.EnableDiagnosticLogging = enabled;
        RuntimeLog.MinimumLevel = enabled ? LogLevel.Debug : LogLevel.None;
        PushLivePreview();
    }

    private void EnableSpotlightHistoryCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings) return;
        _settings.EnableSpotlightHistory = EnableSpotlightHistoryCheck.IsChecked ?? true;
        PushLivePreview();
    }

    private void ClearSpotlightHistory_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string usagePath = System.IO.Path.Combine(appDataPath, "V-Notch", "spotlight-usage.json");
            if (File.Exists(usagePath))
            {
                File.Delete(usagePath);
            }

            AnimateButtonSuccessFeedback(ClearSpotlightHistoryButton, Loc.Get("settings.privacy.cleared"), "settings.privacy.clearSpotlight");
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("PRIVACY-CLEAR", ex.Message);
        }
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            RuntimeLog.ClearLog();
            AnimateButtonSuccessFeedback(ClearLogButton, Loc.Get("settings.privacy.cleared"), "settings.privacy.clearLog");
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("PRIVACY-CLEAR", ex.Message);
        }
    }

    private void AnimateButtonSuccessFeedback(Button button, string successText, string defaultTextKey)
    {
        if (button.Tag is DispatcherTimer existingTimer)
        {
            existingTimer.Stop();
            button.Tag = null;
        }

        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        var fadeOut = new DoubleAnimation(button.Opacity, 0.0, TimeSpan.FromMilliseconds(120)) { EasingFunction = ease };

        fadeOut.Completed += (_, _) =>
        {
            button.Content = successText;
            button.Foreground = new SolidColorBrush(Color.FromRgb(0x50, 0xC8, 0x78));

            var fadeIn = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease };

            fadeIn.Completed += (_, _) =>
            {
                var timer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(1300)
                };

                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    button.Tag = null;

                    var revertFadeOut = new DoubleAnimation(button.Opacity, 0.0, TimeSpan.FromMilliseconds(140)) { EasingFunction = ease };

                    revertFadeOut.Completed += (_, _) =>
                    {
                        button.Content = Loc.Get(defaultTextKey);
                        button.ClearValue(ForegroundProperty);

                        var revertFadeIn = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease };
                        button.BeginAnimation(OpacityProperty, revertFadeIn);
                    };

                    button.BeginAnimation(OpacityProperty, revertFadeOut);
                };

                button.Tag = timer;
                timer.Start();
            };

            button.BeginAnimation(OpacityProperty, fadeIn);
        };

        button.BeginAnimation(OpacityProperty, fadeOut);
    }

    private void ApplySupplementalLocalization()
    {
        Language = System.Windows.Markup.XmlLanguage.GetLanguage(Loc.GetCulture().IetfLanguageTag);
        Title = Loc.Get("settings.windowTitle");
        ApplyTooltips();

        DownloadUpdateButton.Content = Loc.Get("settings.downloadInstall");
        EnableWeatherCheck.Content = Loc.Get("settings.enableWeather");
        EnableWeatherHint.Text = Loc.Get("settings.enableWeather.hint");
        ManualCityLabel.Text = Loc.Get("settings.manualCity");
        ManualCityHint.Text = Loc.Get("settings.manualCity.hint");

        GpuRefractionCheck.Content = Loc.Get("settings.gpuRefraction");
        GpuRefractionHint.Text = Loc.Get("settings.gpuRefraction.hint");

        EnableSpotifyCanvasCheck.Content = Loc.Get("settings.enableSpotifyCanvas");
        EnableSpotifyCanvasHint.Text = Loc.Get("settings.enableSpotifyCanvas.hint");
        SpotifyCanvasAccountLabel.Text = Loc.Get("settings.spotifyCanvasAccount");
        SpotifyCanvasAccountHint.Text = Loc.Get("settings.spotifyCanvasAccount.hint");
        SpotifyConnectButton.Content = Loc.Get("settings.spotifyCanvas.connect");
        SpotifyDisconnectButton.Content = Loc.Get("settings.spotifyCanvas.disconnect");
        CopyShelfClipboardHint.Text = Loc.Get("settings.copyShelfClipboard.hint");
        if (YouTubeSubtitlesAlphaBadge != null)
            YouTubeSubtitlesAlphaBadge.Text = Loc.Get("settings.badge.alpha");

        if (GpuPreferenceLabel != null)
            GpuPreferenceLabel.Text = Loc.Get("settings.gpuPreference");
        if (GpuPreferenceHint != null)
            GpuPreferenceHint.Text = Loc.Get("settings.gpuPreference.hint");
        if (GpuPreferenceRestartNote != null)
            GpuPreferenceRestartNote.Text = Loc.Get("settings.gpuPreference.restart");
        if (GpuPreferenceRestartBadge != null)
            GpuPreferenceRestartBadge.Text = Loc.Get("settings.badge.restartRequired");
        if (ProcessPriorityLabel != null)
            ProcessPriorityLabel.Text = Loc.Get("settings.processPriority");
        if (ProcessPriorityHint != null)
            ProcessPriorityHint.Text = Loc.Get("settings.processPriority.hint");

        if (BackupHeader != null)
            BackupHeader.Text = Loc.Get("settings.section.backup");
        if (ExportSettingsLabel != null)
            ExportSettingsLabel.Text = Loc.Get("settings.exportSettings");
        if (ExportSettingsHint != null)
            ExportSettingsHint.Text = Loc.Get("settings.exportSettings.hint");
        if (ExportSettingsButton != null)
            ExportSettingsButton.Content = Loc.Get("settings.exportSettings.btn");
        if (ImportSettingsLabel != null)
            ImportSettingsLabel.Text = Loc.Get("settings.importSettings");
        if (ImportSettingsHint != null)
            ImportSettingsHint.Text = Loc.Get("settings.importSettings.hint");
        if (ImportSettingsButton != null)
            ImportSettingsButton.Content = Loc.Get("settings.importSettings.btn");

        if (RestartPromptTitle != null)
            RestartPromptTitle.Text = Loc.Get("settings.restartBanner.title");
        if (RestartPromptMessage != null)
            RestartPromptMessage.Text = Loc.Get("settings.restartBanner.message");
        if (RestartNowButton != null)
            RestartNowButton.Content = Loc.Get("settings.restartBanner.restartNow");
        if (RestartLaterButton != null)
            RestartLaterButton.Content = Loc.Get("settings.restartBanner.later");

        UpdateSpotifyCanvasConnectionStatus();
        UpdateYouTubeApiKeyStatus();
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

    private void EnableSpotifyCanvasCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings) return;
        UpdateSpotifyCanvasDependentControls(animate: true);
        PushLivePreview();
    }

    private void SpotifyCanvasBrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        PushLivePreview();
    }

    private void SpotifyConnectButton_Click(object sender, RoutedEventArgs e)
    {
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
        bool canvasEnabled = EnableSpotifyCanvasCheck.IsChecked ?? true;

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
        PushLivePreview();
    }

    #region Subtitle Priority

    private class SubtitlePriorityItem
    {
        public string Key { get; set; } = "";
        public string DisplayName { get; set; } = "";

        public override string ToString() => DisplayName;
    }

    private readonly System.Collections.ObjectModel.ObservableCollection<SubtitlePriorityItem> _subtitleItems = new();
    private Point _subtitleDragStart;
    private bool _subtitleIsDragging;

    private void LoadSubtitlePriority()
    {
        _subtitleItems.Clear();

        var keys = (_settings.SubtitlePriority ?? "native,english,auto")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var allKeys = new[] { "native", "english", "auto" };
        var ordered = keys.Where(k => allKeys.Contains(k)).ToList();
        foreach (var k in allKeys)
        {
            if (!ordered.Contains(k)) ordered.Add(k);
        }

        foreach (var key in ordered)
        {
            _subtitleItems.Add(new SubtitlePriorityItem
            {
                Key = key,
                DisplayName = GetSubtitleModeName(key)
            });
        }

        SubtitlePriorityItems.ItemsSource = _subtitleItems;
    }

    private static string GetSubtitleModeName(string key) => key switch
    {
        "native" => Loc.Get("settings.subtitleMode.native"),
        "english" => Loc.Get("settings.subtitleMode.english"),
        "auto" => Loc.Get("settings.subtitleMode.auto"),
        _ => key
    };

    private void SubtitlePriorityItem_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _subtitleDragStart = e.GetPosition(null);
        _subtitleIsDragging = false;
        e.Handled = true;
    }

    private void SubtitlePriorityItem_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;
        if (_subtitleIsDragging) return;

        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _subtitleDragStart.X) > SystemParameters.MinimumHorizontalDragDistance ||
            Math.Abs(pos.Y - _subtitleDragStart.Y) > SystemParameters.MinimumVerticalDragDistance)
        {
            _subtitleIsDragging = true;

            if (sender is FrameworkElement fe && fe.DataContext is SubtitlePriorityItem item)
            {
                var data = new DataObject("SubtitlePriorityItem", item);
                DragDrop.DoDragDrop(fe, data, DragDropEffects.Move);
            }

            _subtitleIsDragging = false;
        }
    }

    private void SubtitlePriorityItem_GiveFeedback(object sender, GiveFeedbackEventArgs e)
    {
        e.UseDefaultCursors = true;
        e.Handled = true;
    }

    private void SubtitlePriority_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent("SubtitlePriorityItem"))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void SubtitlePriority_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent("SubtitlePriorityItem")) return;

        var draggedItem = e.Data.GetData("SubtitlePriorityItem") as SubtitlePriorityItem;
        if (draggedItem == null) return;

        var dropPos = e.GetPosition(SubtitlePriorityItems);
        int newIndex = GetSubtitleDropIndex(dropPos);

        int oldIndex = _subtitleItems.IndexOf(draggedItem);
        if (oldIndex < 0 || oldIndex == newIndex) return;

        var positions = new Dictionary<SubtitlePriorityItem, double>();
        for (int i = 0; i < _subtitleItems.Count; i++)
        {
            var container = SubtitlePriorityItems.ItemContainerGenerator.ContainerFromIndex(i) as FrameworkElement;
            if (container != null)
                positions[_subtitleItems[i]] = container.TranslatePoint(new Point(0, 0), SubtitlePriorityItems).Y;
        }

        _subtitleItems.Move(oldIndex, newIndex);

        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            for (int i = 0; i < _subtitleItems.Count; i++)
            {
                var container = SubtitlePriorityItems.ItemContainerGenerator.ContainerFromIndex(i) as FrameworkElement;
                if (container == null) continue;

                var item = _subtitleItems[i];
                double newY = container.TranslatePoint(new Point(0, 0), SubtitlePriorityItems).Y;

                if (positions.TryGetValue(item, out double oldY) && Math.Abs(oldY - newY) > 1)
                {
                    var translate = container.RenderTransform as TranslateTransform;
                    if (translate == null)
                    {
                        translate = new TranslateTransform();
                        container.RenderTransform = translate;
                    }

                    translate.Y = oldY - newY;
                    var anim = new DoubleAnimation(oldY - newY, 0, TimeSpan.FromMilliseconds(250))
                    {
                        EasingFunction = ease
                    };
                    System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(anim, VNotch.Services.AnimationConfig.TargetFps);
                    translate.BeginAnimation(TranslateTransform.YProperty, anim);
                }

                if (item == draggedItem)
                {
                    var scale = container.RenderTransform as ScaleTransform;
                    if (container.RenderTransform is TranslateTransform)
                    {
                        var group = new TransformGroup();
                        group.Children.Add(container.RenderTransform);
                        var sc = new ScaleTransform(1, 1);
                        group.Children.Add(sc);
                        container.RenderTransformOrigin = new Point(0.5, 0.5);
                        container.RenderTransform = group;

                        var scaleAnim = new DoubleAnimation(1.03, 1.0, TimeSpan.FromMilliseconds(200))
                        {
                            EasingFunction = ease
                        };
                        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(scaleAnim, VNotch.Services.AnimationConfig.TargetFps);
                        sc.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
                        sc.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);
                    }
                }
            }
        });

        ApplySettingsFromUi(persist: true);
    }

    private int GetSubtitleDropIndex(Point dropPoint)
    {
        double y = 0;
        for (int i = 0; i < _subtitleItems.Count; i++)
        {
            var container = SubtitlePriorityItems.ItemContainerGenerator.ContainerFromIndex(i) as FrameworkElement;
            if (container == null) continue;

            double itemHeight = container.ActualHeight;
            if (dropPoint.Y < y + itemHeight / 2)
                return i;
            y += itemHeight;
        }
        return _subtitleItems.Count - 1;
    }

    private string GetSubtitlePriorityString()
    {
        return string.Join(",", _subtitleItems.Select(i => i.Key));
    }

    #endregion

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
        UpdateLiquidGlassAvailability(islandEnabled, animate);
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
            _settingsService.Save(_settings);
            _originalSettings = _settings.Clone();
            AnimateLocalizationChange();
            SettingsChanged?.Invoke(this, _settings);
        }
    }

    private void PopulateWidgetCombo()
    {
        WidgetCombo.Items.Clear();
        WidgetCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = Loc.Get("settings.widget.calendar"), Tag = "calendar" });
        WidgetCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = Loc.Get("settings.widget.clock"), Tag = "clock" });
        WidgetCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = Loc.Get("settings.widget.wordclock"), Tag = "wordclock" });
        WidgetCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = Loc.Get("settings.widget.digitalclock"), Tag = "digitalclock" });
        WidgetCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = Loc.Get("settings.widget.weather"), Tag = "weather" });
        WidgetCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = Loc.Get("settings.widget.sysmon"), Tag = "sysmon" });
        WidgetCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = Loc.Get("settings.widget.none"), Tag = "none" });
        WidgetCombo.SelectedIndex = _settings.ExpandedWidget switch
        {
            "clock" => 1,
            "wordclock" => 2,
            "digitalclock" => 3,
            "weather" => 4,
            "sysmon" => 5,
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
        ShelfWidgetCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = Loc.Get("settings.shelfWidget.sysmon"), Tag = "sysmon" });
        ShelfWidgetCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = Loc.Get("settings.shelfWidget.weather"), Tag = "weather" });
        ShelfWidgetCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = Loc.Get("settings.shelfWidget.clock"), Tag = "clock" });
        ShelfWidgetCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = Loc.Get("settings.shelfWidget.none"), Tag = "none" });

        ShelfWidgetCombo.SelectedIndex = (_settings.ShelfWidget ?? "camera").ToLowerInvariant() switch
        {
            "sysmon" => 1,
            "weather" => 2,
            "clock" => 3,
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
        ClockPageStyleCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = Loc.Get("settings.clockPageStyle.wordclock"), Tag = "wordclock" });

        ClockPageStyleCombo.SelectedIndex = (_settings.ClockPageStyle ?? "analog").ToLowerInvariant() switch
        {
            "digital" => 1,
            "wordclock" => 2,
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

    private void PopulateNavTabsSettings()
    {
        if (NavTabsSettingsContainer == null) return;
        NavTabsSettingsContainer.Children.Clear();

        var tabMetadata = new Dictionary<string, (string title, string iconPath)>
        {
            ["Media"] = ("Home", "M8 0L0 6V8H1V15H4V10H7V15H15V8H16V6L14 4.5V1H11V2.25L8 0ZM9 10H12V13H9V10Z"),
            ["Secondary"] = ("File Shelf", "M479.66,268.7l-32-151.81C441.48,83.77,417.68,64,384,64H128c-16.8,0-31,4.69-42.1,13.94s-18.37,22.31-21.58,38.89l-32,151.87A16.65,16.65,0,0,0,32,272V384a64,64,0,0,0,64,64H416a64,64,0,0,0,64-64V272A16.65,16.65,0,0,0,479.66,268.7Zm-384-145.4c0-.1,0-.19,0-.28,3.55-18.43,13.81-27,32.29-27H384c18.61,0,28.87,8.55,32.27,26.91,0,.13.05.26.07.39l26.93,127.88a4,4,0,0,1-3.92,4.82H320a15.92,15.92,0,0,0-16,15.82,48,48,0,1,1-96,0A15.92,15.92,0,0,0,192,256H72.65a4,4,0,0,1-3.92-4.82Z"),
            ["Timer"] = ("Clock & Timer", "M2 12C2 6.47715 6.47715 2 12 2C17.5228 2 22 6.47715 22 12C22 17.5228 17.5228 22 12 22C6.47715 22 2 17.5228 2 12ZM15.8321 14.5547C15.5257 15.0142 14.9048 15.1384 14.4453 14.8321L11.8451 13.0986C11.3171 12.7466 11 12.1541 11 11.5196V11.5V7C11 6.44772 11.4477 6 12 6C12.5523 6 13 6.44772 13 7V11.4648L15.5547 13.1679C16.0142 13.4743 16.1384 14.0952 15.8321 14.5547Z"),
            ["AudioMixer"] = ("Audio Mixer", "M13.5 2.5C13.5 2.1 13.05 1.86 12.72 2.09L6.8 6.2H3.5C2.95 6.2 2.5 6.65 2.5 7.2V12.8C2.5 13.35 2.95 13.8 3.5 13.8H6.8L12.72 17.91C13.05 18.14 13.5 17.9 13.5 17.5V2.5ZM16.04 6.05C15.74 5.79 15.28 5.82 15.02 6.13C14.76 6.43 14.79 6.89 15.1 7.15C16.0 7.93 16.5 8.93 16.5 10C16.5 11.07 16.0 12.07 15.1 12.85C14.79 13.11 14.76 13.57 15.02 13.87C15.28 14.18 15.74 14.21 16.04 13.95C17.25 12.91 18 11.5 18 10C18 8.5 17.25 7.09 16.04 6.05Z")
        };

        var orderTokens = (_settings.NavTabOrder ?? "Media,Secondary,Timer,AudioMixer")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var key in tabMetadata.Keys)
        {
            if (!orderTokens.Contains(key, StringComparer.OrdinalIgnoreCase))
                orderTokens.Add(key);
        }

        var visibleTokens = new HashSet<string>(
            (_settings.VisibleNavTabs ?? "Media,Secondary,Timer,AudioMixer")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);
        visibleTokens.Add("Media");

        for (int i = 0; i < orderTokens.Count; i++)
        {
            string token = orderTokens[i];
            if (!tabMetadata.TryGetValue(token, out var meta)) continue;

            var rowGrid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var leftStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            var check = new CheckBox
            {
                IsChecked = visibleTokens.Contains(token),
                IsEnabled = !string.Equals(token, "Media", StringComparison.OrdinalIgnoreCase),
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            string capturedToken = token;
            check.Checked += (s, e) =>
            {
                visibleTokens.Add(capturedToken);
                _settings.VisibleNavTabs = string.Join(",", visibleTokens);
                ApplySettingsFromUi(persist: true);
            };
            check.Unchecked += (s, e) =>
            {
                visibleTokens.Remove(capturedToken);
                _settings.VisibleNavTabs = string.Join(",", visibleTokens);
                ApplySettingsFromUi(persist: true);
            };
            leftStack.Children.Add(check);

            var iconBox = new Viewbox { Width = 14, Height = 14, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            iconBox.Child = new System.Windows.Shapes.Path { Data = Geometry.Parse(meta.iconPath), Fill = Brushes.White };
            leftStack.Children.Add(iconBox);

            var titleText = new TextBlock { Text = meta.title, Style = (Style)FindResource("ValueText"), VerticalAlignment = VerticalAlignment.Center };
            leftStack.Children.Add(titleText);

            rowGrid.Children.Add(leftStack);
            Grid.SetColumn(leftStack, 0);

            var buttonStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            if (i > 0)
            {
                var upBtn = new Button
                {
                    Content = "▲",
                    Width = 28, Height = 24,
                    Padding = new Thickness(0),
                    Margin = new Thickness(0, 0, 4, 0),
                    ToolTip = Loc.Get("settings.tab.moveUp")
                };
                int currentIndex = i;
                upBtn.Click += (s, e) =>
                {
                    string temp = orderTokens[currentIndex - 1];
                    orderTokens[currentIndex - 1] = orderTokens[currentIndex];
                    orderTokens[currentIndex] = temp;
                    _settings.NavTabOrder = string.Join(",", orderTokens);
                    PopulateNavTabsSettings();
                    ApplySettingsFromUi(persist: true);
                };
                buttonStack.Children.Add(upBtn);
            }
            else
            {
                buttonStack.Children.Add(new Border { Width = 28, Height = 24, Margin = new Thickness(0, 0, 4, 0) });
            }

            if (i < orderTokens.Count - 1)
            {
                var downBtn = new Button
                {
                    Content = "▼",
                    Width = 28, Height = 24,
                    Padding = new Thickness(0),
                    ToolTip = Loc.Get("settings.tab.moveDown")
                };
                int currentIndex = i;
                downBtn.Click += (s, e) =>
                {
                    string temp = orderTokens[currentIndex + 1];
                    orderTokens[currentIndex + 1] = orderTokens[currentIndex];
                    orderTokens[currentIndex] = temp;
                    _settings.NavTabOrder = string.Join(",", orderTokens);
                    PopulateNavTabsSettings();
                    ApplySettingsFromUi(persist: true);
                };
                buttonStack.Children.Add(downBtn);
            }
            else
            {
                buttonStack.Children.Add(new Border { Width = 28, Height = 24 });
            }

            rowGrid.Children.Add(buttonStack);
            Grid.SetColumn(buttonStack, 1);

            NavTabsSettingsContainer.Children.Add(rowGrid);
        }
    }

    private void ResetTabOrderButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.NavTabOrder = "Media,Secondary,Timer,AudioMixer";
        _settings.VisibleNavTabs = "Media,Secondary,Timer,AudioMixer";
        PopulateNavTabsSettings();
        ApplySettingsFromUi(persist: true);
    }

    #region Liquid Glass skin

    // Snapshot of the user's manually-tuned glass values, preserved so the
    private Models.LiquidGlassConfig? _customGlassSnapshot;
    private bool _suppressGlassPresetChange;

    private static Models.LiquidGlassConfig FrostedGlassPreset() => new()
    {
        BlurAmount = 0.20,
        Refraction = 0.6,
        EdgeBend = 1.0,
        ChromaticAberration = 0.06,
        EdgeHighlight = 0.30,
        Specular = 0.0,
        Fresnel = 0.0,
        Distortion = 0.03,
        ZRadius = 0.35,
        Opacity = 1.0,
        Saturation = 0.0,
        Brightness = 0.0,
        ShadowOpacity = 0.50,
        ShadowSpread = 18,
        BevelMode = 0,
        Variant = 0,
        PowerFactor = 3.0,
        RefractionA = 0.7,
        RefractionB = 2.3,
        RefractionC = 5.2,
        RefractionD = 6.9,
        FPower = 1.0,
        Noise = 0.085,
        GlowWeight = 0.60,
        GlowBias = -0.02,
        GlowEdge0 = 0.40,
        GlowEdge1 = -0.40
    };

    private static Models.LiquidGlassConfig DarkGlassPreset() => new()
    {
        BlurAmount = 0.25,
        Refraction = 1.0,
        EdgeBend = 1.2,
        ChromaticAberration = 0.10,
        EdgeHighlight = 0.12,
        Specular = 0.0,
        Fresnel = 0.0,
        Distortion = 0.06,
        ZRadius = 0.45,
        Opacity = 1.0,
        Saturation = -0.20,
        Brightness = -0.15,
        ShadowOpacity = 0.80,
        ShadowSpread = 30,
        BevelMode = 1,
        Variant = 0,
        PowerFactor = 3.0,
        RefractionA = 0.7,
        RefractionB = 2.3,
        RefractionC = 5.2,
        RefractionD = 6.9,
        FPower = 1.0,
        Noise = 0.060,
        GlowWeight = 0.50,
        GlowBias = -0.06,
        GlowEdge0 = 0.35,
        GlowEdge1 = -0.35
    };

    private static Models.LiquidGlassConfig RegularGlassPreset() => new()
    {
        BlurAmount = 0.20,
        Refraction = 0.7,
        EdgeBend = 1.0,
        ChromaticAberration = 0.08,
        EdgeHighlight = 0.25,
        Specular = 0.0,
        Fresnel = 0.0,
        Distortion = 0.04,
        ZRadius = 0.38,
        Opacity = 1.0,
        Saturation = 0.0,
        Brightness = 0.0,
        ShadowOpacity = 0.65,
        ShadowSpread = 20,
        BevelMode = 0,
        Variant = 0,
        PowerFactor = 3.0,
        RefractionA = 0.7,
        RefractionB = 2.3,
        RefractionC = 5.2,
        RefractionD = 6.9,
        FPower = 1.0,
        Noise = 0.066,
        GlowWeight = 0.692,
        GlowBias = -0.040,
        GlowEdge0 = 0.441,
        GlowEdge1 = -0.474
    };

    private static Models.LiquidGlassConfig ClearGlassPreset() => new()
    {
        BlurAmount = 0.05,
        Refraction = 0.15,
        EdgeBend = 1.25,
        ChromaticAberration = 0.04,
        EdgeHighlight = 0.30,
        Specular = 0.35,
        Fresnel = 0.45,
        Distortion = 0.01,
        Noise = 0.0,
        ZRadius = 0.15,
        Opacity = 1.0,
        Saturation = 0.05,
        Brightness = 0.05,
        ShadowOpacity = 0.20,
        ShadowSpread = 10,
        BevelMode = 0,
        Variant = 1
    };

    private static Models.LiquidGlassConfig UltraThinGlassPreset() => new()
    {
        BlurAmount = 0.10,
        Refraction = 0.20,
        EdgeBend = 1.10,
        ChromaticAberration = 0.04,
        EdgeHighlight = 0.20,
        Specular = 0.35,
        Fresnel = 0.25,
        Distortion = 0.015,
        Noise = 0.04,
        ZRadius = 0.10,
        Opacity = 1.0,
        Saturation = -0.05,
        Brightness = 0.0,
        ShadowOpacity = 0.30,
        ShadowSpread = 10,
        BevelMode = 0,
        Variant = 0
    };

    private static Models.LiquidGlassConfig ThinGlassPreset() => new()
    {
        BlurAmount = 0.20,
        Refraction = 0.30,
        EdgeBend = 1.30,
        ChromaticAberration = 0.05,
        EdgeHighlight = 0.22,
        Specular = 0.32,
        Fresnel = 0.30,
        Distortion = 0.02,
        Noise = 0.06,
        ZRadius = 0.15,
        Opacity = 1.0,
        Saturation = -0.10,
        Brightness = -0.05,
        ShadowOpacity = 0.55,
        ShadowSpread = 15,
        BevelMode = 0,
        Variant = 0
    };

    private static Models.LiquidGlassConfig ThickGlassPreset() => new()
    {
        BlurAmount = 0.35,
        Refraction = 0.8,
        EdgeBend = 1.75,
        ChromaticAberration = 0.10,
        EdgeHighlight = 0.28,
        Specular = 0.28,
        Fresnel = 0.40,
        Distortion = 0.05,
        Noise = 0.10,
        ZRadius = 0.48,
        Opacity = 1.0,
        Saturation = -0.20,
        Brightness = -0.15,
        ShadowOpacity = 0.75,
        ShadowSpread = 25,
        BevelMode = 0,
        Variant = 0
    };

    private static Models.LiquidGlassConfig UltraThickGlassPreset() => new()
    {
        BlurAmount = 0.50,
        Refraction = 0.9,
        EdgeBend = 1.90,
        ChromaticAberration = 0.12,
        EdgeHighlight = 0.32,
        Specular = 0.25,
        Fresnel = 0.45,
        Distortion = 0.06,
        Noise = 0.12,
        ZRadius = 0.60,
        Opacity = 1.0,
        Saturation = -0.25,
        Brightness = -0.20,
        ShadowOpacity = 0.85,
        ShadowSpread = 32,
        BevelMode = 0,
        Variant = 0
    };

    private void EnsureGlassPresetItems()
    {
        if (GlassPresetCombo.Items.Count > 0) return;
        GlassPresetCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = Loc.Get("settings.glass.preset.custom"), Tag = "custom" });
        GlassPresetCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = Loc.Get("settings.glass.preset.frosted"), Tag = "frosted" });
        GlassPresetCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = Loc.Get("settings.glass.preset.dark"), Tag = "dark" });
        GlassPresetCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = Loc.Get("settings.glass.preset.ultrathin"), Tag = "ultrathin" });
        GlassPresetCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = Loc.Get("settings.glass.preset.thin"), Tag = "thin" });
        GlassPresetCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = Loc.Get("settings.glass.preset.regular"), Tag = "regular" });
        GlassPresetCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = Loc.Get("settings.glass.preset.thick"), Tag = "thick" });
        GlassPresetCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = Loc.Get("settings.glass.preset.ultrathick"), Tag = "ultrathick" });
        GlassPresetCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = Loc.Get("settings.glass.preset.clear"), Tag = "clear" });
    }

    private Models.LiquidGlassConfig ReadGlassConfigFromSliders()
    {
        var c = (_settings.LiquidGlass ?? new Models.LiquidGlassConfig()).Clone();
        c.BlurAmount = GlassBlurSlider.Value / 100.0;
        c.Refraction = GlassRefractionSlider.Value / 100.0;
        c.EdgeBend = GlassEdgeBendSlider.Value / 100.0;
        c.ChromaticAberration = GlassChromSlider.Value / 100.0;
        c.EdgeHighlight = GlassEdgeHighlightSlider.Value / 100.0;
        c.TouchLight = GlassTouchLightSlider.Value / 100.0;
        c.Specular = GlassSpecularSlider.Value / 100.0;
        c.Fresnel = GlassFresnelSlider.Value / 100.0;
        c.Distortion = GlassDistortionSlider.Value / 100.0;
        c.Noise = GlassGrainSlider.Value / 100.0;
        c.ZRadius = GlassZRadiusSlider.Value / 100.0;
        c.Opacity = GlassOpacitySlider.Value / 100.0;
        c.Saturation = GlassSaturationSlider.Value / 100.0;
        c.Brightness = GlassBrightnessSlider.Value / 100.0;
        c.ShadowOpacity = GlassShadowOpacitySlider.Value / 100.0;
        c.ShadowSpread = (int)Math.Round(GlassShadowSpreadSlider.Value);
        c.BevelMode = (int)Math.Round(GlassBevelModeSlider.Value);
        c.TargetFps = (int)Math.Round(GlassFpsSlider.Value);
        return c;
    }

    private void ApplyGlassConfigToSliders(Models.LiquidGlassConfig c)
    {
        bool prev = _isLoadingSettings;
        _isLoadingSettings = true;
        try
        {
            GlassBlurSlider.Value = Math.Round(c.BlurAmount * 100);
            GlassRefractionSlider.Value = Math.Round(c.Refraction * 100);
            GlassEdgeBendSlider.Value = Math.Round(c.EdgeBend * 100);
            GlassChromSlider.Value = Math.Round(c.ChromaticAberration * 100);
            GlassEdgeHighlightSlider.Value = Math.Round(c.EdgeHighlight * 100);
            GlassTouchLightSlider.Value = Math.Round(c.TouchLight * 100);
            GlassSpecularSlider.Value = Math.Round(c.Specular * 100);
            GlassFresnelSlider.Value = Math.Round(c.Fresnel * 100);
            GlassDistortionSlider.Value = Math.Round(c.Distortion * 100);
            GlassGrainSlider.Value = Math.Round(c.Noise * 100);
            GlassZRadiusSlider.Value = Math.Round(c.ZRadius * 100);
            GlassOpacitySlider.Value = Math.Round(c.Opacity * 100);
            GlassSaturationSlider.Value = Math.Round(c.Saturation * 100);
            GlassBrightnessSlider.Value = Math.Round(c.Brightness * 100);
            GlassShadowOpacitySlider.Value = Math.Round(c.ShadowOpacity * 100);
            GlassShadowSpreadSlider.Value = c.ShadowSpread;
            GlassBevelModeSlider.Value = c.BevelMode;
            GlassFpsSlider.Value = (c.TargetFps <= 0 || c.TargetFps == 60) ? 0 : c.TargetFps;
        }
        finally
        {
            _isLoadingSettings = prev;
        }
    }

    private void SelectGlassPreset(string tag)
    {
        EnsureGlassPresetItems();
        for (int i = 0; i < GlassPresetCombo.Items.Count; i++)
        {
            if (GlassPresetCombo.Items[i] is System.Windows.Controls.ComboBoxItem item &&
                (item.Tag as string) == tag)
            {
                _suppressGlassPresetChange = true;
                GlassPresetCombo.SelectedIndex = i;
                _suppressGlassPresetChange = false;
                return;
            }
        }
    }

    private void LoadLiquidGlassUi()
    {
        bool prev = _isLoadingSettings;
        _isLoadingSettings = true;
        try
        {
            if (SkinCombo.Items.Count == 0)
            {
                PopulateSkinItems();
            }

            EnsureGlassPresetItems();

            bool glass = string.Equals(_settings.NotchStyle, "liquidglass", StringComparison.OrdinalIgnoreCase);
            SkinCombo.SelectedIndex = glass ? 1 : 0;

            var c = _settings.LiquidGlass ?? new Models.LiquidGlassConfig();
            ApplyGlassConfigToSliders(c);
            if (GpuRefractionCheck != null)
                GpuRefractionCheck.IsChecked = c.UseGpuRefraction;

            // The user's tuned values live in their own persistent slot. If it's
            _settings.LiquidGlassCustom ??= c.Clone();
            _customGlassSnapshot = _settings.LiquidGlassCustom.Clone();

            string preset = string.IsNullOrWhiteSpace(_settings.LiquidGlassPreset) ? "custom" : _settings.LiquidGlassPreset;
            SelectGlassPreset(preset);

            LiquidGlassConfigPanel.Visibility = glass ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            _isLoadingSettings = prev;
        }
    }

    private void SaveLiquidGlassUi()
    {
        string requestedStyle =
            (SkinCombo.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string ?? "default";
        _settings.NotchStyle = string.Equals(requestedStyle, "liquidglass", StringComparison.OrdinalIgnoreCase)
            ? "liquidglass"
            : "default";

        var c = _settings.LiquidGlass ??= new Models.LiquidGlassConfig();
        var ui = ReadGlassConfigFromSliders();
        c.BlurAmount = ui.BlurAmount;
        c.Refraction = ui.Refraction;
        c.EdgeBend = ui.EdgeBend;
        c.ChromaticAberration = ui.ChromaticAberration;
        c.EdgeHighlight = ui.EdgeHighlight;
        c.TouchLight = ui.TouchLight;
        c.Specular = ui.Specular;
        c.Fresnel = ui.Fresnel;
        c.Distortion = ui.Distortion;
        c.Noise = ui.Noise;
        c.ZRadius = ui.ZRadius;
        c.Opacity = ui.Opacity;
        c.Saturation = ui.Saturation;
        c.Brightness = ui.Brightness;
        c.ShadowOpacity = ui.ShadowOpacity;
        c.ShadowSpread = ui.ShadowSpread;
        c.BevelMode = ui.BevelMode;
        c.TargetFps = ui.TargetFps;

        string activePreset = (GlassPresetCombo.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string ?? "custom";
        if (activePreset == "clear") c.Variant = 1;
        else if (activePreset == "regular" || activePreset == "frosted" || activePreset == "dark" || activePreset == "ultrathin" || activePreset == "thin" || activePreset == "thick" || activePreset == "ultrathick") c.Variant = 0;
        else c.Variant = _customGlassSnapshot?.Variant ?? c.Variant;

        c.UseGpuRefraction = GpuRefractionCheck?.IsChecked ?? false;

        // Persist which preset is active and the user's custom slot. A built-in
        _settings.LiquidGlassPreset = (GlassPresetCombo.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string ?? "custom";
        if (_customGlassSnapshot != null)
            _settings.LiquidGlassCustom = _customGlassSnapshot.Clone();
    }

    private void GlassPresetCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_isLoadingSettings || _suppressGlassPresetChange) return;
        if (GlassPresetCombo.SelectedItem is not System.Windows.Controls.ComboBoxItem item) return;

        Models.LiquidGlassConfig? preset = null;
        switch (item.Tag as string)
        {
            case "frosted": preset = FrostedGlassPreset(); break;
            case "dark": preset = DarkGlassPreset(); break;
            case "regular": preset = RegularGlassPreset(); break;
            case "ultrathin": preset = UltraThinGlassPreset(); break;
            case "thin": preset = ThinGlassPreset(); break;
            case "thick": preset = ThickGlassPreset(); break;
            case "ultrathick": preset = UltraThickGlassPreset(); break;
            case "clear": preset = ClearGlassPreset(); break;
            case "custom":
            default:
                if (_customGlassSnapshot != null) preset = _customGlassSnapshot;
                break;
        }

        if (preset != null)
        {
            var currentFps = _settings.LiquidGlass?.TargetFps ?? 0;
            var currentGpu = _settings.LiquidGlass?.UseGpuRefraction ?? true;
            _settings.LiquidGlass = preset.Clone();
            _settings.LiquidGlass.TargetFps = currentFps;
            _settings.LiquidGlass.UseGpuRefraction = currentGpu;
            ApplyGlassConfigToSliders(_settings.LiquidGlass);
        }

        PushLivePreview();
    }

    private void SkinCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateLiquidGlassAvailability(DynamicIslandModeCheck?.IsChecked ?? _settings.EnableDynamicIslandMode, animate: true);

        if (_isLoadingSettings) return;
        PushLivePreview();
    }

    // Populates the skin selector. Plain string items so the selection box renders
    private void PopulateSkinItems()
    {
        SkinCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = Loc.Get("settings.skin.default"), Tag = "default" });
        SkinCombo.Items.Add(new System.Windows.Controls.ComboBoxItem
        {
            Content = Loc.Get("settings.skin.liquidglass"),
            Tag = "liquidglass",
            IsEnabled = true
        });
    }

    private void UpdateLiquidGlassAvailability(bool islandEnabled, bool animate = false)
    {
        if (SkinCombo == null) return;

        bool glassSelected = SkinCombo.SelectedItem is System.Windows.Controls.ComboBoxItem selected &&
                             (selected.Tag as string) == "liquidglass";

        AnimateCollapsibleRow(LiquidGlassConfigPanel, glassSelected, animate);
    }

    private void GlassConfigSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isLoadingSettings) return;

        // A manual slider tweak means the values no longer match a named preset â€”
        _customGlassSnapshot = ReadGlassConfigFromSliders();
        SelectGlassPreset("custom");

        PushLivePreview();
    }

    private void GlassConfigCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings) return;
        PushLivePreview();
    }

    private void GpuRefractionCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings) return;
        PushLivePreview();
    }

    private void ApplyLiquidGlassLocalization()
    {
        if (SkinLabel == null) return;

        if (SkinHeader != null) SkinHeader.Text = Loc.Get("settings.skins");
        SkinLabel.Text = Loc.Get("settings.skin");
        if (SkinAlphaBadge != null) SkinAlphaBadge.Text = Loc.Get("settings.badge.alpha");
        SkinHint.Text = Loc.Get("settings.skin.hint");
        if (SkinWarningNote != null) SkinWarningNote.Text = Loc.Get("settings.skin.warning");

        int idx = SkinCombo.SelectedIndex;
        bool prev = _isLoadingSettings;
        _isLoadingSettings = true;
        SkinCombo.Items.Clear();
        PopulateSkinItems();
        SkinCombo.SelectedIndex = idx < 0 ? 0 : idx;
        UpdateLiquidGlassAvailability(DynamicIslandModeCheck?.IsChecked ?? _settings.EnableDynamicIslandMode);
        _isLoadingSettings = prev;

        if (GlassPresetLabel != null) GlassPresetLabel.Text = Loc.Get("settings.glass.preset");
        if (GlassAdvancedWarning != null) GlassAdvancedWarning.Text = Loc.Get("settings.glass.advancedWarning");
        int presetIdx = GlassPresetCombo.SelectedIndex;
        _suppressGlassPresetChange = true;
        GlassPresetCombo.Items.Clear();
        GlassPresetCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = Loc.Get("settings.glass.preset.custom"), Tag = "custom" });
        GlassPresetCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = Loc.Get("settings.glass.preset.frosted"), Tag = "frosted" });
        GlassPresetCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = Loc.Get("settings.glass.preset.dark"), Tag = "dark" });
        GlassPresetCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = Loc.Get("settings.glass.preset.regular"), Tag = "regular" });
        GlassPresetCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = Loc.Get("settings.glass.preset.clear"), Tag = "clear" });
        GlassPresetCombo.SelectedIndex = presetIdx < 0 ? 0 : presetIdx;
        _suppressGlassPresetChange = false;

        GlassBlurSlider.Label = Loc.Get("settings.glass.blur");
        GlassRefractionSlider.Label = Loc.Get("settings.glass.refraction");
        GlassEdgeBendSlider.Label = Loc.Get("settings.glass.edgeBend");
        GlassChromSlider.Label = Loc.Get("settings.glass.chrom");
        GlassEdgeHighlightSlider.Label = Loc.Get("settings.glass.edgeHighlight");
        GlassTouchLightSlider.Label = Loc.Get("settings.glass.touchLight") ?? "Touch Light";
        GlassSpecularSlider.Label = Loc.Get("settings.glass.specular");
        GlassFresnelSlider.Label = Loc.Get("settings.glass.fresnel");
        GlassDistortionSlider.Label = Loc.Get("settings.glass.distortion");
        GlassGrainSlider.Label = Loc.Get("settings.glass.grain");
        GlassZRadiusSlider.Label = Loc.Get("settings.glass.zRadius");
        GlassOpacitySlider.Label = Loc.Get("settings.glass.opacity");
        GlassSaturationSlider.Label = Loc.Get("settings.glass.saturation");
        GlassBrightnessSlider.Label = Loc.Get("settings.glass.brightness");
        GlassShadowOpacitySlider.Label = Loc.Get("settings.glass.shadowOpacity");
        GlassShadowSpreadSlider.Label = Loc.Get("settings.glass.shadowSpread");
        GlassBevelModeSlider.Label = Loc.Get("settings.glass.bevelMode");
    }

    #endregion

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
                ApplySettingsFromUi(persist: false);
                _settingsService.ExportSettingsToFile(dialog.FileName, _settings);

                if (BackupStatusText != null)
                {
                    BackupStatusText.Text = Loc.Get("settings.export.success");
                    BackupStatusText.Foreground = new SolidColorBrush(Color.FromRgb(48, 209, 88));
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

    private void ImportSettings_Click(object sender, RoutedEventArgs e)
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
                var (imported, requiresRestart) = _settingsService.ImportSettingsFromFile(dialog.FileName, _settings);

                _settings = imported.Clone();
                _originalSettings = imported.Clone();
                _settingsService.Save(_settings);
                StartupManager.SetAutoStart(_settings.AutoStart);

                LoadSettings();
                SettingsChanged?.Invoke(this, _settings);

                if (BackupStatusText != null)
                {
                    BackupStatusText.Text = Loc.Get("settings.import.success");
                    BackupStatusText.Foreground = new SolidColorBrush(Color.FromRgb(48, 209, 88));
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

    private void ApplyProcessPriority(string priority)
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
            VNotch.Services.RuntimeLog.Log("SETTINGS", $"Failed to set process priority: {ex.Message}");
        }
    }

    private void ApplyGpuPreference(int preference)
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
            VNotch.Services.RuntimeLog.Log("SETTINGS", $"Failed to set GPU preference: {ex.Message}");
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
            YouTubeApiKeyStatus.Foreground = new SolidColorBrush(Color.FromRgb(107, 114, 128));
        }
        else if (key.Length < 30)
        {
            YouTubeApiKeyStatus.Text = Loc.Get("settings.youtubeApi.statusTooShort");
            YouTubeApiKeyStatus.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68));
        }
        else if (!key.StartsWith("AIza", StringComparison.Ordinal))
        {
            YouTubeApiKeyStatus.Text = Loc.Get("settings.youtubeApi.statusMustStart");
            YouTubeApiKeyStatus.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68));
        }
        else if (key.Length >= 35 && key.Length <= 45)
        {
            YouTubeApiKeyStatus.Text = Loc.Get("settings.youtubeApi.statusValid");
            YouTubeApiKeyStatus.Foreground = new SolidColorBrush(Color.FromRgb(74, 222, 128));
        }
        else
        {
            YouTubeApiKeyStatus.Text = Loc.Get("settings.youtubeApi.statusUnexpectedLength");
            YouTubeApiKeyStatus.Foreground = new SolidColorBrush(Color.FromRgb(234, 179, 8));
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
            (SettingsTitleText, () => SettingsTitleText.Text = Loc.Get("settings.title")),
            (SettingsSubtitleText, () => SettingsSubtitleText.Text = Loc.Get("settings.subtitle")),
            (SettingsVersionBadgeText, () => { if (SettingsVersionBadgeText != null) SettingsVersionBadgeText.Text = $"v{GetAppVersion()}"; }),
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
                int monitorIdx = MonitorCombo.SelectedIndex;
                var monitors = NotchManager.GetMonitorNames();
                MonitorCombo.ItemsSource = monitors;
                MonitorCombo.SelectedIndex = Math.Min(monitorIdx < 0 ? _settings.MonitorIndex : monitorIdx, monitors.Length - 1);
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

            (AnimationFpsLabel, () => { AnimationFpsLabel.Text = Loc.Get("settings.animationFps"); AnimationFpsSlider.Label = Loc.Get("settings.animationFps"); AnimationFpsSlider.Description = Loc.Get("settings.animationFps.hint"); }),
            (EnableBlurEffectsHint, () => EnableBlurEffectsHint.Text = Loc.Get("settings.enableBlurEffects.hint")),
            (EnableSubjectBlurHint, () => EnableSubjectBlurHint.Text = Loc.Get("settings.enableSubjectBlur.hint")),
            (EnableSmartCropHint, () => EnableSmartCropHint.Text = Loc.Get("settings.enableSmartCrop.hint")),
            (MediaArtBackgroundHint, () => { MediaArtBackgroundCheck.Content = Loc.Get("settings.mediaArtBackground"); MediaArtBackgroundHint.Text = Loc.Get("settings.mediaArtBackground.hint"); }),

            (DonatingTitle, () => DonatingTitle.Text = Loc.Get("settings.donating.title")),
            (DonatingDescription, () => DonatingDescription.Text = Loc.Get("settings.donating.description")),
            (DonatingBankTitle, () => DonatingBankTitle.Text = Loc.Get("settings.donating.bank")),
            (DonatingBankHint, () => DonatingBankHint.Text = Loc.Get("settings.donating.bank.hint")),

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

        AnimateContentChange(ExportSettingsButton, () => ExportSettingsButton.Content = Loc.Get("settings.exportSettings.btn"), staggerMs, easeOut, fps, slideDist);
        staggerMs += staggerStep;
        AnimateContentChange(ImportSettingsButton, () => ImportSettingsButton.Content = Loc.Get("settings.importSettings.btn"), staggerMs, easeOut, fps, slideDist);
        staggerMs += staggerStep;
        AnimateContentChange(RestartNowButton, () => RestartNowButton.Content = Loc.Get("settings.restartBanner.restartNow"), staggerMs, easeOut, fps, slideDist);
        staggerMs += staggerStep;
        AnimateContentChange(RestartLaterButton, () => RestartLaterButton.Content = Loc.Get("settings.restartBanner.later"), staggerMs, easeOut, fps, slideDist);
        staggerMs += staggerStep;
        TooltipHelper.SetLocalizedTooltip(ExportSettingsButton, "tooltip.exportSettings");
        TooltipHelper.SetLocalizedTooltip(ImportSettingsButton, "tooltip.importSettings");

        AnimateContentChange(CheckUpdateButton, () => CheckUpdateButton.Content = Loc.Get("settings.checkUpdate"), staggerMs, easeOut, fps, slideDist);
        staggerMs += staggerStep;
        AnimateContentChange(DownloadUpdateButton, () => DownloadUpdateButton.Content = Loc.Get("settings.downloadInstall"), staggerMs, easeOut, fps, slideDist);
        staggerMs += staggerStep;
        AnimateContentChange(DonatePaypalButton, () => DonatePaypalButton.Content = Loc.Get("settings.donating.paypal"), staggerMs, easeOut, fps, slideDist);
        staggerMs += staggerStep;
        AnimateContentChange(ResetButton, () => ResetButton.Content = Loc.Get("settings.btn.reset"), staggerMs, easeOut, fps, slideDist);
        staggerMs += staggerStep;
        AnimateContentChange(ApplyButton, () => ApplyButton.Content = Loc.Get("settings.btn.apply"), staggerMs, easeOut, fps, slideDist);
        staggerMs += staggerStep;
        AnimateContentChange(SaveButton, () => SaveButton.Content = Loc.Get("settings.btn.save"), staggerMs, easeOut, fps, slideDist);
        staggerMs += staggerStep;

        AnimateContentChange(AutoStartCheck, () => AutoStartCheck.Content = Loc.Get("settings.autoStart"), staggerMs, easeOut, fps, slideDist);
        staggerMs += staggerStep;
        AnimateContentChange(StayBehindWindowsCheck, () => StayBehindWindowsCheck.Content = Loc.Get("settings.stayBehindWindows"), staggerMs, easeOut, fps, slideDist);
        staggerMs += staggerStep;
        AnimateContentChange(HelloGreetingCheck, () => HelloGreetingCheck.Content = Loc.Get("settings.helloGreeting"), staggerMs, easeOut, fps, slideDist);
        staggerMs += staggerStep;
        AnimateContentChange(HideOnExclusiveFullscreenCheck, () => HideOnExclusiveFullscreenCheck.Content = Loc.Get("settings.hideExclusiveFs"), staggerMs, easeOut, fps, slideDist);
        staggerMs += staggerStep;
        AnimateContentChange(HideOnWindowedFullscreenCheck, () => HideOnWindowedFullscreenCheck.Content = Loc.Get("settings.hideWindowedFs"), staggerMs, easeOut, fps, slideDist);
        staggerMs += staggerStep;
        AnimateContentChange(MusicNotifyCheck, () => MusicNotifyCheck.Content = Loc.Get("settings.musicNotify"), staggerMs, easeOut, fps, slideDist);
        staggerMs += staggerStep;
        AnimateContentChange(SystemNotifyCheck, () => SystemNotifyCheck.Content = Loc.Get("settings.systemNotify"), staggerMs, easeOut, fps, slideDist);
        staggerMs += staggerStep;
        AnimateContentChange(ShelfUnlockCheck, () => ShelfUnlockCheck.Content = Loc.Get("settings.shelfUnlock"), staggerMs, easeOut, fps, slideDist);
        staggerMs += staggerStep;
        AnimateContentChange(CopyShelfClipboardCheck, () => CopyShelfClipboardCheck.Content = Loc.Get("settings.copyShelfClipboard"), staggerMs, easeOut, fps, slideDist);
        staggerMs += staggerStep;
        AnimateContentChange(ShowBatteryCheck, () => ShowBatteryCheck.Content = Loc.Get("settings.showBattery"), staggerMs, easeOut, fps, slideDist);
        staggerMs += staggerStep;
        AnimateContentChange(EnableSpotlightCheck, () => EnableSpotlightCheck.Content = Loc.Get("settings.enableSpotlight"), staggerMs, easeOut, fps, slideDist);
        staggerMs += staggerStep;
        AnimateContentChange(YouTubeApiCheck, () => YouTubeApiCheck.Content = Loc.Get("settings.youtubeApi"), staggerMs, easeOut, fps, slideDist);
        staggerMs += staggerStep;
        AnimateContentChange(HoverExpandCheck, () => HoverExpandCheck.Content = Loc.Get("settings.hoverExpand"), staggerMs, easeOut, fps, slideDist);
        staggerMs += staggerStep;
        AnimateContentChange(DisableMouseLeaveAutoCloseCheck, () => DisableMouseLeaveAutoCloseCheck.Content = Loc.Get("settings.disableAutoClose"), staggerMs, easeOut, fps, slideDist);
        staggerMs += staggerStep;
        AnimateContentChange(ReopenLastViewCheck, () => ReopenLastViewCheck.Content = Loc.Get("settings.reopenLastView"), staggerMs, easeOut, fps, slideDist);
        staggerMs += staggerStep;
        AnimateContentChange(IdleAutoHideCheck, () => IdleAutoHideCheck.Content = Loc.Get("settings.idleAutoHide"), staggerMs, easeOut, fps, slideDist);
        staggerMs += staggerStep;
        AnimateContentChange(EnableSpotifyLyricsCheck, () => EnableSpotifyLyricsCheck.Content = Loc.Get("settings.enableSpotifyLyrics"), staggerMs, easeOut, fps, slideDist);
        staggerMs += staggerStep;
        AnimateContentChange(EnableSpotifyCanvasCheck, () => EnableSpotifyCanvasCheck.Content = Loc.Get("settings.enableSpotifyCanvas"), staggerMs, easeOut, fps, slideDist);
        staggerMs += staggerStep;
        AnimateContentChange(SpotifyConnectButton, () => SpotifyConnectButton.Content = Loc.Get("settings.spotifyCanvas.connect"), staggerMs, easeOut, fps, slideDist);
        staggerMs += staggerStep;
        AnimateContentChange(SpotifyDisconnectButton, () => SpotifyDisconnectButton.Content = Loc.Get("settings.spotifyCanvas.disconnect"), staggerMs, easeOut, fps, slideDist);
        staggerMs += staggerStep;
        AnimateContentChange(EnableYouTubeSubtitlesCheck, () => EnableYouTubeSubtitlesLabel.Text = Loc.Get("settings.enableYouTubeSubtitles"), staggerMs, easeOut, fps, slideDist);
        staggerMs += staggerStep;
        AnimateContentChange(IgnoreYouTubeAutoSubtitlesCheck, () => IgnoreYouTubeAutoSubtitlesLabel.Text = Loc.Get("settings.ignoreYouTubeAutoSubtitles"), staggerMs, easeOut, fps, slideDist);
        staggerMs += staggerStep;
        AnimateContentChange(EnableBlurEffectsCheck, () => EnableBlurEffectsCheck.Content = Loc.Get("settings.enableBlurEffects"), staggerMs, easeOut, fps, slideDist);
        staggerMs += staggerStep;
        AnimateContentChange(EnableSubjectBlurCheck, () => EnableSubjectBlurCheck.Content = Loc.Get("settings.enableSubjectBlur"), staggerMs, easeOut, fps, slideDist);
        staggerMs += staggerStep;
        AnimateContentChange(EnableSmartCropCheck, () => EnableSmartCropCheck.Content = Loc.Get("settings.enableSmartCrop"), staggerMs, easeOut, fps, slideDist);
        staggerMs += staggerStep;
        AnimateContentChange(EnableWeatherCheck, () => EnableWeatherCheck.Content = Loc.Get("settings.enableWeather"), staggerMs, easeOut, fps, slideDist);
        staggerMs += staggerStep;

        foreach (var (element, update) in textUpdates)
        {
            if (element == null) continue;
            AnimateTextSwap(element, update, staggerMs, easeOut, fps, slideDist);
            staggerMs += staggerStep;
        }

    }

    private void AnimateTextSwap(FrameworkElement element, Action updateText, int delayMs, IEasingFunction easing, int fps, double slideDist)
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
        Timeline.SetDesiredFrameRate(fadeOut, VNotch.Services.AnimationConfig.TargetFps);

        var slideOut = new DoubleAnimation
        {
            To = -10,
            Duration = TimeSpan.FromMilliseconds(100),
            EasingFunction = easing,
            BeginTime = TimeSpan.FromMilliseconds(delayMs)
        };
        Timeline.SetDesiredFrameRate(slideOut, VNotch.Services.AnimationConfig.TargetFps);

        fadeOut.Completed += (s, e) =>
        {
            updateText();

            translate.X = 14;

            var fadeIn = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(220),
                EasingFunction = easing
            };
            Timeline.SetDesiredFrameRate(fadeIn, VNotch.Services.AnimationConfig.TargetFps);

            var slideIn = new DoubleAnimation
            {
                From = 14,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(300),
                EasingFunction = easing
            };
            Timeline.SetDesiredFrameRate(slideIn, VNotch.Services.AnimationConfig.TargetFps);

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

    private void AnimateContentChange(FrameworkElement element, Action updateContent, int delayMs, IEasingFunction easing, int fps, double slideDist)
    {
        element.BeginAnimation(OpacityProperty, null);

        var fadeOut = new DoubleAnimation
        {
            To = 0,
            Duration = TimeSpan.FromMilliseconds(120),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
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
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
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
                ApplySettingsFromUi(persist: false);
            };
        }

        _livePreviewDebounce.Stop();
        _livePreviewDebounce.Start();
    }

    #endregion

    #region Entrance Animation

    private void PlayEntranceAnimation()
    {
        var easeOut = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 6 };
        var easeOutStrong = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 7 };
        var itemEase = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 6 };
        int fps = VNotch.Services.AnimationConfig.TargetFps;

        var totalDur = TimeSpan.FromMilliseconds(650);

        double notchLeft = 0, notchTop = 0, notchW = 230, notchH = 32, notchRadius = 8;
        if (Owner is MainWindow mainWindow)
        {
            var rect = mainWindow.GetNotchScreenRect();
            notchLeft = rect.Left;
            notchTop = rect.Top;
            notchW = rect.Width;
            notchH = rect.Height;
            notchRadius = rect.CornerRadius;
        }

        double shellWidth = ActualWidth > 0 ? ActualWidth - 36 : 824;
        double shellHeight = ActualHeight > 0 ? ActualHeight - 36 : 584;
        double startScaleX = Math.Max(0.02, notchW / shellWidth);
        double startScaleY = Math.Max(0.02, notchH / shellHeight);
        double startRadius = Math.Max(notchRadius, 12);

        MainShell.Opacity = 1.0;
        MainShell.RenderTransformOrigin = new Point(0.5, 0.0);
        MainShell.Effect = null;

        ShellContent.CacheMode = new System.Windows.Media.BitmapCache { RenderAtScale = 1.0 };
        ShellScale.ScaleX = startScaleX;
        ShellScale.ScaleY = startScaleY;
        ShellTranslate.Y = 0;
        MainShell.CornerRadius = new CornerRadius(startRadius);
        FooterBar.CornerRadius = new CornerRadius(0, 0, startRadius, startRadius);

        double finalLeft = Left;
        double finalTop = Top;

        Left = notchLeft + notchW / 2.0 - ActualWidth / 2.0;
        Top = notchTop;

        var expandX = new DoubleAnimation(startScaleX, 1.0, totalDur)
        {
            EasingFunction = easeOutStrong
        };
        Timeline.SetDesiredFrameRate(expandX, fps);

        var expandY = new DoubleAnimation(startScaleY, 1.0, totalDur)
        {
            EasingFunction = easeOutStrong
        };
        Timeline.SetDesiredFrameRate(expandY, fps);

        _shellCornerRadius = startRadius;
        var cornerAnim = new DoubleAnimation(startRadius, 24, totalDur)
        {
            EasingFunction = easeOut
        };
        Timeline.SetDesiredFrameRate(cornerAnim, fps);

        var moveTop = new DoubleAnimation(Top, finalTop, totalDur)
        {
            EasingFunction = easeOutStrong
        };
        Timeline.SetDesiredFrameRate(moveTop, fps);

        var moveLeft = new DoubleAnimation(Left, finalLeft, totalDur)
        {
            EasingFunction = easeOutStrong
        };
        Timeline.SetDesiredFrameRate(moveLeft, fps);

        // Release the HoldEnd fill once the fly-in finishes so Top/Left track
        moveTop.Completed += (s, e) =>
        {
            if (_isClosing) return;
            Top = finalTop;
            this.BeginAnimation(TopProperty, null);
        };
        moveLeft.Completed += (s, e) =>
        {
            if (_isClosing) return;
            Left = finalLeft;
            this.BeginAnimation(LeftProperty, null);
        };

        expandX.Completed += (s, e) =>
        {
            if (_isClosing) return;

            ShellContent.CacheMode = null;
            MainShell.RenderTransformOrigin = new Point(0.5, 0.5);

            Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
            {
                if (_isClosing) return;

                var shadow = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = System.Windows.Media.Colors.Black,
                    BlurRadius = 30,
                    ShadowDepth = 0,
                    Opacity = 0.0
                };
                MainShell.Effect = shadow;

                var shadowFade = new DoubleAnimation(0.0, 0.42, TimeSpan.FromMilliseconds(180))
                {
                    EasingFunction = easeOut
                };
                Timeline.SetDesiredFrameRate(shadowFade, fps);
                shadow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty, shadowFade);
            }));
        };

        ShellScale.BeginAnimation(ScaleTransform.ScaleXProperty, expandX);
        ShellScale.BeginAnimation(ScaleTransform.ScaleYProperty, expandY);
        this.BeginAnimation(ShellCornerRadiusProperty, cornerAnim);
        this.BeginAnimation(TopProperty, moveTop);
        this.BeginAnimation(LeftProperty, moveLeft);

        int contentDelay = 250;
        AnimateEntranceItem(SettingsHeader, HeaderTranslate, contentDelay);

        int socialDelay = contentDelay + 80;
        AnimateSocialIcon(SocialWebsite, SocialWebsiteTranslate, socialDelay);
        AnimateSocialIcon(SocialGitHub, SocialGitHubTranslate, socialDelay + 60);
        AnimateSocialIcon(SocialFacebook, SocialFacebookTranslate, socialDelay + 120);
        AnimateSocialIcon(SocialDiscord, SocialDiscordTranslate, socialDelay + 180);

        AnimateEntranceItem(NavPanel, NavPanelTranslate, contentDelay + 40);

        AnimateActivePanel(_activeNav);

        AnimateEntranceItem(FooterBar, FooterTranslate, contentDelay + 160);

        void AnimateSocialIcon(UIElement element, TranslateTransform translate, int delayMs)
        {
            var fade = CreateAnimation(0, 1, 350, itemEase);
            fade.BeginTime = TimeSpan.FromMilliseconds(delayMs);
            element.BeginAnimation(OpacityProperty, fade);

            var slide = CreateAnimation(6, 0, 400, itemEase);
            slide.BeginTime = TimeSpan.FromMilliseconds(delayMs);
            translate.BeginAnimation(TranslateTransform.YProperty, slide);
        }

        void AnimateEntranceItem(UIElement element, TranslateTransform translate, int delayMs)
        {
            var fade = CreateAnimation(0, 1, 420, itemEase);
            fade.BeginTime = TimeSpan.FromMilliseconds(delayMs);
            element.BeginAnimation(OpacityProperty, fade);

            var slide = CreateAnimation(12, 0, 520, itemEase);
            slide.BeginTime = TimeSpan.FromMilliseconds(delayMs);
            translate.BeginAnimation(TranslateTransform.YProperty, slide);
        }
    }

    private static DoubleAnimation CreateAnimation(double from, double to, int durationMs, IEasingFunction easing)
    {
        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(durationMs))
        {
            EasingFunction = easing
        };

        Timeline.SetDesiredFrameRate(animation, VNotch.Services.AnimationConfig.TargetFps);
        return animation;
    }

    #endregion

    #region Button Handlers

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            bool confirmed = VNotch.Windows.ConfirmationDialog.Show(
                this,
                Loc.Get("settings.reset.confirm"),
                Loc.Get("settings.reset.title"),
                Loc.Get("dialog.confirm"),
                Loc.Get("dialog.cancel"),
                VNotch.Windows.ConfirmationDialog.DialogIcon.Warning,
                VNotch.Windows.ConfirmationDialog.DialogStyle.Danger);

            if (!confirmed) return;

            var defaults = new NotchSettings();

            WidthSlider.Value = defaults.Width;
            DynamicIslandWidthSlider.Value = defaults.DynamicIslandWidth;
            DynamicIslandHeightSlider.Value = defaults.DynamicIslandHeight;
            HeightSlider.Value = defaults.Height;
            RadiusSlider.Value = defaults.CornerRadius;
            OpacitySlider.Value = defaults.Opacity * 100;
            BlurBrightnessSlider.Value = defaults.MediaBlurBrightnessBoost * 100;
            BlurDarkOverlaySlider.Value = defaults.MediaBlurDarkOverlay * 100;
            SpotifyCanvasBrightnessSlider.Value = defaults.SpotifyCanvasBrightness * 100;
            AnimationFpsSlider.Value = defaults.AnimationFps;
            EnableBlurEffectsCheck.IsChecked = defaults.EnableBlurEffects;
            MediaArtBackgroundCheck.IsChecked = defaults.ShowMediaArtBackground;
            _settings.NotchStyle = defaults.NotchStyle;
            _settings.LiquidGlass = (defaults.LiquidGlass ?? new Models.LiquidGlassConfig()).Clone();
            LoadLiquidGlassUi();
            EnableSubjectBlurCheck.IsChecked = defaults.EnableSubjectBlur;
            EnableSmartCropCheck.IsChecked = defaults.EnableSmartCrop;
            UpdatePerformanceDependentControls(defaults.EnableBlurEffects);
            EnableSpotifyLyricsCheck.IsChecked = defaults.EnableSpotifyLyrics;
            EnableSpotifyCanvasCheck.IsChecked = defaults.EnableSpotifyCanvas;
            EnableYouTubeSubtitlesCheck.IsChecked = defaults.EnableYouTubeSubtitles;
            IgnoreYouTubeAutoSubtitlesCheck.IsChecked = defaults.IgnoreYouTubeAutoSubtitles;
            UpdateLyricsDependentControls(defaults.EnableSpotifyLyrics);
            UpdateSpotifyCanvasDependentControls();
            UpdateYouTubeSubtitlesDependentControls(defaults.EnableYouTubeSubtitles);

            _settings.SubtitlePriority = defaults.SubtitlePriority;
            LoadSubtitlePriority();

            DynamicIslandModeCheck.IsChecked = defaults.EnableDynamicIslandMode;
            UpdateDynamicIslandDependentControls(defaults.EnableDynamicIslandMode);

            HoverExpandCheck.IsChecked = defaults.EnableHoverExpand;
            HoverDelaySlider.Value = defaults.HoverExpandDelay;
            HoverDelaySlider.IsEnabled = defaults.EnableHoverExpand;
            HoverDelaySlider.Opacity = defaults.EnableHoverExpand ? 1.0 : 0.4;
            DisableMouseLeaveAutoCloseCheck.IsChecked = defaults.DisableMouseLeaveAutoClose;
            ReopenLastViewCheck.IsChecked = defaults.ReopenLastViewOnExpand;

            MusicNotifyCheck.IsChecked = defaults.ShowMusicNotifications;
            SystemNotifyCheck.IsChecked = defaults.ShowSystemNotifications;
            StayBehindWindowsCheck.IsChecked = defaults.StayBehindWindows;
            ShelfUnlockCheck.IsChecked = defaults.IsShelfUploadLimitUnlocked;
            CopyShelfClipboardCheck.IsChecked = defaults.CopyShelfFilesToClipboard;
            EnableSpotlightCheck.IsChecked = defaults.EnableSpotlight;
            EnableDebugModeCheck.IsChecked = defaults.EnableDebugMode;
            ShowBatteryCheck.IsChecked = defaults.ShowBatteryIndicator;
            _settings.BatteryDeviceId = defaults.BatteryDeviceId;
            HideOnExclusiveFullscreenCheck.IsChecked = defaults.HideOnExclusiveFullscreen;
            HideOnWindowedFullscreenCheck.IsChecked = defaults.HideOnWindowedFullscreen;
            IdleAutoHideCheck.IsChecked = defaults.EnableIdleAutoHide;
            IdleAutoHideDelaySlider.Value = Math.Max(2, defaults.IdleAutoHideDelay / 1000.0);
            IdleAutoHideDelaySlider.IsEnabled = defaults.EnableIdleAutoHide;
            IdleAutoHideDelaySlider.Opacity = defaults.EnableIdleAutoHide ? 1.0 : 0.4;

            LocalOnlyModeCheck.IsChecked = defaults.EnableLocalOnlyMode;
            AutoCheckUpdatesCheck.IsChecked = defaults.AutoCheckUpdates;
            EnableOnlineArtworkCheck.IsChecked = defaults.EnableOnlineArtworkLookup;
            EnableOnlineLyricsCheck.IsChecked = defaults.EnableOnlineLyrics;
            EnablePrivacyIndicatorsCheck.IsChecked = defaults.EnablePrivacyIndicators;
            EnableBrowserUrlInspectionCheck.IsChecked = defaults.EnableBrowserUrlInspection;
            EnableDiagnosticLoggingCheck.IsChecked = defaults.EnableDiagnosticLogging;
            EnableSpotlightHistoryCheck.IsChecked = defaults.EnableSpotlightHistory;
            UpdateLocalOnlyDependentControls(defaults.EnableLocalOnlyMode);
            int defLangIndex = 0;
            for (int i = 0; i < LanguageCombo.Items.Count; i++)
            {
                if (LanguageCombo.Items[i] is System.Windows.Controls.ComboBoxItem item && item.Tag as string == defaults.Language)
                {
                    defLangIndex = i;
                    break;
                }
            }
            LanguageCombo.SelectedIndex = defLangIndex;
            _settings.ExpandedWidget = defaults.ExpandedWidget;
            _settings.ShelfWidget = defaults.ShelfWidget;
            _settings.ClockPageStyle = defaults.ClockPageStyle;
            _settings.NavTabOrder = defaults.NavTabOrder;
            _settings.VisibleNavTabs = defaults.VisibleNavTabs;
            WidgetCombo.SelectedIndex = defaults.ExpandedWidget switch
            {
                "clock" => 1,
                "wordclock" => 2,
                "digitalclock" => 3,
                "weather" => 4,
                "sysmon" => 5,
                "none" => 6,
                _ => 0
            };
            PopulateShelfWidgetCombo();
            PopulateClockPageStyleCombo();
            PopulateNavTabsSettings();
        }
        catch (Exception ex)
        {
            VNotch.Services.RuntimeLog.Error("SETTINGS", ex, "Error in Reset_Click");
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        RevertLivePreviewIfNeeded();
        CloseWithAnimation();
    }
    private void RevertLivePreviewIfNeeded()
    {
        SettingsChanged?.Invoke(this, _originalSettings.Clone());
    }

    private void SocialLink_Website_Click(object sender, RoutedEventArgs e)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "https://v-notch.vercel.app/",
            UseShellExecute = true
        });
    }

    private void SocialLink_GitHub_Click(object sender, RoutedEventArgs e)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "https://github.com/rainaku/V-Notch",
            UseShellExecute = true
        });
    }

    private void SocialLink_Facebook_Click(object sender, RoutedEventArgs e)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "https://www.facebook.com/rain.107/",
            UseShellExecute = true
        });
    }

    private void SocialLink_Discord_Click(object sender, RoutedEventArgs e)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "https://www.facebook.com/rain.107/",
            UseShellExecute = true
        });
    }

    private void DonatePaypal_Click(object sender, RoutedEventArgs e)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "https://www.paypal.com/paypalme/PhuocLe678",
            UseShellExecute = true
        });
    }

    private void ReportBug_Click(object sender, RoutedEventArgs e)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "https://github.com/rainaku/V-Notch/issues/new",
            UseShellExecute = true
        });
    }

    private void RequestFeature_Click(object sender, RoutedEventArgs e)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "https://github.com/rainaku/V-Notch/issues/new?labels=enhancement&template=feature_request.md",
            UseShellExecute = true
        });
    }

    private void ClearCache_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            bool confirmed = VNotch.Windows.ConfirmationDialog.Show(
                this,
                Loc.Get("settings.clearCache.confirm"),
                Loc.Get("settings.clearCache.title"),
                Loc.Get("dialog.confirm"),
                Loc.Get("dialog.cancel"),
                VNotch.Windows.ConfirmationDialog.DialogIcon.Trash,
                VNotch.Windows.ConfirmationDialog.DialogStyle.Normal,
                Loc.Get("settings.clearCache.detail"));

            if (!confirmed) return;

            int deletedCount = 0;
            var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "V-Notch");
            var baseDir = AppContext.BaseDirectory;

            // In-memory cache resets
            FileIconProvider.ClearCache();

            // Clear cache directories
            var cacheDirs = new[]
            {
                Path.Combine(appData, "cache"),
                Path.Combine(appData, "canvas_cache"),
                Path.Combine(appData, "lyrics_cache"),
                Path.Combine(appData, "thumbnails"),
                Path.Combine(appData, "temp"),
            };

            foreach (var dir in cacheDirs)
            {
                try
                {
                    if (Directory.Exists(dir))
                    {
                        var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
                        foreach (var f in files)
                        {
                            try { File.Delete(f); deletedCount++; } catch { }
                        }
                    }
                }
                catch { }
            }

            var filesToDelete = new[]
            {
                Path.Combine(appData, "source_cache.json"),
                Path.Combine(baseDir, "vnotch-debug.log.old"),
            };

            foreach (var file in filesToDelete)
            {
                try
                {
                    if (File.Exists(file))
                    {
                        File.Delete(file);
                        deletedCount++;
                    }
                }
                catch { }
            }

            try
            {
                if (Directory.Exists(appData))
                {
                    foreach (var corrupt in Directory.GetFiles(appData, "settings.corrupt-*.json"))
                    {
                        try { File.Delete(corrupt); deletedCount++; } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                VNotch.Services.RuntimeLog.Warn("SETTINGS", $"Failed to enumerate corrupt backups: {ex.Message}");
            }

            ClearCacheHint.Text = deletedCount > 0
                ? Loc.Get("settings.clearCache.done", deletedCount)
                : Loc.Get("settings.clearCache.clean");
        }
        catch (Exception ex)
        {
            VNotch.Services.RuntimeLog.Error("SETTINGS", ex, "Error in ClearCache_Click");
        }
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        ApplySettingsFromUi(persist: true);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ApplySettingsFromUi(persist: true);
        CloseWithAnimation();
    }
    private void CloseWithAnimation()
    {
        if (_isClosing) return;
        _isClosing = true;

        AnimatedClosing?.Invoke(this, EventArgs.Empty);

        var easeIn = new ExponentialEase { EasingMode = EasingMode.EaseIn, Exponent = 6 };
        var easeInStrong = new ExponentialEase { EasingMode = EasingMode.EaseIn, Exponent = 7 };
        var itemEase = new ExponentialEase { EasingMode = EasingMode.EaseIn, Exponent = 5 };
        int fps = VNotch.Services.AnimationConfig.TargetFps;

        var totalDur = TimeSpan.FromMilliseconds(650);

        // Window.Top/Left can still be held at the entrance animation's end value
        double currentTop = Top;
        double currentLeft = Left;
        double currentShellOpacity = Math.Clamp(MainShell.Opacity, 0.0, 1.0);
        double currentScaleX = Math.Max(0.02, ShellScale.ScaleX);
        double currentScaleY = Math.Max(0.02, ShellScale.ScaleY);
        double currentTranslateX = ShellTranslate.X;
        double currentTranslateY = ShellTranslate.Y;
        double currentRadius = Math.Max(0.0, ShellCornerRadius);
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero && Win32Interop.GetWindowRect(hwnd, out var winRect))
        {
            var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice;
            var pos = transform?.Transform(new Point(winRect.Left, winRect.Top))
                      ?? new Point(winRect.Left, winRect.Top);
            currentLeft = pos.X;
            currentTop = pos.Y;
        }

        MainShell.BeginAnimation(OpacityProperty, null);
        ShellScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        ShellScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        ShellTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        ShellTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        this.BeginAnimation(TopProperty, null);
        this.BeginAnimation(LeftProperty, null);
        this.BeginAnimation(ShellCornerRadiusProperty, null);

        Top = currentTop;
        Left = currentLeft;

        // Removing a WPF animation exposes its base value. Preserve the
        MainShell.Opacity = currentShellOpacity;
        ShellScale.ScaleX = currentScaleX;
        ShellScale.ScaleY = currentScaleY;
        ShellTranslate.X = currentTranslateX;
        ShellTranslate.Y = currentTranslateY;
        ShellCornerRadius = currentRadius;
        MainShell.RenderTransformOrigin = new Point(0.5, 0.0);

        MainShell.Effect = null;

        // --- Performance Optimizations ---
        MainShell.CacheMode = new BitmapCache { EnableClearType = false, RenderAtScale = 1.0 };
        // 2. Disable pixel snapping and layout rounding during animation to prevent animation jitter
        MainShell.SnapsToDevicePixels = false;
        MainShell.UseLayoutRounding = false;
        // 3. Set scaling mode to LowQuality (bilinear) for faster scaling animation on the GPU
        RenderOptions.SetBitmapScalingMode(MainShell, BitmapScalingMode.LowQuality);

        AnimateExitItem(FooterBar, FooterTranslate, 0);
        AnimateExitItem(NavPanel, NavPanelTranslate, 40);

        UIElement? activeCard = _activeNav switch
        {
            "Appearance" => AppearanceCard,
            "Behavior" => BehaviorCard,
            "Devices" => DisplayCard,
            "System" => SystemCard,
            "Privacy" => PrivacyCard,
            "Spotlight" => SpotlightCard,
            "Advanced" => AdvancedCard,
            "Performance" => PerformanceCard,
            "Donating" => DonatingCard,
            "Updates" => UpdatesCard,
            "Searching" => SearchingCard,
            "Skins" => SkinCard,
            _ => null
        };
        TranslateTransform? activeTranslate = _activeNav switch
        {
            "Appearance" => AppearanceCardTranslate,
            "Behavior" => BehaviorCardTranslate,
            "Devices" => DisplayCardTranslate,
            "System" => SystemCardTranslate,
            "Privacy" => PrivacyCardTranslate,
            "Spotlight" => SpotlightCardTranslate,
            "Advanced" => AdvancedCardTranslate,
            "Performance" => PerformanceCardTranslate,
            "Donating" => DonatingCardTranslate,
            "Updates" => UpdatesCardTranslate,
            "Searching" => SearchingCardTranslate,
            "Skins" => SkinCardTranslate,
            _ => null
        };
        if (activeCard != null && activeTranslate != null)
            AnimateExitItem(activeCard, activeTranslate, 60);
        if (_activeNav == "System" && BackupCard != null && BackupCardTranslate != null)
            AnimateExitItem(BackupCard, BackupCardTranslate, 80);

        AnimateExitItem(SettingsHeader, HeaderTranslate, 100);

        double notchRadius = 8;
        double notchW = 230, notchH = 32;
        double notchLeft = 0, notchTop = 0;
        if (Owner is MainWindow mainWindow)
        {
            var rect = mainWindow.GetNotchScreenRect();
            notchLeft = rect.Left;
            notchTop = rect.Top;
            notchW = rect.Width;
            notchH = rect.Height;
            notchRadius = rect.CornerRadius;
        }

        double shellWidth = ActualWidth > 0 ? ActualWidth - 36 : 824;
        double shellHeight = ActualHeight > 0 ? ActualHeight - 36 : 584;
        double targetScaleX = Math.Max(0.02, notchW / shellWidth);
        double targetScaleY = Math.Max(0.02, notchH / shellHeight);
        double targetRadius = Math.Max(notchRadius, 12);

        var squishX = new DoubleAnimation(currentScaleX, targetScaleX, totalDur)
        {
            EasingFunction = easeInStrong
        };
        Timeline.SetDesiredFrameRate(squishX, fps);

        var shrinkY = new DoubleAnimation(currentScaleY, targetScaleY, totalDur)
        {
            EasingFunction = easeInStrong
        };
        Timeline.SetDesiredFrameRate(shrinkY, fps);

        _shellCornerRadius = currentRadius;
        var cornerAnim = new DoubleAnimation(currentRadius, targetRadius, totalDur)
        {
            EasingFunction = easeIn
        };
        Timeline.SetDesiredFrameRate(cornerAnim, fps);

        double targetLeft = notchLeft + notchW / 2.0 - ActualWidth / 2.0;
        double targetTop = notchTop;

        var flyUpWindow = new DoubleAnimation(Top, targetTop, totalDur)
        {
            EasingFunction = easeInStrong
        };
        Timeline.SetDesiredFrameRate(flyUpWindow, Math.Min(60, fps));

        var flyLeftWindow = new DoubleAnimation(Left, targetLeft, totalDur)
        {
            EasingFunction = easeInStrong
        };
        Timeline.SetDesiredFrameRate(flyLeftWindow, Math.Min(60, fps));

        squishX.Completed += (s, e) =>
        {
            // Blank and hide the layered window before destroying it so
            Opacity = 0;
            Hide();
            Close();
        };

        ShellScale.BeginAnimation(ScaleTransform.ScaleXProperty, squishX);
        ShellScale.BeginAnimation(ScaleTransform.ScaleYProperty, shrinkY);
        this.BeginAnimation(ShellCornerRadiusProperty, cornerAnim);
        this.BeginAnimation(TopProperty, flyUpWindow);
        this.BeginAnimation(LeftProperty, flyLeftWindow);

        void AnimateExitItem(UIElement element, TranslateTransform translate, int delayMs)
        {
            // Enable bitmap caching on child elements to animate their fade & slide on GPU
            element.CacheMode = new BitmapCache { EnableClearType = false, RenderAtScale = 1.0 };

            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(550))
            {
                EasingFunction = easeInStrong,
                BeginTime = TimeSpan.FromMilliseconds(delayMs)
            };
            Timeline.SetDesiredFrameRate(fade, fps);
            element.BeginAnimation(OpacityProperty, fade);

            var slide = new DoubleAnimation(0, 12, TimeSpan.FromMilliseconds(550))
            {
                EasingFunction = easeInStrong,
                BeginTime = TimeSpan.FromMilliseconds(delayMs)
            };
            Timeline.SetDesiredFrameRate(slide, fps);
            translate.BeginAnimation(TranslateTransform.YProperty, slide);
        }
    }

    private bool _isClosing = false;
    private double _shellCornerRadius = 24;
    public static readonly DependencyProperty ShellCornerRadiusProperty =
            DependencyProperty.Register("ShellCornerRadius", typeof(double), typeof(SettingsWindow),
                new PropertyMetadata(24.0, OnShellCornerRadiusChanged));

    public double ShellCornerRadius
    {
        get => (double)GetValue(ShellCornerRadiusProperty);
        set => SetValue(ShellCornerRadiusProperty, value);
    }

    private static void OnShellCornerRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SettingsWindow window)
        {
            double r = (double)e.NewValue;
            window.MainShell.CornerRadius = new CornerRadius(r);
            window.FooterBar.CornerRadius = new CornerRadius(0, 0, r, r);
        }
    }

    private void ApplySettingsFromUi(bool persist = true)
    {
        _settings.Width = (int)WidthSlider.Value;
        _settings.DynamicIslandWidth = Math.Max(100, (int)DynamicIslandWidthSlider.Value);
        _settings.DynamicIslandHeight = Math.Max(24, (int)DynamicIslandHeightSlider.Value);
        _settings.Height = (int)HeightSlider.Value;
        _settings.CornerRadius = (int)RadiusSlider.Value;
        _settings.Opacity = OpacitySlider.Value / 100.0;
        _settings.MediaBlurBrightnessBoost = BlurBrightnessSlider.Value / 100.0;
        _settings.MediaBlurDarkOverlay = BlurDarkOverlaySlider.Value / 100.0;
        _settings.SpotifyCanvasBrightness = SpotifyCanvasBrightnessSlider.Value / 100.0;
        _settings.AnimationFps = (int)Math.Round(AnimationFpsSlider.Value);
        VNotch.Services.AnimationConfig.Configure(_settings.AnimationFps);
        AnimationPrimitives.ApplyFpsToTree(this);
        _settings.EnableBlurEffects = EnableBlurEffectsCheck.IsChecked ?? true;
        _settings.ShowMediaArtBackground = MediaArtBackgroundCheck.IsChecked ?? true;
        SaveLiquidGlassUi();
        _settings.EnableSubjectBlur = EnableSubjectBlurCheck.IsChecked ?? true;
        _settings.EnableSmartCrop = EnableSmartCropCheck.IsChecked ?? true;
        _settings.EnableSpotifyLyrics = EnableSpotifyLyricsCheck.IsChecked ?? true;
        _settings.EnableSpotifyCanvas = EnableSpotifyCanvasCheck.IsChecked ?? true;
        _settings.EnableYouTubeSubtitles = EnableYouTubeSubtitlesCheck.IsChecked ?? true;
        _settings.IgnoreYouTubeAutoSubtitles = IgnoreYouTubeAutoSubtitlesCheck.IsChecked ?? false;

        _settings.SubtitlePriority = GetSubtitlePriorityString();

        _settings.EnableDynamicIslandMode = DynamicIslandModeCheck.IsChecked ?? false;

        _settings.EnableHoverExpand = HoverExpandCheck.IsChecked ?? true;
        _settings.HoverExpandDelay = (int)HoverDelaySlider.Value;
        _settings.DisableMouseLeaveAutoClose = DisableMouseLeaveAutoCloseCheck.IsChecked ?? false;
        _settings.ReopenLastViewOnExpand = ReopenLastViewCheck.IsChecked ?? false;

        _settings.MonitorIndex = MonitorCombo.SelectedIndex;
        if (CameraCombo.SelectedItem is CameraDeviceItem selectedCamera)
            _settings.CameraDeviceId = selectedCamera.Id;
        if (VisualizerAudioCombo.SelectedItem is AudioDeviceItem selectedAudioDevice)
            _settings.VisualizerAudioDeviceId = selectedAudioDevice.Id;
        _settings.AutoStart = AutoStartCheck.IsChecked ?? false;
        _settings.StayBehindWindows = StayBehindWindowsCheck.IsChecked ?? false;
        _settings.EnableHelloGreeting = HelloGreetingCheck.IsChecked ?? true;
        _settings.EnableSpotlight = EnableSpotlightCheck.IsChecked ?? true;
        _settings.EnableDebugMode = EnableDebugModeCheck.IsChecked ?? false;
        _settings.HideOnExclusiveFullscreen = HideOnExclusiveFullscreenCheck.IsChecked ?? true;
        _settings.HideOnWindowedFullscreen = HideOnWindowedFullscreenCheck.IsChecked ?? true;
        _settings.EnableIdleAutoHide = IdleAutoHideCheck.IsChecked ?? false;
        _settings.IdleAutoHideDelay = Math.Max(1000, (int)(IdleAutoHideDelaySlider.Value * 1000));
        _settings.ShowMusicNotifications = MusicNotifyCheck.IsChecked ?? true;
        _settings.ShowSystemNotifications = SystemNotifyCheck.IsChecked ?? true;
        _settings.IsShelfUploadLimitUnlocked = ShelfUnlockCheck.IsChecked ?? false;
        _settings.CopyShelfFilesToClipboard = CopyShelfClipboardCheck.IsChecked ?? false;
        _settings.ShowBatteryIndicator = ShowBatteryCheck.IsChecked ?? true;

        _settings.EnableWeather = EnableWeatherCheck.IsChecked ?? false;
        _settings.ManualCity = ManualCityTextBox.Text?.Trim() ?? string.Empty;

        _settings.EnableLocalOnlyMode = LocalOnlyModeCheck.IsChecked ?? false;
        _settings.AutoCheckUpdates = AutoCheckUpdatesCheck.IsChecked ?? true;
        _settings.EnableOnlineArtworkLookup = EnableOnlineArtworkCheck.IsChecked ?? true;
        _settings.EnableOnlineLyrics = EnableOnlineLyricsCheck.IsChecked ?? true;
        _settings.EnablePrivacyIndicators = EnablePrivacyIndicatorsCheck.IsChecked ?? true;
        _settings.EnableBrowserUrlInspection = EnableBrowserUrlInspectionCheck.IsChecked ?? true;
        _settings.EnableDiagnosticLogging = EnableDiagnosticLoggingCheck.IsChecked ?? true;
        _settings.EnableSpotlightHistory = EnableSpotlightHistoryCheck.IsChecked ?? true;

        _settings.EnableYouTubeApi = YouTubeApiCheck.IsChecked ?? false;
        _settings.YouTubeApiKey = YouTubeApiKeyPasswordBox.Password?.Trim() ?? "";

        if (LanguageCombo.SelectedItem is System.Windows.Controls.ComboBoxItem langItem && langItem.Tag is string langCode)
            _settings.Language = langCode;

        if (WidgetCombo.SelectedItem is System.Windows.Controls.ComboBoxItem widgetItem && widgetItem.Tag is string widgetCode)
            _settings.ExpandedWidget = widgetCode;

        if (ShelfWidgetCombo?.SelectedItem is System.Windows.Controls.ComboBoxItem shelfItem && shelfItem.Tag is string shelfCode)
            _settings.ShelfWidget = shelfCode;

        if (ClockPageStyleCombo?.SelectedItem is System.Windows.Controls.ComboBoxItem clockItem && clockItem.Tag is string clockCode)
            _settings.ClockPageStyle = clockCode;
        if (persist)
        {
            _settingsService.Save(_settings);
            StartupManager.SetAutoStart(_settings.AutoStart);
            _originalSettings = _settings.Clone();
        }

        ApplyLiquidGlassSkin();
        SettingsChanged?.Invoke(this, _settings);
    }

    #endregion

    #region Update Handlers

    private async Task CheckForUpdatesAsync()
    {
        try
        {
            UpdateStatusText.Text = Loc.Get("settings.checkingUpdates");
            CheckUpdateButton.IsEnabled = false;
            DownloadUpdateButton.Visibility = Visibility.Collapsed;

            _availableUpdate = await _updateService.CheckForUpdatesAsync();

            if (_availableUpdate == null)
            {
                UpdateStatusText.Text = Loc.Get("settings.checkUpdate");
                CheckUpdateButton.IsEnabled = true;
                return;
            }

            if (_availableUpdate.IsNewerVersion)
            {
                UpdateStatusText.Text = Loc.Get("settings.updateAvailable", _availableUpdate.Version);
                DownloadUpdateButton.Visibility = Visibility.Visible;
            }
            else
            {
                UpdateStatusText.Text = Loc.Get("settings.upToDate");
            }

            CheckUpdateButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = Loc.Get("error.updateStatus", ex.Message);
            CheckUpdateButton.IsEnabled = true;
        }
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await CheckForUpdatesAsync();
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("SETTINGS", ex, "CheckUpdate failed");
        }
    }

    private async void DownloadUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_availableUpdate == null) return;

        UpdateStatusText.Text = Loc.Get("update.preparing");
        DownloadUpdateButton.IsEnabled = false;
        CheckUpdateButton.IsEnabled = false;

        var updateProgressWindow = new UpdateDownloadWindow();
        updateProgressWindow.SetIndeterminate(Loc.Get("update.preparing"));
        updateProgressWindow.Show();

        var downloadProgress = new Progress<double>(p =>
        {
            if (p < 0)
            {
                updateProgressWindow.SetIndeterminate(Loc.Get("update.downloading"));
                UpdateStatusText.Text = Loc.Get("update.downloading");
                return;
            }

            updateProgressWindow.SetStatus(Loc.Get("update.downloadingPercent", (int)p));
            updateProgressWindow.SetProgress(p);
            UpdateStatusText.Text = Loc.Get("update.downloadingPercent", (int)p);
        });

        try
        {
            var success = await _updateService.DownloadAndInstallUpdateAsync(_availableUpdate, downloadProgress);

            if (!success)
            {
                updateProgressWindow.Close();
                UpdateStatusText.Text = Loc.Get("settings.updateAvailable", _availableUpdate.Version);
                DownloadUpdateButton.IsEnabled = true;
                CheckUpdateButton.IsEnabled = true;
                MessageBox.Show(
                    Loc.Get("error.updateFailed"),
                    Loc.Get("error.updateFailedTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            updateProgressWindow.Close();
            UpdateStatusText.Text = Loc.Get("error.updateStatus", ex.Message);
            DownloadUpdateButton.IsEnabled = true;
            CheckUpdateButton.IsEnabled = true;
        }
    }

    private void ViewChangelog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var changelogWindow = new VNotch.Windows.ChangelogWindow(_updateService)
            {
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            changelogWindow.ShowDialog();
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("SETTINGS", ex, "Failed to open changelog window");
            MessageBox.Show(
                Loc.Get("settings.changelogOpenFailed", ex.Message),
                Loc.Get("error.title"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    #endregion

    #region Navigation

    private string _activeNav = "Appearance";
    private readonly Dictionary<string, StackPanel> _navPanels = new();
    private readonly Dictionary<string, Border> _navButtons = new();

    private void InitializeNavigation()
    {
        _navPanels["Searching"] = PanelSearching;
        _navPanels["Appearance"] = PanelAppearance;
        _navPanels["Skins"] = PanelSkins;
        _navPanels["Behavior"] = PanelBehavior;
        _navPanels["Devices"] = PanelDevices;
        _navPanels["System"] = PanelSystem;
        _navPanels["Privacy"] = PanelPrivacy;
        _navPanels["Spotlight"] = PanelSpotlight;
        _navPanels["Advanced"] = PanelAdvanced;
        _navPanels["Performance"] = PanelPerformance;
        _navPanels["Donating"] = PanelDonating;
        _navPanels["Updates"] = PanelUpdates;

        _navButtons["Searching"] = NavSearching;
        _navButtons["Appearance"] = NavAppearance;
        _navButtons["Skins"] = NavSkins;
        _navButtons["Behavior"] = NavBehavior;
        _navButtons["Devices"] = NavDevices;
        _navButtons["System"] = NavSystem;
        _navButtons["Privacy"] = NavPrivacy;
        _navButtons["Spotlight"] = NavSpotlight;
        _navButtons["Advanced"] = NavAdvanced;
        _navButtons["Performance"] = NavPerformance;
        _navButtons["Donating"] = NavDonating;
        _navButtons["Updates"] = NavUpdates;
    }

    private void Nav_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border && border.Tag is string section)
        {
            if (_isSearchMode && section != "Searching")
            {
                return;
            }

            NavigateToSection(section);
        }
    }

    private static readonly string[] _navOrder =
    {
        "Searching", "Appearance", "Skins", "Behavior", "Devices",
        "System", "Privacy", "Spotlight", "Advanced", "Performance", "Donating", "Updates"
    };

    private int _navTransitionVersion;

    private void UpdateNavButtonVisual(Border btn, bool isActive)
    {
        btn.Background = isActive ? (SolidColorBrush)FindResource("NavItemActiveBg") : _transparentBrush;
        var stack = btn.Child as StackPanel;
        if (stack == null || stack.Children.Count < 2) return;

        var targetBrush = isActive ? _whiteBrush : _navInactiveBrush;

        if (stack.Children[0] is Viewbox vb)
        {
            if (vb.Child is System.Windows.Shapes.Path path)
            {
                path.Fill = targetBrush;
            }
            else if (vb.Child is Canvas canvas)
            {
                foreach (var child in canvas.Children)
                {
                    if (child is System.Windows.Shapes.Path cp) cp.Fill = targetBrush;
                }
            }
        }
        else if (stack.Children[0] is TextBlock iconText)
        {
            iconText.Foreground = targetBrush;
        }

        if (stack.Children[1] is TextBlock text)
        {
            text.Foreground = targetBrush;
        }
    }

    private void NavigateToSection(string section)
    {
        if (section == _activeNav) return;

        string previous = _activeNav;

        if (_navButtons.TryGetValue(previous, out var oldBtn))
        {
            UpdateNavButtonVisual(oldBtn, false);
        }

        _activeNav = section;

        if (_navButtons.TryGetValue(section, out var newBtn))
        {
            UpdateNavButtonVisual(newBtn, true);
        }

        int version = ++_navTransitionVersion;
        int direction = Math.Sign(Array.IndexOf(_navOrder, section) - Array.IndexOf(_navOrder, previous));
        if (direction == 0) direction = 1;

        var (oldCard, oldTranslate) = GetSectionCardParts(previous);
        _navPanels.TryGetValue(previous, out var oldPanel);

        void RevealIncoming()
        {
            if (version != _navTransitionVersion ||
                !string.Equals(_activeNav, section, StringComparison.Ordinal))
                return;

            foreach (var kvp in _navPanels)
                if (kvp.Key != section) kvp.Value.Visibility = Visibility.Collapsed;
            if (_navPanels.TryGetValue(section, out var newPanel))
                newPanel.Visibility = Visibility.Visible;

            SettingsScrollViewer.ScrollToTop();
            AnimateActivePanel(section, direction);
        }

        if (oldCard == null || oldTranslate == null || VNotch.Services.AnimationConfig.ReduceMotion)
        {
            RevealIncoming();
            return;
        }

        int fps = VNotch.Services.AnimationConfig.TargetFps;
        var exitEase = new CubicEase { EasingMode = EasingMode.EaseIn };
        var exitDur = TimeSpan.FromMilliseconds(130);

        var fadeOut = new DoubleAnimation(oldCard.Opacity, 0, exitDur) { EasingFunction = exitEase };
        var slideOut = new DoubleAnimation(oldTranslate.Y, -14 * direction, exitDur) { EasingFunction = exitEase };
        Timeline.SetDesiredFrameRate(fadeOut, fps);
        Timeline.SetDesiredFrameRate(slideOut, fps);

        fadeOut.Completed += (_, _) =>
        {
            oldCard.BeginAnimation(OpacityProperty, null);
            oldCard.Opacity = 0;
            oldTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            oldTranslate.Y = 12;
            if (oldPanel != null && !string.Equals(previous, _activeNav, StringComparison.Ordinal))
                oldPanel.Visibility = Visibility.Collapsed;
            RevealIncoming();
        };

        oldCard.BeginAnimation(OpacityProperty, fadeOut);
        oldTranslate.BeginAnimation(TranslateTransform.YProperty, slideOut);
    }

    private (FrameworkElement? Card, TranslateTransform? Translate) GetSectionCardParts(string section)
    {
        FrameworkElement? card = section switch
        {
            "Appearance" => AppearanceCard,
            "Searching" => SearchingCard,
            "Behavior" => BehaviorCard,
            "Devices" => DisplayCard,
            "System" => SystemCard,
            "Privacy" => PrivacyCard,
            "Spotlight" => SpotlightCard,
            "Advanced" => AdvancedCard,
            "Performance" => PerformanceCard,
            "Donating" => DonatingCard,
            "Updates" => UpdatesCard,
            "Skins" => SkinCard,
            _ => null
        };

        TranslateTransform? translate = section switch
        {
            "Appearance" => AppearanceCardTranslate,
            "Searching" => SearchingCardTranslate,
            "Behavior" => BehaviorCardTranslate,
            "Devices" => DisplayCardTranslate,
            "System" => SystemCardTranslate,
            "Privacy" => PrivacyCardTranslate,
            "Spotlight" => SpotlightCardTranslate,
            "Advanced" => AdvancedCardTranslate,
            "Performance" => PerformanceCardTranslate,
            "Donating" => DonatingCardTranslate,
            "Updates" => UpdatesCardTranslate,
            "Skins" => SkinCardTranslate,
            _ => null
        };

        return (card, translate);
    }

    private static ScaleTransform EnsureCardScale(FrameworkElement card, TranslateTransform translate)
    {
        if (card.RenderTransform is TransformGroup existing &&
            existing.Children.Count > 0 && existing.Children[0] is ScaleTransform s)
            return s;

        var scale = new ScaleTransform(1, 1);
        var group = new TransformGroup();
        group.Children.Add(scale);
        group.Children.Add(translate);
        card.RenderTransform = group;
        card.RenderTransformOrigin = new Point(0.5, 0.5);
        return scale;
    }

    private void AnimateActivePanel(string section, int direction = 1)
    {
        var (card, translate) = GetSectionCardParts(section);
        if (card == null || translate == null) return;

        if (VNotch.Services.AnimationConfig.ReduceMotion)
        {
            card.BeginAnimation(OpacityProperty, null);
            card.Opacity = 1;
            translate.BeginAnimation(TranslateTransform.YProperty, null);
            translate.Y = 0;
            return;
        }

        int fps = VNotch.Services.AnimationConfig.TargetFps;
        var ease = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 6 };
        var scale = EnsureCardScale(card, translate);

        double fromY = 18 * direction;
        card.Opacity = 0;
        translate.Y = fromY;
        scale.ScaleX = 0.985;
        scale.ScaleY = 0.985;

        var systemCardDelay = section == "System" && BackupCard != null ? TimeSpan.FromMilliseconds(40) : TimeSpan.Zero;
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300)) { EasingFunction = ease, BeginTime = systemCardDelay };
        var slide = new DoubleAnimation(fromY, 0, TimeSpan.FromMilliseconds(420)) { EasingFunction = ease, BeginTime = systemCardDelay };
        var grow = new DoubleAnimation(0.985, 1, TimeSpan.FromMilliseconds(420)) { EasingFunction = ease, BeginTime = systemCardDelay };
        Timeline.SetDesiredFrameRate(fade, fps);
        Timeline.SetDesiredFrameRate(slide, fps);
        Timeline.SetDesiredFrameRate(grow, fps);

        card.BeginAnimation(OpacityProperty, fade);
        translate.BeginAnimation(TranslateTransform.YProperty, slide);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);

        if (section == "System" && BackupCard != null && BackupCardTranslate != null)
        {
            if (VNotch.Services.AnimationConfig.ReduceMotion)
            {
                BackupCard.BeginAnimation(OpacityProperty, null);
                BackupCard.Opacity = 1;
                BackupCardTranslate.BeginAnimation(TranslateTransform.YProperty, null);
                BackupCardTranslate.Y = 0;
            }
            else
            {
                var backupScale = EnsureCardScale(BackupCard, BackupCardTranslate);
                BackupCard.Opacity = 0;
                BackupCardTranslate.Y = fromY;
                backupScale.ScaleX = 0.985;
                backupScale.ScaleY = 0.985;

                var backupFade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300)) { EasingFunction = ease };
                var backupSlide = new DoubleAnimation(fromY, 0, TimeSpan.FromMilliseconds(420)) { EasingFunction = ease };
                var backupGrow = new DoubleAnimation(0.985, 1, TimeSpan.FromMilliseconds(420)) { EasingFunction = ease };

                Timeline.SetDesiredFrameRate(backupFade, fps);
                Timeline.SetDesiredFrameRate(backupSlide, fps);
                Timeline.SetDesiredFrameRate(backupGrow, fps);

                BackupCard.BeginAnimation(OpacityProperty, backupFade);
                BackupCardTranslate.BeginAnimation(TranslateTransform.YProperty, backupSlide);
                backupScale.BeginAnimation(ScaleTransform.ScaleXProperty, backupGrow);
                backupScale.BeginAnimation(ScaleTransform.ScaleYProperty, backupGrow);
            }
        }
    }

    #endregion

    #region Search

    private DispatcherTimer? _searchDebounce;
    private bool _isSearchMode;
    private readonly List<SearchRowEntry> _searchRows = new();

    private void SettingsSearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        string query = SettingsSearchBox.Text?.Trim() ?? "";

        UpdateSearchPlaceholderVisibility();

        if (string.IsNullOrEmpty(query))
        {
            _searchDebounce?.Stop();
            ExitSearchMode();
            return;
        }

        if (_searchDebounce == null)
        {
            _searchDebounce = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(150)
            };
            _searchDebounce.Tick += (s, _) =>
            {
                _searchDebounce.Stop();
                ExecuteSearch(SettingsSearchBox.Text?.Trim() ?? "");
            };
        }
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    private void SettingsSearchBox_GotFocus(object sender, RoutedEventArgs e)
    {
        UpdateSearchPlaceholderVisibility();
    }

    private void SettingsSearchBox_LostFocus(object sender, RoutedEventArgs e)
    {
        UpdateSearchPlaceholderVisibility();
    }

    private void UpdateSearchPlaceholderVisibility()
    {
        string query = SettingsSearchBox.Text?.Trim() ?? "";
        if (SettingsSearchBox.IsFocused || !string.IsNullOrEmpty(query))
        {
            SearchPlaceholder.Visibility = Visibility.Collapsed;
        }
        else
        {
            SearchPlaceholder.Visibility = Visibility.Visible;
        }
    }

    private void ExecuteSearch(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return;

        string normalizedQuery = SettingsSearchMatcher.Normalize(query);
        EnterSearchMode();
        SearchResultsStack.Children.Clear();

        var matches = new List<SearchRowEntry>();
        foreach (var row in _searchRows)
        {
            if (SettingsSearchMatcher.IsNormalizedMatch(row.NormalizedSearchText, normalizedQuery))
            {
                matches.Add(row);
            }
        }

        foreach (var match in matches)
        {
            match.OriginalVisibility = match.Row.Visibility;

            if (match.Row.Parent is StackPanel currentParent)
            {
                currentParent.Children.Remove(match.Row);
            }

            match.Row.Visibility = Visibility.Visible;
            SearchResultsStack.Children.Add(match.Row);
        }

        SearchingEmptyText.Visibility = matches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SettingsScrollViewer.ScrollToTop();
        AnimateActivePanel("Searching");
    }

    private void EnterSearchMode()
    {
        if (!_isSearchMode)
        {
            _isSearchMode = true;
            IndexSearchRows();
            RefreshSearchRows();
        }
        else
        {
            RestoreSearchRows();
        }

        _activeNav = "Searching";

        foreach (var kvp in _navPanels)
        {
            kvp.Value.Visibility = kvp.Key == "Searching" ? Visibility.Visible : Visibility.Collapsed;
        }

        foreach (var kvp in _navButtons)
        {
            bool isSearching = kvp.Key == "Searching";
            kvp.Value.Visibility = Visibility.Visible;
            kvp.Value.IsHitTestVisible = isSearching;
            kvp.Value.Opacity = isSearching ? 1.0 : 0.35;
            kvp.Value.Background = isSearching
                ? (SolidColorBrush)FindResource("NavItemActiveBg")
                : _transparentBrush;

            var stack = kvp.Value.Child as StackPanel;
            if (stack?.Children.Count > 1 && stack.Children[1] is TextBlock txt)
                txt.Foreground = isSearching ? _whiteBrush : _navInactiveBrush;
        }
    }

    private void ExitSearchMode()
    {
        RestoreSearchRows();
        SearchResultsStack.Children.Clear();
        SearchingEmptyText.Visibility = Visibility.Collapsed;
        _isSearchMode = false;

        if (_activeNav == "Searching")
        {
            _activeNav = "Appearance";
        }

        ShowAllNavItems();
        if (_navButtons.TryGetValue("Searching", out var searchButton))
        {
            searchButton.Visibility = Visibility.Collapsed;
        }

        foreach (var kvp in _navPanels)
        {
            kvp.Value.Visibility = kvp.Key == _activeNav ? Visibility.Visible : Visibility.Collapsed;
        }

        AnimateActivePanel(_activeNav);
    }

    private void IndexSearchRows()
    {
        if (_searchRows.Count > 0) return;

        var rowStyle = FindResource("SettingRowBorder") as Style;
        foreach (var kvp in _navPanels)
        {
            if (kvp.Key == "Searching") continue;

            foreach (var row in FindVisualChildren<Border>(kvp.Value))
            {
                if (row.Style != rowStyle || row.Parent is not StackPanel parent)
                {
                    continue;
                }

                _searchRows.Add(new SearchRowEntry(
                    kvp.Key,
                    row,
                    parent,
                    parent.Children.IndexOf(row),
                    row.Visibility,
                    BuildSearchText(row, kvp.Key)));
            }
        }
    }

    private void RestoreSearchRows()
    {
        foreach (var row in _searchRows.OrderBy(r => r.OriginalIndex))
        {
            if (row.Row.Parent is StackPanel currentParent)
            {
                currentParent.Children.Remove(row.Row);
            }

            int insertIndex = Math.Clamp(row.OriginalIndex, 0, row.OriginalParent.Children.Count);
            row.OriginalParent.Children.Insert(insertIndex, row.Row);
            row.Row.Visibility = row.OriginalVisibility;
        }
    }

    private string BuildSearchText(DependencyObject root, string section)
    {
        var parts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { section };
        if (_navButtons.TryGetValue(section, out var navButton))
        {
            CollectSearchText(navButton, parts);
        }

        CollectSearchText(root, parts);
        return string.Join(" ", parts);
    }

    private void AddAllTranslations(string text, ISet<string> parts)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        foreach (string translation in Loc.GetAllTranslations(text))
        {
            parts.Add(translation);
        }
    }

    private void RefreshSearchRows()
    {
        foreach (var row in _searchRows)
        {
            row.OriginalVisibility = row.Row.Visibility;
            row.UpdateSearchText(BuildSearchText(row.Row, row.Section));
        }
    }

    private void CollectSearchText(DependencyObject current, ISet<string> parts)
    {
        switch (current)
        {
            case TextBlock textBlock when !string.IsNullOrWhiteSpace(textBlock.Text):
                AddAllTranslations(textBlock.Text, parts);
                break;
            case CheckBox checkBox when checkBox.Content is string checkText:
                AddAllTranslations(checkText, parts);
                break;
            case ContentControl contentControl when contentControl.Content is string contentText:
                AddAllTranslations(contentText, parts);
                break;
            case ElasticSlider slider:
                AddAllTranslations(slider.Label, parts);
                AddAllTranslations(slider.Description, parts);
                AddAllTranslations(slider.Unit, parts);
                break;
            case ItemsControl itemsControl:
                foreach (object item in itemsControl.Items)
                {
                    AddSearchItemText(item, parts);
                }

                if (ReferenceEquals(itemsControl, LanguageCombo))
                {
                    AddLanguageSearchTerms(parts);
                }

                break;
        }

        int childCount = VisualTreeHelper.GetChildrenCount(current);
        for (int i = 0; i < childCount; i++)
        {
            CollectSearchText(VisualTreeHelper.GetChild(current, i), parts);
        }
    }

    private void AddSearchItemText(object? item, ISet<string> parts)
    {
        switch (item)
        {
            case null:
                return;
            case string text:
                AddAllTranslations(text, parts);
                return;
            case ContentControl contentControl:
                AddSearchItemText(contentControl.Content, parts);
                if (contentControl.Tag is string tag && !string.IsNullOrWhiteSpace(tag))
                {
                    parts.Add(tag);
                }

                return;
            case SubtitlePriorityItem subtitleItem:
                AddAllTranslations(subtitleItem.DisplayName, parts);
                parts.Add(subtitleItem.Key);
                return;
            case CameraDeviceItem camera:
                parts.Add(camera.Name);
                return;
            case AudioDeviceItem audioDevice:
                parts.Add(audioDevice.Name);
                return;
        }
    }

    private void AddLanguageSearchTerms(ISet<string> parts)
    {
        foreach (var (code, nativeName) in Loc.GetAvailableLanguages())
        {
            parts.Add(code);
            parts.Add(nativeName);

            string cultureName = code switch
            {
                "vi" => "vi-VN",
                "es" => "es-ES",
                "fr" => "fr-FR",
                "de" => "de-DE",
                "ja" => "ja-JP",
                "hi" => "hi-IN",
                _ => "en-US"
            };
            var culture = System.Globalization.CultureInfo.GetCultureInfo(cultureName);
            parts.Add(culture.EnglishName);
            parts.Add(culture.NativeName);
        }
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        int childCount = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < childCount; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typedChild)
            {
                yield return typedChild;
            }

            foreach (var descendant in FindVisualChildren<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private sealed class SearchRowEntry
    {
        public SearchRowEntry(
            string section,
            Border row,
            StackPanel originalParent,
            int originalIndex,
            Visibility originalVisibility,
            string searchText)
        {
            Section = section;
            Row = row;
            OriginalParent = originalParent;
            OriginalIndex = originalIndex;
            OriginalVisibility = originalVisibility;
            UpdateSearchText(searchText);
        }

        public string Section { get; }
        public Border Row { get; }
        public StackPanel OriginalParent { get; }
        public int OriginalIndex { get; }
        public Visibility OriginalVisibility { get; set; }
        public string NormalizedSearchText { get; private set; } = string.Empty;

        public void UpdateSearchText(string searchText)
        {
            NormalizedSearchText = SettingsSearchMatcher.Normalize(searchText);
        }
    }

    private static readonly SolidColorBrush _whiteBrush = new(Colors.White);
    private static readonly SolidColorBrush _navInactiveBrush = new(Color.FromRgb(0xAA, 0xAA, 0xAA));
    private static readonly SolidColorBrush _transparentBrush = new(Colors.Transparent);

    static SettingsWindow()
    {
        _whiteBrush.Freeze();
        _navInactiveBrush.Freeze();
        _transparentBrush.Freeze();
    }

    private void ShowAllNavItems()
    {
        foreach (var kvp in _navButtons)
        {
            kvp.Value.IsHitTestVisible = true;
            kvp.Value.Opacity = 1.0;
            bool isActive = kvp.Key == _activeNav;
            kvp.Value.Background = isActive
                ? (SolidColorBrush)FindResource("NavItemActiveBg")
                : _transparentBrush;
            var stack = kvp.Value.Child as StackPanel;
            if (stack?.Children.Count > 1 && stack.Children[1] is TextBlock txt)
                txt.Foreground = isActive ? _whiteBrush : _navInactiveBrush;
        }
    }

    #endregion

    #region Smooth Scroll

    private double _scrollVelocity;
    private double _scrollTarget;
    private bool _isScrollAnimating;
    private const double ScrollFriction = 0.82;
    private const double ScrollSensitivity = 1.2;
    private const double ScrollMinVelocity = 0.3;

    private bool IsAnyComboBoxDropDownOpen()
    {
        if (WidgetCombo?.IsDropDownOpen == true ||
            MonitorCombo?.IsDropDownOpen == true ||
            LanguageCombo?.IsDropDownOpen == true ||
            CameraCombo?.IsDropDownOpen == true ||
            VisualizerAudioCombo?.IsDropDownOpen == true ||
            SkinCombo?.IsDropDownOpen == true ||
            GlassPresetCombo?.IsDropDownOpen == true ||
            ProcessPriorityCombo?.IsDropDownOpen == true ||
            GpuPreferenceCombo?.IsDropDownOpen == true)
        {
            return true;
        }

        return FindVisualChildren<ComboBox>(this).Any(c => c.IsDropDownOpen);
    }

    private void SettingsScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (IsAnyComboBoxDropDownOpen())
        {
            // A ComboBox popup is logically connected to this window, so this
            var dropdownScrollViewer = FindVisualAncestor<ScrollViewer>(e.OriginalSource as DependencyObject);
            if (dropdownScrollViewer != null && !ReferenceEquals(dropdownScrollViewer, SettingsScrollViewer))
            {
                double target = CalculateDropDownWheelTarget(
                    dropdownScrollViewer.VerticalOffset,
                    dropdownScrollViewer.ScrollableHeight,
                    e.Delta);
                dropdownScrollViewer.ScrollToVerticalOffset(target);
            }

            // Never scroll the settings page behind an open dropdown.
            e.Handled = true;
            return;
        }

        e.Handled = true;

        double delta = -e.Delta * ScrollSensitivity;
        double maxScroll = SettingsScrollViewer.ScrollableHeight;

        if (!_isScrollAnimating)
        {
            _scrollTarget = SettingsScrollViewer.VerticalOffset;
        }

        _scrollVelocity += delta * 0.3;
        _scrollTarget = Math.Clamp(_scrollTarget + delta, 0, maxScroll);

        if (!_isScrollAnimating)
        {
            _isScrollAnimating = true;
            CompositionTarget.Rendering += SmoothScroll_Tick;
        }
    }

    internal static double CalculateDropDownWheelTarget(double currentOffset, double scrollableHeight, int wheelDelta)
    {
        const double wheelScale = 0.35;
        return Math.Clamp(currentOffset - wheelDelta * wheelScale, 0, Math.Max(0, scrollableHeight));
    }

    private static T? FindVisualAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        var current = source;
        while (current != null)
        {
            if (current is T match)
                return match;

            current = current is Visual
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return null;
    }

    private void SmoothScroll_Tick(object? sender, EventArgs e)
    {
        double current = SettingsScrollViewer.VerticalOffset;
        double diff = _scrollTarget - current;

        _scrollVelocity *= ScrollFriction;

        double step = diff * 0.18 + _scrollVelocity * 0.4;
        double newOffset = Math.Clamp(current + step, 0, SettingsScrollViewer.ScrollableHeight);
        SettingsScrollViewer.ScrollToVerticalOffset(newOffset);

        if (Math.Abs(diff) < ScrollMinVelocity && Math.Abs(_scrollVelocity) < ScrollMinVelocity)
        {
            SettingsScrollViewer.ScrollToVerticalOffset(_scrollTarget);
            _scrollVelocity = 0;
            _isScrollAnimating = false;
            CompositionTarget.Rendering -= SmoothScroll_Tick;
        }
    }

    #endregion

    #region Camera Device

    private async Task LoadCameraDevices()
    {
        try
        {
            var groups = await global::Windows.Media.Capture.Frames.MediaFrameSourceGroup.FindAllAsync();
            var cameras = groups
                .Where(g => g.SourceInfos.Any(s => s.SourceKind == global::Windows.Media.Capture.Frames.MediaFrameSourceKind.Color))
                .Select(g => new CameraDeviceItem { Id = g.Id, Name = g.DisplayName })
                .ToList();

            if (cameras.Count == 0)
            {
                cameras.Add(new CameraDeviceItem { Id = "", Name = Loc.Get("settings.camera.none") });
            }

            CameraCombo.ItemsSource = cameras;
            CameraCombo.DisplayMemberPath = "Name";

            var selectedIdx = cameras.FindIndex(c => c.Id == _settings.CameraDeviceId);
            CameraCombo.SelectedIndex = selectedIdx >= 0 ? selectedIdx : 0;
        }
        catch
        {
            CameraCombo.ItemsSource = new[] { new CameraDeviceItem { Id = "", Name = Loc.Get("settings.camera.none") } };
            CameraCombo.DisplayMemberPath = "Name";
            CameraCombo.SelectedIndex = 0;
        }
    }

    private void CameraCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CameraCombo.SelectedItem is CameraDeviceItem item)
        {
            _settings.CameraDeviceId = item.Id;
        }
    }

    #endregion

    #region Visualizer Audio Device

    private void SetVisualizerAudioDevicePlaceholder()
    {
        VisualizerAudioCombo.ItemsSource = new[]
        {
            new AudioDeviceItem { Id = _settings.VisualizerAudioDeviceId, Name = Loc.Get("settings.visualizerAudio.default") }
        };
        VisualizerAudioCombo.DisplayMemberPath = "Name";
        VisualizerAudioCombo.SelectedIndex = 0;
    }

    private async Task LoadVisualizerAudioDevices()
    {
        List<(string Id, string Name)> rawDevices;
        try
        {
            rawDevices = await System.Threading.Tasks.Task.Run(() =>
            {
                var found = new List<(string, string)>();
                using var enumerator = new MMDeviceEnumerator();
                foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                {
                    found.Add((device.ID, device.FriendlyName));
                    device.Dispose();
                }
                return found;
            });
        }
        catch
        {
            rawDevices = new List<(string, string)>();
        }

        var devices = new List<AudioDeviceItem>
        {
            new() { Id = "", Name = Loc.Get("settings.visualizerAudio.default") }
        };

        foreach (var (id, name) in rawDevices)
        {
            devices.Add(new AudioDeviceItem { Id = id, Name = name });
        }

        if (!string.IsNullOrWhiteSpace(_settings.VisualizerAudioDeviceId) &&
            devices.All(d => d.Id != _settings.VisualizerAudioDeviceId))
        {
            devices.Add(new AudioDeviceItem
            {
                Id = _settings.VisualizerAudioDeviceId,
                Name = Loc.Get("settings.visualizerAudio.unavailable", _settings.VisualizerAudioDeviceId)
            });
        }

        VisualizerAudioCombo.ItemsSource = devices;
        VisualizerAudioCombo.DisplayMemberPath = "Name";

        var selectedIdx = devices.FindIndex(d => d.Id == _settings.VisualizerAudioDeviceId);
        VisualizerAudioCombo.SelectedIndex = selectedIdx >= 0 ? selectedIdx : 0;
    }

    private void VisualizerAudioCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (VisualizerAudioCombo.SelectedItem is AudioDeviceItem item)
        {
            _settings.VisualizerAudioDeviceId = item.Id;
        }
    }

    #endregion

    #region Liquid Glass UI Support

    private void ApplyLiquidGlassSkin()
    {
        if (GlassBackdropHost == null) return;

        // Liquid Glass is a notch skin only. Keeping a second full-window
        MainShell.Background = (Brush)FindResource("WindowGlow");
        GlassBackdropHost.Background = null;
        GlassBackdropHost.Visibility = Visibility.Collapsed;
        GlassTintOverlay.Visibility = Visibility.Collapsed;
        GlassDarkOverlay.Visibility = Visibility.Collapsed;
        if (GlassGrainOverlay != null) GlassGrainOverlay.Visibility = Visibility.Collapsed;

        CompositionTarget.Rendering -= OnGlassRegionRendering;
        _liquidGlass?.ClearLiveRegion();
        _liquidGlass?.SetAnimating(false);
        _liquidGlass?.Stop();
        DetachGpuRefraction();
    }

    private static readonly SolidColorBrush _glassBaseFill = CreateFrozenBrush(0x0B, 0x0E, 0x12);

    private static SolidColorBrush CreateFrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    /// <summary>Physical-pixel envelope the glass surface must cover: the full
    /// window at the current DPI (the shell always fits inside it, including
    /// during the open/close scale animation, whose scale never exceeds 1).</summary>
    private (int Width, int Height) GetGlassSurfaceEnvelope()
    {
        double wDip = ActualWidth > 0 ? ActualWidth : Width;
        double hDip = ActualHeight > 0 ? ActualHeight : Height;
        if (!double.IsFinite(wDip) || wDip <= 0) wDip = 860;
        if (!double.IsFinite(hDip) || hDip <= 0) hDip = 620;

        double dpiScale = GetGlassDpiScale();
        return ((int)Math.Ceiling(wDip * dpiScale), (int)Math.Ceiling(hDip * dpiScale));
    }

    private bool _glassRebuildQueued;

    /// <summary>PerMonitorV2: moving to a higher-DPI monitor can outgrow the
    /// fixed presentation surface. Tear the renderer down and rebuild it with a
    /// fresh envelope instead of presenting a truncated backdrop.</summary>
    private void QueueGlassRendererRebuildIfTooSmall()
    {
        var lg = _liquidGlass;
        if (lg == null || _glassRebuildQueued) return;

        var (needW, needH) = GetGlassSurfaceEnvelope();
        if (needW <= lg.MaxRegionWidth && needH <= lg.MaxRegionHeight) return;

        _glassRebuildQueued = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _glassRebuildQueued = false;
            if (_liquidGlass == null) return;
            CompositionTarget.Rendering -= OnGlassRegionRendering;
            _liquidGlass.ClearLiveRegion();
            _liquidGlass.Stop();
            DetachGpuRefraction();
            _liquidGlass = null;
            ApplyLiquidGlassSkin();
        }));
    }

    private void OnGlassRegionRendering(object? sender, EventArgs e)
    {
        _liquidGlass?.SetLiveRegion(GetGlassCaptureRegion());
    }

    private void ApplyLiquidGlassConfig()
    {
        if (GlassBackdropHost == null) return;
        var cfg = _settings.LiquidGlass ?? new Models.LiquidGlassConfig();

        double dipRadius = Math.Clamp(cfg.BlurAmount, 0, 1) * 28.0;
        double dpiScale = GetGlassDpiScale();
        int gaussianSigma = (int)Math.Round(dipRadius * dpiScale);

        if (_liquidGlass != null)
        {
            _liquidGlass.SetBlur(gaussianSigma);
            int targetFps = cfg.TargetFps;
            if (targetFps <= 0 || targetFps == 60) targetFps = AnimationConfig.TargetFps;
            _liquidGlass.UpdateFps(targetFps);
            bool useGpu = (_settings.LiquidGlass?.UseGpuRefraction ?? true) && LiquidGlassRefractionEffect.IsAvailable;
            if (useGpu)
            {
                GlassBackdropImage.HorizontalAlignment = HorizontalAlignment.Left;
                GlassBackdropImage.VerticalAlignment = VerticalAlignment.Top;
                GlassBackdropImage.Width = _liquidGlass.SurfaceWidth / dpiScale;
                GlassBackdropImage.Height = _liquidGlass.SurfaceHeight / dpiScale;
            }
        }

        // GPU mode blurs on the host element instead of the CPU box blur.
        ApplyGpuBlur(cfg.BlurAmount);

        GlassBackdropHost.Opacity = Math.Clamp(cfg.Opacity, 0, 1);

        if (GlassGrainOverlay != null)
        {
            double grainOpacity = Math.Clamp(cfg.Noise * 1.5, 0.0, 1.0);
            GlassGrainOverlay.Opacity = grainOpacity;
            GlassGrainOverlay.Visibility = grainOpacity > 0.005 ? Visibility.Visible : Visibility.Collapsed;
            GlassGrainOverlay.Background = GlassGrainBrush.Instance;
        }

        _liquidGlass?.SetParams(new LiquidGlassController.GlassParams
        {
            PowerFactor = cfg.PowerFactor,
            RefractionA = cfg.RefractionA,
            RefractionB = cfg.RefractionB,
            RefractionC = cfg.RefractionC,
            RefractionD = cfg.RefractionD,
            FPower = cfg.FPower,
            Noise = cfg.Noise,
            GlowWeight = cfg.GlowWeight,
            GlowBias = cfg.GlowBias,
            GlowEdge0 = cfg.GlowEdge0,
            GlowEdge1 = cfg.GlowEdge1,
            Refraction = cfg.Refraction,
            EdgeBend = cfg.EdgeBend,
            ChromaticAberration = cfg.ChromaticAberration,
            Distortion = cfg.Distortion,
            ZRadius = cfg.ZRadius,
            Saturation = cfg.Saturation,
            Brightness = cfg.Brightness,
            BevelMode = cfg.BevelMode,
            TopCornerRadius = MainShell.CornerRadius.TopLeft,
            BottomCornerRadius = MainShell.CornerRadius.BottomLeft
        });
    }

    private LiquidGlassController.CaptureRegion? GetGlassCaptureRegion()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || MainShell == null || GlassBackdropHost == null) return null;

        double shellW = GlassBackdropHost.ActualWidth;
        double shellH = GlassBackdropHost.ActualHeight;
        if (shellW <= 0 || shellH <= 0) return null;

        double dpiScale = GetGlassDpiScale();
        if (Math.Abs(dpiScale - _lastAppliedDpiScale) > 0.01)
        {
            _lastAppliedDpiScale = dpiScale;
            bool useGpu = (_settings.LiquidGlass?.UseGpuRefraction ?? true) && LiquidGlassRefractionEffect.IsAvailable;
            if (_liquidGlass != null && GlassBackdropHost.Visibility == Visibility.Visible && useGpu)
            {
                GlassBackdropImage.HorizontalAlignment = HorizontalAlignment.Left;
                GlassBackdropImage.VerticalAlignment = VerticalAlignment.Top;
                GlassBackdropImage.Width = _liquidGlass.SurfaceWidth / dpiScale;
                GlassBackdropImage.Height = _liquidGlass.SurfaceHeight / dpiScale;
            }
            QueueGlassRendererRebuildIfTooSmall();
        }

        int physW = (int)Math.Round(shellW * dpiScale);
        int physH = (int)Math.Round(shellH * dpiScale);

        try
        {
            // Project both corners so the open/close ShellScale animation is baked
            var tl = GlassBackdropHost.PointToScreen(new Point(0, 0));
            var br = GlassBackdropHost.PointToScreen(new Point(shellW, shellH));

            int physLeft = (int)Math.Round(tl.X, MidpointRounding.AwayFromZero);
            int physTop = (int)Math.Round(tl.Y, MidpointRounding.AwayFromZero);
            int scaledW = (int)Math.Round(br.X, MidpointRounding.AwayFromZero) - physLeft;
            int scaledH = (int)Math.Round(br.Y, MidpointRounding.AwayFromZero) - physTop;
            if (scaledW > 1) physW = scaledW;
            if (scaledH > 1) physH = scaledH;

            // Carry the fractional screen position so the present can compensate
            double subX = tl.X - Math.Round(tl.X);
            double subY = tl.Y - Math.Round(tl.Y);

            if (physTop < 0) { physH += physTop; physTop = 0; }
            if (physLeft < 0) { physW += physLeft; physLeft = 0; }
            if (physW <= 1 || physH <= 1) return null;

            return new LiquidGlassController.CaptureRegion(
                physLeft, physTop, physW, physH,
                MainShell.CornerRadius.TopLeft,
                MainShell.CornerRadius.BottomLeft,
                subX, subY);
        }
        catch
        {
            return null;
        }
    }

    private void ConfigureGpuRefraction()
    {
        if (_liquidGlass == null) return;

        bool useGpu = (_settings.LiquidGlass?.UseGpuRefraction ?? true) && LiquidGlassRefractionEffect.IsAvailable;
        if (!useGpu)
        {
            if (_gpuRefractionConfigured || GlassBackdropImage.Effect != null)
            {
                DetachGpuRefraction();
                _liquidGlass.SetGpuMode(false, null);
            }
            return;
        }

        if (_gpuRefractionConfigured && ReferenceEquals(GlassBackdropImage.Effect, _glassRefractionEffect))
            return;

        try
        {
            _glassRefractionEffect ??= new LiquidGlassRefractionEffect();
            GlassBackdropImage.Effect = _glassRefractionEffect;
            if (!_liquidGlass.SetGpuMode(true, ApplyGpuGeometry, OnGpuRefractionFailure))
            {
                DetachGpuRefraction();
                _liquidGlass.SetGpuMode(false, null);
                return;
            }
            _gpuRefractionConfigured = true;
        }
        catch
        {
            DetachGpuRefraction();
            _liquidGlass.SetGpuMode(false, null);
        }
    }

    private System.Windows.Media.Effects.BlurEffect? _glassHostBlur;

    /// <summary>Applies the GPU-mode host blur. CPU Liquid Glass blurs the
    /// captured source before refraction instead.</summary>
    private void ApplyGpuBlur(double blurAmount)
    {
        bool useGpu = (_settings.LiquidGlass?.UseGpuRefraction ?? true) && LiquidGlassRefractionEffect.IsAvailable;
        if (!useGpu || GlassBackdropHost == null) return;

        double radius = Math.Clamp(blurAmount, 0, 1) * 14.0;
        if (radius < 0.5)
        {
            GlassBackdropHost.Effect = null;
            _glassHostBlur = null;
            return;
        }

        if (_glassHostBlur == null)
        {
            _glassHostBlur = new System.Windows.Media.Effects.BlurEffect
            {
                KernelType = System.Windows.Media.Effects.KernelType.Gaussian,
                RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance
            };
            GlassBackdropHost.Effect = _glassHostBlur;
        }
        _glassHostBlur.Radius = radius;
    }

    private void DetachGpuRefraction()
    {
        _gpuRefractionConfigured = false;
        if (GlassBackdropImage != null && ReferenceEquals(GlassBackdropImage.Effect, _glassRefractionEffect))
        {
            GlassBackdropImage.Effect = null;
            // Restore CPU-present layout defaults (GPU mode set explicit size).
            GlassBackdropImage.Width = double.NaN;
            GlassBackdropImage.Height = double.NaN;
        }
        if (GlassBackdropHost != null)
            GlassBackdropHost.Effect = null;
        _glassHostBlur = null;
    }

    private LiquidGlassController.GpuGeometry? _lastAppliedSettingsOptics;

    /// <summary>Pushes the per-frame shader geometry from the controller into the
    /// effect. The shader samples the presenter's fixed D3D surface, so SrcW/SrcH
    /// must be the surface dimensions, not the per-frame capture size.</summary>
    private void ApplyGpuGeometry(LiquidGlassController.GpuGeometry g)
    {
        var fx = _glassRefractionEffect;
        var lg = _liquidGlass;
        if (fx == null || lg == null) return;

        if (Math.Abs(fx.SrcW - lg.SurfaceWidth) > 0.1) fx.SrcW = lg.SurfaceWidth;
        if (Math.Abs(fx.SrcH - lg.SurfaceHeight) > 0.1) fx.SrcH = lg.SurfaceHeight;
        if (Math.Abs(fx.NotchW - g.NotchW) > 0.1) fx.NotchW = g.NotchW;
        if (Math.Abs(fx.NotchH - g.NotchH) > 0.1) fx.NotchH = g.NotchH;
        if (Math.Abs(fx.OffX - g.OffX) > 0.1) fx.OffX = g.OffX;
        if (Math.Abs(fx.OffY - g.OffY) > 0.1) fx.OffY = g.OffY;
        if (Math.Abs(fx.TopCornerR - g.TopCornerR) > 0.1) fx.TopCornerR = g.TopCornerR;
        if (Math.Abs(fx.BottomCornerR - g.BottomCornerR) > 0.1) fx.BottomCornerR = g.BottomCornerR;

        if (_lastAppliedSettingsOptics == null || !_lastAppliedSettingsOptics.Value.Equals(g))
        {
            _lastAppliedSettingsOptics = g;
            fx.PowerFactor = g.PowerFactor;
            fx.A = g.A;
            fx.B = g.B;
            fx.C = g.C;
            fx.D = g.D;
            fx.FPower = g.FPower;
            fx.Noise = g.Noise;
            fx.GlowWeight = g.GlowWeight;
            fx.GlowBias = g.GlowBias;
            fx.GlowEdge0 = g.GlowEdge0;
            fx.GlowEdge1 = g.GlowEdge1;
            fx.Chroma = g.Chroma;
            fx.EdgeBend = g.EdgeBend;
            fx.BevelMode = g.BevelMode;
            fx.SatFactor = g.SatFactor;
            fx.BrightAdd = g.BrightAdd;
        }
    }

    private void OnGpuRefractionFailure(Exception ex)
    {
        DetachGpuRefraction();
        _liquidGlass?.SetGpuMode(false, null);
    }

    private double GetGlassDpiScale()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return 1.0;
        uint dpi = Win32Interop.GetDpiForWindow(hwnd);
        return dpi / 96.0;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // WS_EX_TOOLWINDOW keeps third-party dock/animation tools (e.g. MyDockFinder)
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            int exStyle = Win32Interop.GetWindowLong(hwnd, Win32Interop.GWL_EXSTYLE);
            Win32Interop.SetWindowLong(hwnd, Win32Interop.GWL_EXSTYLE, exStyle | Win32Interop.WS_EX_TOOLWINDOW);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        CompositionTarget.Rendering -= OnGlassRegionRendering;
        _liquidGlass?.Stop();
        DetachGpuRefraction();
        _liquidGlass = null;
        _glassRefractionEffect = null;
        MemoryOptimizerService.Instance.ScheduleTrim(200, aggressive: true);
    }

    #endregion
}

public class CameraDeviceItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    public override string ToString() => Name;
}

public class AudioDeviceItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    public override string ToString() => Name;
}

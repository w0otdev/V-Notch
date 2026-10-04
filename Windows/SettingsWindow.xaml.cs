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

public partial class SettingsWindow : Window
{
    private static readonly SolidColorBrush ConfirmationGreenBrush = CreateSettingsBrush(Color.FromRgb(0x50, 0xC8, 0x78));
    private static readonly SolidColorBrush CardBackgroundBrush = CreateSettingsBrush(Color.FromArgb(12, 255, 255, 255));
    private static readonly SolidColorBrush CardBorderBrush = CreateSettingsBrush(Color.FromArgb(18, 255, 255, 255));
    private static readonly SolidColorBrush DragHandleBackgroundBrush = CreateSettingsBrush(Color.FromArgb(16, 255, 255, 255));
    private static readonly SolidColorBrush DragHandleGlyphBrush = CreateSettingsBrush(Color.FromArgb(160, 255, 255, 255));
    private static readonly SolidColorBrush DragHandleHoverBrush = CreateSettingsBrush(Color.FromArgb(40, 255, 255, 255));
    private static readonly SolidColorBrush DraggingBackgroundBrush = CreateSettingsBrush(Color.FromArgb(50, 255, 255, 255));
    private static readonly SolidColorBrush DraggingBorderBrush = CreateSettingsBrush(Color.FromArgb(80, 255, 255, 255));
    private static readonly SolidColorBrush BackupSuccessBrush = CreateSettingsBrush(Color.FromRgb(48, 209, 88));
    private static readonly SolidColorBrush StatusMutedBrush = CreateSettingsBrush(Color.FromRgb(107, 114, 128));
    private static readonly SolidColorBrush StatusErrorBrush = CreateSettingsBrush(Color.FromRgb(239, 68, 68));
    private static readonly SolidColorBrush StatusSuccessBrush = CreateSettingsBrush(Color.FromRgb(74, 222, 128));
    private static readonly SolidColorBrush StatusWarningBrush = CreateSettingsBrush(Color.FromRgb(234, 179, 8));

    private static SolidColorBrush CreateSettingsBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private const string LogCategory = "SETTINGS";
    private const string DefaultNavTabs = "Media,Secondary,Timer,AudioMixer";
    private const string SubtitlePriorityDataFormat = "SubtitlePriorityItem";
    private const string NavTabMedia = "Media";
    private const string WidgetClock = "clock";
    private const string WidgetWordClock = "wordclock";
    private const string WidgetWeather = "weather";
    private const string WidgetSysMon = "sysmon";
    private const string SkinLiquidGlass = "liquidglass";
    private const string GlassPresetCustom = "custom";
    private const string GlassPresetFrosted = "frosted";
    private const string GlassPresetRegular = "regular";
    private const string GlassPresetClear = "clear";

    // Navigation sections
    private const string NavSectionAppearance = "Appearance";
    private const string NavSectionSearching = "Searching";
    private const string NavSectionSkins = "Skins";
    private const string NavSectionBehavior = "Behavior";
    private const string NavSectionDevices = "Devices";
    private const string NavSectionSystem = "System";
    private const string NavSectionPrivacy = "Privacy";
    private const string NavSectionSpotlight = "Spotlight";
    private const string NavSectionAdvanced = "Advanced";
    private const string NavSectionPerformance = "Performance";
    private const string NavSectionDonating = "Donating";
    private const string NavSectionUpdates = "Updates";

    // Localization key constants
    private const string LocKeySearching = "settings.searching";
    private const string LocKeyWidth = "settings.width";
    private const string LocKeyDynamicIslandWidth = "settings.dynamicIslandWidth";
    private const string LocKeyDynamicIslandHeight = "settings.dynamicIslandHeight";
    private const string LocKeyHeight = "settings.height";
    private const string LocKeyCornerRadius = "settings.cornerRadius";
    private const string LocKeyOpacity = "settings.opacity";
    private const string LocKeyBlurBrightness = "settings.blurBrightness";
    private const string LocKeyLyricsDarkOverlay = "settings.lyricsDarkOverlay";
    private const string LocKeyBadgeAlpha = "settings.badge.alpha";
    private const string LocKeyExpandDelay = "settings.expandDelay";
    private const string LocKeyAnimationFps = "settings.animationFps";

    private NotchSettings _settings;
    private NotchSettings _originalSettings;
    private NotchSettings _appliedSettings;
    private readonly ISettingsApplicationService _settingsAppService;
    private readonly SettingsUpdatePresenter _updatePresenter;
    private bool _isLoadingSettings = true;
    private DispatcherTimer? _livePreviewDebounce;
    private bool _isSpotlightHotkeyRegistered;
    private int _lastAppliedFps;

    // Liquid Glass UI components
    private LiquidGlassController? _liquidGlass;
    private double _lastAppliedDpiScale = 1.0;

    public event EventHandler<NotchSettings>? SettingsChanged;
    public event EventHandler? AnimatedClosing;

    public SettingsWindow(
        NotchSettings settings,
        ISettingsApplicationService settingsAppService,
        BluetoothModule? bluetoothModule = null,
        bool isSpotlightHotkeyRegistered = true)
    {
        InitializeComponent();
        AnimationPrimitives.ApplyFpsToTree(this);

        _ = bluetoothModule;
        _settings = settings.Clone();
        _originalSettings = settings.Clone();
        _appliedSettings = settings.Clone();
        _settingsAppService = settingsAppService ?? throw new ArgumentNullException(nameof(settingsAppService));
        _isSpotlightHotkeyRegistered = isSpotlightHotkeyRegistered;
        _lastAppliedFps = settings.AnimationFps;
        _updatePresenter = new SettingsUpdatePresenter(new UpdateService(),
            new SettingsUpdateViewRefs(this, UpdateStatusText, CheckUpdateButton, DownloadUpdateButton));

        InitializeNavigation();
        LoadSettings();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnMonitorConfigurationChanged;
        _appliedSettings = ReadSettingsFromUi();
        _settings = _appliedSettings.Clone();
        if (_settings.AutoCheckUpdates)
            _updatePresenter.CheckForUpdatesAsync().SafeFireAndForget("SETTINGS-UPDATE-CHECK");
    }

    public SettingsWindow(
        NotchSettings settings,
        ISettingsService settingsService,
        BluetoothModule? bluetoothModule = null,
        bool isSpotlightHotkeyRegistered = true)
        : this(settings, new SettingsApplicationService(settingsService), bluetoothModule, isSpotlightHotkeyRegistered)
    {
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
        int maxDepth = 64;
        while (source != null && maxDepth-- > 0)
        {
            if (source is ButtonBase or Slider or Thumb or ComboBox or ComboBoxItem or CheckBox or ScrollBar or TextBox or PasswordBox)
            {
                return true;
            }

            if (source is FrameworkElement fe)
            {
                if (fe.Name is "SubtitlePriorityItems" or "NavTabsSettingsContainer" or "NavTabsSettingsCard")
                    return true;

                if (fe.Tag is string tag &&
                    (tag is "NavTabRow" or NavTabMedia or "Secondary" or "Timer" or "AudioMixer" ||
                     Array.IndexOf(_navOrder, tag) >= 0))
                {
                    return true;
                }
            }

            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? SafeGetVisualParent(source)
                : SafeGetLogicalParent(source);
        }

        return false;
    }

    private static DependencyObject? SafeGetVisualParent(DependencyObject source)
    {
        try { return VisualTreeHelper.GetParent(source); }
        catch (Exception) { return null; }
    }

    private static DependencyObject? SafeGetLogicalParent(DependencyObject source)
    {
        try { return LogicalTreeHelper.GetParent(source); }
        catch (Exception) { return null; }
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
        AutoAnimationFpsCheck.IsChecked = _settings.AutoAnimationFps;
        AnimationFpsSlider.IsEnabled = !_settings.AutoAnimationFps;
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
        BrightenDarkLyricsBackgroundCheck.IsChecked = _settings.BrightenDarkLyricsBackground;
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
        KeepMediaPinnedCheck.IsChecked = _settings.KeepMediaPinnedOnTrackChange;

        RefreshMonitorChoices(preserveSelection: false);

        LoadCameraDevices().SafeFireAndForget("SETTINGS-CAMERA-DEVICES");
        SetVisualizerAudioDevicePlaceholder();

        AutoStartCheck.IsChecked = _settingsAppService.IsAutoStartEnabled();
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
        ScreenshotTrayCheck.IsChecked = _settings.EnableScreenshotTray;
        ScreenshotTrayDurationSlider.Value = _settings.ScreenshotTrayDurationSeconds;
        CopyShelfClipboardCheck.IsChecked = _settings.CopyShelfFilesToClipboard;
        EnableSpotlightCheck.IsChecked = _settings.EnableSpotlight;
        LoadSpotlightAiSettings(_settings);
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
        LoadAdditionalPrivacy(_settings);
        UpdateLocalOnlyDependentControls(_settings.EnableLocalOnlyMode);

        ApplyLiquidGlassSkin();
        _isLoadingSettings = false;
        ApplyLocalization();
    }

    private static string GetAppVersion()
    {
        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        return v != null ? FormatVersion(v) : "2.0";
    }

    private static string FormatVersion(Version v)
    {
        if (v.Revision > 0)
            return $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
        if (v.Build > 0)
            return $"{v.Major}.{v.Minor}.{v.Build}";
        return $"{v.Major}.{v.Minor}";
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
        LocalizedPresentation.Apply(this);
        ApplySupplementalLocalization();
        string appVersion = GetAppVersion();
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
        LocalizeSpotlightAiSettings();
        SpotlightHotkeyWarning.Text = Loc.Get("settings.enableSpotlight.conflict");
        SearchingHeader.Text = Loc.Get(LocKeySearching);
        SearchingEmptyText.Text = Loc.Get("settings.search.noResults");
        SearchingEmptyHint.Text = Loc.Get("settings.search.emptyHint");
        SearchingClearButton.Content = Loc.Get("settings.search.clear");

        NavSearchingText.Text = Loc.Get(LocKeySearching);
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
        WidthLabel.Text = Loc.Get(LocKeyWidth);
        WidthSlider.Label = Loc.Get(LocKeyWidth);
        WidthSlider.Description = Loc.Get("settings.width.hint");
        DynamicIslandWidthLabel.Text = Loc.Get(LocKeyDynamicIslandWidth);
        DynamicIslandWidthSlider.Label = Loc.Get(LocKeyDynamicIslandWidth);
        DynamicIslandWidthSlider.Description = Loc.Get("settings.dynamicIslandWidth.hint");
        DynamicIslandHeightLabel.Text = Loc.Get(LocKeyDynamicIslandHeight);
        DynamicIslandHeightSlider.Label = Loc.Get(LocKeyDynamicIslandHeight);
        DynamicIslandHeightSlider.Description = Loc.Get("settings.dynamicIslandHeight.hint");
        HeightLabel.Text = Loc.Get(LocKeyHeight);
        HeightSlider.Label = Loc.Get(LocKeyHeight);
        HeightSlider.Description = Loc.Get("settings.height.hint");
        RadiusLabel.Text = Loc.Get(LocKeyCornerRadius);
        RadiusSlider.Label = Loc.Get(LocKeyCornerRadius);
        RadiusSlider.Description = Loc.Get("settings.cornerRadius.hint");
        OpacityLabel.Text = Loc.Get(LocKeyOpacity);
        OpacitySlider.Label = Loc.Get(LocKeyOpacity);
        OpacitySlider.Description = Loc.Get("settings.opacity.hint");
        BlurLabel.Text = Loc.Get(LocKeyBlurBrightness);
        BlurBrightnessSlider.Label = Loc.Get(LocKeyBlurBrightness);
        BlurBrightnessSlider.Description = Loc.Get("settings.blurBrightness.hint");
        DarkOverlayLabel.Text = Loc.Get(LocKeyLyricsDarkOverlay);
        BlurDarkOverlaySlider.Label = Loc.Get(LocKeyLyricsDarkOverlay);
        BlurDarkOverlaySlider.Description = Loc.Get("settings.lyricsDarkOverlay.hint");
        EnableDebugModeCheck.Content = Loc.Get("settings.enableDebugMode");
        EnableDebugModeHint.Text = Loc.Get("settings.enableDebugMode.hint");
        EnableSpotifyLyricsCheck.Content = Loc.Get("settings.enableSpotifyLyrics");
        BrightenDarkLyricsBackgroundCheck.Content = Loc.Get("settings.brightenDarkLyricsBackground");
        if (EnableSpotifyCanvasLabel != null) EnableSpotifyCanvasLabel.Text = Loc.Get("settings.enableSpotifyCanvas");
        if (SpotifyCanvasAlphaBadge != null) SpotifyCanvasAlphaBadge.Text = Loc.Get(LocKeyBadgeAlpha);
        EnableSpotifyCanvasHint.Text = Loc.Get("settings.enableSpotifyCanvas.hint");
        SpotifyCanvasBrightnessSlider.Label = Loc.Get("settings.spotifyCanvasBrightness");
        SpotifyCanvasBrightnessSlider.Description = Loc.Get("settings.spotifyCanvasBrightness.hint");
        SpotifyCanvasAccountLabel.Text = Loc.Get("settings.spotifyCanvasAccount");
        SpotifyCanvasAccountHint.Text = Loc.Get("settings.spotifyCanvasAccount.hint");
        SpotifyConnectButton.Content = Loc.Get("settings.spotifyCanvas.connect");
        SpotifyDisconnectButton.Content = Loc.Get("settings.spotifyCanvas.disconnect");
        UpdateSpotifyCanvasConnectionStatus();
        EnableYouTubeSubtitlesLabel.Text = Loc.Get("settings.enableYouTubeSubtitles");
        if (YouTubeSubtitlesAlphaBadge != null) YouTubeSubtitlesAlphaBadge.Text = Loc.Get(LocKeyBadgeAlpha);
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
        ExpandDelayLabel.Text = Loc.Get(LocKeyExpandDelay);
        HoverDelaySlider.Label = Loc.Get(LocKeyExpandDelay);
        HoverDelaySlider.Description = Loc.Get("settings.expandDelay.hint");
        DisableMouseLeaveAutoCloseCheck.Content = Loc.Get("settings.disableAutoClose");
        DisableMouseLeaveAutoCloseHint.Text = Loc.Get("settings.disableAutoClose.hint");
        ReopenLastViewCheck.Content = Loc.Get("settings.reopenLastView");
        ReopenLastViewHint.Text = Loc.Get("settings.reopenLastView.hint");
        KeepMediaPinnedCheck.Content = Loc.Get("settings.keepMediaPinned");
        KeepMediaPinnedHint.Text = Loc.Get("settings.keepMediaPinned.hint");
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
        RefreshMonitorChoices();

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
        ScreenshotTrayCheck.Content = Loc.Get("settings.screenshotTray");
        ScreenshotTrayHint.Text = Loc.Get("settings.screenshotTray.hint");
        ScreenshotTrayDurationSlider.Label = Loc.Get("settings.screenshotTrayDuration");
        ScreenshotTrayDurationSlider.Description = Loc.Get("settings.screenshotTrayDuration.hint");
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

        AnimationFpsLabel.Text = Loc.Get(LocKeyAnimationFps);
        AutoAnimationFpsCheck.Content = Loc.Get("settings.animationFps.auto");
        AnimationFpsSlider.Label = Loc.Get(LocKeyAnimationFps);
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
        StarRepoTitle.Text = Loc.Get("settings.donating.star.title");
        StarRepoDescription.Text = Loc.Get("settings.donating.star.description");
        StarRepoButtonText.Text = Loc.Get("settings.donating.star.button");
        OtherProjectsHeader.Text = Loc.Get("settings.donating.otherProjects.title");
        OtherProjectsSubtext.Text = Loc.Get("settings.donating.otherProjects.subtitle");
        ProjectVertexDesc.Text = Loc.Get("settings.donating.otherProjects.vertex.desc");
        ProjectScrollVDesc.Text = Loc.Get("settings.donating.otherProjects.scrollv.desc");
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
        UpdatePrivacyNetworkSection(isLocalOnly, animate);
        UpdateLocalOnlyActiveBadge(isLocalOnly, animate);

        if (AutoCheckUpdatesCheck != null) AutoCheckUpdatesCheck.IsEnabled = !isLocalOnly;
        if (EnableOnlineArtworkCheck != null) EnableOnlineArtworkCheck.IsEnabled = !isLocalOnly;
        if (EnableOnlineLyricsCheck != null) EnableOnlineLyricsCheck.IsEnabled = !isLocalOnly;
        if (CheckUpdateButton != null) CheckUpdateButton.IsEnabled = !isLocalOnly;
        if (DownloadUpdateButton != null) DownloadUpdateButton.IsEnabled = !isLocalOnly;
    }

    private void UpdatePrivacyNetworkSection(bool isLocalOnly, bool animate)
    {
        if (PrivacyNetworkSection == null) return;

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

    private void UpdateLocalOnlyActiveBadge(bool isLocalOnly, bool animate)
    {
        if (LocalOnlyActiveBadge == null) return;

        if (isLocalOnly)
        {
            ShowLocalOnlyBadge(animate);
        }
        else
        {
            HideLocalOnlyBadge(animate);
        }
    }

    private void ShowLocalOnlyBadge(bool animate)
    {
        if (LocalOnlyActiveBadge == null) return;
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

    private void HideLocalOnlyBadge(bool animate)
    {
        if (LocalOnlyActiveBadge == null) return;
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

    private void LocalOnlyModeCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings) return;
        bool isLocalOnly = LocalOnlyModeCheck.IsChecked ?? false;
        _settings.EnableLocalOnlyMode = isLocalOnly;
        UpdateLocalOnlyDependentControls(isLocalOnly, animate: true);
        PushLivePreview();
    }

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

    private void AnimateCollapsibleRow(FrameworkElement? element, bool visible, bool animate = false)
    {
        if (element == null) return;

        if (visible)
        {
            ExpandCollapsibleRow(element, animate);
        }
        else
        {
            CollapseCollapsibleRow(element, animate);
        }
    }

    private void ExpandCollapsibleRow(FrameworkElement element, bool animate)
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

    private void CollapseCollapsibleRow(FrameworkElement element, bool animate)
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
        VNotch.Services.WindowTitleScanner.UpdateInspectionAllowed(_settings.EnableBrowserUrlInspection);
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
            button.Foreground = ConfirmationGreenBrush;

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

        if (EnableSpotifyCanvasLabel != null) EnableSpotifyCanvasLabel.Text = Loc.Get("settings.enableSpotifyCanvas");
        if (SpotifyCanvasAlphaBadge != null) SpotifyCanvasAlphaBadge.Text = Loc.Get(LocKeyBadgeAlpha);
        EnableSpotifyCanvasHint.Text = Loc.Get("settings.enableSpotifyCanvas.hint");
        SpotifyCanvasAccountLabel.Text = Loc.Get("settings.spotifyCanvasAccount");
        SpotifyCanvasAccountHint.Text = Loc.Get("settings.spotifyCanvasAccount.hint");
        SpotifyConnectButton.Content = Loc.Get("settings.spotifyCanvas.connect");
        SpotifyDisconnectButton.Content = Loc.Get("settings.spotifyCanvas.disconnect");
        ScreenshotTrayHint.Text = Loc.Get("settings.screenshotTray.hint");
        ScreenshotTrayDurationSlider.Label = Loc.Get("settings.screenshotTrayDuration");
        ScreenshotTrayDurationSlider.Description = Loc.Get("settings.screenshotTrayDuration.hint");
        CopyShelfClipboardHint.Text = Loc.Get("settings.copyShelfClipboard.hint");
        if (YouTubeSubtitlesAlphaBadge != null)
            YouTubeSubtitlesAlphaBadge.Text = Loc.Get(LocKeyBadgeAlpha);

        ApplyGpuAndProcessLocalization();
        ApplyBackupSectionLocalization();
        ApplyRestartBannerLocalization();

        UpdateSpotifyCanvasConnectionStatus();
        UpdateYouTubeApiKeyStatus();
    }

    private void ApplyGpuAndProcessLocalization()
    {
        if (GpuPreferenceLabel != null) GpuPreferenceLabel.Text = Loc.Get("settings.gpuPreference");
        if (GpuPreferenceHint != null) GpuPreferenceHint.Text = Loc.Get("settings.gpuPreference.hint");
        if (GpuPreferenceRestartNote != null) GpuPreferenceRestartNote.Text = Loc.Get("settings.gpuPreference.restart");
        if (GpuPreferenceRestartBadge != null) GpuPreferenceRestartBadge.Text = Loc.Get("settings.badge.restartRequired");
        if (ProcessPriorityLabel != null) ProcessPriorityLabel.Text = Loc.Get("settings.processPriority");
        if (ProcessPriorityHint != null) ProcessPriorityHint.Text = Loc.Get("settings.processPriority.hint");

        if (ProcessPriorityCombo != null && ProcessPriorityCombo.Items.Count >= 3)
        {
            ((ComboBoxItem)ProcessPriorityCombo.Items[0]).Content = Loc.Get("settings.processPriority.normal");
            ((ComboBoxItem)ProcessPriorityCombo.Items[1]).Content = Loc.Get("settings.processPriority.high");
            ((ComboBoxItem)ProcessPriorityCombo.Items[2]).Content = Loc.Get("settings.processPriority.realtime");
        }

        if (GpuPreferenceCombo != null && GpuPreferenceCombo.Items.Count >= 3)
        {
            ((ComboBoxItem)GpuPreferenceCombo.Items[0]).Content = Loc.Get("settings.gpuPreference.auto");
            ((ComboBoxItem)GpuPreferenceCombo.Items[1]).Content = Loc.Get("settings.gpuPreference.igpu");
            ((ComboBoxItem)GpuPreferenceCombo.Items[2]).Content = Loc.Get("settings.gpuPreference.dgpu");
        }
    }

    private void ApplyBackupSectionLocalization()
    {
        if (BackupHeader != null) BackupHeader.Text = Loc.Get("settings.section.backup");
        if (ExportSettingsLabel != null) ExportSettingsLabel.Text = Loc.Get("settings.exportSettings");
        if (ExportSettingsHint != null) ExportSettingsHint.Text = Loc.Get("settings.exportSettings.hint");
        if (ExportSettingsButton != null) ExportSettingsButton.Content = Loc.Get("settings.exportSettings.btn");
        if (ImportSettingsLabel != null) ImportSettingsLabel.Text = Loc.Get("settings.importSettings");
        if (ImportSettingsHint != null) ImportSettingsHint.Text = Loc.Get("settings.importSettings.hint");
        if (ImportSettingsButton != null) ImportSettingsButton.Content = Loc.Get("settings.importSettings.btn");
    }

    private void ApplyRestartBannerLocalization()
    {
        if (RestartPromptTitle != null) RestartPromptTitle.Text = Loc.Get("settings.restartBanner.title");
        if (RestartPromptMessage != null) RestartPromptMessage.Text = Loc.Get("settings.restartBanner.message");
        if (RestartNowButton != null) RestartNowButton.Content = Loc.Get("settings.restartBanner.restartNow");
        if (RestartLaterButton != null) RestartLaterButton.Content = Loc.Get("settings.restartBanner.later");
    }

    private void CheckUpdate_Click(object sender, RoutedEventArgs e)
        => _updatePresenter.CheckForUpdatesAsync().SafeFireAndForget("SETTINGS-UPDATE-CHECK");

    private void DownloadUpdate_Click(object sender, RoutedEventArgs e)
        => _updatePresenter.DownloadUpdateAsync().SafeFireAndForget("SETTINGS-UPDATE-DOWNLOAD");

    private void ViewChangelog_Click(object sender, RoutedEventArgs e)
        => _updatePresenter.ShowChangelog();

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

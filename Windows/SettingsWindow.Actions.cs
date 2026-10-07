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
            AutoAnimationFpsCheck.IsChecked = defaults.AutoAnimationFps;
            EnableBlurEffectsCheck.IsChecked = defaults.EnableBlurEffects;
            MediaArtBackgroundCheck.IsChecked = defaults.ShowMediaArtBackground;
            _settings.NotchStyle = defaults.NotchStyle;
            _settings.LiquidGlass = (defaults.LiquidGlass ?? new Models.LiquidGlassConfig()).Clone();
            LoadLiquidGlassUi();
            EnableSubjectBlurCheck.IsChecked = defaults.EnableSubjectBlur;
            EnableSmartCropCheck.IsChecked = defaults.EnableSmartCrop;
            UpdatePerformanceDependentControls(defaults.EnableBlurEffects);
            EnableSpotifyLyricsCheck.IsChecked = defaults.EnableSpotifyLyrics;
            BrightenDarkLyricsBackgroundCheck.IsChecked = defaults.BrightenDarkLyricsBackground;
            SpotifyCanvasConsent.Revoke(_settings);
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
            KeepMediaPinnedCheck.IsChecked = defaults.KeepMediaPinnedOnTrackChange;

            MusicNotifyCheck.IsChecked = defaults.ShowMusicNotifications;
            SystemNotifyCheck.IsChecked = defaults.ShowSystemNotifications;
            StayBehindWindowsCheck.IsChecked = defaults.StayBehindWindows;
            EnableSpotlightCheck.IsChecked = defaults.EnableSpotlight;
            LoadSpotlightAiSettings(defaults);
            EnableDebugModeCheck.IsChecked = defaults.EnableDebugMode;
            ShowBatteryCheck.IsChecked = defaults.ShowBatteryIndicator;
            HideCameraCheck.IsChecked = defaults.HideCamera;
            ClipboardHotkeyBox.Text = defaults.ClipboardHotkey;
            LoadTrayAdvanced(defaults);
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
            LoadAdditionalPrivacy(defaults);
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
            PopulateClockPageStyleCombo();
            PopulateNavTabsSettings();
        }
        catch (Exception ex)
        {
            VNotch.Services.RuntimeLog.Error(LogCategory, ex, "Error in Reset_Click");
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

#pragma warning disable S1075 // External community, project and donation URLs
    private void SocialLink_Website_Click(object sender, RoutedEventArgs e) => SafeLauncher.TryOpenUrl("https://v-notch.vercel.app/");
    private void SocialLink_GitHub_Click(object sender, RoutedEventArgs e) => SafeLauncher.TryOpenUrl("https://github.com/rainaku/V-Notch");
    private void SocialLink_Facebook_Click(object sender, RoutedEventArgs e) => SafeLauncher.TryOpenUrl("https://www.facebook.com/rain.107/");
    private void SocialLink_Discord_Click(object sender, RoutedEventArgs e) => SafeLauncher.TryOpenUrl("https://discord.com/users/298304189535092737");
    private void DonatePaypal_Click(object sender, RoutedEventArgs e) => SafeLauncher.TryOpenUrl("https://www.paypal.com/paypalme/PhuocLe678");
    private void StarRepoButton_Click(object sender, RoutedEventArgs e) => SafeLauncher.TryOpenUrl("https://github.com/rainaku/V-Notch");
    private void ProjectVertex_Click(object sender, RoutedEventArgs e) => SafeLauncher.TryOpenUrl("https://github.com/rainaku/Vertex");
    private void ProjectScrollV_Click(object sender, RoutedEventArgs e) => SafeLauncher.TryOpenUrl("https://github.com/rainaku/Scroll-V");
    private void ReportBug_Click(object sender, RoutedEventArgs e) => SafeLauncher.TryOpenUrl("https://github.com/rainaku/V-Notch/issues/new");
    private void RequestFeature_Click(object sender, RoutedEventArgs e) => SafeLauncher.TryOpenUrl("https://github.com/rainaku/V-Notch/issues/new?labels=enhancement&template=feature_request.md");
#pragma warning restore S1075
    private void ClearCache_Click(object sender, RoutedEventArgs e)

    {
        try
        {
            bool confirmed = VNotch.Windows.ConfirmationDialog.Show(
                this,
                Loc.Get("settings.clearCache.confirm"),
                new VNotch.Windows.ConfirmationDialog.DialogOptions(
                    Title: Loc.Get("settings.clearCache.title"),
                    ConfirmText: Loc.Get("dialog.confirm"),
                    CancelText: Loc.Get("dialog.cancel"),
                    Icon: VNotch.Windows.ConfirmationDialog.DialogIcon.Trash,
                    Style: VNotch.Windows.ConfirmationDialog.DialogStyle.Normal,
                    DetailText: Loc.Get("settings.clearCache.detail")));

            if (!confirmed) return;

            // In-memory cache resets
            FileIconProvider.ClearCache();

            var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "V-Notch");
            var baseDir = AppContext.BaseDirectory;

            int deletedCount = DeleteCacheDirectories(appData)
                             + DeleteExplicitCacheFiles(appData, baseDir)
                             + DeleteCorruptSettingsFiles(appData);

            ClearCacheHint.Text = deletedCount > 0
                ? Loc.Get("settings.clearCache.done", deletedCount)
                : Loc.Get("settings.clearCache.clean");
        }
        catch (Exception ex)
        {
            VNotch.Services.RuntimeLog.Error(LogCategory, ex, "Error in ClearCache_Click");
        }
    }

    private static int DeleteCacheDirectories(string appData)
    {
        int count = 0;
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
                if (!Directory.Exists(dir)) continue;

                var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
                foreach (var f in files)
                {
                    try
                    {
                        File.Delete(f);
                        count++;
                    }
                    catch (Exception)
                    {
                        // In-use or locked cache files are safely ignored
                    }
                }
            }
            catch (Exception)
            {
                // Inaccessible directory is safely skipped
            }
        }

        return count;
    }

    private static int DeleteExplicitCacheFiles(string appData, string baseDir)
    {
        int count = 0;
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
                    count++;
                }
            }
            catch (Exception)
            {
                // In-use or locked file is safely ignored
            }
        }

        return count;
    }

    private static int DeleteCorruptSettingsFiles(string appData)
    {
        int count = 0;
        try
        {
            if (Directory.Exists(appData))
            {
                foreach (var corrupt in Directory.GetFiles(appData, "settings.corrupt-*.json"))
                {
                    try
                    {
                        File.Delete(corrupt);
                        count++;
                    }
                    catch (Exception)
                    {
                        // Locked corrupt settings backup is safely ignored
                    }
                }
            }
        }
        catch (Exception ex)
        {
            VNotch.Services.RuntimeLog.Warn(LogCategory, $"Failed to enumerate corrupt backups: {ex.Message}");
        }

        return count;
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

        // Removing a WPF animation exposes its base value. Preserve the active appearance
        MainShell.Opacity = currentShellOpacity;
        ShellScale.ScaleX = currentScaleX;
        ShellScale.ScaleY = currentScaleY;
        ShellTranslate.X = currentTranslateX;
        ShellTranslate.Y = currentTranslateY;
        ShellCornerRadius = currentRadius;
        MainShell.RenderTransformOrigin = new Point(0.5, 0.0);

        MainShell.Effect = null;

        MainShell.SnapsToDevicePixels = false;
        MainShell.UseLayoutRounding = false;
        this.SnapsToDevicePixels = false;
        this.UseLayoutRounding = false;
        ShellContent.SnapsToDevicePixels = false;
        ShellContent.UseLayoutRounding = false;
        RenderOptions.SetBitmapScalingMode(MainShell, BitmapScalingMode.LowQuality);

        UIElement? activeCard = _activeNav switch
        {
            NavSectionAppearance => AppearanceCard,
            NavSectionBehavior => BehaviorCard,
            NavSectionDevices => DisplayCard,
            NavSectionSystem => SystemCard,
            NavSectionPrivacy => PrivacyCard,
            NavSectionSpotlight => SpotlightCard,
            NavSectionFileTray => FileTrayCard,
            NavSectionAdvanced => AdvancedCard,
            NavSectionPerformance => PerformanceCard,
            NavSectionDonating => DonatingCard,
            NavSectionUpdates => UpdatesCard,
            NavSectionSearching => SearchingCard,
            NavSectionSkins => SkinCard,
            _ => null
        };
        TranslateTransform? activeTranslate = _activeNav switch
        {
            NavSectionAppearance => AppearanceCardTranslate,
            NavSectionBehavior => BehaviorCardTranslate,
            NavSectionDevices => DisplayCardTranslate,
            NavSectionSystem => SystemCardTranslate,
            NavSectionPrivacy => PrivacyCardTranslate,
            NavSectionSpotlight => SpotlightCardTranslate,
            NavSectionFileTray => FileTrayCardTranslate,
            NavSectionAdvanced => AdvancedCardTranslate,
            NavSectionPerformance => PerformanceCardTranslate,
            NavSectionDonating => DonatingCardTranslate,
            NavSectionUpdates => UpdatesCardTranslate,
            NavSectionSearching => SearchingCardTranslate,
            NavSectionSkins => SkinCardTranslate,
            _ => null
        };

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
        Timeline.SetDesiredFrameRate(flyUpWindow, fps);

        if (Math.Abs(Left - targetLeft) >= 0.5)
        {
            var flyLeftWindow = new DoubleAnimation(Left, targetLeft, totalDur)
            {
                EasingFunction = easeInStrong
            };
            Timeline.SetDesiredFrameRate(flyLeftWindow, fps);
            this.BeginAnimation(LeftProperty, flyLeftWindow);
        }
        else
        {
            Left = targetLeft;
        }

        squishX.Completed += (s, e) =>
        {
            // Blank and hide the layered window before destroying it
            Opacity = 0;
            Hide();
            Close();
        };

        // Fade-out order: top → bottom (Header → Card → Nav → Footer)
        AnimateExitItem(SettingsHeader, HeaderTranslate, 0);

        if (activeCard != null && activeTranslate != null)
            AnimateExitItem(activeCard, activeTranslate, 60);
        if (_activeNav == NavSectionSystem && BackupCard != null && BackupCardTranslate != null)
            AnimateExitItem(BackupCard, BackupCardTranslate, 80);

        AnimateExitItem(NavPanel, NavPanelTranslate, 120);
        AnimateExitItem(FooterBar, FooterTranslate, 160);

        ShellScale.BeginAnimation(ScaleTransform.ScaleXProperty, squishX);
        ShellScale.BeginAnimation(ScaleTransform.ScaleYProperty, shrinkY);
        this.BeginAnimation(ShellCornerRadiusProperty, cornerAnim);
        this.BeginAnimation(TopProperty, flyUpWindow);

        void AnimateExitItem(UIElement element, TranslateTransform translate, int delayMs)
        {
            double startOpacity = Math.Max(0.0, Math.Min(1.0, element.Opacity));
            var fade = new DoubleAnimation(startOpacity, 0, TimeSpan.FromMilliseconds(380))
            {
                EasingFunction = easeIn,
                BeginTime = TimeSpan.FromMilliseconds(delayMs)
            };
            Timeline.SetDesiredFrameRate(fade, fps);
            element.BeginAnimation(OpacityProperty, fade);

            // Slide upward (-8px) as items disappear — the shell is collapsing up
            // toward the notch, so content should lift in the same direction.
            double startY = translate.Y;
            var slide = new DoubleAnimation(startY, startY - 8, TimeSpan.FromMilliseconds(380))
            {
                EasingFunction = easeIn,
                BeginTime = TimeSpan.FromMilliseconds(delayMs)
            };
            Timeline.SetDesiredFrameRate(slide, fps);
            translate.BeginAnimation(TranslateTransform.YProperty, slide);
        }
    }

    private bool _isClosing = false;
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

    internal NotchSettings ReadSettingsFromUi()
    {
        var snapshot = _settings.Clone();

        snapshot.Width = (int)WidthSlider.Value;
        snapshot.DynamicIslandWidth = Math.Max(100, (int)DynamicIslandWidthSlider.Value);
        snapshot.DynamicIslandHeight = Math.Max(24, (int)DynamicIslandHeightSlider.Value);
        snapshot.Height = (int)HeightSlider.Value;
        snapshot.CornerRadius = (int)RadiusSlider.Value;
        snapshot.Opacity = OpacitySlider.Value / 100.0;
        snapshot.MediaBlurBrightnessBoost = BlurBrightnessSlider.Value / 100.0;
        snapshot.MediaBlurDarkOverlay = BlurDarkOverlaySlider.Value / 100.0;
        snapshot.SpotifyCanvasBrightness = SpotifyCanvasBrightnessSlider.Value / 100.0;
        snapshot.AnimationFps = (int)Math.Round(AnimationFpsSlider.Value);
        snapshot.AutoAnimationFps = AutoAnimationFpsCheck.IsChecked == true;
        snapshot.EnableBlurEffects = EnableBlurEffectsCheck.IsChecked ?? true;
        snapshot.ShowMediaArtBackground = MediaArtBackgroundCheck.IsChecked ?? true;
        ReadLiquidGlassUi(snapshot);
        snapshot.EnableSubjectBlur = EnableSubjectBlurCheck.IsChecked ?? true;
        snapshot.EnableSmartCrop = EnableSmartCropCheck.IsChecked ?? true;
        snapshot.EnableSpotifyLyrics = EnableSpotifyLyricsCheck.IsChecked ?? true;
        snapshot.BrightenDarkLyricsBackground = BrightenDarkLyricsBackgroundCheck.IsChecked == true;
        snapshot.EnableSpotifyCanvas = EnableSpotifyCanvasCheck.IsChecked == true && SpotifyCanvasConsent.HasAccepted(snapshot);
        snapshot.EnableYouTubeSubtitles = EnableYouTubeSubtitlesCheck.IsChecked ?? true;
        snapshot.IgnoreYouTubeAutoSubtitles = IgnoreYouTubeAutoSubtitlesCheck.IsChecked ?? false;

        snapshot.SubtitlePriority = GetSubtitlePriorityString();

        snapshot.EnableDynamicIslandMode = DynamicIslandModeCheck.IsChecked ?? false;

        snapshot.EnableHoverExpand = HoverExpandCheck.IsChecked ?? true;
        snapshot.HoverExpandDelay = (int)HoverDelaySlider.Value;
        snapshot.DisableMouseLeaveAutoClose = DisableMouseLeaveAutoCloseCheck.IsChecked ?? false;
        snapshot.KeepMediaPinnedOnTrackChange = KeepMediaPinnedCheck.IsChecked ?? true;
        snapshot.ReopenLastViewOnExpand = ReopenLastViewCheck.IsChecked ?? false;

        if (MonitorCombo.SelectedItem is MonitorSelection.Choice monitor)
        {
            snapshot.MonitorIndex = monitor.Index;
            snapshot.MonitorDeviceId = monitor.Id;
        }
        if (CameraCombo.SelectedItem is CameraDeviceItem selectedCamera)
            snapshot.CameraDeviceId = selectedCamera.Id;
        if (VisualizerAudioCombo.SelectedItem is AudioDeviceItem selectedAudioDevice)
            snapshot.VisualizerAudioDeviceId = selectedAudioDevice.Id;
        snapshot.AutoStart = AutoStartCheck.IsChecked ?? false;
        snapshot.StayBehindWindows = StayBehindWindowsCheck.IsChecked ?? false;
        snapshot.EnableHelloGreeting = HelloGreetingCheck.IsChecked ?? true;
        snapshot.EnableSpotlight = EnableSpotlightCheck.IsChecked ?? true;
        ReadSpotlightAiSettings(snapshot);
        snapshot.EnableDebugMode = EnableDebugModeCheck.IsChecked ?? false;
        snapshot.HideOnExclusiveFullscreen = HideOnExclusiveFullscreenCheck.IsChecked ?? true;
        snapshot.HideOnWindowedFullscreen = HideOnWindowedFullscreenCheck.IsChecked ?? true;
        snapshot.EnableIdleAutoHide = IdleAutoHideCheck.IsChecked ?? false;
        snapshot.IdleAutoHideDelay = Math.Max(1000, (int)(IdleAutoHideDelaySlider.Value * 1000));
        snapshot.ShowMusicNotifications = MusicNotifyCheck.IsChecked ?? true;
        snapshot.ShowSystemNotifications = SystemNotifyCheck.IsChecked ?? true;
        snapshot.ShowBatteryIndicator = ShowBatteryCheck.IsChecked ?? true;
        snapshot.HideCamera = HideCameraCheck.IsChecked ?? false;
        snapshot.ClipboardHotkey = ClipboardHotkeyBox.Text.Trim();
        snapshot.ClipboardCaptureText = TrayTextCheck.IsChecked == true;
        snapshot.ClipboardCaptureImages = TrayImagesCheck.IsChecked == true;
        snapshot.ClipboardCaptureFiles = TrayFilesCheck.IsChecked == true;
        snapshot.ClipboardCaptureDelay = (int)TrayDelaySlider.Value;

        snapshot.EnableWeather = EnableWeatherCheck.IsChecked ?? false;
        snapshot.ManualCity = ManualCityTextBox.Text?.Trim() ?? string.Empty;

        snapshot.EnableLocalOnlyMode = LocalOnlyModeCheck.IsChecked ?? false;
        snapshot.AutoCheckUpdates = AutoCheckUpdatesCheck.IsChecked ?? true;
        snapshot.EnableOnlineArtworkLookup = EnableOnlineArtworkCheck.IsChecked ?? true;
        snapshot.EnableOnlineLyrics = EnableOnlineLyricsCheck.IsChecked ?? true;
        snapshot.EnablePrivacyIndicators = EnablePrivacyIndicatorsCheck.IsChecked ?? true;
        snapshot.EnableBrowserUrlInspection = EnableBrowserUrlInspectionCheck.IsChecked ?? true;
        snapshot.EnableDiagnosticLogging = EnableDiagnosticLoggingCheck.IsChecked ?? true;
        snapshot.EnableSpotlightHistory = EnableSpotlightHistoryCheck.IsChecked ?? true;
        ReadAdditionalPrivacy(snapshot);

        snapshot.EnableYouTubeApi = YouTubeApiCheck.IsChecked ?? false;
        snapshot.YouTubeApiKey = YouTubeApiKeyPasswordBox.Password?.Trim() ?? "";

        if (LanguageCombo.SelectedItem is System.Windows.Controls.ComboBoxItem langItem && langItem.Tag is string langCode)
            snapshot.Language = langCode;

        if (WidgetCombo.SelectedItem is System.Windows.Controls.ComboBoxItem widgetItem && widgetItem.Tag is string widgetCode)
            snapshot.ExpandedWidget = widgetCode;


        if (ClockPageStyleCombo?.SelectedItem is System.Windows.Controls.ComboBoxItem clockItem && clockItem.Tag is string clockCode)
            snapshot.ClockPageStyle = clockCode;

        return snapshot;
    }

    internal bool ApplyPreview(NotchSettings snapshot)
    {
        if (_lastAppliedFps != snapshot.AnimationFps || _appliedSettings.AutoAnimationFps != snapshot.AutoAnimationFps)
        {
            _lastAppliedFps = snapshot.AnimationFps;
            VNotch.Services.AnimationConfig.Configure(snapshot.AnimationFps, snapshot.AutoAnimationFps);
            AnimationPrimitives.ApplyFpsToTree(this);
        }

        if (_appliedSettings.EnableBrowserUrlInspection != snapshot.EnableBrowserUrlInspection)
        {
            VNotch.Services.WindowTitleScanner.UpdateInspectionAllowed(snapshot.EnableBrowserUrlInspection);
        }

        if (IsLiquidGlassConfigChanged(_appliedSettings, snapshot))
        {
            ApplyLiquidGlassSkin();
        }

        bool hasChanges = !_appliedSettings.ValueEquals(snapshot);
        _settings = snapshot.Clone();

        if (hasChanges)
        {
            _appliedSettings = snapshot.Clone();
            SettingsChanged?.Invoke(this, _settings);
        }

        return hasChanges;
    }

    private static bool IsLiquidGlassConfigChanged(NotchSettings? oldSettings, NotchSettings? newSettings)
    {
        if (oldSettings == null || newSettings == null) return true;
        if (!string.Equals(oldSettings.NotchStyle, newSettings.NotchStyle, StringComparison.OrdinalIgnoreCase))
            return true;
        if (!string.Equals(oldSettings.LiquidGlassPreset, newSettings.LiquidGlassPreset, StringComparison.OrdinalIgnoreCase))
            return true;
        if ((oldSettings.LiquidGlass == null) != (newSettings.LiquidGlass == null))
            return true;
        if (oldSettings.LiquidGlass != null && newSettings.LiquidGlass != null && !oldSettings.LiquidGlass.ValueEquals(newSettings.LiquidGlass))
            return true;
        return false;
    }

    internal async Task SaveAsync(NotchSettings snapshot, CancellationToken ct = default)
    {
        _originalSettings = snapshot.Clone();
        await _settingsAppService.ApplyAsync(snapshot, ct);
    }

    private void Save(NotchSettings snapshot)
    {
        SaveAsync(snapshot).SafeFireAndForget("SETTINGS-APPLY");
    }

    private void ApplySettingsFromUi(bool persist = true)
    {
        var snapshot = ReadSettingsFromUi();
        ApplyPreview(snapshot);
        if (persist)
        {
            Save(snapshot);
        }
    }

    #endregion
}

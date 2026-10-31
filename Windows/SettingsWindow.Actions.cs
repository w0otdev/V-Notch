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

            _livePreviewDebounce?.Stop();
            _hasRevertedLivePreview = false;

            _settings = new NotchSettings();
            SpotifyCanvasConsent.Revoke(_settings);
            Loc.SetLanguage(_settings.Language);

            LoadSettings();

            _isLoadingSettings = true;
            try
            {
                AutoStartCheck.IsChecked = _settings.AutoStart;
            }
            finally
            {
                _isLoadingSettings = false;
            }

            UpdateSliderValues();
            UpdateSectionHeader();
            RefreshSearchRows();
            ApplyEffectiveGpuPreference();
            ProcessPriorityPolicy.Apply(_settings.ProcessPriority);
            RuntimeLog.MinimumLevel = _settings.EnableDiagnosticLogging ? LogLevel.Debug : LogLevel.Info;
            (Application.Current?.MainWindow as MainWindow)?.ToggleDebugMode(_settings.EnableDebugMode);

            ApplyPreview(ReadSettingsFromUi());
        }
        catch (Exception ex)
        {
            VNotch.Services.RuntimeLog.Error(LogCategory, ex, "Error in Reset_Click");
        }
    }

    private void UpdateSliderValues()
    {
        if (WidthValue != null) WidthValue.Text = ((int)WidthSlider.Value).ToString();
        if (DynamicIslandWidthValue != null) DynamicIslandWidthValue.Text = ((int)DynamicIslandWidthSlider.Value).ToString();
        if (DynamicIslandHeightValue != null) DynamicIslandHeightValue.Text = ((int)DynamicIslandHeightSlider.Value).ToString();
        if (HeightValue != null) HeightValue.Text = ((int)HeightSlider.Value).ToString();
        if (RadiusValue != null) RadiusValue.Text = ((int)RadiusSlider.Value).ToString();
        if (OpacityValue != null) OpacityValue.Text = ((int)OpacitySlider.Value).ToString();
        if (BlurBrightnessValue != null) BlurBrightnessValue.Text = ((int)BlurBrightnessSlider.Value).ToString();
        if (BlurDarkOverlayValue != null) BlurDarkOverlayValue.Text = ((int)BlurDarkOverlaySlider.Value).ToString();
        if (HoverDelayValue != null) HoverDelayValue.Text = ((int)HoverDelaySlider.Value).ToString();
        if (AnimationFpsValue != null) AnimationFpsValue.Text = ((int)Math.Round(AnimationFpsSlider.Value)).ToString();
    }

    internal async Task ApplyImportedSettingsAsync(NotchSettings imported, CancellationToken ct = default)
    {
        if (imported == null) throw new ArgumentNullException(nameof(imported));

        _livePreviewDebounce?.Stop();
        _hasRevertedLivePreview = false;

        _settings = imported.Clone();
        Loc.SetLanguage(_settings.Language);
        LoadSettings();
        UpdateSliderValues();
        UpdateSectionHeader();
        RefreshSearchRows();

        _settings = ReadSettingsFromUi();
        ApplyPreview(_settings);
        await _settingsAppService.ApplyAsync(_settings, ct);
        _editSession.Commit(_settings);
    }

    private bool _hasRevertedLivePreview;

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        RevertLivePreviewIfNeeded();
        CloseWithAnimation();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_isSaving && !_hasRevertedLivePreview && HasUncommittedLivePreview())
        {
            RevertLivePreviewIfNeeded();
        }
        base.OnClosing(e);
    }

    private bool HasUncommittedLivePreview()
    {
        var baseline = _editSession.Baseline;
        return !baseline.ValueEquals(_editSession.Applied)
            || !baseline.ValueEquals(_settings)
            || !string.Equals(Loc.CurrentLanguage, baseline.Language, StringComparison.OrdinalIgnoreCase);
    }

    private void RevertLivePreviewIfNeeded()
    {
        _livePreviewDebounce?.Stop();
        _hasRevertedLivePreview = true;
        var baseline = _editSession.Revert();
        _settings = baseline.Clone();
        ApplyGpuPreference(GetEffectiveGpuPreference(baseline));
        InteractionSounds.Configure(baseline.EnableInteractionSounds, baseline.InteractionSoundVolume);
        ProcessPriorityPolicy.Apply(baseline.ProcessPriority);
        WindowTitleScanner.UpdateInspectionAllowed(baseline.EnableBrowserUrlInspection);
        RuntimeLog.MinimumLevel = baseline.EnableDiagnosticLogging ? LogLevel.Debug : LogLevel.Info;
        _lastAppliedFps = baseline.AnimationFps;
        AnimationConfig.Configure(baseline.AnimationFps, baseline.AutoAnimationFps);
        (Application.Current?.MainWindow as MainWindow)?.ToggleDebugMode(baseline.EnableDebugMode);
        if (Loc.CurrentLanguage != baseline.Language)
        {
            Loc.SetLanguage(baseline.Language);
        }
        SettingsChanged?.Invoke(this, baseline);
    }

    private void SocialLink_Website_Click(object sender, RoutedEventArgs e) => SafeLauncher.TryOpenUrl(string.Concat("https://", "v-notch.vercel.app/"));
    private void SocialLink_GitHub_Click(object sender, RoutedEventArgs e) => SafeLauncher.TryOpenUrl(string.Concat("https://", "github.com/w0otdev/V-Notch"));
    private void SocialLink_Facebook_Click(object sender, RoutedEventArgs e) => SafeLauncher.TryOpenUrl(string.Concat("https://", "www.facebook.com/rain.107/"));
    private void SocialLink_Discord_Click(object sender, RoutedEventArgs e) => SafeLauncher.TryOpenUrl(string.Concat("https://", "discord.com/users/298304189535092737"));
    private void DonatePaypal_Click(object sender, RoutedEventArgs e) => SafeLauncher.TryOpenUrl(string.Concat("https://", "www.paypal.com/paypalme/PhuocLe678"));
    private void StarRepoButton_Click(object sender, RoutedEventArgs e) => SafeLauncher.TryOpenUrl(string.Concat("https://", "github.com/w0otdev/V-Notch"));
    private void ProjectVertex_Click(object sender, RoutedEventArgs e) => SafeLauncher.TryOpenUrl(string.Concat("https://", "github.com/w0otdev/Vertex"));
    private void ProjectScrollV_Click(object sender, RoutedEventArgs e) => SafeLauncher.TryOpenUrl(string.Concat("https://", "github.com/w0otdev/Scroll-V"));
    private void ReportBug_Click(object sender, RoutedEventArgs e) => SafeLauncher.TryOpenUrl(string.Concat("https://", "github.com/w0otdev/V-Notch/issues/new"));
    private void RequestFeature_Click(object sender, RoutedEventArgs e) => SafeLauncher.TryOpenUrl(string.Concat("https://", "github.com/w0otdev/V-Notch/issues/new?labels=enhancement&template=feature_request.md"));
    private void Discussion_Click(object sender, RoutedEventArgs e) => SafeLauncher.TryOpenUrl(string.Concat("https://", "github.com/w0otdev/V-Notch/discussions"));

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
                             + DeleteExplicitCacheFiles(appData, baseDir);

            ClearCacheHint.Text = deletedCount > 0
                ? Loc.Get("settings.clearCache.done", deletedCount)
                : Loc.Get("settings.clearCache.clean");
        }
        catch (Exception ex)
        {
            VNotch.Services.RuntimeLog.Error(LogCategory, ex, "Error in ClearCache_Click");
        }
    }

    internal static int DeleteCacheDirectories(string appData)
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

                SafeFileDeletion.ValidatePath(dir);
                var files = Directory.EnumerateFiles(dir, "*", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                    IgnoreInaccessible = true
                });
                foreach (var f in files)
                {
                    try
                    {
                        SafeFileDeletion.DeleteFile(f);
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
                    SafeFileDeletion.DeleteFile(file);
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

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        await ApplyWithoutClosingAsync();
    }

    private bool _isSaving;

    internal Task<bool> ApplyAndCloseAsync(Action<Exception>? showError = null) =>
        ApplySettingsAsync(closeAfterSave: true, showError);

    internal Task<bool> ApplyWithoutClosingAsync(Action<Exception>? showError = null) =>
        ApplySettingsAsync(closeAfterSave: false, showError);

    private async Task<bool> ApplySettingsAsync(bool closeAfterSave, Action<Exception>? showError)
    {
        if (_isClosing || _isSaving) return false;
        _isSaving = true;
        try
        {
            var snapshot = ReadSettingsFromUi();
            ApplyPreview(snapshot);
            await SaveAsync(snapshot);
            if (closeAfterSave) CloseWithAnimation();
            return true;
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("SETTINGS-APPLY", ex, "Settings were not fully applied.");
            if (showError != null) showError(ex);
            else MessageBox.Show(this, Loc.Get("error.settingsSave", ex.Message),
                Loc.Get("error.settingsNotSavedTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        finally { _isSaving = false; }
    }

    private async void Save_Click(object sender, RoutedEventArgs e) => await ApplyAndCloseAsync();
    private void CloseWithAnimation()
    {
        if (_isClosing) return;
        _isClosing = true;

        AnimatedClosing?.Invoke(this, EventArgs.Empty);

        var easeIn = new ExponentialEase { EasingMode = EasingMode.EaseIn, Exponent = 6 };
        var easeInStrong = new ExponentialEase { EasingMode = EasingMode.EaseIn, Exponent = 7 };
        int fps = VNotch.Services.AnimationConfig.TargetFps;

        var totalDur = TimeSpan.FromMilliseconds(520);

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

        var (activeCard, activeTranslate) = GetSectionCardParts(_activeNav);

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

        double shellTopMargin = MainShell.Margin.Top;
        double targetLeft = notchLeft + notchW / 2.0 - ActualWidth / 2.0;
        double targetTop = notchTop - shellTopMargin;

        var flyUpWindow = new DoubleAnimation(currentTop, targetTop, totalDur)
        {
            EasingFunction = easeInStrong
        };
        Timeline.SetDesiredFrameRate(flyUpWindow, fps);

        if (Math.Abs(targetLeft - currentLeft) >= 0.5)
        {
            var flyLeftWindow = new DoubleAnimation(currentLeft, targetLeft, totalDur)
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

        if (Math.Abs(currentTranslateY) >= 0.5)
        {
            var resetTranslateY = new DoubleAnimation(currentTranslateY, 0.0, totalDur)
            {
                EasingFunction = easeInStrong
            };
            Timeline.SetDesiredFrameRate(resetTranslateY, fps);
            ShellTranslate.BeginAnimation(TranslateTransform.YProperty, resetTranslateY);
        }
        else
        {
            ShellTranslate.Y = 0.0;
        }

        if (Math.Abs(currentTranslateX) >= 0.5)
        {
            var resetTranslateX = new DoubleAnimation(currentTranslateX, 0.0, totalDur)
            {
                EasingFunction = easeInStrong
            };
            Timeline.SetDesiredFrameRate(resetTranslateX, fps);
            ShellTranslate.BeginAnimation(TranslateTransform.XProperty, resetTranslateX);
        }
        else
        {
            ShellTranslate.X = 0.0;
        }

        squishX.Completed += (s, e) =>
        {
            StopSmoothScroll();
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
            if (Math.Abs(window.MainShell.CornerRadius.TopLeft - r) < 0.25 && Math.Abs(r - 24.0) > 0.01)
                return;
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
        snapshot.EnableInteractionSounds = InteractionSoundsCheck.IsChecked == true;
        snapshot.InteractionSoundVolume = InteractionSoundVolumeSlider.Value / 100;
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
        ReadTranslationSettings(snapshot);
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
        snapshot.ClipboardAutoDeleteSensitive = TraySensitiveCheck.IsChecked == true;
        snapshot.ClipboardCaptureText = TrayTextCheck.IsChecked == true;
        snapshot.ClipboardCaptureImages = TrayImagesCheck.IsChecked == true;
        snapshot.ClipboardCaptureFiles = TrayFilesCheck.IsChecked == true;
        snapshot.ClipboardCaptureDelay = (int)TrayDelaySlider.Value;

        snapshot.EnableWeather = EnableWeatherCheck.IsChecked ?? false;
        snapshot.ManualCity = ManualCityTextBox.Text?.Trim() ?? string.Empty;

        snapshot.EnableLocalOnlyMode = LocalOnlyModeCheck.IsChecked ?? false;
        snapshot.AutoCheckUpdates = AutoCheckUpdatesCheck.IsChecked ?? true;
        snapshot.IncludePrereleaseUpdates = IncludePrereleaseUpdatesCheck.IsChecked == true;
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

        if (ProcessPriorityCombo?.SelectedItem is System.Windows.Controls.ComboBoxItem priorityItem && priorityItem.Tag is string priorityTag)
            snapshot.ProcessPriority = ProcessPriorityPolicy.Normalize(priorityTag);

        if (GpuPreferenceCombo?.SelectedItem is System.Windows.Controls.ComboBoxItem gpuItem && gpuItem.Tag is string gpuTag && int.TryParse(gpuTag, out int gpuPref))
            snapshot.GpuPreference = gpuPref;

        return snapshot;
    }

    internal bool ApplyPreview(NotchSettings snapshot)
    {
        _hasRevertedLivePreview = false;
        var appliedSettings = _editSession.Applied;
        InteractionSounds.Configure(snapshot.EnableInteractionSounds, snapshot.InteractionSoundVolume);
        if (_lastAppliedFps != snapshot.AnimationFps || appliedSettings.AutoAnimationFps != snapshot.AutoAnimationFps)
        {
            _lastAppliedFps = snapshot.AnimationFps;
            VNotch.Services.AnimationConfig.Configure(snapshot.AnimationFps, snapshot.AutoAnimationFps);
            AnimationPrimitives.ApplyFpsToTree(this);
        }

        if (appliedSettings.EnableBrowserUrlInspection != snapshot.EnableBrowserUrlInspection)
        {
            VNotch.Services.WindowTitleScanner.UpdateInspectionAllowed(snapshot.EnableBrowserUrlInspection);
        }

        if (IsLiquidGlassConfigChanged(appliedSettings, snapshot))
        {
            ApplyLiquidGlassSkin();
        }

        bool hasChanges = _editSession.Preview(snapshot);
        _settings = snapshot.Clone();

        if (hasChanges)
        {
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
        await _settingsAppService.ApplyAsync(snapshot, ct);
        _editSession.Commit(snapshot);
    }

    private void Save(NotchSettings snapshot)
    {
        SaveAsync(snapshot, _translationDownload?.Token ?? CancellationToken.None).SafeFireAndForget("SETTINGS-APPLY");
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

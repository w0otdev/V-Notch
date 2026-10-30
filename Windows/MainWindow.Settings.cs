using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using VNotch.Controllers;
using VNotch.Models;
using VNotch.Services;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch;

public partial class MainWindow
{
    private void OpenAppSettings(bool showTranslation = false)
    {
        if (IsGreetingInteractionBlocked) return;
        if (_activeSettingsWindow is { IsVisible: true } activeSettings)
        {
            if (showTranslation) activeSettings.NavigateToTranslation();
            if (activeSettings.WindowState == WindowState.Minimized)
                activeSettings.WindowState = WindowState.Normal;
            BringSettingsToFront(activeSettings);
            return;
        }

        var settingsWindow = TakeSettingsWindow();
        _activeSettingsWindow = settingsWindow;

        settingsWindow.UpdateDetected += (_, updateInfo) =>
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
            {
                SetAvailableUpdate(updateInfo);
            }));
        };

        settingsWindow.TranslationPreviewRequested += () => _liveTranslation?.OpenFromClipboard();
        if (showTranslation) settingsWindow.NavigateToTranslation();

        settingsWindow.SettingsChanged += (_, newSettings) =>
            OnSettingsWindowSettingsChanged(settingsWindow, newSettings);

        settingsWindow.Closed += (_, _) =>
        {
            if (ReferenceEquals(_activeSettingsWindow, settingsWindow))
            {
                _activeSettingsWindow = null;
            }
            PlayNotchReturnBounce();
            ScheduleDesktopLayerDemotionIfIdle();
            if (_updateService?.LatestUpdateInfo is { IsNewerVersion: true } update)
            {
                SetAvailableUpdate(update);
            }
        };

        settingsWindow.Owner = this;
        settingsWindow.ShowActivated = true;
        settingsWindow.Show();
        BringSettingsToFront(settingsWindow);
    }

    private void BringSettingsToFront(SettingsWindow window)
    {
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Topmost = Topmost;
        window.Activate();
        window.Focus();
        window.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (!window.IsVisible) return;
            window.Activate();
            window.Focus();
        }));
    }

    private void OnSettingsWindowSettingsChanged(SettingsWindow settingsWindow, NotchSettings newSettings)
    {
        var oldSettings = _settings;
        bool sizeChanged = HasNotchGeometryChanged(oldSettings, newSettings);
        bool languageChanged = !string.Equals(newSettings.Language, oldSettings.Language, StringComparison.Ordinal);
        bool spotifyCanvasChanged = HasSpotifyCanvasConfigChanged(oldSettings, newSettings);
        _modeTransitionPending = newSettings.EnableDynamicIslandMode != oldSettings.EnableDynamicIslandMode;
        bool oldIgnoreAuto = oldSettings.IgnoreYouTubeAutoSubtitles;
        string oldSubtitlePriority = oldSettings.SubtitlePriority ?? "";

        _settings = newSettings.Clone();
        _clipboardHotkey?.Apply(_settings.ClipboardHotkey);
        _clipboardHistory?.ApplySettings(_settings);
        if (_settings.HideCamera && IsCameraPreviewLifecycleActive) StopCameraPreviewForViewExit();
        NetworkPrivacy.Current.Apply(_settings);
        ApplyInvisibleMode();
        _liveTranslation?.ApplySettings(_settings);

        UpdateWindowAndNotchPlacementSettings(oldSettings, newSettings, settingsWindow);
        ApplySettings(oldSettings, sizeChanged);
        UpdateWeatherAndCanvasSettings(oldSettings, newSettings, spotifyCanvasChanged);
        RefetchSubtitlesIfPriorityChanged(oldSubtitlePriority, oldIgnoreAuto);

        if (languageChanged)
        {
            Loc.SetLanguage(_settings.Language);
            RefreshNotchLocalization();
        }

        RefreshUpdateCheckIfSettingsChanged(oldSettings, newSettings);
    }

    private static bool HasNotchGeometryChanged(NotchSettings oldSettings, NotchSettings newSettings) =>
        newSettings.Width != oldSettings.Width ||
        newSettings.DynamicIslandWidth != oldSettings.DynamicIslandWidth ||
        newSettings.DynamicIslandHeight != oldSettings.DynamicIslandHeight ||
        newSettings.Height != oldSettings.Height ||
        newSettings.CornerRadius != oldSettings.CornerRadius;

    private static bool HasSpotifyCanvasConfigChanged(NotchSettings oldSettings, NotchSettings newSettings) =>
        newSettings.EnableSpotifyCanvas != oldSettings.EnableSpotifyCanvas ||
        newSettings.AllowOnlineCanvas != oldSettings.AllowOnlineCanvas ||
        newSettings.SpotifyCanvasConsentVersion != oldSettings.SpotifyCanvasConsentVersion ||
        newSettings.EnableLocalOnlyMode != oldSettings.EnableLocalOnlyMode ||
        !string.Equals(newSettings.SpotifySpDc, oldSettings.SpotifySpDc, StringComparison.Ordinal);

    private void UpdateWindowAndNotchPlacementSettings(
        NotchSettings oldSettings,
        NotchSettings newSettings,
        SettingsWindow settingsWindow)
    {
        if (oldSettings.HideOnExclusiveFullscreen != newSettings.HideOnExclusiveFullscreen ||
            oldSettings.HideOnWindowedFullscreen != newSettings.HideOnWindowedFullscreen)
        {
            _fullscreenController.UpdateSettings(_settings);
        }

        bool monitorChanged = oldSettings.MonitorIndex != newSettings.MonitorIndex ||
                              oldSettings.MonitorDeviceId != newSettings.MonitorDeviceId;
        if (monitorChanged ||
            oldSettings.EnableHoverExpand != newSettings.EnableHoverExpand ||
            oldSettings.Height != newSettings.Height ||
            oldSettings.AnimationFps != newSettings.AnimationFps)
        {
            _notchManager.UpdateSettings(_settings);
        }

        if (monitorChanged)
        {
            PositionAtTop();
        }

        // Forward every settings snapshot. SpotlightController owns change detection,
        // including provider, model and credentials; filtering here leaves it stale.
        _spotlightController.ApplySettings(_settings);
        if (oldSettings.EnableSpotlight != newSettings.EnableSpotlight)
        {
            settingsWindow.SetSpotlightHotkeyStatus(_spotlightController.IsHotkeyRegistered);
        }
    }

    private void UpdateWeatherAndCanvasSettings(
        NotchSettings oldSettings,
        NotchSettings newSettings,
        bool spotifyCanvasChanged)
    {
        if (oldSettings.EnableWeather != newSettings.EnableWeather ||
            oldSettings.AllowOnlineWeather != newSettings.AllowOnlineWeather ||
            oldSettings.EnableLocalOnlyMode != newSettings.EnableLocalOnlyMode ||
            !string.Equals(oldSettings.ManualCity, newSettings.ManualCity, StringComparison.Ordinal))
        {
            WeatherWidgetView.ApplySettings(
                _settings.EnableWeather && !_settings.EnableLocalOnlyMode && _settings.AllowOnlineWeather);
            _weatherModule.OnSettingsChanged(_settings);
        }

        if (spotifyCanvasChanged)
        {
            _spotifyCanvasController?.ClearCache();
            _spotifyCanvasController?.UpdateSettings(_settings);
        }
    }

    private void RefetchSubtitlesIfPriorityChanged(string oldSubtitlePriority, bool oldIgnoreAuto)
    {
        bool priorityChanged = !string.Equals(oldSubtitlePriority, _settings.SubtitlePriority, StringComparison.Ordinal);
        bool ignoreAutoChanged = oldIgnoreAuto != _settings.IgnoreYouTubeAutoSubtitles;
        if (!priorityChanged && !ignoreAutoChanged) return;

        VNotch.Services.RuntimeLog.Log("SUBTITLE-PRIORITY",
            $"Settings changed: priority='{oldSubtitlePriority}' -> '{_settings.SubtitlePriority}', ignoreAuto={_settings.IgnoreYouTubeAutoSubtitles} " +
            $"hasMedia={_currentMediaInfo != null} videoId='{_lastKnownYouTubeVideoId}'");

        if (string.IsNullOrEmpty(_lastKnownYouTubeVideoId)) return;

        _youtubeSubtitleService.Reset();
        _lyricsTrackKey = "";
        _syncedTextSource = SyncedTextSource.None;
        var refetchInfo = _currentMediaInfo ?? new VNotch.Models.MediaInfo();
        if (string.IsNullOrEmpty(refetchInfo.YouTubeVideoId))
        {
            refetchInfo.YouTubeVideoId = _lastKnownYouTubeVideoId;
        }
        FetchSubtitlesForTrack(refetchInfo, force: true).SafeFireAndForget("SUBTITLES");
    }

    private void RefreshUpdateCheckIfSettingsChanged(NotchSettings oldSettings, NotchSettings newSettings)
    {
        bool autoCheckUpdatesChanged = oldSettings.AutoCheckUpdates != newSettings.AutoCheckUpdates;
        bool updateChannelChanged = oldSettings.IncludePrereleaseUpdates != newSettings.IncludePrereleaseUpdates;
        if (updateChannelChanged)
        {
            System.Threading.Interlocked.Increment(ref _updateCheckGeneration);
            if (!_isUpdateInstalling)
            {
                _isUpdateAvailable = false;
                _availableUpdate = null;
                HideUpdateNotification();
            }
        }
        if ((autoCheckUpdatesChanged || updateChannelChanged) && newSettings.AutoCheckUpdates)
        {
            CheckForUpdatesAsync().SafeFireAndForget("SETTINGS-AUTOCHECK-ENABLED");
        }
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

#pragma warning disable S3776 // Synchronizes multiple WPF visual tree elements with application configuration
    private void ApplySettings(bool animatePulse = false) => ApplySettings(null, animatePulse);

    private void ApplySettings(NotchSettings? oldSettings, bool animatePulse = false)
    {
        InteractionSounds.Configure(_settings.EnableInteractionSounds, _settings.InteractionSoundVolume);
        if (oldSettings == null || oldSettings.EnablePrivacyIndicators != _settings.EnablePrivacyIndicators)
            ApplyPrivacyIndicatorSettings();
        _mediaService.KeepPinnedOnTrackChange = _settings.KeepMediaPinnedOnTrackChange;
        if (oldSettings == null || oldSettings.AnimationFps != _settings.AnimationFps ||
            oldSettings.AutoAnimationFps != _settings.AutoAnimationFps)
        {
            VNotch.Services.AnimationConfig.Configure(_settings.AnimationFps, _settings.AutoAnimationFps);
            AnimationPrimitives.ApplyFpsToTree(this);
        }

        if (oldSettings == null || !string.Equals(oldSettings.VisualizerAudioDeviceId, _settings.VisualizerAudioDeviceId, StringComparison.Ordinal))
        {
            VNotch.Controls.MusicVisualizer.ConfigureAudioDevice(_settings.VisualizerAudioDeviceId);
        }

        if (oldSettings == null || oldSettings.EnableBlurEffects != _settings.EnableBlurEffects)
        {
            ApplyPerformanceSettings();
        }

        if (oldSettings == null || !string.Equals(oldSettings.ProcessPriority, _settings.ProcessPriority, StringComparison.Ordinal))
        {
            App.ApplyProcessPriority(_settings.ProcessPriority);
        }

        if (oldSettings == null || oldSettings.StayBehindWindows != _settings.StayBehindWindows)
        {
            ResetDesktopEdgePromotionIfDisabled();
            ScheduleDesktopLayerDemotionIfIdle();

            // Reconfigure immediately so changing this option does not require a restart.
            if (_hwnd != IntPtr.Zero)
                ConfigureOverlayWindow();
        }

        if (oldSettings == null || !string.Equals(oldSettings.ExpandedWidget, _settings.ExpandedWidget, StringComparison.Ordinal))
        {
            ApplyExpandedWidgetMode();
        }

        if (oldSettings == null || oldSettings.HideCamera != _settings.HideCamera || !string.Equals(oldSettings.NavTabOrder, _settings.NavTabOrder, StringComparison.Ordinal) || !string.Equals(oldSettings.VisibleNavTabs, _settings.VisibleNavTabs, StringComparison.Ordinal))
        {
            ApplyNavTabOrderAndVisibility();
        }



        if (oldSettings == null || !string.Equals(oldSettings.ClockPageStyle, _settings.ClockPageStyle, StringComparison.Ordinal))
        {
            ApplyClockPageStyle();
        }

        if (oldSettings == null
            || oldSettings.HoverCollapseDelay != _settings.HoverCollapseDelay
            || oldSettings.HoverExpandDelay != _settings.HoverExpandDelay
            || oldSettings.DisableMouseLeaveAutoClose != _settings.DisableMouseLeaveAutoClose)
        {
            _hoverCollapseTimer.Interval = TimeSpan.FromMilliseconds(_settings.HoverCollapseDelay);
            _hoverThumbnailDelayTimer.Interval = TimeSpan.FromMilliseconds(_settings.HoverExpandDelay);

            if (_settings.DisableMouseLeaveAutoClose)
            {
                _hoverCollapseTimer.Stop();
            }
        }

        if (oldSettings == null
            || oldSettings.EnableIdleAutoHide != _settings.EnableIdleAutoHide
            || oldSettings.IdleAutoHideDelay != _settings.IdleAutoHideDelay)
        {
            ApplyIdleAutoHideSettings();
        }

        if (oldSettings == null || oldSettings.EnableDebugMode != _settings.EnableDebugMode)
        {
            ToggleDebugMode(_settings.EnableDebugMode);
        }

        if (oldSettings == null
            || !string.Equals(oldSettings.SubtitlePriority, _settings.SubtitlePriority, StringComparison.Ordinal)
            || oldSettings.IgnoreYouTubeAutoSubtitles != _settings.IgnoreYouTubeAutoSubtitles
            || !string.Equals(oldSettings.Language, _settings.Language, StringComparison.Ordinal))
        {
            _youtubeSubtitleService.SetMode(_settings.SubtitlePriority);
            _youtubeSubtitleService.SetIgnoreAutoGenerated(_settings.IgnoreYouTubeAutoSubtitles);
            _youtubeSubtitleService.SetNativeLangHint(_settings.Language);
        }

        if (_hwnd != IntPtr.Zero && (oldSettings == null
            || oldSettings.HideOnExclusiveFullscreen != _settings.HideOnExclusiveFullscreen
            || oldSettings.HideOnWindowedFullscreen != _settings.HideOnWindowedFullscreen))
        {
            UpdateFullscreenAutoHideState(OverlayWindowController.GetForegroundWindowHandle(), force: true);
        }

        bool layoutChanged = oldSettings == null
            || oldSettings.Width != _settings.Width
            || oldSettings.Height != _settings.Height
            || oldSettings.DynamicIslandWidth != _settings.DynamicIslandWidth
            || oldSettings.DynamicIslandHeight != _settings.DynamicIslandHeight
            || oldSettings.CornerRadius != _settings.CornerRadius
            || oldSettings.EnableDynamicIslandMode != _settings.EnableDynamicIslandMode;

        if (layoutChanged)
        {
            _collapsedWidth = GetCollapsedWidth();
            _collapsedHeight = GetCollapsedHeight();
            _expandedHeight = _settings.EnableDynamicIslandMode ? 154 : 147;
            _cornerRadiusCollapsed = GetCollapsedCornerRadius();
            _cachedThumbnailExpandTarget = null;

            bool willModeTransition = _modeTransitionPending
                                      && !_isExpanded && !_isAnimating
                                      && NotchBorder.ActualWidth > 0 && NotchBorder.ActualHeight > 0;
            _modeTransitionPending = false;

            ApplyDynamicIslandLayout(animateTransition: willModeTransition);

            if (!_isExpanded && !_isAnimating)
            {
                double currentRadius = NotchBorder.CornerRadius.BottomLeft;
                if (currentRadius > 0)
                    CurrentCornerRadius = currentRadius;
                else
                    CurrentCornerRadius = _cornerRadiusCollapsed;

                NotchBorder.BeginAnimation(WidthProperty, null);
                NotchBorder.BeginAnimation(HeightProperty, null);
                this.BeginAnimation(CurrentCornerRadiusProperty, null);

                bool isFirstLayout = NotchBorder.ActualWidth <= 0 || NotchBorder.ActualHeight <= 0;
                int fps = VNotch.Services.AnimationConfig.TargetFps;

                if (isFirstLayout)
                {
                    ApplyCollapsedLayoutDirect();
                }
                else if (willModeTransition)
                {
                    AnimateModeTransition(fps);
                }
                else if (_isModeTransitioning)
                {
                    // Mode transition in flight, maintain current animated dimensions
                }
                else if (animatePulse && (Math.Abs(NotchBorder.ActualWidth - _collapsedWidth) > 0.5 ||
                                          Math.Abs(NotchBorder.ActualHeight - _collapsedHeight) > 0.5 ||
                                          Math.Abs(_cornerRadiusCollapsed - currentRadius) > 0.5))
                {
                    var dur = _dur200;
                    var easing = _easeExpOut6;

                    var widthAnim = new DoubleAnimation(NotchBorder.ActualWidth > 0 ? NotchBorder.ActualWidth : _collapsedWidth, _collapsedWidth, dur)
                    {
                        EasingFunction = easing,
                        FillBehavior = FillBehavior.Stop
                    };
                    Timeline.SetDesiredFrameRate(widthAnim, fps);
                    widthAnim.Completed += (s, e) =>
                    {
                        NotchBorder.BeginAnimation(WidthProperty, null);
                        NotchBorder.Width = _collapsedWidth;
                    };

                    var heightAnim = new DoubleAnimation(NotchBorder.ActualHeight > 0 ? NotchBorder.ActualHeight : _collapsedHeight, _collapsedHeight, dur)
                    {
                        EasingFunction = easing,
                        FillBehavior = FillBehavior.Stop
                    };
                    Timeline.SetDesiredFrameRate(heightAnim, fps);
                    heightAnim.Completed += (s, e) =>
                    {
                        NotchBorder.BeginAnimation(HeightProperty, null);
                        NotchBorder.Height = _collapsedHeight;
                    };

                    NotchBorder.BeginAnimation(WidthProperty, widthAnim);
                    NotchBorder.BeginAnimation(HeightProperty, heightAnim);

                    double targetRadius = _cornerRadiusCollapsed;
                    if (Math.Abs(targetRadius - currentRadius) > 0.5)
                    {
                        CurrentCornerRadius = currentRadius;
                        var radiusAnim = new DoubleAnimation(currentRadius, targetRadius, dur)
                        {
                            EasingFunction = easing
                        };
                        Timeline.SetDesiredFrameRate(radiusAnim, fps);
                        this.BeginAnimation(CurrentCornerRadiusProperty, radiusAnim);
                    }
                    else
                    {
                        var cr = MakeNotchCornerRadius(_cornerRadiusCollapsed);
                        NotchBorder.CornerRadius = cr;
                        InnerClipBorder.CornerRadius = cr;
                        NotchBackground.CornerRadius = cr;
                        NotchBorderShadow.CornerRadius = cr;
                        MediaBackground.CornerRadius = cr;
                        MediaBackground2.CornerRadius = cr;
                        _glassMaterialPresenter.SyncCornerRadius(cr);
                        CurrentCornerRadius = _cornerRadiusCollapsed;
                    }
                }
                else
                {
                    ApplyCollapsedLayoutDirect();
                }

                UpdateNotchClip();
                UpdateMediaBackgroundFootprint();

                if (animatePulse)
                {
                    NotchScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                    NotchScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                    NotchShadowScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                    NotchShadowScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);

                    var pulse = new DoubleAnimationUsingKeyFrames();
                    pulse.KeyFrames.Add(new EasingDoubleKeyFrame(1.04,
                        KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(80)),
                        _easeQuadOut));
                    pulse.KeyFrames.Add(new EasingDoubleKeyFrame(1.0,
                        KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(300)),
                        _easeSoftSpring));
                    Timeline.SetDesiredFrameRate(pulse, fps);

                    NotchScale.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
                    NotchScale.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
                    NotchShadowScale.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
                    NotchShadowScale.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
                }
            }

            if (_hwnd != IntPtr.Zero)
            {
                _overlayWindow.ReassertBounds();
            }
        }

        if (oldSettings == null || Math.Abs(oldSettings.Opacity - _settings.Opacity) > 0.001)
        {
            ApplyAppearanceOpacity();
        }

        if (oldSettings == null || oldSettings.BrightenDarkLyricsBackground != _settings.BrightenDarkLyricsBackground)
        {
            AnimateLyricsBlurImageSwitch(_currentMediaInfo?.Thumbnail);
        }

        if (oldSettings == null || Math.Abs(oldSettings.MediaBlurDarkOverlay - _settings.MediaBlurDarkOverlay) > 0.001)
        {
            double lyricsImageOpacity = Math.Max(0.2, 1.0 - _settings.MediaBlurDarkOverlay);
            if (LyricsBlurImage != null)
            {
                LyricsBlurImage.BeginAnimation(UIElement.OpacityProperty, null);
                LyricsBlurImage.Opacity = lyricsImageOpacity;
            }
        }

        if (oldSettings == null || Math.Abs(oldSettings.SpotifyCanvasBrightness - _settings.SpotifyCanvasBrightness) > 0.001)
        {
            ApplySpotifyCanvasBrightness();
        }

        if (oldSettings == null || oldSettings.EnableSpotifyLyrics != _settings.EnableSpotifyLyrics)
        {
            if (!_settings.EnableSpotifyLyrics)
            {
                if (_isLyricsActive && _syncedTextSource == SyncedTextSource.SpotifyLyrics)
                {
                    _lyricsTrackKey = "";
                    HideLyricsWidget();
                }
            }
            else if (_currentMediaInfo != null
                     && _currentMediaInfo.Platform == MediaPlatform.Spotify
                     && !string.IsNullOrEmpty(_currentMediaInfo.CurrentTrack)
                     && !_isLyricsActive)
            {
                _lyricsTrackKey = "";
                FetchLyricsForTrack(_currentMediaInfo).SafeFireAndForget("LYRICS");
            }
        }

        if (oldSettings == null || oldSettings.EnableYouTubeSubtitles != _settings.EnableYouTubeSubtitles)
        {
            if (!_settings.EnableYouTubeSubtitles)
            {
                if (_isLyricsActive && _syncedTextSource == SyncedTextSource.YouTubeSubtitles)
                {
                    _lyricsTrackKey = "";
                    HideLyricsWidget();
                }
            }
            else if (_currentMediaInfo != null
                     && IsCurrentTrackYouTube(_currentMediaInfo)
                     && !_isLyricsActive)
            {
                _lyricsTrackKey = "";
                FetchSubtitlesForTrack(_currentMediaInfo, force: true).SafeFireAndForget("SUBTITLES");
            }
        }

        bool smartCropChanged = oldSettings == null || oldSettings.EnableSmartCrop != _settings.EnableSmartCrop;
        if (smartCropChanged)
        {
            _mediaService.ArtworkService.ConfigureSmartCrop(_settings.EnableSmartCrop);
        }

        bool mediaArtBgChanged = oldSettings == null || oldSettings.ShowMediaArtBackground != _settings.ShowMediaArtBackground;
        if (mediaArtBgChanged && !_settings.ShowMediaArtBackground)
        {
            HideMediaBackground();
        }

        if (_currentMediaInfo != null && !_isExpanded && (smartCropChanged || (mediaArtBgChanged && _settings.ShowMediaArtBackground)))
        {
            UpdateMediaBackground(_currentMediaInfo, forceRefresh: true);
        }

        if (oldSettings == null || IsLiquidGlassConfigChanged(oldSettings, _settings))
        {
            ApplyLiquidGlassSkin();
        }
    }
#pragma warning restore S3776
}

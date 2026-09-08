using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VNotch.Models;
using VNotch.Services;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch;

public partial class MainWindow
{
    #region Media Background & Color Extraction

    private Color _lastDominantColor = Colors.Transparent;
    private string? _lastTrackId = null;
    private bool _isFadingTrack = false;
    private int _mediaBackgroundAnimationVersion = 0;
    private int _mediaBackgroundRecoveryVersion = 0;
    private DateTime _lastMediaBackgroundFadeStartUtc = DateTime.MinValue;

    private void UpdateMediaBackground(MediaInfo? info, bool forceRefresh = false)
    {
        bool glass = IsLiquidGlassEnabled;

        // Outside glass mode the blurred album-art backdrop honours the user's
        // toggles. In glass mode we skip the backdrop image entirely but still
        // derive the accent colours (progress bar, time text, visualiser, ...)
        // from the album art so themed UI keeps working over the glass.
        if (!glass && (!_settings.ShowMediaArtBackground || !_settings.EnableBlurEffects))
        {
            HideMediaBackground();
            return;
        }

        if (info == null || !info.IsAnyMediaPlaying)
        {
            HideMediaBackground();
            return;
        }

        if (info.Thumbnail == null)
        {
            return;
        }

        bool suppressBackdrop = glass;

        var palette = DynamicIslandColorExtractor.GetDynamicIslandPalette(info.Thumbnail);
        var dominantColor = palette.Main;
        var subColor = palette.Sub;

        if (glass)
        {
            UpdateGlassMediaTint(dominantColor);
        }

        string currentTrackId = $"{info.CurrentTrack}|{info.CurrentArtist}";
        bool isNewTrack = _lastTrackId != null && _lastTrackId != currentTrackId;
        _lastTrackId = currentTrackId;

        if (isNewTrack && !forceRefresh && !_isFadingTrack && _isExpanded && !suppressBackdrop)
        {
            _isFadingTrack = true;
            FadeToBlackThenUpdate(info);
            return;
        }

        if (!suppressBackdrop)
            UpdateBlurredBackgroundAsync(info.Thumbnail, allowInterimThumbnail: forceRefresh || isNewTrack).SafeFireAndForget("MEDIA-BG-BLUR");
        else
            HideMediaBackgroundOverlay();

        double brightnessDimOpacity = suppressBackdrop ? 0 : DynamicIslandColorExtractor.GetBrightnessDimOverlay(info.Thumbnail);
        var dimOverlayAnim = new DoubleAnimation
        {
            To = brightnessDimOpacity,
            Duration = TimeSpan.FromMilliseconds(400),
            EasingFunction = _easeQuadOut
        };
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(dimOverlayAnim, VNotch.Services.AnimationConfig.TargetFps);
        BrightnessDimOverlay.BeginAnimation(OpacityProperty, dimOverlayAnim);
        BrightnessDimOverlay2.BeginAnimation(OpacityProperty, dimOverlayAnim);

        if (!forceRefresh && dominantColor == _lastDominantColor && !isNewTrack
            && (suppressBackdrop || MediaBackground.Opacity > 0.49))
        {
            return;
        }

        _lastDominantColor = dominantColor;

        var liftedSub = LiftDarkColor(subColor);
        var vibrantTargetColor = Color.FromRgb(liftedSub.R, liftedSub.G, liftedSub.B);
        double dominantLuminance = (0.2126 * dominantColor.R + 0.7152 * dominantColor.G + 0.0722 * dominantColor.B) / 255.0;

        var uiColorAnim = new ColorAnimation
        {
            To = vibrantTargetColor,
            Duration = TimeSpan.FromMilliseconds(420),
            EasingFunction = _easeQuadOut
        };

        double targetOpacity = 0;
        if (!suppressBackdrop && _isExpanded && (!_isAnimating || forceRefresh))
        {
            targetOpacity = DynamicIslandColorExtractor.GetAdaptiveBlurOpacity(dominantLuminance, _settings.MediaBlurBrightnessBoost);
        }
        if (targetOpacity > 0 && dominantLuminance < 0.25)
        {
            double darknessBoost = 1.0 + (0.25 - dominantLuminance) * 1.4;
            targetOpacity = Math.Min(targetOpacity * darknessBoost, 0.95);
        }
        int animationVersion = ++_mediaBackgroundAnimationVersion;

        var opacityAnim = new DoubleAnimation
        {
            To = targetOpacity,
            Duration = TimeSpan.FromMilliseconds(500),
            EasingFunction = _easeQuadOut
        };

        if (targetOpacity > 0)
        {
            MediaBackground.Visibility = Visibility.Visible;
            MediaBackground2.Visibility = Visibility.Visible;
        }
        else
        {
            opacityAnim.Completed += (s, e) =>
            {
                if (animationVersion != _mediaBackgroundAnimationVersion)
                {
                    return;
                }

                if (MediaBackground.Opacity <= 0.001 && (!_isExpanded || _isAnimating))
                {
                    MediaBackground.Visibility = Visibility.Collapsed;
                    MediaBackground2.Visibility = Visibility.Collapsed;
                }
            };
        }

        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(opacityAnim, VNotch.Services.AnimationConfig.TargetFps);
        MediaBackground.BeginAnimation(OpacityProperty, opacityAnim);
        MediaBackground2.BeginAnimation(OpacityProperty, opacityAnim);
        EnsureUnfrozen(IndeterminateProgress.Background, c => IndeterminateProgress.Background = new SolidColorBrush(c ?? Colors.White));
        EnsureUnfrozen(CurrentTimeText.Foreground, c => CurrentTimeText.Foreground = new SolidColorBrush(c ?? Color.FromRgb(136, 136, 136)));
        EnsureUnfrozen(RemainingTimeText.Foreground, c => RemainingTimeText.Foreground = new SolidColorBrush(c ?? Color.FromRgb(136, 136, 136)));
        EnsureUnfrozen(CompactTitleMarquee.Foreground, c => CompactTitleMarquee.Foreground = new SolidColorBrush(c ?? Colors.White));

        var progressDarkColor = Color.FromArgb(
            vibrantTargetColor.A,
            (byte)(vibrantTargetColor.R * 0.65),
            (byte)(vibrantTargetColor.G * 0.65),
            (byte)(vibrantTargetColor.B * 0.65));

        var progressStartAnim = new ColorAnimation
        {
            To = vibrantTargetColor,
            Duration = TimeSpan.FromMilliseconds(420),
            EasingFunction = _easeQuadOut
        };
        var progressEndAnim = new ColorAnimation
        {
            To = progressDarkColor,
            Duration = TimeSpan.FromMilliseconds(420),
            EasingFunction = _easeQuadOut
        };
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(progressStartAnim, VNotch.Services.AnimationConfig.TargetFps);
        ProgressBarGradientStart.BeginAnimation(GradientStop.ColorProperty, progressStartAnim);
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(progressEndAnim, VNotch.Services.AnimationConfig.TargetFps);
        ProgressBarGradientEnd.BeginAnimation(GradientStop.ColorProperty, progressEndAnim);

        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(uiColorAnim, VNotch.Services.AnimationConfig.TargetFps);
        if (IndeterminateProgress.Background is SolidColorBrush ipb && !ipb.IsFrozen)
            ipb.BeginAnimation(SolidColorBrush.ColorProperty, uiColorAnim);

        if (CurrentTimeText.Foreground is SolidColorBrush ctf && !ctf.IsFrozen)
            ctf.BeginAnimation(SolidColorBrush.ColorProperty, uiColorAnim);

        if (RemainingTimeText.Foreground is SolidColorBrush rtf && !rtf.IsFrozen)
            rtf.BeginAnimation(SolidColorBrush.ColorProperty, uiColorAnim);

        if (CompactTitleMarquee.Foreground is SolidColorBrush cmt && !cmt.IsFrozen)
            cmt.BeginAnimation(SolidColorBrush.ColorProperty, uiColorAnim);

        AnimateTitleGradient(vibrantTargetColor);

        if (Resources["MusicVisualizerBrush"] is SolidColorBrush visualizerBrush && !visualizerBrush.IsFrozen)
        {
            visualizerBrush.BeginAnimation(SolidColorBrush.ColorProperty, uiColorAnim);
        }

        EnsureUnfrozen(VolumeIcon.Foreground, c => VolumeIcon.Foreground = new SolidColorBrush(c ?? Color.FromRgb(136, 136, 136)));
        ((SolidColorBrush)VolumeIcon.Foreground).BeginAnimation(SolidColorBrush.ColorProperty, uiColorAnim);

        var volStartAnim = new ColorAnimation
        {
            To = vibrantTargetColor,
            Duration = TimeSpan.FromMilliseconds(420),
            EasingFunction = _easeQuadOut
        };
        var volEndAnim = new ColorAnimation
        {
            To = progressDarkColor,
            Duration = TimeSpan.FromMilliseconds(420),
            EasingFunction = _easeQuadOut
        };
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(volStartAnim, VNotch.Services.AnimationConfig.TargetFps);
        VolumeBarGradientStart.BeginAnimation(GradientStop.ColorProperty, volStartAnim);
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(volEndAnim, VNotch.Services.AnimationConfig.TargetFps);
        VolumeBarGradientEnd.BeginAnimation(GradientStop.ColorProperty, volEndAnim);

        if (VolumeIndicatorFill != null)
        {
            var volIndStartAnim = new ColorAnimation
            {
                To = vibrantTargetColor,
                Duration = TimeSpan.FromMilliseconds(420),
                EasingFunction = _easeQuadOut
            };
            var volIndEndAnim = new ColorAnimation
            {
                To = progressDarkColor,
                Duration = TimeSpan.FromMilliseconds(420),
                EasingFunction = _easeQuadOut
            };
            System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(volIndStartAnim, VNotch.Services.AnimationConfig.TargetFps);
            VolumeIndicatorGradientStart.BeginAnimation(GradientStop.ColorProperty, volIndStartAnim);
            System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(volIndEndAnim, VNotch.Services.AnimationConfig.TargetFps);
            VolumeIndicatorGradientEnd.BeginAnimation(GradientStop.ColorProperty, volIndEndAnim);
        }

        void EnsureUnfrozenFill(System.Windows.Shapes.Shape shape)
        {
            var brush = shape.Fill as SolidColorBrush;
            if (brush == null || brush.IsFrozen) shape.Fill = new SolidColorBrush(brush?.Color ?? Colors.White);
            ((SolidColorBrush)shape.Fill).BeginAnimation(SolidColorBrush.ColorProperty, uiColorAnim);
        }

        EnsureUnfrozenFill(InlinePrevArrow0);
        EnsureUnfrozenFill(InlinePrevArrow1);
        EnsureUnfrozenFill(InlinePrevArrow2);
        EnsureUnfrozenFill(InlinePauseIconPath);
        EnsureUnfrozenFill(InlinePlayIconPath);
        EnsureUnfrozenFill(InlineNextArrow0);
        EnsureUnfrozenFill(InlineNextArrow1);
        EnsureUnfrozenFill(InlineNextArrow2);

    }

    private static void EnsureUnfrozen(Brush? brush, Action<Color?> replace)
    {
        var sb = brush as SolidColorBrush;
        if (sb == null || sb.IsFrozen)
        {
            replace(sb?.Color);
        }
    }

    private static Color LiftDarkColor(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double v = Math.Max(r, Math.Max(g, b));

        if (v >= 0.55) return c;

        double targetV;
        if (v < 0.20) targetV = 0.55;
        else if (v < 0.35) targetV = 0.55;
        else if (v < 0.50) targetV = 0.55;
        else targetV = v;

        if (targetV <= v) return c;

        double scale = targetV / Math.Max(v, 0.01);
        byte newR = (byte)Math.Clamp(c.R * scale, 0, 255);
        byte newG = (byte)Math.Clamp(c.G * scale, 0, 255);
        byte newB = (byte)Math.Clamp(c.B * scale, 0, 255);

        return Color.FromRgb(newR, newG, newB);
    }

    private void FadeToBlackThenUpdate(MediaInfo info)
    {
        _isFadingTrack = false;
        _suppressNextBlurDissolve = false;

        int version = ++_blurCrossfadeVersion;
        var activeImg = _blurBackIsActive ? MediaBackgroundImageBack : MediaBackgroundImage;
        var activeImg2 = _blurBackIsActive ? MediaBackgroundImageBack2 : MediaBackgroundImage2;

        double currentOpacity = activeImg.Opacity;
        activeImg.BeginAnimation(OpacityProperty, null);
        activeImg2.BeginAnimation(OpacityProperty, null);
        activeImg.Opacity = currentOpacity;
        activeImg2.Opacity = currentOpacity;

        var fadeOut = new DoubleAnimation
        {
            From = currentOpacity,
            To = 0.0,
            Duration = TimeSpan.FromMilliseconds(400),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        Timeline.SetDesiredFrameRate(fadeOut, VNotch.Services.AnimationConfig.TargetFps);

        fadeOut.Completed += (_, _) =>
        {
            if (version != _blurCrossfadeVersion) return;
            activeImg.BeginAnimation(OpacityProperty, null);
            activeImg2.BeginAnimation(OpacityProperty, null);
            activeImg.Opacity = 0.0;
            activeImg2.Opacity = 0.0;
            activeImg.Source = null;
            activeImg2.Source = null;
        };

        activeImg.BeginAnimation(OpacityProperty, fadeOut);
        activeImg2.BeginAnimation(OpacityProperty, fadeOut);

        _lastBlurThumbnailRef = null;
        UpdateMediaBackground(info, forceRefresh: true);
    }

    private void HideMediaBackground()
    {
        ClearGlassMediaTint();
        HideMediaBackgroundOverlay();

        if (_isTimerView || _isAudioView) return;

        var defaultColorAnim = new ColorAnimation
        {
            To = Colors.White,
            Duration = TimeSpan.FromMilliseconds(400)
        };
        var defaultTextAnim = new ColorAnimation
        {
            To = Color.FromRgb(136, 136, 136),
            Duration = TimeSpan.FromMilliseconds(400)
        };

        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(defaultColorAnim, VNotch.Services.AnimationConfig.TargetFps);
        ProgressBarGradientStart.BeginAnimation(GradientStop.ColorProperty, defaultColorAnim);
        var defaultGradientEndAnim = new ColorAnimation
        {
            To = Color.FromRgb(140, 140, 140),
            Duration = TimeSpan.FromMilliseconds(400)
        };
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(defaultGradientEndAnim, VNotch.Services.AnimationConfig.TargetFps);
        ProgressBarGradientEnd.BeginAnimation(GradientStop.ColorProperty, defaultGradientEndAnim);
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(defaultTextAnim, VNotch.Services.AnimationConfig.TargetFps);
        if (IndeterminateProgress.Background is SolidColorBrush ipb && !ipb.IsFrozen) ipb.BeginAnimation(SolidColorBrush.ColorProperty, defaultColorAnim);
        if (CurrentTimeText.Foreground is SolidColorBrush st && !st.IsFrozen) st.BeginAnimation(SolidColorBrush.ColorProperty, defaultTextAnim);
        if (RemainingTimeText.Foreground is SolidColorBrush rt && !rt.IsFrozen) rt.BeginAnimation(SolidColorBrush.ColorProperty, defaultTextAnim);
        if (CompactTitleMarquee.Foreground is SolidColorBrush cmt && !cmt.IsFrozen) cmt.BeginAnimation(SolidColorBrush.ColorProperty, defaultColorAnim);

        ResetTitleGradientToWhite();

        if (VolumeIcon.Foreground is SolidColorBrush volIco && !volIco.IsFrozen) volIco.BeginAnimation(SolidColorBrush.ColorProperty, defaultTextAnim);
        VolumeBarGradientStart.BeginAnimation(GradientStop.ColorProperty, defaultColorAnim);
        var defaultVolEndAnim = new ColorAnimation
        {
            To = Color.FromRgb(140, 140, 140),
            Duration = TimeSpan.FromMilliseconds(400)
        };
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(defaultVolEndAnim, VNotch.Services.AnimationConfig.TargetFps);
        VolumeBarGradientEnd.BeginAnimation(GradientStop.ColorProperty, defaultVolEndAnim);
        var defaultVolIndEndAnim = new ColorAnimation
        {
            To = Color.FromRgb(204, 204, 204),
            Duration = TimeSpan.FromMilliseconds(400)
        };
        VolumeIndicatorGradientStart.BeginAnimation(GradientStop.ColorProperty, defaultColorAnim);
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(defaultVolIndEndAnim, VNotch.Services.AnimationConfig.TargetFps);
        VolumeIndicatorGradientEnd.BeginAnimation(GradientStop.ColorProperty, defaultVolIndEndAnim);

        void ResetUnfrozenFill(System.Windows.Shapes.Shape shape)
        {
            if (shape.Fill is SolidColorBrush brush && !brush.IsFrozen)
                brush.BeginAnimation(SolidColorBrush.ColorProperty, defaultColorAnim);
        }

        ResetUnfrozenFill(InlinePrevArrow0);
        ResetUnfrozenFill(InlinePrevArrow1);
        ResetUnfrozenFill(InlinePrevArrow2);
        ResetUnfrozenFill(InlinePauseIconPath);
        ResetUnfrozenFill(InlinePlayIconPath);
        ResetUnfrozenFill(InlineNextArrow0);
        ResetUnfrozenFill(InlineNextArrow1);
        ResetUnfrozenFill(InlineNextArrow2);
    }

    private void HideMediaBackgroundOverlay()
    {
        if (MediaBackground.Opacity == 0 && MediaBackground.Visibility != Visibility.Visible) return;

        _lastDominantColor = Colors.Transparent;
        int animationVersion = ++_mediaBackgroundAnimationVersion;
        var opacityAnim = new DoubleAnimation(0, TimeSpan.FromMilliseconds(400))
        {
            EasingFunction = _easePowerIn2
        };

        opacityAnim.Completed += (s, e) =>
        {
            if (animationVersion != _mediaBackgroundAnimationVersion)
            {
                return;
            }

            if (MediaBackground.Opacity <= 0.001)
            {
                MediaBackground.Visibility = Visibility.Collapsed;
                MediaBackground2.Visibility = Visibility.Collapsed;
            }
        };

        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(opacityAnim, VNotch.Services.AnimationConfig.TargetFps);
        MediaBackground.BeginAnimation(OpacityProperty, opacityAnim);
        MediaBackground2.BeginAnimation(OpacityProperty, opacityAnim);
    }

    private void ShowMediaBackground()
    {
        if (!_isExpanded || _isAnimating || _currentMediaInfo == null) return;

        // In Liquid Glass mode there's no blurred album-art backdrop, but the
        // accent colours (progress bar, time text, visualiser, ...) still need to
        // be re-derived after a view switch — the music view brushes get reset
        // when the file tray / secondary view is shown, so without this they stay
        // grey ("mất màu chủ đạo") on the way back.
        if (IsLiquidGlassEnabled)
        {
            UpdateMediaBackground(_currentMediaInfo, forceRefresh: true);
            return;
        }

        if (!_settings.ShowMediaArtBackground) return;
        if (!_settings.EnableBlurEffects) return;

        MediaBackground.BeginAnimation(OpacityProperty, null);
        MediaBackground2.BeginAnimation(OpacityProperty, null);
        MediaBackground.Visibility = Visibility.Visible;
        MediaBackground2.Visibility = Visibility.Visible;

        _lastMediaBackgroundFadeStartUtc = DateTime.UtcNow;
        UpdateMediaBackground(_currentMediaInfo, forceRefresh: true);
        ScheduleMediaBackgroundRecovery();
    }

    private void ScheduleMediaBackgroundRecovery()
    {
        int recoveryVersion = Interlocked.Increment(ref _mediaBackgroundRecoveryVersion);

        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            EnsureMediaBackgroundVisible(recoveryVersion);
        }));

        var recoveryTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(180)
        };

        recoveryTimer.Tick += (s, e) =>
        {
            recoveryTimer.Stop();
            EnsureMediaBackgroundVisible(recoveryVersion);
        };

        recoveryTimer.Start();
    }

    private void EnsureMediaBackgroundVisible(int recoveryVersion)
    {
        if (IsLiquidGlassEnabled || !_settings.ShowMediaArtBackground)
        {
            return;
        }

        if (!_settings.EnableBlurEffects)
        {
            return;
        }

        if (recoveryVersion != _mediaBackgroundRecoveryVersion)
        {
            return;
        }

        if (!_isExpanded || _isAnimating || _currentMediaInfo?.Thumbnail == null || !_currentMediaInfo.IsAnyMediaPlaying)
        {
            return;
        }

        bool fadeInProgress = (DateTime.UtcNow - _lastMediaBackgroundFadeStartUtc).TotalMilliseconds < 600;

        bool visibilityFailed =
            MediaBackground.Visibility != Visibility.Visible ||
            MediaBackground2.Visibility != Visibility.Visible;

        bool opacityFailed = !fadeInProgress &&
            (MediaBackground.Opacity < 0.05 || MediaBackground2.Opacity < 0.05);

        if (!visibilityFailed && !opacityFailed)
        {
            return;
        }

        MediaBackground.BeginAnimation(OpacityProperty, null);
        MediaBackground2.BeginAnimation(OpacityProperty, null);
        MediaBackground.Visibility = Visibility.Visible;
        MediaBackground2.Visibility = Visibility.Visible;

        _lastMediaBackgroundFadeStartUtc = DateTime.UtcNow;
        UpdateMediaBackground(_currentMediaInfo, forceRefresh: true);
    }

    private const string BlurCrossfadeLogTag = "BLUR-CROSSFADE";
    private int _blurCrossfadeVersion = 0;
    private int _blurTaskVersion = 0;
    private BitmapImage? _lastBlurThumbnailRef;
    private bool _suppressNextBlurDissolve = false;
    private DispatcherTimer? _blurDissolveDebounce;
    private BitmapSource? _pendingBlurResult;

    private async Task UpdateBlurredBackgroundAsync(BitmapImage thumbnail, bool allowInterimThumbnail = false)
    {
        try
        {
            if (!_settings.EnableBlurEffects)
            {
                return;
            }

            if (ReferenceEquals(thumbnail, _lastBlurThumbnailRef))
            {
                return;
            }

            bool hasExistingBlur = MediaBackgroundImage.Source != null || MediaBackgroundImageBack.Source != null;
            if (!allowInterimThumbnail && hasExistingBlur && thumbnail.PixelWidth < 200 && thumbnail.PixelHeight < 200)
            {
                RuntimeLog.Log(BlurCrossfadeLogTag, $"SKIP-SMALL thumb={thumbnail.PixelWidth}x{thumbnail.PixelHeight} (waiting for better)");
                return;
            }

            _lastBlurThumbnailRef = thumbnail;

            int taskVersion = ++_blurTaskVersion;

            RuntimeLog.Log(BlurCrossfadeLogTag, $"START taskVer={taskVersion} thumb={thumbnail.PixelWidth}x{thumbnail.PixelHeight} subjectBlur={_settings.EnableSubjectBlur}");

            BitmapSource? blurredImage;

            if (_settings.EnableSubjectBlur)
            {
                SubjectBounds? subject = null;
                try
                {
                    subject = await Task.Run(() => _mediaService.ArtworkService.GetDominantSubjectBounds(thumbnail), CancellationToken.None);
                }
                catch (Exception ex)
                {
                    RuntimeLog.Error("MEDIA-BG-SUBJECT", ex.ToString());
                }

                if (taskVersion != _blurTaskVersion)
                {
                    RuntimeLog.Log(BlurCrossfadeLogTag, $"DISCARDED (stale after subject) taskVer={taskVersion} current={_blurTaskVersion}");
                    return;
                }

                RuntimeLog.Log(BlurCrossfadeLogTag, $"subject={subject?.CenterX:F2},{subject?.CenterY:F2} w={subject?.Width:F2} h={subject?.Height:F2}");

                blurredImage = await SubjectAwareBlurService.GetSubjectBlurredAsync(thumbnail, subject);
            }
            else
            {
                blurredImage = await FastBlurService.GetBlurredImageAsync(thumbnail);
            }

            if (taskVersion != _blurTaskVersion)
            {
                RuntimeLog.Log(BlurCrossfadeLogTag, $"DISCARDED (stale after blur) taskVer={taskVersion} current={_blurTaskVersion}");
                return;
            }

            if (blurredImage == null) return;

            RuntimeLog.Log(BlurCrossfadeLogTag, $"RESULT taskVer={taskVersion} blurSize={blurredImage.PixelWidth}x{blurredImage.PixelHeight} suppress={_suppressNextBlurDissolve} frontNull={MediaBackgroundImage.Source == null} backNull={MediaBackgroundImageBack.Source == null}");

            if (_suppressNextBlurDissolve || (MediaBackgroundImage.Source == null && MediaBackgroundImageBack.Source == null))
            {
                _suppressNextBlurDissolve = false;
                _pendingBlurResult = null;
                _blurDissolveDebounce?.Stop();
                RuntimeLog.Log(BlurCrossfadeLogTag, $"APPLY-IMMEDIATE taskVer={taskVersion}");
                ApplyBlurredBackgroundImmediate(blurredImage);
                return;
            }

            RuntimeLog.Log(BlurCrossfadeLogTag, $"SCHEDULE-DISSOLVE taskVer={taskVersion}");
            ScheduleBlurredBackgroundDissolve(blurredImage);
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("MEDIA-BG-BLUR", ex.ToString());
        }
    }

    private void ApplyBlurredBackgroundImmediate(BitmapSource blurred)
    {
        ++_blurCrossfadeVersion;
        MediaBackgroundImage.BeginAnimation(OpacityProperty, null);
        MediaBackgroundImage2.BeginAnimation(OpacityProperty, null);
        MediaBackgroundImageBack.BeginAnimation(OpacityProperty, null);
        MediaBackgroundImageBack2.BeginAnimation(OpacityProperty, null);

        _blurBackIsActive = false;
        MediaBackgroundImage.Source = blurred;
        MediaBackgroundImage2.Source = blurred;
        MediaBackgroundImage.Opacity = 1.0;
        MediaBackgroundImage2.Opacity = 1.0;
        MediaBackgroundImageBack.Source = null;
        MediaBackgroundImageBack2.Source = null;
        MediaBackgroundImageBack.Opacity = 0.0;
        MediaBackgroundImageBack2.Opacity = 0.0;
    }

    private void ScheduleBlurredBackgroundDissolve(BitmapSource blurred)
    {
        _pendingBlurResult = blurred;

        var activeImg = _blurBackIsActive ? MediaBackgroundImageBack : MediaBackgroundImage;
        if (activeImg.Opacity < 0.05 || activeImg.Source == null)
        {
            _pendingBlurResult = null;
            _blurDissolveDebounce?.Stop();
            RuntimeLog.Log(BlurCrossfadeLogTag, $"CROSSFADE-START (immediate, old gone) blurSize={blurred.PixelWidth}x{blurred.PixelHeight} backIsActive={_blurBackIsActive}");
            CrossfadeBlurredBackground(blurred);
            return;
        }

        if (_blurDissolveDebounce == null)
        {
            _blurDissolveDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
            _blurDissolveDebounce.Tick += (_, _) =>
            {
                _blurDissolveDebounce.Stop();
                var pending = _pendingBlurResult;
                _pendingBlurResult = null;
                if (pending != null)
                {
                    RuntimeLog.Log(BlurCrossfadeLogTag, $"CROSSFADE-START blurSize={pending.PixelWidth}x{pending.PixelHeight} backIsActive={_blurBackIsActive}");
                    CrossfadeBlurredBackground(pending);
                }
            };
        }

        _blurDissolveDebounce.Stop();
        _blurDissolveDebounce.Start();
    }

    private bool _blurBackIsActive = false;

    private void CrossfadeBlurredBackground(BitmapSource blurred)
    {
        int version = ++_blurCrossfadeVersion;

        var activeImg = _blurBackIsActive ? MediaBackgroundImageBack : MediaBackgroundImage;
        var activeImg2 = _blurBackIsActive ? MediaBackgroundImageBack2 : MediaBackgroundImage2;
        var targetImg = _blurBackIsActive ? MediaBackgroundImage : MediaBackgroundImageBack;
        var targetImg2 = _blurBackIsActive ? MediaBackgroundImage2 : MediaBackgroundImageBack2;

        double activeOpacity = activeImg.Opacity;

        MediaBackgroundImage.BeginAnimation(OpacityProperty, null);
        MediaBackgroundImage2.BeginAnimation(OpacityProperty, null);
        MediaBackgroundImageBack.BeginAnimation(OpacityProperty, null);
        MediaBackgroundImageBack2.BeginAnimation(OpacityProperty, null);

        activeImg.Opacity = activeOpacity;
        activeImg2.Opacity = activeOpacity;
        targetImg.Opacity = 0.0;
        targetImg2.Opacity = 0.0;

        targetImg.Source = blurred;
        targetImg2.Source = blurred;

        bool oldAlreadyGone = activeOpacity < 0.05 || activeImg.Source == null;
        var dur = oldAlreadyGone ? TimeSpan.FromMilliseconds(380) : TimeSpan.FromMilliseconds(550);
        var ease = new CubicEase { EasingMode = oldAlreadyGone ? EasingMode.EaseOut : EasingMode.EaseInOut };

        var fadeIn = new DoubleAnimation { From = 0.0, To = 1.0, Duration = dur, EasingFunction = ease };
        var fadeOut = new DoubleAnimation { From = activeOpacity, To = 0.0, Duration = dur, EasingFunction = ease };

        Timeline.SetDesiredFrameRate(fadeIn, VNotch.Services.AnimationConfig.TargetFps);
        Timeline.SetDesiredFrameRate(fadeOut, VNotch.Services.AnimationConfig.TargetFps);

        fadeIn.Completed += (_, _) =>
        {
            if (version != _blurCrossfadeVersion) return;

            activeImg.Opacity = 0.0;
            activeImg2.Opacity = 0.0;
            targetImg.Opacity = 1.0;
            targetImg2.Opacity = 1.0;

            MediaBackgroundImage.BeginAnimation(OpacityProperty, null);
            MediaBackgroundImage2.BeginAnimation(OpacityProperty, null);
            MediaBackgroundImageBack.BeginAnimation(OpacityProperty, null);
            MediaBackgroundImageBack2.BeginAnimation(OpacityProperty, null);

            activeImg.Source = null;
            activeImg2.Source = null;
        };

        _blurBackIsActive = !_blurBackIsActive;

        targetImg.BeginAnimation(OpacityProperty, fadeIn);
        targetImg2.BeginAnimation(OpacityProperty, fadeIn);
        activeImg.BeginAnimation(OpacityProperty, fadeOut);
        activeImg2.BeginAnimation(OpacityProperty, fadeOut);
    }

    #endregion

    #region Title Gradient Animation

    private void AnimateTitleGradient(Color vibrantColor)
    {
        const double tintStrength = 0.15;
        var tintedWhite = Color.FromRgb(
            (byte)(255 - (255 - vibrantColor.R) * tintStrength),
            (byte)(255 - (255 - vibrantColor.G) * tintStrength),
            (byte)(255 - (255 - vibrantColor.B) * tintStrength));

        var tintedArtist = Color.FromArgb(
            191,
            tintedWhite.R, tintedWhite.G, tintedWhite.B);

        var colorAnim = new ColorAnimation { To = tintedWhite, Duration = TimeSpan.FromMilliseconds(500), EasingFunction = _easeQuadOut };
        var artistColorAnim = new ColorAnimation { To = tintedArtist, Duration = TimeSpan.FromMilliseconds(500), EasingFunction = _easeQuadOut };
        Timeline.SetDesiredFrameRate(colorAnim, VNotch.Services.AnimationConfig.TargetFps);
        Timeline.SetDesiredFrameRate(artistColorAnim, VNotch.Services.AnimationConfig.TargetFps);

        if (Resources["TrackTitleGradient"] is LinearGradientBrush titleBrush)
        {
            foreach (var stop in titleBrush.GradientStops)
            {
                stop.BeginAnimation(GradientStop.ColorProperty, colorAnim);
            }
        }

        if (Resources["TrackTitleNextGradient"] is LinearGradientBrush titleNextBrush)
        {
            foreach (var stop in titleNextBrush.GradientStops)
                stop.BeginAnimation(GradientStop.ColorProperty, colorAnim);
        }

        AnimateForegroundColor(TrackArtist, artistColorAnim);
        AnimateForegroundColor(TrackArtistNext, artistColorAnim);

        AnimateForegroundColor(LyricTextA, artistColorAnim);
        AnimateForegroundColor(LyricTextB, artistColorAnim);

        AnimatePathFillAndStroke(PrevArrow0, colorAnim);
        AnimatePathFillAndStroke(PrevArrow1, colorAnim);
        AnimatePathFillAndStroke(PrevArrow2, colorAnim);
        AnimatePathFillAndStroke(NextArrow0, colorAnim);
        AnimatePathFillAndStroke(NextArrow1, colorAnim);
        AnimatePathFillAndStroke(NextArrow2, colorAnim);
        AnimatePathFill(PauseIconPath, colorAnim);
        AnimatePathFill(PlayIconPath, colorAnim);
    }

    private static void AnimateForegroundColor(System.Windows.Controls.TextBlock tb, ColorAnimation anim)
    {
        if (tb.Foreground is SolidColorBrush brush && !brush.IsFrozen)
        {
            brush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
        }
        else
        {
            var newBrush = new SolidColorBrush(tb.Foreground is SolidColorBrush sb ? sb.Color : Colors.White);
            tb.Foreground = newBrush;
            newBrush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
        }
    }

    private static void AnimatePathFill(System.Windows.Shapes.Path path, ColorAnimation anim)
    {
        if (path.Fill is SolidColorBrush fillBrush && !fillBrush.IsFrozen)
        {
            fillBrush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
        }
        else
        {
            var newBrush = new SolidColorBrush(path.Fill is SolidColorBrush sb ? sb.Color : Colors.White);
            path.Fill = newBrush;
            newBrush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
        }
    }

    private static void AnimatePathFillAndStroke(System.Windows.Shapes.Path path, ColorAnimation anim)
    {
        AnimatePathFill(path, anim);
        if (path.Stroke is SolidColorBrush strokeBrush && !strokeBrush.IsFrozen)
        {
            strokeBrush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
        }
        else if (path.Stroke != null)
        {
            var newBrush = new SolidColorBrush(path.Stroke is SolidColorBrush sb ? sb.Color : Colors.White);
            path.Stroke = newBrush;
            newBrush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
        }
    }

    private void StopTitleGradientShift()
    {
        StopGradientAnimations("TrackTitleGradient");
        StopGradientAnimations("TrackTitleNextGradient");
        StopForegroundAnimation(TrackArtist);
        StopForegroundAnimation(TrackArtistNext);
        StopForegroundAnimation(LyricTextA);
        StopForegroundAnimation(LyricTextB);
        StopPathAnimations(PrevArrow0);
        StopPathAnimations(PrevArrow1);
        StopPathAnimations(PrevArrow2);
        StopPathAnimations(NextArrow0);
        StopPathAnimations(NextArrow1);
        StopPathAnimations(NextArrow2);
        StopPathAnimations(PauseIconPath);
        StopPathAnimations(PlayIconPath);
    }

    private void StopGradientAnimations(string resourceKey)
    {
        if (Resources[resourceKey] is not LinearGradientBrush brush) return;

        foreach (var stop in brush.GradientStops)
            stop.BeginAnimation(GradientStop.ColorProperty, null);
    }

    private static void StopForegroundAnimation(System.Windows.Controls.TextBlock textBlock)
    {
        if (textBlock.Foreground is SolidColorBrush brush && !brush.IsFrozen)
            brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
    }

    private static void StopPathAnimations(System.Windows.Shapes.Path path)
    {
        if (path.Fill is SolidColorBrush fill && !fill.IsFrozen)
            fill.BeginAnimation(SolidColorBrush.ColorProperty, null);
        if (path.Stroke is SolidColorBrush stroke && !stroke.IsFrozen)
            stroke.BeginAnimation(SolidColorBrush.ColorProperty, null);
    }

    private void ResetTitleGradientToWhite()
    {
        StopTitleGradientShift();

        var whiteAnim = new ColorAnimation
        {
            To = Colors.White,
            Duration = TimeSpan.FromMilliseconds(400)
        };

        var artistWhiteAnim = new ColorAnimation
        {
            To = Color.FromArgb(191, 255, 255, 255),
            Duration = TimeSpan.FromMilliseconds(400)
        };
        Timeline.SetDesiredFrameRate(whiteAnim, VNotch.Services.AnimationConfig.TargetFps);
        Timeline.SetDesiredFrameRate(artistWhiteAnim, VNotch.Services.AnimationConfig.TargetFps);

        if (Resources["TrackTitleGradient"] is LinearGradientBrush titleBrush)
        {
            foreach (var stop in titleBrush.GradientStops)
            {
                stop.BeginAnimation(GradientStop.ColorProperty, whiteAnim);
            }
        }

        if (Resources["TrackTitleNextGradient"] is LinearGradientBrush titleNextBrush)
        {
            foreach (var stop in titleNextBrush.GradientStops)
                stop.BeginAnimation(GradientStop.ColorProperty, whiteAnim);
        }

        AnimateForegroundColor(TrackArtist, artistWhiteAnim);
        AnimateForegroundColor(TrackArtistNext, artistWhiteAnim);

        AnimateForegroundColor(LyricTextA, artistWhiteAnim);
        AnimateForegroundColor(LyricTextB, artistWhiteAnim);

        AnimatePathFillAndStroke(PrevArrow0, whiteAnim);
        AnimatePathFillAndStroke(PrevArrow1, whiteAnim);
        AnimatePathFillAndStroke(PrevArrow2, whiteAnim);
        AnimatePathFillAndStroke(NextArrow0, whiteAnim);
        AnimatePathFillAndStroke(NextArrow1, whiteAnim);
        AnimatePathFillAndStroke(NextArrow2, whiteAnim);
        AnimatePathFill(PauseIconPath, whiteAnim);
        AnimatePathFill(PlayIconPath, whiteAnim);
    }

    #endregion
}

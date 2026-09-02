using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VNotch.Controllers;
using VNotch.Models;
using VNotch.Services;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch;

public partial class MainWindow
{
    private string _lastAnimatedTrackSignature = "";
    private string _lastColorTrackSignature = "";
    private string _lastRenderedMediaSource = "";
    private ImageSource? _pendingFlipThumbnail;
    private ImageSource? _lastAnimatedThumbnail;
    private DateTime _lastAnimationStartTime = DateTime.MinValue;
    private int _thumbnailSwitchGeneration = 0;
    private int _compactThumbnailAnimationGeneration = 0;
    private bool _thumbnailShownForCurrentTrack = false;
    private bool _isThumbnailSwitchActive = false;

    #region Media Changed Handler

    private void OnMediaChanged(object? sender, MediaInfo info)
    {
        bool isThumbnailOnlyUpdate = info.IsThumbnailOnlyUpdate;
        if (!isThumbnailOnlyUpdate)
        {
            _currentMediaInfo = info;
        }
        else if (_currentMediaInfo != null && !string.IsNullOrEmpty(info.YouTubeVideoId))
        {
            _currentMediaInfo.YouTubeVideoId = info.YouTubeVideoId;
        }

        Dispatcher.BeginInvoke(() =>
        {
            WakeFromIdle();

            if (isThumbnailOnlyUpdate && _currentMediaInfo != null)
            {
                string incomingTrack = info.CurrentTrack ?? "";
                string currentTrack = _currentMediaInfo.CurrentTrack ?? "";
                if (!string.IsNullOrEmpty(currentTrack) && !string.IsNullOrEmpty(incomingTrack) &&
                    !string.Equals(incomingTrack, currentTrack, StringComparison.OrdinalIgnoreCase))
                {
                    VNotch.Services.RuntimeLog.Log("MEDIA-THUMB", $"Rejected stale thumbnail: incoming='{incomingTrack}' current='{currentTrack}'");
                    return;
                }
            }

            var result = _mediaDisplayController.ProcessMediaUpdate(
                info, _isExpanded, _isMusicExpanded, _isMusicCompactMode, _isAnimating);

            UpdateSpotifyCanvasPlaybackState(info);

            if (result.Action == MediaDisplayAction.Ignore)
                return;

            _lastAnimatedTrackSignature = _mediaDisplayController.LastAnimatedTrackSignature;
            _lastColorTrackSignature = _mediaDisplayController.LastColorTrackSignature;
            _lastRenderedMediaSource = _mediaDisplayController.LastRenderedMediaSource;
            _lastAnimatedThumbnail = _mediaDisplayController.LastAnimatedThumbnail;
            _thumbnailShownForCurrentTrack = _mediaDisplayController.ThumbnailShownForCurrentTrack;

            string trackIdentity = result.TrackIdentity;
            string renderedSource = result.RenderedSource;

            if (result.IsNewTrack)
            {
                TransitionTrackText(result.DisplayText.Title, result.DisplayText.Artist);
            }
            else
            {
                UpdateTitleText(result.DisplayText.Title);
                UpdateArtistText(result.DisplayText.Artist);
            }
            CompactTitleMarquee.SetCurrentValue(TextBlock.TextProperty, result.DisplayText.Title);

            bool isSpotify = result.HasRealTrack && MediaPlatformExtensions.ParsePlatform(renderedSource) == MediaPlatform.Spotify;
            bool isYouTube = result.HasRealTrack && (
                MediaPlatformExtensions.ParsePlatform(renderedSource) is MediaPlatform.YouTube or MediaPlatform.Browser ||
                !string.IsNullOrEmpty(info.YouTubeVideoId) ||
                info.Platform == MediaPlatform.YouTube ||
                MediaPlatformExtensions.ParsePlatform(info.CurrentArtist) == MediaPlatform.YouTube ||
                (info.CurrentArtist != null && info.CurrentArtist.Contains("YouTube", StringComparison.OrdinalIgnoreCase))
            );

            if (result.IsNewTrack)
            {
                if (isSpotify)
                {
                    FetchLyricsForTrack(info).SafeFireAndForget("LYRICS");
                }
                else if (isYouTube)
                {
                    FetchSubtitlesForTrack(info).SafeFireAndForget("SUBTITLES");
                }
                else
                {
                    ClearLyrics();
                }
            }
            else
            {
                if (isYouTube && (!string.IsNullOrEmpty(info.YouTubeVideoId) || !string.IsNullOrEmpty(info.CurrentTrack)))
                {
                    string targetKey = !string.IsNullOrEmpty(info.YouTubeVideoId)
                        ? $"yt:{info.YouTubeVideoId}"
                        : $"yt:{info.CurrentTrack}|{info.CurrentArtist}";

                    if (targetKey != _lyricsTrackKey)
                    {
                        FetchSubtitlesForTrack(info).SafeFireAndForget("SUBTITLES");
                    }
                }
            }

            QueueCompactMarqueeRefresh();

            if (!result.HasRealTrack && !info.IsAnyMediaPlaying)
            {
                StartTitleShimmer();
            }
            else
            {
                StopTitleShimmer();
            }

            if (result.HasRealTrack)
            {
                if (result.HasThumbnail && info.Thumbnail != null)
                {
                    if (LyricsBlurImage != null && _isExpanded && _isLyricsActive)
                    {
                        if (!ReferenceEquals(LyricsBlurImage.Source, info.Thumbnail) &&
                            !ReferenceEquals(LyricsBlurImageNext.Source, info.Thumbnail))
                        {
                            LyricsBlurImageNext.BeginAnimation(OpacityProperty, null);
                            if (LyricsBlurImageNext.Opacity > 0.5 && LyricsBlurImageNext.Source != null)
                            {
                                LyricsBlurImage.Source = LyricsBlurImageNext.Source;
                            }
                            LyricsBlurImageNext.Opacity = 0;
                            LyricsBlurImageNext.Source = info.Thumbnail;
                            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(400))
                            {
                                EasingFunction = new ExponentialEase { Exponent = 4, EasingMode = EasingMode.EaseOut }
                            };
                            fadeIn.Completed += (s, e) =>
                            {
                                if (ReferenceEquals(LyricsBlurImageNext.Source, info.Thumbnail))
                                {
                                    LyricsBlurImage.Source = info.Thumbnail;
                                    LyricsBlurImageNext.BeginAnimation(OpacityProperty, null);
                                    LyricsBlurImageNext.Opacity = 0;
                                }
                            };
                            System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(fadeIn, VNotch.Services.AnimationConfig.TargetFps);
                            LyricsBlurImageNext.BeginAnimation(OpacityProperty, fadeIn);
                        }
                    }
                    else if (LyricsBlurImage != null)
                    {
                        LyricsBlurImage.Source = info.Thumbnail;
                    }

                    switch (result.ThumbnailAction)
                    {
                        case ThumbnailAction.RevealFirst:
                            ThumbnailImage.Source = info.Thumbnail;
                            CompactThumbnail.Source = info.Thumbnail;
                            PlayThumbnailRevealAnimation();
                            break;

                        case ThumbnailAction.AnimateSwitch:
                            AnimateThumbnailSwitchOnly(info.Thumbnail, force: true);
                            PlayTrackChangeBounce();
                            break;

                        case ThumbnailAction.AnimateUpdate:
                            AnimateThumbnailSwitchOnly(info.Thumbnail, force: true);
                            break;

                        case ThumbnailAction.None:
                            break;
                    }

                    ThumbnailImage.Visibility = Visibility.Visible;
                    ThumbnailFallback.Visibility = Visibility.Collapsed;

                    if (result.NeedsBackgroundUpdate)
                    {
                        UpdateMediaBackground(info);
                        if (_isExpanded)
                        {
                            ScheduleMediaBackgroundRecovery();
                        }
                    }
                }
                else if (result.IsNewTrack)
                {
                    if (ThumbnailImage.Source == null)
                    {
                        ThumbnailImage.Visibility = Visibility.Collapsed;
                        ThumbnailFallback.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        double transitionBlurRadius = _settings.EnableBlurEffects ? 12 : 0;
                        var transitionBlur = new DoubleAnimation(0, transitionBlurRadius, TimeSpan.FromMilliseconds(300))
                        {
                            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                        };
                        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(transitionBlur, VNotch.Services.AnimationConfig.TargetFps);
                        ThumbnailOutBlur.BeginAnimation(BlurEffect.RadiusProperty, transitionBlur);
                        CompactThumbnailOutBlur.BeginAnimation(BlurEffect.RadiusProperty,
                            new DoubleAnimation(0, _settings.EnableBlurEffects ? 6 : 0, TimeSpan.FromMilliseconds(300))
                            {
                                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                            });

                        if (CompactThumbnail.Source == null)
                        {
                            CompactThumbnail.Source = ThumbnailImage.Source;
                        }
                        ThumbnailImage.Visibility = Visibility.Visible;
                        ThumbnailFallback.Visibility = Visibility.Collapsed;
                    }
                }
            }
            else
            {
                if (info.IsAnyMediaPlaying)
                {
                    if (ThumbnailImage.Source != null)
                    {
                        ThumbnailImage.Visibility = Visibility.Visible;
                        ThumbnailFallback.Visibility = Visibility.Collapsed;
                    }
                    else
                    {
                        ThumbnailImage.Visibility = Visibility.Collapsed;
                        ThumbnailFallback.Visibility = Visibility.Visible;
                    }
                }
                else
                {
                    _thumbnailSwitchGeneration = _mediaDisplayController.ThumbnailSwitchGeneration;
                    CancelThumbnailSwitchAnimations();
                    ThumbnailImage.Visibility = Visibility.Collapsed;
                    ThumbnailFallback.Visibility = Visibility.Visible;
                    HideMediaBackground();
                    ClearLyrics();
                }
            }

            if ((DateTime.Now - _lastMediaActionTime).TotalMilliseconds > 500 && _isPlaying != info.IsPlaying)
            {
                _isPlaying = info.IsPlaying;
                UpdatePlayPauseIcon();
            }

            UpdateProgressTracking(info);
            UpdateMusicCompactMode(info);

            MusicViz.TrackId = info?.GetSignature() ?? "";
            MusicViz.IsPlaying = info?.IsPlaying ?? false;
        });
    }

    private void AnimateThumbnailSwitchOnly(ImageSource newThumb, bool force = false)
    {
        if (IsCountdownCompletionVisualActive)
        {
            ThumbnailImage.Source = newThumb;
            CompactThumbnail.Source = newThumb;
            SuppressCompactMediaChromeForCountdownCompletion();
            VNotch.Services.RuntimeLog.Debug("THUMB-ANIM", "skipped (countdown completion overlay active)");
            return;
        }

        if (_isAnimating)
        {
            VNotch.Services.RuntimeLog.Debug("THUMB-ANIM", () => $"queued-pending (isAnimating=true) force={force}");
            _pendingFlipThumbnail = newThumb;
            return;
        }
        if (_isThumbnailSwitchActive && !force)
        {
            VNotch.Services.RuntimeLog.Debug("THUMB-ANIM", () =>
                $"coalesced (blur morph already active, updating target) force={force}");
            ThumbnailImageNext.Source = newThumb;
            CompactThumbnailNext.Source = newThumb;
            return;
        }
        if (!force && newThumb != null && ReferenceEquals(ThumbnailImage.Source, newThumb))
        {
            VNotch.Services.RuntimeLog.Debug("THUMB-ANIM", "skipped (same reference, no force)");
            return;
        }
        if (!force && _thumbnailShownForCurrentTrack && newThumb != null)
        {
            VNotch.Services.RuntimeLog.Debug("THUMB-ANIM",
                "skipped (thumbnail already shown for current track, no force)");
            return;
        }

        VNotch.Services.RuntimeLog.Debug("THUMB-ANIM", () => $"BLUR-MORPH-START force={force}");

        _suppressOutsideClickUntilUtc = DateTime.UtcNow.AddMilliseconds(700);

        CancelThumbnailSwitchAnimations();

        var generation = ++_thumbnailSwitchGeneration;
        _isThumbnailSwitchActive = true;

        var outDur = TimeSpan.FromMilliseconds(420);
        var inDur = TimeSpan.FromMilliseconds(440);
        var overlap = TimeSpan.FromMilliseconds(180);
        var totalDur = overlap + inDur;

        double expandedPeakBlur = _settings.EnableBlurEffects ? 18.0 : 0.0;
        double compactPeakBlur = _settings.EnableBlurEffects ? 6.0 : 0.0;

        var quintOut = new QuinticEase { EasingMode = EasingMode.EaseOut };
        var cubicOut = new CubicEase { EasingMode = EasingMode.EaseOut };
        var cubicIn = new CubicEase { EasingMode = EasingMode.EaseIn };
        var sineInOut = new SineEase { EasingMode = EasingMode.EaseInOut };

        ThumbnailImageNext.Source = newThumb;
        CompactThumbnailNext.Source = newThumb;
        ThumbnailImageNext.Visibility = Visibility.Visible;
        CompactThumbnailNext.Visibility = Visibility.Visible;

        ThumbnailNextScale.ScaleX = 1.05;
        ThumbnailNextScale.ScaleY = 1.05;
        ThumbnailImageNext.Opacity = 0.0;
        ThumbnailNextBlur.Radius = expandedPeakBlur;

        CompactThumbnailNextScale.ScaleX = 1.05;
        CompactThumbnailNextScale.ScaleY = 1.05;
        CompactThumbnailNext.Opacity = 0.0;
        CompactThumbnailNextBlur.Radius = compactPeakBlur;

        ThumbnailOutScale.ScaleX = 1.0;
        ThumbnailOutScale.ScaleY = 1.0;
        ThumbnailImage.Opacity = 1.0;
        ThumbnailOutBlur.Radius = 0.0;

        CompactThumbnailOutScale.ScaleX = 1.0;
        CompactThumbnailOutScale.ScaleY = 1.0;
        CompactThumbnailOutBlur.Radius = 0.0;

        bool compactWasHidden = CompactThumbnailBorder.Visibility != Visibility.Visible ||
                                CompactThumbnailBorder.Opacity < 0.01;
        CompactThumbnail.Opacity = compactWasHidden ? 0.0 : 1.0;

        var outBlurExpanded = new DoubleAnimation(0.0, expandedPeakBlur, outDur) { EasingFunction = cubicOut };
        var outBlurCompact = new DoubleAnimation(0.0, compactPeakBlur, outDur) { EasingFunction = cubicOut };
        var outFade = new DoubleAnimation(1.0, 0.0, outDur) { EasingFunction = cubicIn };
        var outScale = new DoubleAnimation(1.0, 0.96, totalDur) { EasingFunction = sineInOut };
        Timeline.SetDesiredFrameRate(outBlurExpanded, VNotch.Services.AnimationConfig.TargetFps);
        Timeline.SetDesiredFrameRate(outBlurCompact, VNotch.Services.AnimationConfig.TargetFps);
        Timeline.SetDesiredFrameRate(outFade, VNotch.Services.AnimationConfig.TargetFps);
        Timeline.SetDesiredFrameRate(outScale, VNotch.Services.AnimationConfig.TargetFps);

        DoubleAnimationUsingKeyFrames MakeInBlur(double peak)
        {
            var a = new DoubleAnimationUsingKeyFrames();
            a.KeyFrames.Add(new DiscreteDoubleKeyFrame(peak, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            a.KeyFrames.Add(new DiscreteDoubleKeyFrame(peak, KeyTime.FromTimeSpan(overlap)));
            a.KeyFrames.Add(new EasingDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(totalDur), quintOut));
            return a;
        }

        var inBlurExpanded = MakeInBlur(expandedPeakBlur);
        var inBlurCompact = MakeInBlur(compactPeakBlur);

        var inFade = new DoubleAnimationUsingKeyFrames();
        inFade.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        inFade.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(overlap)));
        inFade.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(totalDur), cubicOut));

        var inScale = new DoubleAnimationUsingKeyFrames();
        inScale.KeyFrames.Add(new DiscreteDoubleKeyFrame(1.05, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        inScale.KeyFrames.Add(new DiscreteDoubleKeyFrame(1.05, KeyTime.FromTimeSpan(overlap)));
        inScale.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(totalDur), quintOut));

        Timeline.SetDesiredFrameRate(inBlurExpanded, VNotch.Services.AnimationConfig.TargetFps);
        Timeline.SetDesiredFrameRate(inBlurCompact, VNotch.Services.AnimationConfig.TargetFps);
        Timeline.SetDesiredFrameRate(inFade, VNotch.Services.AnimationConfig.TargetFps);
        Timeline.SetDesiredFrameRate(inScale, VNotch.Services.AnimationConfig.TargetFps);

        ThumbnailImage.BeginAnimation(OpacityProperty, outFade);
        ThumbnailOutScale.BeginAnimation(ScaleTransform.ScaleXProperty, outScale);
        ThumbnailOutScale.BeginAnimation(ScaleTransform.ScaleYProperty, outScale);
        ThumbnailOutBlur.BeginAnimation(BlurEffect.RadiusProperty, outBlurExpanded);
        ThumbnailImageNext.BeginAnimation(OpacityProperty, inFade);
        ThumbnailNextScale.BeginAnimation(ScaleTransform.ScaleXProperty, inScale);
        ThumbnailNextScale.BeginAnimation(ScaleTransform.ScaleYProperty, inScale);
        ThumbnailNextBlur.BeginAnimation(BlurEffect.RadiusProperty, inBlurExpanded);

        CompactThumbnail.BeginAnimation(OpacityProperty, outFade);
        CompactThumbnailOutScale.BeginAnimation(ScaleTransform.ScaleXProperty, outScale);
        CompactThumbnailOutScale.BeginAnimation(ScaleTransform.ScaleYProperty, outScale);
        CompactThumbnailOutBlur.BeginAnimation(BlurEffect.RadiusProperty, outBlurCompact);
        CompactThumbnailNext.BeginAnimation(OpacityProperty, inFade);
        CompactThumbnailNextScale.BeginAnimation(ScaleTransform.ScaleXProperty, inScale);
        CompactThumbnailNextScale.BeginAnimation(ScaleTransform.ScaleYProperty, inScale);
        CompactThumbnailNextBlur.BeginAnimation(BlurEffect.RadiusProperty, inBlurCompact);

        inScale.Completed += (s, e) =>
        {
            if (_thumbnailSwitchGeneration != generation) return;
            _isThumbnailSwitchActive = false;

            ThumbnailImage.BeginAnimation(OpacityProperty, null);
            ThumbnailOutScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            ThumbnailOutScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            ThumbnailOutBlur.BeginAnimation(BlurEffect.RadiusProperty, null);
            ThumbnailImageNext.BeginAnimation(OpacityProperty, null);
            ThumbnailNextScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            ThumbnailNextScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            ThumbnailNextBlur.BeginAnimation(BlurEffect.RadiusProperty, null);

            CompactThumbnail.BeginAnimation(OpacityProperty, null);
            CompactThumbnailOutScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            CompactThumbnailOutScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            CompactThumbnailOutBlur.BeginAnimation(BlurEffect.RadiusProperty, null);
            CompactThumbnailNext.BeginAnimation(OpacityProperty, null);
            CompactThumbnailNextScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            CompactThumbnailNextScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            CompactThumbnailNextBlur.BeginAnimation(BlurEffect.RadiusProperty, null);

            var finalThumb = ThumbnailImageNext.Source ?? newThumb;
            ThumbnailImage.Source = finalThumb;
            CompactThumbnail.Source = finalThumb;
            if (_isLyricsActive && LyricsBlurImage != null)
            {
                LyricsBlurImage.BeginAnimation(OpacityProperty, null);
                LyricsBlurImage.Opacity = 1;
            }
            ThumbnailImageNext.Source = null;
            CompactThumbnailNext.Source = null;
            ThumbnailImageNext.Visibility = Visibility.Collapsed;
            CompactThumbnailNext.Visibility = Visibility.Collapsed;

            ThumbnailImage.Opacity = 1.0;
            CompactThumbnail.Opacity = 1.0;
            ThumbnailImageNext.Opacity = 0.0;
            CompactThumbnailNext.Opacity = 0.0;
            ThumbnailOutScale.ScaleX = 1.0;
            ThumbnailOutScale.ScaleY = 1.0;
            CompactThumbnailOutScale.ScaleX = 1.0;
            CompactThumbnailOutScale.ScaleY = 1.0;
            ThumbnailNextScale.ScaleX = 1.0;
            ThumbnailNextScale.ScaleY = 1.0;
            CompactThumbnailNextScale.ScaleX = 1.0;
            CompactThumbnailNextScale.ScaleY = 1.0;
            ThumbnailOutBlur.Radius = 0.0;
            ThumbnailNextBlur.Radius = 0.0;
            CompactThumbnailOutBlur.Radius = 0.0;
            CompactThumbnailNextBlur.Radius = 0.0;

            if (_isMusicCompactMode && _currentMediaInfo?.IsPlaying == true
                && !_isClipboardPeekActive && !_isVolumeIndicatorActive)
            {
                ShowMusicVisualizer(animate: false);
            }
        };
    }

    private void CancelThumbnailSwitchAnimations(ImageSource? targetThumb = null)
    {
        ++_thumbnailSwitchGeneration;
        _isThumbnailSwitchActive = false;

        ThumbnailImage.BeginAnimation(OpacityProperty, null);
        ThumbnailImage.BeginAnimation(Image.SourceProperty, null);
        ThumbnailOutScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        ThumbnailOutScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        ThumbnailOutBlur.BeginAnimation(BlurEffect.RadiusProperty, null);
        ThumbnailImageNext.BeginAnimation(OpacityProperty, null);
        ThumbnailImageNext.BeginAnimation(Image.SourceProperty, null);
        ThumbnailNextScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        ThumbnailNextScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        ThumbnailNextBlur.BeginAnimation(BlurEffect.RadiusProperty, null);

        CompactThumbnail.BeginAnimation(OpacityProperty, null);
        CompactThumbnail.BeginAnimation(Image.SourceProperty, null);
        CompactThumbnailOutScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        CompactThumbnailOutScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        CompactThumbnailOutBlur.BeginAnimation(BlurEffect.RadiusProperty, null);
        CompactThumbnailNext.BeginAnimation(OpacityProperty, null);
        CompactThumbnailNext.BeginAnimation(Image.SourceProperty, null);
        CompactThumbnailNextScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        CompactThumbnailNextScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        CompactThumbnailNextBlur.BeginAnimation(BlurEffect.RadiusProperty, null);

        ThumbnailOutScale.ScaleX = 1.0;
        ThumbnailOutScale.ScaleY = 1.0;
        CompactThumbnailOutScale.ScaleX = 1.0;
        CompactThumbnailOutScale.ScaleY = 1.0;
        ThumbnailNextScale.ScaleX = 1.0;
        ThumbnailNextScale.ScaleY = 1.0;
        CompactThumbnailNextScale.ScaleX = 1.0;
        CompactThumbnailNextScale.ScaleY = 1.0;
        ThumbnailOutBlur.Radius = 0.0;
        ThumbnailNextBlur.Radius = 0.0;
        CompactThumbnailOutBlur.Radius = 0.0;
        CompactThumbnailNextBlur.Radius = 0.0;

        var overlayTarget = ThumbnailImageNext.Source ?? CompactThumbnailNext.Source;
        var resolvedThumb = targetThumb ?? overlayTarget
                            ?? ThumbnailImage.Source ?? CompactThumbnail.Source;
        if (resolvedThumb != null)
        {
            ThumbnailImage.Source = resolvedThumb;
            CompactThumbnail.Source = resolvedThumb;
        }

        ThumbnailImageNext.Source = null;
        CompactThumbnailNext.Source = null;
        ThumbnailImageNext.Visibility = Visibility.Collapsed;
        CompactThumbnailNext.Visibility = Visibility.Collapsed;
        ThumbnailImageNext.Opacity = 0.0;
        CompactThumbnailNext.Opacity = 0.0;
        ThumbnailImage.Opacity = 1.0;
        CompactThumbnail.Opacity = 1.0;
    }

    private void CancelThumbnailSwitchForExpand()
    {
        ++_thumbnailSwitchGeneration;
        _isThumbnailSwitchActive = false;

        ThumbnailImage.BeginAnimation(OpacityProperty, null);
        ThumbnailImage.BeginAnimation(Image.SourceProperty, null);
        ThumbnailOutScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        ThumbnailOutScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        ThumbnailOutBlur.BeginAnimation(BlurEffect.RadiusProperty, null);
        ThumbnailImageNext.BeginAnimation(OpacityProperty, null);
        ThumbnailImageNext.BeginAnimation(Image.SourceProperty, null);
        ThumbnailNextScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        ThumbnailNextScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        ThumbnailNextBlur.BeginAnimation(BlurEffect.RadiusProperty, null);

        CompactThumbnail.BeginAnimation(Image.SourceProperty, null);

        double compactOutScaleX = CompactThumbnailOutScale.ScaleX;
        double compactOutScaleY = CompactThumbnailOutScale.ScaleY;
        double compactOutBlur = CompactThumbnailOutBlur.Radius;
        CompactThumbnailOutScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        CompactThumbnailOutScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        CompactThumbnailOutBlur.BeginAnimation(BlurEffect.RadiusProperty, null);
        CompactThumbnailOutScale.ScaleX = compactOutScaleX;
        CompactThumbnailOutScale.ScaleY = compactOutScaleY;
        CompactThumbnailOutBlur.Radius = compactOutBlur;

        CompactThumbnailNext.BeginAnimation(OpacityProperty, null);
        CompactThumbnailNext.BeginAnimation(Image.SourceProperty, null);
        CompactThumbnailNextScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        CompactThumbnailNextScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        CompactThumbnailNextBlur.BeginAnimation(BlurEffect.RadiusProperty, null);

        double compactThumbOpacity = CompactThumbnail.Opacity;
        CompactThumbnail.BeginAnimation(OpacityProperty, null);
        CompactThumbnail.Opacity = compactThumbOpacity;

        ThumbnailOutScale.ScaleX = 1.0;
        ThumbnailOutScale.ScaleY = 1.0;
        ThumbnailNextScale.ScaleX = 1.0;
        ThumbnailNextScale.ScaleY = 1.0;
        ThumbnailOutBlur.Radius = 0.0;
        ThumbnailNextBlur.Radius = 0.0;

        var overlayTarget = ThumbnailImageNext.Source ?? CompactThumbnailNext.Source;
        var resolvedThumb = overlayTarget
                            ?? ThumbnailImage.Source ?? CompactThumbnail.Source;
        if (resolvedThumb != null)
        {
            ThumbnailImage.Source = resolvedThumb;
            CompactThumbnail.Source = resolvedThumb;
        }

        ThumbnailImageNext.Source = null;
        CompactThumbnailNext.Source = null;
        ThumbnailImageNext.Visibility = Visibility.Collapsed;
        CompactThumbnailNext.Visibility = Visibility.Collapsed;
        ThumbnailImageNext.Opacity = 0.0;
        CompactThumbnailNext.Opacity = 0.0;
        ThumbnailImage.Opacity = 1.0;
    }

    private void ResetCompactThumbnailNextLayer()
    {
        CompactThumbnailNext.BeginAnimation(OpacityProperty, null);
        CompactThumbnailNext.BeginAnimation(Image.SourceProperty, null);
        CompactThumbnailNextScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        CompactThumbnailNextScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        CompactThumbnailNextBlur.BeginAnimation(BlurEffect.RadiusProperty, null);

        CompactThumbnailNext.Source = null;
        CompactThumbnailNext.Visibility = Visibility.Collapsed;
        CompactThumbnailNext.Opacity = 0.0;
        CompactThumbnailNextScale.ScaleX = 1.0;
        CompactThumbnailNextScale.ScaleY = 1.0;
        CompactThumbnailNextBlur.Radius = 0.0;
    }

    #endregion

    #region Music Compact Mode

    private void ShowMusicVisualizer(bool animate = true, Duration? duration = null)
    {
        double currentOpacity = Math.Clamp(MusicViz.Opacity, 0.0, 1.0);

        MusicViz.Opacity = currentOpacity;
        MusicViz.BeginAnimation(OpacityProperty, null);
        MusicViz.Visibility = Visibility.Visible;

        if (!animate || currentOpacity >= 0.5)
        {
            MusicViz.Opacity = 1.0;
            return;
        }

        var vizFadeIn = MakeAnim(currentOpacity, 1.0, duration ?? _dur200, _easeQuadOut);
        vizFadeIn.Completed += (s, e) =>
        {
            MusicViz.Opacity = 1.0;
            MusicViz.BeginAnimation(OpacityProperty, null);
        };
        MusicViz.BeginAnimation(OpacityProperty, vizFadeIn);
    }

    private DispatcherTimer? _musicCompactExitDebounceTimer;
    private static readonly TimeSpan MusicCompactExitDebounceDelay = TimeSpan.FromMilliseconds(1500);

    private void UpdateMusicCompactMode(MediaInfo info)
    {
        bool shouldBeCompact = _mediaDisplayController.ShouldBeCompactMode(info);

        _collapsedWidth = GetCollapsedWidth();

        if (IsCountdownCompletionVisualActive)
        {
            CancelPendingMusicCompactExit();
            _isMusicCompactMode = shouldBeCompact;

            if (info?.Thumbnail != null)
            {
                ThumbnailImage.Source = info.Thumbnail;
                CompactThumbnail.Source = info.Thumbnail;
            }

            SuppressCompactMediaChromeForCountdownCompletion();
            return;
        }

        if (shouldBeCompact)
        {
            CancelPendingMusicCompactExit();
        }
        else if (_isMusicCompactMode)
        {
            // SMTC drops the session for a moment between tracks (playlist
            // advance, browser navigation). Defer leaving compact mode so the
            // pill doesn't flicker out of the music layout and back — and the
            // privacy dot doesn't hop between positions — on every track change.
            SchedulePendingMusicCompactExit();
            return;
        }

        ApplyMusicCompactMode(shouldBeCompact, info);
    }

    private void SchedulePendingMusicCompactExit()
    {
        if (_musicCompactExitDebounceTimer == null)
        {
            _musicCompactExitDebounceTimer = new DispatcherTimer { Interval = MusicCompactExitDebounceDelay };
            _musicCompactExitDebounceTimer.Tick += (_, _) =>
            {
                _musicCompactExitDebounceTimer!.Stop();
                var current = _currentMediaInfo;
                if (_isMusicCompactMode && !_mediaDisplayController.ShouldBeCompactMode(current))
                {
                    ApplyMusicCompactMode(shouldBeCompact: false, current);
                }
            };
        }

        if (!_musicCompactExitDebounceTimer.IsEnabled)
        {
            _musicCompactExitDebounceTimer.Start();
        }
    }

    private void CancelPendingMusicCompactExit() => _musicCompactExitDebounceTimer?.Stop();

    private void ApplyMusicCompactMode(bool shouldBeCompact, MediaInfo? info)
    {
        if (shouldBeCompact == _isMusicCompactMode)
        {
            if (shouldBeCompact)
            {
                if (info?.Thumbnail != null)
                {
                    if (_mediaDisplayController.ShouldAnimateCompactThumbnail(info))
                    {
                        _lastAnimatedTrackSignature = _mediaDisplayController.LastAnimatedTrackSignature;
                        if (!_isClipboardPeekActive)
                        {
                            AnimateThumbnailSwitchOnly(info.Thumbnail);
                            PlayTrackChangeBounce();
                        }
                    }
                }

                if (info != null)
                {
                    if (!_isClipboardPeekActive)
                    {
                        MusicViz.IsPlaying = info.IsPlaying;
                        MusicViz.TrackId = info.GetSignature();

                        if (info.IsPlaying && !_isVolumeIndicatorActive)
                        {
                            ShowMusicVisualizer(duration: _dur200);
                        }
                    }
                }
            }
            return;
        }

        _isMusicCompactMode = shouldBeCompact;

        if (_privacyIndicatorsVisible) UpdatePrivacyDotPosition(animate: true);

        if (!_isExpanded)
        {
            if (_isBluetoothNotificationVisible || _isChargingNotificationVisible || _isGreetingActive || _isAnimating)
            {
                return;
            }

            VNotch.Services.RuntimeLog.Log("NOTCH-WIDTH",
                $"UpdateMusicCompactMode -> animating to _collapsedWidth={_collapsedWidth}, shouldBeCompact={shouldBeCompact}, _isExpanded={_isExpanded}");

            var widthAnim = new DoubleAnimation(_collapsedWidth, TimeSpan.FromMilliseconds(450))
            {
                EasingFunction = _easeExpOut6
            };
            System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(widthAnim, VNotch.Services.AnimationConfig.TargetFps);
            NotchBorder.BeginAnimation(WidthProperty, widthAnim);

            if (_isMusicCompactMode)
            {
                if (info?.Thumbnail != null && !_isClipboardPeekActive)
                {
                    string compactTrackId = $"{info.CurrentTrack}|{info.CurrentArtist}";
                    if (compactTrackId != _lastAnimatedTrackSignature && !_thumbnailShownForCurrentTrack)
                    {
                        bool wasHidden = CompactThumbnailBorder.Visibility != Visibility.Visible ||
                                         CompactThumbnailBorder.Opacity < 0.01;

                        if (wasHidden)
                        {
                            CompactThumbnailScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                            CompactThumbnailScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                            CompactThumbnailBorder.BeginAnimation(OpacityProperty, null);
                            CompactThumbnailBorder.Visibility = Visibility.Visible;
                            CompactThumbnail.Source = info.Thumbnail;
                            ThumbnailImage.Source = info.Thumbnail;
                            PlayThumbnailRevealAnimation();
                        }
                        else
                        {
                            AnimateThumbnailSwitchOnly(info.Thumbnail);
                        }
                        _thumbnailShownForCurrentTrack = true;
                    }
                    else
                    {
                        ResetCompactThumbnailRestingState();
                    }
                }
                else
                {
                    ResetCompactThumbnailRestingState();
                }
                if (!_isClipboardPeekActive)
                {
                    FadeSwitch(CollapsedContent, MusicCompactContent);
                }
            }
            else
            {
                PlayCompactThumbnailExitAnimation(() =>
                {
                    if (_isMusicCompactMode) return;
                    FadeSwitch(MusicCompactContent, CollapsedContent);
                });
            }
        }
        else
        {
            MusicCompactContent.Visibility = Visibility.Collapsed;
            MusicCompactContent.Opacity = 0;
            CollapsedContent.Visibility = Visibility.Collapsed;
            CollapsedContent.Opacity = 0;
        }
    }

    #endregion

    #region Thumbnail Transition

    private void PlayThumbnailRevealAnimation()
    {
        int generation = ++_compactThumbnailAnimationGeneration;

        if (IsCountdownCompletionVisualActive)
        {
            SuppressCompactMediaChromeForCountdownCompletion();
            return;
        }

        CompactThumbnailScale.ScaleX = 0.0;
        CompactThumbnailScale.ScaleY = 0.0;
        CompactThumbnailBorder.Opacity = 0.0;

        var duration = TimeSpan.FromMilliseconds(450);
        var dur = new Duration(duration);

        var scaleAnimX = MakeAnim(0.0, 1.0, dur, _easeSoftSpring);
        var scaleAnimY = MakeAnim(0.0, 1.0, dur, _easeSoftSpring);
        Timeline.SetDesiredFrameRate(scaleAnimX, VNotch.Services.AnimationConfig.TargetFps);
        Timeline.SetDesiredFrameRate(scaleAnimY, VNotch.Services.AnimationConfig.TargetFps);

        var opacityAnim = MakeAnim(0.0, 1.0, new Duration(TimeSpan.FromMilliseconds(250)), _easeQuadOut);
        Timeline.SetDesiredFrameRate(opacityAnim, VNotch.Services.AnimationConfig.TargetFps);

        CompactThumbnailScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnimX);
        CompactThumbnailScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnimY);
        CompactThumbnailBorder.BeginAnimation(OpacityProperty, opacityAnim);

        scaleAnimX.Completed += (s, e) =>
        {
            if (generation != _compactThumbnailAnimationGeneration) return;

            CompactThumbnailScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            CompactThumbnailScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            CompactThumbnailScale.ScaleX = 1.0;
            CompactThumbnailScale.ScaleY = 1.0;
        };

        opacityAnim.Completed += (s, e) =>
        {
            if (generation != _compactThumbnailAnimationGeneration) return;

            CompactThumbnailBorder.BeginAnimation(OpacityProperty, null);
            CompactThumbnailBorder.Opacity = 1.0;
        };
    }

    private void PlayCompactThumbnailExitAnimation(Action? onCompleted = null)
    {
        int generation = ++_compactThumbnailAnimationGeneration;
        var originalOrigin = CompactThumbnailBorder.RenderTransformOrigin;
        CompactThumbnailBorder.RenderTransformOrigin = new Point(0.5, 0.5);

        CompactThumbnailScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        CompactThumbnailScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        CompactThumbnailBorder.BeginAnimation(OpacityProperty, null);
        CompactThumbnailBorder.RenderTransformOrigin = _settings.EnableDynamicIslandMode
            ? new Point(0.5, 0.5)
            : new Point(0, 0);
        CompactThumbnailScale.ScaleX = 1.0;
        CompactThumbnailScale.ScaleY = 1.0;
        CompactThumbnailBorder.Opacity = 1.0;

        var dur = new Duration(TimeSpan.FromMilliseconds(260));

        var backIn = new BackEase { EasingMode = EasingMode.EaseIn, Amplitude = 0.3 };
        var scaleAnimX = MakeAnim(1.0, 0.0, dur, backIn);
        var scaleAnimY = MakeAnim(1.0, 0.0, dur, backIn);
        Timeline.SetDesiredFrameRate(scaleAnimX, VNotch.Services.AnimationConfig.TargetFps);
        Timeline.SetDesiredFrameRate(scaleAnimY, VNotch.Services.AnimationConfig.TargetFps);

        var opacityAnim = MakeAnim(1.0, 0.0, new Duration(TimeSpan.FromMilliseconds(200)), _easeQuadIn);
        Timeline.SetDesiredFrameRate(opacityAnim, VNotch.Services.AnimationConfig.TargetFps);

        MusicViz.BeginAnimation(OpacityProperty, null);
        var vizFadeOut = MakeAnim(1.0, 0.0, new Duration(TimeSpan.FromMilliseconds(200)), _easeQuadIn);
        vizFadeOut.Completed += (s, e) =>
        {
            if (generation != _compactThumbnailAnimationGeneration) return;

            MusicViz.Visibility = Visibility.Collapsed;
            MusicViz.IsPlaying = false;
        };
        MusicViz.BeginAnimation(OpacityProperty, vizFadeOut);

        NotchScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        NotchScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        NotchShadowScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        NotchShadowScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);

        var squeezePeak = TimeSpan.FromMilliseconds(130);
        var squeezeEnd = TimeSpan.FromMilliseconds(400);

        var squeezeX = new DoubleAnimationUsingKeyFrames();
        squeezeX.KeyFrames.Add(new EasingDoubleKeyFrame(0.97,
            KeyTime.FromTimeSpan(squeezePeak), _easeQuadOut));
        squeezeX.KeyFrames.Add(new EasingDoubleKeyFrame(1.0,
            KeyTime.FromTimeSpan(squeezeEnd), _easeSoftSpring));
        Timeline.SetDesiredFrameRate(squeezeX, VNotch.Services.AnimationConfig.TargetFps);

        var squeezeY = new DoubleAnimationUsingKeyFrames();
        squeezeY.KeyFrames.Add(new EasingDoubleKeyFrame(1.02,
            KeyTime.FromTimeSpan(squeezePeak), _easeQuadOut));
        squeezeY.KeyFrames.Add(new EasingDoubleKeyFrame(1.0,
            KeyTime.FromTimeSpan(squeezeEnd), _easeSoftSpring));
        Timeline.SetDesiredFrameRate(squeezeY, VNotch.Services.AnimationConfig.TargetFps);

        NotchScale.BeginAnimation(ScaleTransform.ScaleXProperty, squeezeX);
        NotchScale.BeginAnimation(ScaleTransform.ScaleYProperty, squeezeY);
        NotchShadowScale.BeginAnimation(ScaleTransform.ScaleXProperty, squeezeX);
        NotchShadowScale.BeginAnimation(ScaleTransform.ScaleYProperty, squeezeY);

        scaleAnimX.Completed += (s, e) =>
        {
            if (generation != _compactThumbnailAnimationGeneration) return;

            CompactThumbnailBorder.Visibility = Visibility.Collapsed;
            CompactThumbnailScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            CompactThumbnailScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            CompactThumbnailBorder.BeginAnimation(OpacityProperty, null);
            CompactThumbnailScale.ScaleX = 1.0;
            CompactThumbnailScale.ScaleY = 1.0;
            CompactThumbnailBorder.Opacity = 1.0;
            CompactThumbnailBorder.RenderTransformOrigin = originalOrigin;
            onCompleted?.Invoke();
        };

        CompactThumbnailScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnimX);
        CompactThumbnailScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnimY);
        CompactThumbnailBorder.BeginAnimation(OpacityProperty, opacityAnim);
    }

    private void ResetCompactThumbnailRestingState()
    {
        ++_compactThumbnailAnimationGeneration;

        if (IsCountdownCompletionVisualActive)
        {
            SuppressCompactMediaChromeForCountdownCompletion();
            return;
        }

        if (!_isThumbnailSwitchActive)
        {
            ResetCompactThumbnailNextLayer();
        }

        CompactThumbnailScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        CompactThumbnailScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        CompactThumbnailBorder.BeginAnimation(OpacityProperty, null);
        CompactThumbnailScale.ScaleX = 1.0;
        CompactThumbnailScale.ScaleY = 1.0;
        CompactThumbnailBorder.Opacity = 1.0;
        CompactThumbnailBorder.Visibility = Visibility.Visible;
    }

    #endregion

    #region Title Shimmer Effect

    private Storyboard? _titleShimmerStoryboard;
    private bool _isShimmerActive;
    private bool _shimmerWanted;

    private void StartTitleShimmer()
    {
        _shimmerWanted = true;
        if (!AmbientAnimationsAllowed) return;
        if (_isShimmerActive) return;
        _isShimmerActive = true;

        var shimmerBrush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5),
            EndPoint = new Point(1, 0.5),
            MappingMode = BrushMappingMode.RelativeToBoundingBox
        };

        var stop0 = new GradientStop(Color.FromArgb(180, 255, 255, 255), -0.5);
        var stop1 = new GradientStop(Color.FromArgb(255, 255, 255, 255), -0.3);
        var stop2 = new GradientStop(Color.FromArgb(180, 255, 255, 255), -0.1);

        shimmerBrush.GradientStops.Add(stop0);
        shimmerBrush.GradientStops.Add(stop1);
        shimmerBrush.GradientStops.Add(stop2);

        TrackTitle.Foreground = shimmerBrush;

        var duration = TimeSpan.FromMilliseconds(2500);
        var storyboard = new Storyboard
        {
            RepeatBehavior = RepeatBehavior.Forever
        };
        Timeline.SetDesiredFrameRate(storyboard, VNotch.Services.AnimationConfig.TargetFps);

        var anim0 = new DoubleAnimation
        {
            From = -0.5,
            To = 1.1,
            Duration = duration
        };
        Storyboard.SetTarget(anim0, TrackTitle);
        Storyboard.SetTargetProperty(anim0,
            new PropertyPath("(TextBlock.Foreground).(GradientBrush.GradientStops)[0].(GradientStop.Offset)"));

        var anim1 = new DoubleAnimation
        {
            From = -0.3,
            To = 1.3,
            Duration = duration
        };
        Storyboard.SetTarget(anim1, TrackTitle);
        Storyboard.SetTargetProperty(anim1,
            new PropertyPath("(TextBlock.Foreground).(GradientBrush.GradientStops)[1].(GradientStop.Offset)"));

        var anim2 = new DoubleAnimation
        {
            From = -0.1,
            To = 1.5,
            Duration = duration
        };
        Storyboard.SetTarget(anim2, TrackTitle);
        Storyboard.SetTargetProperty(anim2,
            new PropertyPath("(TextBlock.Foreground).(GradientBrush.GradientStops)[2].(GradientStop.Offset)"));

        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(anim0, VNotch.Services.AnimationConfig.TargetFps);
        storyboard.Children.Add(anim0);
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(anim1, VNotch.Services.AnimationConfig.TargetFps);
        storyboard.Children.Add(anim1);
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(anim2, VNotch.Services.AnimationConfig.TargetFps);
        storyboard.Children.Add(anim2);

        _titleShimmerStoryboard = storyboard;
        storyboard.Begin(this, true);
    }

    private void StopTitleShimmer()
    {
        _shimmerWanted = false;
        StopTitleShimmerInternal();
    }

    private void StopTitleShimmerInternal()
    {
        if (!_isShimmerActive) return;
        _isShimmerActive = false;

        _titleShimmerStoryboard?.Stop(this);
        _titleShimmerStoryboard = null;

        if (TryFindResource("TrackTitleGradient") is Brush normalBrush)
        {
            TrackTitle.Foreground = normalBrush;
        }
        else
        {
            TrackTitle.Foreground = Brushes.White;
        }
    }

    #endregion
}

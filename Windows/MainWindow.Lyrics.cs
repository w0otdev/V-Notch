using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VNotch.Controllers;
using VNotch.Models;
using VNotch.Presenters;
using VNotch.Services;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch;

public partial class MainWindow
{
    private readonly LyricsService _lyricsService = new();
    private readonly YouTubeSubtitleService _youtubeSubtitleService = new();

    private SpotifyCanvasPresenter? _spotifyCanvasPresenter;
    private SpotifyCanvasController? _spotifyCanvasController;
    private SpotifyCanvasController SpotifyCanvasController
    {
        get
        {
            if (_spotifyCanvasController == null)
            {
                var refs = new SpotifyCanvasViewRefs
                {
                    Background = LyricsCanvasBackground,
                    Viewport = LyricsCanvasViewport,
                    Video = LyricsCanvasVideo,
                    VideoAlt = LyricsCanvasVideoAlt,
                    BrightnessOverlay = LyricsCanvasBrightnessOverlay,
                    BlurFallbackBackground = LyricsBlurBackground,
                    BlurFallbackImage = LyricsBlurImage
                };
                _spotifyCanvasPresenter = new SpotifyCanvasPresenter(refs, Dispatcher);
                _spotifyCanvasController = new SpotifyCanvasController(
                    new SpotifyCanvasService(),
                    _spotifyCanvasPresenter,
                    action =>
                    {
                        if (Dispatcher.CheckAccess()) action();
                        else Dispatcher.BeginInvoke(action);
                    });
                _spotifyCanvasController.UpdateSettings(
                    _settings.EnableSpotifyCanvas,
                    _settings.SpotifySpDc,
                    _settings.SpotifyCanvasBrightness,
                    _settings.EnableLocalOnlyMode);
                _spotifyCanvasController.UpdatePresentationContext(
                    canFadeIn: _isLyricsActive && _isExpanded && !_isAnimating,
                    blurFallbackEnabled: _settings.EnableBlurEffects && !IsLiquidGlassEnabled,
                    isLyricsActive: _isLyricsActive);
            }
            return _spotifyCanvasController;
        }
    }

    private bool _isSpotifyCanvasMediaOpen => _spotifyCanvasPresenter?.IsMediaOpen ?? false;

    private List<LyricLine>? _currentLyrics;
    private int _currentLyricIndex = -1;
    private string _lyricsTrackKey = "";
    private string _lyricsProvider = "";
    private DateTime _lastSubtitleFetchFailureTime = DateTime.MinValue;
    private int _subtitleFetchFailureCount;
    private Storyboard? _lyricsSearchShimmerStoryboard;
    private bool _isLyricsSearchVisible;
    private int _lyricsSearchTransitionVersion;
    private Brush? _lyricsLayerFadeMask;
    private int _lyricsLineTransitionVersion;
    private int _lyricsPlaceholderTransitionVersion;
    private bool _isLyricsPlaceholderActive;
    private int _lyricsFetchGeneration;
    private readonly SubtitleSearchController _subtitleSearchController = new();
    private bool _isLyricsActive
    {
        get => _notchState.IsLyricsActive;
        set => _notchState.IsLyricsActive = value;
    }
    private string _lastKnownYouTubeVideoId = "";

    private bool IsSpotifyCanvasSurfaceVisible =>
        _isLyricsActive &&
        !_isSecondaryView &&
        !_isTimerView &&
        !_isAudioView &&
        ExpandedContent?.Visibility == Visibility.Visible;

    private enum SyncedTextSource { None, SpotifyLyrics, YouTubeSubtitles }
    private SyncedTextSource _syncedTextSource = SyncedTextSource.None;

    private async Task FetchLyricsForTrack(MediaInfo info)
    {
        _subtitleSearchController.Invalidate();
        if (!_settings.EnableSpotifyLyrics || !_settings.EnableOnlineLyrics || _settings.EnableLocalOnlyMode)
        {
            ResetSpotifyCanvas();
            HideLyricsWidget();
            return;
        }

        int generation = ++_lyricsFetchGeneration;
        string trackKey = $"{info.CurrentTrack}|{info.CurrentArtist}";

        if (trackKey == _lyricsTrackKey && (_currentLyrics != null && _currentLyrics.Count > 0 || !_isLyricsActive)) return;
        _lyricsTrackKey = trackKey;
        _syncedTextSource = SyncedTextSource.SpotifyLyrics;

        if (info.Platform != MediaPlatform.Spotify)
        {
            ResetSpotifyCanvas();
            HideLyricsWidget();
            return;
        }

        StartSpotifyCanvasFetch(info, trackKey);

        if (_isLyricsActive || _isExpanded)
        {
            _currentLyrics = null;
            _currentLyricIndex = -1;
            _lyricsProvider = "";
            ShowLyricsSearchState(isYouTube: false);
        }

        int durationSec = (int)info.Duration.TotalSeconds;
        if (durationSec <= 0) durationSec = 240;

        LyricsResult? lyrics = await _lyricsService.FetchSyncedLyricsAsync(
            info.CurrentTrack, info.CurrentArtist, durationSec);

        if (generation != _lyricsFetchGeneration || trackKey != _lyricsTrackKey) return;

        ApplySyncedLines(lyrics?.Lines, info, provider: lyrics?.Provider);
    }

    private void StartSpotifyCanvasFetch(MediaInfo info, string trackKey)
    {
        _ = trackKey;
        SpotifyCanvasController.UpdateTrack(info);
    }

    private void RefreshSpotifyCanvasForCurrentTrack()
    {
        _spotifyCanvasController?.RefreshForCurrentTrack();
    }

    private void ShowSpotifyCanvasBackgroundIfAvailable()
    {
        _spotifyCanvasController?.SetSurfaceVisibility(IsSpotifyCanvasSurfaceVisible);
    }

    private void SuspendSpotifyCanvasLifecycle()
    {
        _spotifyCanvasController?.SetSurfaceVisibility(false);
    }

    private void ResumeSpotifyCanvasLifecycle()
    {
        _spotifyCanvasController?.SetSurfaceVisibility(IsSpotifyCanvasSurfaceVisible);
    }

    private void DisposeSpotifyCanvasLifecycle()
    {
        _spotifyCanvasController?.Dispose();
        _spotifyCanvasController = null;
        _spotifyCanvasPresenter = null;
    }

    internal void UpdateSpotifyCanvasPresentationContext()
    {
        if (_spotifyCanvasController == null) return;

        bool canFadeIn = _isLyricsActive &&
                         _isExpanded &&
                         !_isAnimating &&
                         !_isTimerView &&
                         !_isAudioView &&
                         !_isSecondaryView;

        bool blurFallbackEnabled = _settings.EnableBlurEffects && !IsLiquidGlassEnabled;

        _spotifyCanvasController.UpdatePresentationContext(
            canFadeIn: canFadeIn,
            blurFallbackEnabled: blurFallbackEnabled,
            isLyricsActive: _isLyricsActive);
    }

    private void ApplySpotifyCanvasBrightness()
    {
        _spotifyCanvasController?.UpdateSettings(
            _settings.EnableSpotifyCanvas,
            _settings.SpotifySpDc,
            _settings.SpotifyCanvasBrightness,
            _settings.EnableLocalOnlyMode);
        UpdateSpotifyCanvasPresentationContext();
    }

    private void HideLyricsBlurForCanvas()
    {
        _spotifyCanvasPresenter?.HideBlurFallback();
    }

    private void RestoreLyricsBlurFallback()
    {
        _spotifyCanvasPresenter?.RestoreBlurFallback();
    }

    private void ResetSpotifyCanvas()
    {
        _spotifyCanvasController?.Reset();
    }

    private void UpdateSpotifyCanvasPlaybackState(MediaInfo info)
    {
        _spotifyCanvasController?.UpdatePlaybackState(info.IsPlaying);
    }

    private void FadeInSpotifyCanvasBackgroundIfReady()
    {
        UpdateSpotifyCanvasPresentationContext();
        _spotifyCanvasPresenter?.FadeInBackgroundIfReady();
    }

    private async Task FetchSubtitlesForTrack(MediaInfo info, bool force = false)
    {
        if (!_settings.EnableYouTubeSubtitles || _settings.EnableLocalOnlyMode)
        {
            HideLyricsWidget();
            return;
        }

        string videoId = info.YouTubeVideoId ?? "";

        // Preserve resolved "yt:{id}" key and avoid overwriting with unresolved
        // fallback when MediaChanged fires with empty videoId while subtitles are already loaded.
        if (string.IsNullOrEmpty(videoId) && !string.IsNullOrEmpty(info.CurrentTrack)
            && !force && _lyricsTrackKey.StartsWith("yt:", StringComparison.Ordinal)
            && !_lyricsTrackKey.StartsWith("yt-lrc:", StringComparison.Ordinal)
            && _currentLyrics != null && _currentLyrics.Count > 0)
        {
            return;
        }

        string trackKey = !string.IsNullOrEmpty(videoId) ? $"yt:{videoId}" : $"yt-lrc:{info.CurrentTrack}|{info.CurrentArtist}";

        if (trackKey != _lyricsTrackKey)
        {
            _subtitleFetchFailureCount = 0;
            _lastSubtitleFetchFailureTime = DateTime.MinValue;
        }
        else if (!force)
        {
            if (_currentLyrics != null && _currentLyrics.Count > 0)
            {
                return;
            }

            if (!_isLyricsActive)
            {
                bool cooldownExpired = (DateTime.UtcNow - _lastSubtitleFetchFailureTime) > TimeSpan.FromSeconds(5);
                if (!cooldownExpired || _subtitleFetchFailureCount >= 4)
                {
                    return;
                }
            }
        }

        _lyricsTrackKey = trackKey;
        _syncedTextSource = SyncedTextSource.YouTubeSubtitles;

        // Reset current lyrics immediately so UpdateLyricsDisplay won't run old subtitles for the new track
        _currentLyrics = null;
        _currentLyricIndex = -1;
        _lyricsProvider = "";

        await _subtitleSearchController.SearchAsync(
            () => ResolveSubtitleVideoIdAsync(info, videoId),
            id => _youtubeSubtitleService.FetchSubtitlesAsync(id, force: force),
            id =>
            {
                info.YouTubeVideoId = id;
                trackKey = $"yt:{id}";
                _lyricsTrackKey = trackKey;
                if (_isLyricsActive || _isExpanded)
                    ShowLyricsSearchState(isYouTube: true);
            },
            subtitles =>
            {
                if (trackKey != _lyricsTrackKey) return;

                if (subtitles == null || subtitles.Count == 0)
                {
                    _lastSubtitleFetchFailureTime = DateTime.UtcNow;
                    _subtitleFetchFailureCount++;
                }
                else
                {
                    _subtitleFetchFailureCount = 0;
                    _lastSubtitleFetchFailureTime = DateTime.MinValue;
                }

                ApplySyncedLines(subtitles, info, provider: "YouTube");
            });
    }

    private async Task<string> ResolveSubtitleVideoIdAsync(MediaInfo info, string videoId)
    {
        if (!string.IsNullOrEmpty(videoId)) return videoId;

        if (!string.IsNullOrEmpty(info.CurrentTrack))
        {
            var lookup = await _mediaService.TryGetYouTubeVideoIdWithInfoAsync(
                info.CurrentTrack, info.CurrentArtist, System.Threading.CancellationToken.None);
            if (!string.IsNullOrEmpty(lookup?.Id)) return lookup.Id;
        }

        string CurrentVideoId() =>
            string.Equals(info.CurrentTrack, _currentMediaInfo?.CurrentTrack, StringComparison.Ordinal)
                ? _currentMediaInfo?.YouTubeVideoId ?? ""
                : "";

        videoId = CurrentVideoId();
        if (!string.IsNullOrEmpty(videoId)) return videoId;

        // Allow the browser extension to deliver metadata without flashing the
        // search widget when no usable ID arrives during this grace period.
        await Task.Delay(350);
        return CurrentVideoId();
    }

    private void ApplySyncedLines(
        List<LyricLine>? lines,
        MediaInfo info,
        string? provider = null)
    {
        if (lines == null || lines.Count == 0)
        {
            _currentLyrics = null;
            _currentLyricIndex = -1;
            _lyricsProvider = "";
            ResetSpotifyCanvas();
            HideLyricsSearchState(immediate: true);
            if (_isLyricsActive)
                HideLyricsWidget();
            return;
        }

        _currentLyrics = lines;
        _currentLyricIndex = -1;
        _lyricsProvider = provider?.Trim() ?? "";

        var thumb = info.Thumbnail ?? _currentMediaInfo?.Thumbnail;
        if (thumb != null)
        {
            AnimateLyricsBlurImageSwitch(thumb);
        }

        // Always call ShowLyricsWidget — it now handles both the first-time and already-active cases,
        // ensuring CalendarWidget is collapsed and LyricsWidget is visible even after races.
        ShowLyricsWidget();

        Dispatcher.Invoke(() =>
        {
            bool transitionFromSearch =
                _isLyricsSearchVisible && LyricsSearchPanel.Visibility == Visibility.Visible;
            HideLyricsSearchState(immediate: true);

            int idx = FindCurrentLyricIndex();
            if (idx >= 0)
            {
                bool fromPlaceholder = LyricsPlaceholderPanel != null &&
                    LyricsPlaceholderPanel.Visibility == Visibility.Visible &&
                    LyricsPlaceholderPanel.Opacity > 0.05;

                _currentLyricIndex = idx;
                HideLyricsPlaceholder();
                AnimateLyricLine(_currentLyrics[idx].Text, transitionFromSearch, transitionFromPlaceholder: fromPlaceholder);
            }
            else
            {
                string placeholderTitle = !string.IsNullOrWhiteSpace(info.CurrentTrack) ? info.CurrentTrack : "";
                string placeholderArtist = (!string.IsNullOrWhiteSpace(info.CurrentArtist) &&
                    MediaPlatformExtensions.ParsePlatform(info.CurrentArtist) is not (MediaPlatform.YouTube or MediaPlatform.Browser))
                    ? info.CurrentArtist
                    : "";
                ShowLyricsPlaceholder(
                    placeholderTitle,
                    placeholderArtist,
                    _lyricsProvider,
                    transitionFromSearch);
            }
        });
    }

    private void ShowLyricsSearchState(bool isYouTube)
    {
        void ShowState()
        {
            if (_currentMediaInfo?.Thumbnail != null)
            {
                AnimateLyricsBlurImageSwitch(_currentMediaInfo.Thumbnail);
            }

            ShowLyricsWidget();

            string searchText = Loc.Get(isYouTube ? "subtitles.searching" : "lyrics.searching");
            if (_isLyricsSearchVisible && LyricsSearchPanel.Visibility == Visibility.Visible)
            {
                LyricsSearchText.Text = searchText;
                if (_lyricsSearchShimmerStoryboard == null)
                    StartLyricsSearchShimmer();
                return;
            }

            LyricTextA.BeginAnimation(OpacityProperty, null);
            LyricTextB.BeginAnimation(OpacityProperty, null);
            LyricTextA.Opacity = 0;
            LyricTextB.Opacity = 0;
            LyricTextA.Text = "";
            LyricTextB.Text = "";

            HideLyricsPlaceholder(immediate: true);

            LyricsSearchText.Text = searchText;
            _isLyricsSearchVisible = true;
            int transitionVersion = ++_lyricsSearchTransitionVersion;
            TranslateTransform searchTranslate = GetLyricsSearchTransform();
            LyricsSearchPanel.BeginAnimation(OpacityProperty, null);
            searchTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            LyricsSearchPanel.Visibility = Visibility.Visible;

            if (AnimationConfig.ReduceMotion)
            {
                LyricsSearchPanel.Opacity = 1;
                searchTranslate.Y = 0;
            }
            else
            {
                LyricsSearchPanel.Opacity = 0;
                searchTranslate.Y = 8;

                var dur = new Duration(TimeSpan.FromMilliseconds(240));
                var easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };
                var fadeIn = new DoubleAnimation(0, 1, dur) { EasingFunction = easeOut };
                var slideIn = new DoubleAnimation(8, 0, dur) { EasingFunction = easeOut };
                Timeline.SetDesiredFrameRate(fadeIn, AnimationConfig.TargetFps);
                Timeline.SetDesiredFrameRate(slideIn, AnimationConfig.TargetFps);
                fadeIn.Completed += (s, e) =>
                {
                    if (!_isLyricsSearchVisible || transitionVersion != _lyricsSearchTransitionVersion)
                        return;
                    LyricsSearchPanel.BeginAnimation(OpacityProperty, null);
                    LyricsSearchPanel.Opacity = 1;
                };
                slideIn.Completed += (s, e) =>
                {
                    if (!_isLyricsSearchVisible || transitionVersion != _lyricsSearchTransitionVersion)
                        return;
                    searchTranslate.BeginAnimation(TranslateTransform.YProperty, null);
                    searchTranslate.Y = 0;
                };
                LyricsSearchPanel.BeginAnimation(OpacityProperty, fadeIn);
                searchTranslate.BeginAnimation(TranslateTransform.YProperty, slideIn);
            }

            StartLyricsSearchShimmer();
        }

        if (Dispatcher.CheckAccess())
            ShowState();
        else
            Dispatcher.Invoke(ShowState);
    }

    private void HideLyricsSearchState(bool immediate = false)
    {
        _isLyricsSearchVisible = false;
        int transitionVersion = ++_lyricsSearchTransitionVersion;
        TranslateTransform searchTranslate = GetLyricsSearchTransform();

        void FinishHide()
        {
            if (_isLyricsSearchVisible)
                return;

            StopLyricsSearchShimmer();
            if (LyricsSearchPanel != null)
            {
                LyricsSearchPanel.BeginAnimation(OpacityProperty, null);
                searchTranslate?.BeginAnimation(TranslateTransform.YProperty, null);
                LyricsSearchPanel.Opacity = 0;
                if (searchTranslate != null) searchTranslate.Y = 0;
                LyricsSearchPanel.Visibility = Visibility.Collapsed;
            }
        }

        if (AnimationConfig.ReduceMotion || immediate || LyricsSearchPanel == null || LyricsSearchPanel.Visibility != Visibility.Visible)
        {
            FinishHide();
            return;
        }

        var duration = new Duration(TimeSpan.FromMilliseconds(160));
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var fadeOut = new DoubleAnimation(LyricsSearchPanel.Opacity, 0, duration)
        {
            EasingFunction = ease
        };
        var slideOut = new DoubleAnimation(searchTranslate.Y, -8, duration)
        {
            EasingFunction = ease
        };
        fadeOut.Completed += (s, e) => FinishHide();
        Timeline.SetDesiredFrameRate(fadeOut, AnimationConfig.TargetFps);
        Timeline.SetDesiredFrameRate(slideOut, AnimationConfig.TargetFps);
        LyricsSearchPanel.BeginAnimation(OpacityProperty, fadeOut);
        searchTranslate.BeginAnimation(TranslateTransform.YProperty, slideOut);
    }

    private TranslateTransform GetLyricsSearchTransform()
    {
        if (LyricsSearchPanel.RenderTransform is TranslateTransform transform)
            return transform;

        var fallback = new TranslateTransform();
        LyricsSearchPanel.RenderTransform = fallback;
        return fallback;
    }

    private void StartLyricsSearchShimmer()
    {
        StopLyricsSearchShimmer();

        if (AnimationConfig.ReduceMotion)
        {
            LyricsSearchText.Foreground = new SolidColorBrush(Color.FromArgb(190, 255, 255, 255));
            return;
        }

        var shimmerBrush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5),
            EndPoint = new Point(1, 0.5),
            MappingMode = BrushMappingMode.RelativeToBoundingBox
        };
        shimmerBrush.GradientStops.Add(new GradientStop(Color.FromArgb(105, 255, 255, 255), -0.45));
        shimmerBrush.GradientStops.Add(new GradientStop(Color.FromArgb(245, 255, 255, 255), -0.20));
        shimmerBrush.GradientStops.Add(new GradientStop(Color.FromArgb(105, 255, 255, 255), 0.05));
        LyricsSearchText.Foreground = shimmerBrush;

        var duration = TimeSpan.FromMilliseconds(1600);
        var storyboard = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        Timeline.SetDesiredFrameRate(storyboard, AnimationConfig.TargetFps);

        AddShimmerAnimation(storyboard, 0, -0.45, 1.05, duration);
        AddShimmerAnimation(storyboard, 1, -0.20, 1.30, duration);
        AddShimmerAnimation(storyboard, 2, 0.05, 1.55, duration);

        _lyricsSearchShimmerStoryboard = storyboard;
        storyboard.Begin(this, true);
    }

    private void AddShimmerAnimation(
        Storyboard storyboard,
        int gradientStopIndex,
        double from,
        double to,
        TimeSpan duration)
    {
        var animation = new DoubleAnimation(from, to, duration);
        Timeline.SetDesiredFrameRate(animation, AnimationConfig.TargetFps);
        Storyboard.SetTarget(animation, LyricsSearchText);
        Storyboard.SetTargetProperty(animation,
            new PropertyPath($"(TextBlock.Foreground).(GradientBrush.GradientStops)[{gradientStopIndex}].(GradientStop.Offset)", Array.Empty<object>()));
        storyboard.Children.Add(animation);
    }

    private void StopLyricsSearchShimmer()
    {
        _lyricsSearchShimmerStoryboard?.Stop(this);
        _lyricsSearchShimmerStoryboard = null;
    }

    private int FindCurrentLyricIndex()
    {
        if (_currentLyrics == null || _currentLyrics.Count == 0) return -1;
        if (_currentMediaInfo == null) return -1;

        TimeSpan position;
        if (_currentMediaInfo.IsPlaying && _currentMediaInfo.Duration.TotalSeconds > 0)
        {
            var frame = _progressEngine.GetUiFrame();
            if (frame.Duration.TotalSeconds > 0 && frame.State == ProgressState.Playing)
            {
                position = frame.Position;
            }
            else
            {
                var elapsed = DateTimeOffset.UtcNow - _currentMediaInfo.LastUpdated.ToUniversalTime();
                if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
                if (elapsed > TimeSpan.FromMinutes(10)) elapsed = TimeSpan.FromMinutes(10);
                position = _currentMediaInfo.Position + elapsed;
            }
        }
        else
        {
            position = _currentMediaInfo.Position;
        }

        return FindLyricIndex(position);
    }

    private TranslateTransform GetLyricsPlaceholderTransform()
    {
        if (LyricsPlaceholderTranslate != null)
            return LyricsPlaceholderTranslate;

        if (LyricsPlaceholderPanel?.RenderTransform is TranslateTransform transform)
            return transform;

        var fallback = new TranslateTransform();
        if (LyricsPlaceholderPanel != null)
            LyricsPlaceholderPanel.RenderTransform = fallback;
        return fallback;
    }

    private void ShowLyricsPlaceholder(
        string title,
        string artist,
        string provider,
        bool transitionFromSearch = false)
    {
        if (LyricsPlaceholderPanel == null) return;

        // Ensure search panel is immediately collapsed so it cannot overlap placeholder text
        HideLyricsSearchState(immediate: true);

        bool isAlreadyShowing = _isLyricsPlaceholderActive &&
                                LyricsPlaceholderPanel.Visibility == Visibility.Visible;
        bool isSameTrack = LyricsPlaceholderTitle.Text == title && LyricsPlaceholderArtist.Text == artist;

        if (isAlreadyShowing && isSameTrack)
        {
            _isLyricsPlaceholderActive = true;
            return;
        }

        _isLyricsPlaceholderActive = true;
        int transitionVersion = ++_lyricsPlaceholderTransitionVersion;
        var transform = GetLyricsPlaceholderTransform();

        // 1. If active lyric lines are currently visible (e.g. transitioning into an instrumental gap),
        // gracefully fade and slide them out instead of snapping to 0.
        bool hasActiveLyricA = LyricTextA != null && LyricTextA.Opacity > 0.05;
        bool hasActiveLyricB = LyricTextB != null && LyricTextB.Opacity > 0.05;
        bool hasActiveLyrics = hasActiveLyricA || hasActiveLyricB;

        if (hasActiveLyrics && !AnimationConfig.ReduceMotion)
        {
            var lyricEase = new CubicEase { EasingMode = EasingMode.EaseIn };
            var lyricDur = new Duration(TimeSpan.FromMilliseconds(220));

            if (hasActiveLyricA)
            {
                LyricTextA!.BeginAnimation(OpacityProperty, null);
                LyricTranslateA?.BeginAnimation(TranslateTransform.YProperty, null);
                var fadeOutA = new DoubleAnimation(LyricTextA.Opacity, 0, lyricDur) { EasingFunction = lyricEase };
                var slideOutA = new DoubleAnimation(LyricTranslateA?.Y ?? 0, -10, lyricDur) { EasingFunction = lyricEase };
                Timeline.SetDesiredFrameRate(fadeOutA, AnimationConfig.TargetFps);
                Timeline.SetDesiredFrameRate(slideOutA, AnimationConfig.TargetFps);
                fadeOutA.Completed += (s, e) =>
                {
                    LyricTextA.BeginAnimation(OpacityProperty, null);
                    LyricTextA.Opacity = 0;
                    LyricTextA.Text = "";
                };
                LyricTextA.BeginAnimation(OpacityProperty, fadeOutA);
                LyricTranslateA?.BeginAnimation(TranslateTransform.YProperty, slideOutA);
            }

            if (hasActiveLyricB)
            {
                LyricTextB!.BeginAnimation(OpacityProperty, null);
                LyricTranslateB?.BeginAnimation(TranslateTransform.YProperty, null);
                var fadeOutB = new DoubleAnimation(LyricTextB.Opacity, 0, lyricDur) { EasingFunction = lyricEase };
                var slideOutB = new DoubleAnimation(LyricTranslateB?.Y ?? 0, -10, lyricDur) { EasingFunction = lyricEase };
                Timeline.SetDesiredFrameRate(fadeOutB, AnimationConfig.TargetFps);
                Timeline.SetDesiredFrameRate(slideOutB, AnimationConfig.TargetFps);
                fadeOutB.Completed += (s, e) =>
                {
                    LyricTextB.BeginAnimation(OpacityProperty, null);
                    LyricTextB.Opacity = 0;
                    LyricTextB.Text = "";
                };
                LyricTextB.BeginAnimation(OpacityProperty, fadeOutB);
                LyricTranslateB?.BeginAnimation(TranslateTransform.YProperty, slideOutB);
            }
        }
        else
        {
            if (LyricTextA != null)
            {
                LyricTextA.BeginAnimation(OpacityProperty, null);
                LyricTranslateA?.BeginAnimation(TranslateTransform.YProperty, null);
                LyricTextA.Opacity = 0;
                LyricTextA.Text = "";
            }
            if (LyricTextB != null)
            {
                LyricTextB.BeginAnimation(OpacityProperty, null);
                LyricTranslateB?.BeginAnimation(TranslateTransform.YProperty, null);
                LyricTextB.Opacity = 0;
                LyricTextB.Text = "";
            }
        }

        // 2. Set new content
        LyricsPlaceholderTitle.Text = title;
        LyricsPlaceholderArtist.Text = artist;
        LyricsPlaceholderProvider.Text = string.IsNullOrWhiteSpace(provider)
            ? ""
            : $"Provided by: {provider}";
        LyricsPlaceholderProvider.Visibility = string.IsNullOrWhiteSpace(provider)
            ? Visibility.Collapsed
            : Visibility.Visible;

        LyricsPlaceholderPanel.Visibility = Visibility.Visible;
        LyricsPlaceholderPanel.BeginAnimation(OpacityProperty, null);
        transform.BeginAnimation(TranslateTransform.YProperty, null);

        if (AnimationConfig.ReduceMotion)
        {
            LyricsPlaceholderPanel.Opacity = 1.0;
            transform.Y = 0;
            return;
        }

        // 3. Coordinate appear animation
        double startY = 6;
        if (transitionFromSearch)
        {
            startY = 10;
        }
        else if (hasActiveLyrics)
        {
            startY = 8;
        }
        LyricsPlaceholderPanel.Opacity = 0;
        transform.Y = startY;

        // A null BeginTime disables a WPF animation instead of starting it immediately.
        TimeSpan delay = TimeSpan.Zero;
        if (transitionFromSearch)
        {
            delay = TimeSpan.FromMilliseconds(70);
        }
        else if (hasActiveLyrics)
        {
            delay = TimeSpan.FromMilliseconds(60);
        }

        var duration = new Duration(TimeSpan.FromMilliseconds(320));
        var easeOut = new ExponentialEase { Exponent = 5, EasingMode = EasingMode.EaseOut };

        var fadeIn = new DoubleAnimation(0, 1, duration)
        {
            EasingFunction = easeOut,
            BeginTime = delay
        };
        var slideIn = new DoubleAnimation(startY, 0, duration)
        {
            EasingFunction = easeOut,
            BeginTime = delay
        };

        fadeIn.Completed += (s, e) =>
        {
            if (!_isLyricsPlaceholderActive || transitionVersion != _lyricsPlaceholderTransitionVersion) return;
            LyricsPlaceholderPanel.BeginAnimation(OpacityProperty, null);
            LyricsPlaceholderPanel.Opacity = 1.0;
        };
        slideIn.Completed += (s, e) =>
        {
            if (!_isLyricsPlaceholderActive || transitionVersion != _lyricsPlaceholderTransitionVersion) return;
            transform.BeginAnimation(TranslateTransform.YProperty, null);
            transform.Y = 0;
        };

        Timeline.SetDesiredFrameRate(fadeIn, AnimationConfig.TargetFps);
        Timeline.SetDesiredFrameRate(slideIn, AnimationConfig.TargetFps);

        LyricsPlaceholderPanel.BeginAnimation(OpacityProperty, fadeIn);
        transform.BeginAnimation(TranslateTransform.YProperty, slideIn);
    }

    private void HideLyricsPlaceholder(bool immediate = false)
    {
        if (LyricsPlaceholderPanel == null) return;
        _isLyricsPlaceholderActive = false;
        if (LyricsPlaceholderPanel.Visibility == Visibility.Collapsed && LyricsPlaceholderPanel.Opacity < 0.01) return;

        int transitionVersion = ++_lyricsPlaceholderTransitionVersion;
        var transform = GetLyricsPlaceholderTransform();

        void FinishHide()
        {
            if (_isLyricsPlaceholderActive || transitionVersion != _lyricsPlaceholderTransitionVersion) return;
            LyricsPlaceholderPanel.BeginAnimation(OpacityProperty, null);
            transform.BeginAnimation(TranslateTransform.YProperty, null);
            LyricsPlaceholderPanel.Opacity = 0;
            transform.Y = 0;
            LyricsPlaceholderPanel.Visibility = Visibility.Collapsed;
        }

        if (AnimationConfig.ReduceMotion || immediate)
        {
            FinishHide();
            return;
        }

        var duration = new Duration(TimeSpan.FromMilliseconds(220));
        var easeIn = new CubicEase { EasingMode = EasingMode.EaseIn };

        double currentOpacity = LyricsPlaceholderPanel.Opacity;
        if (currentOpacity <= 0.01)
        {
            FinishHide();
            return;
        }

        var fadeOut = new DoubleAnimation(currentOpacity, 0, duration)
        {
            EasingFunction = easeIn
        };
        var slideOut = new DoubleAnimation(transform.Y, -8, duration)
        {
            EasingFunction = easeIn
        };

        fadeOut.Completed += (s, e) => FinishHide();

        Timeline.SetDesiredFrameRate(fadeOut, AnimationConfig.TargetFps);
        Timeline.SetDesiredFrameRate(slideOut, AnimationConfig.TargetFps);

        LyricsPlaceholderPanel.BeginAnimation(OpacityProperty, fadeOut);
        transform.BeginAnimation(TranslateTransform.YProperty, slideOut);
    }

    private void UpdateLyricsDisplay()
    {
        if (!_isLyricsActive || _isLyricsSearchVisible || _currentLyrics == null || _currentLyrics.Count == 0)
            return;

        TimeSpan position;
        if (_currentMediaInfo != null && _currentMediaInfo.IsPlaying && _currentMediaInfo.Duration.TotalSeconds > 0)
        {
            var frame = _progressEngine.GetUiFrame();
            if (frame.Duration.TotalSeconds > 0 && frame.State == ProgressState.Playing)
            {
                position = frame.Position;
            }
            else
            {
                var elapsed = DateTimeOffset.UtcNow - _currentMediaInfo.LastUpdated.ToUniversalTime();
                if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
                if (elapsed > TimeSpan.FromMinutes(10)) elapsed = TimeSpan.FromMinutes(10);
                position = _currentMediaInfo.Position + elapsed;
            }
            if (position > _currentMediaInfo.Duration) position = _currentMediaInfo.Duration;
        }
        else if (_currentMediaInfo != null)
        {
            position = _currentMediaInfo.Position;
        }
        else
        {
            var frame = _progressEngine.GetUiFrame();
            if (frame.Duration.TotalSeconds <= 0) return;
            position = frame.Position;
        }

        int newIndex = FindLyricIndex(position);

        if (newIndex != _currentLyricIndex && newIndex >= 0)
        {
            bool fromPlaceholder = _currentLyricIndex < 0 ||
                (LyricsPlaceholderPanel != null && LyricsPlaceholderPanel.Visibility == Visibility.Visible && LyricsPlaceholderPanel.Opacity > 0.05);

            if (_currentLyricIndex < 0)
                HideLyricsPlaceholder();

            _currentLyricIndex = newIndex;
            string lineText = _currentLyrics[newIndex].Text;
            AnimateLyricLine(lineText, transitionFromSearch: false, transitionFromPlaceholder: fromPlaceholder);
        }
        else if (newIndex < 0)
        {
            bool wasLyricActive = _currentLyricIndex >= 0;
            _currentLyricIndex = -1;

            // Show track info during instrumental gaps/intro — always use _currentMediaInfo for accurate data.
            string gapTitle = "";
            string gapArtist = "";

            if (_currentMediaInfo != null)
            {
                gapTitle = _currentMediaInfo.CurrentTrack ?? "";

                // Only show artist if it's a real artist name, not a platform identifier
                if (!string.IsNullOrWhiteSpace(_currentMediaInfo.CurrentArtist) &&
                    MediaPlatformExtensions.ParsePlatform(_currentMediaInfo.CurrentArtist) is not (MediaPlatform.YouTube or MediaPlatform.Browser))
                {
                    gapArtist = _currentMediaInfo.CurrentArtist;
                }
            }

            bool isPlaceholderActive = LyricsPlaceholderPanel != null &&
                LyricsPlaceholderPanel.Visibility == Visibility.Visible &&
                LyricsPlaceholderTitle.Text == gapTitle &&
                LyricsPlaceholderArtist.Text == gapArtist;

            if (wasLyricActive || !isPlaceholderActive)
            {
                ShowLyricsPlaceholder(gapTitle, gapArtist, _lyricsProvider);
            }
        }
    }

    private int FindLyricIndex(TimeSpan position)
    {
        if (_currentLyrics == null || _currentLyrics.Count == 0) return -1;

        if (position < _currentLyrics[0].Time) return -1;

        int lo = 0, hi = _currentLyrics.Count - 1;
        int result = 0;

        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (_currentLyrics[mid].Time <= position)
            {
                result = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return result;
    }

    private void AnimateLyricLine(
        string newText,
        bool transitionFromSearch = false,
        bool transitionFromPlaceholder = false)
    {
        if (LyricTextA == null || LyricTextB == null) return;

        HideLyricsSearchState(immediate: true);
        if (!transitionFromPlaceholder)
        {
            HideLyricsPlaceholder(immediate: true);
        }

        // Apply edge fade mask only during transitions and remove when settled
        // so resting text rows are not dimmed.
        _lyricsLayerFadeMask ??= AnimatedLyricsLayer.OpacityMask;
        int transitionVersion = ++_lyricsLineTransitionVersion;

        bool useA = LyricTextA.Opacity > 0.5;
        TextBlock outgoing = useA ? LyricTextA : LyricTextB;
        TextBlock incoming = useA ? LyricTextB : LyricTextA;
        TranslateTransform outTransform = useA ? LyricTranslateA : LyricTranslateB;
        TranslateTransform inTransform = useA ? LyricTranslateB : LyricTranslateA;

        outgoing.BeginAnimation(OpacityProperty, null);
        outTransform.BeginAnimation(TranslateTransform.YProperty, null);
        incoming.BeginAnimation(OpacityProperty, null);
        inTransform.BeginAnimation(TranslateTransform.YProperty, null);

        double startY = (transitionFromSearch || transitionFromPlaceholder) ? 10 : 14;
        bool hasOutgoingText = !string.IsNullOrWhiteSpace(outgoing.Text);
        outgoing.Opacity = hasOutgoingText ? 1 : 0;
        outTransform.Y = 0;
        incoming.Opacity = 0;
        incoming.Text = newText;
        // Resolve wrapping and the centered multiline height while this layer is
        // still invisible, before attaching the entrance animation clocks.
        AnimatedLyricsLayer.UpdateLayout();
        // Keep the base position identical to the animation endpoint. The
        // delayed animation supplies startY without changing the resting layout.
        inTransform.Y = 0;

        if (AnimationConfig.ReduceMotion)
        {
            outgoing.Opacity = 0;
            incoming.Opacity = 1;
            outTransform.Y = 0;
            inTransform.Y = 0;
            AnimatedLyricsLayer.OpacityMask = null;
            return;
        }

        AnimatedLyricsLayer.OpacityMask = _lyricsLayerFadeMask;

        int fps = VNotch.Services.AnimationConfig.TargetFps;
        var outDur = new Duration(TimeSpan.FromMilliseconds(300));
        var inDur = new Duration(TimeSpan.FromMilliseconds(450));
        double delayMs = 80;
        if (transitionFromSearch)
        {
            delayMs = 75;
        }
        else if (transitionFromPlaceholder)
        {
            delayMs = 110;
        }

        var inDelay = TimeSpan.FromMilliseconds(delayMs);
        var easeOut = new ExponentialEase { Exponent = 5, EasingMode = EasingMode.EaseOut };
        var easeIn = new CubicEase { EasingMode = EasingMode.EaseIn };

        var fadeOut = new DoubleAnimation(1, 0, outDur) { EasingFunction = easeIn };
        var slideOut = new DoubleAnimation(0, -14, outDur) { EasingFunction = easeIn };
        Timeline.SetDesiredFrameRate(fadeOut, fps);
        Timeline.SetDesiredFrameRate(slideOut, fps);

        if (hasOutgoingText)
        {
            outgoing.BeginAnimation(OpacityProperty, fadeOut);
            outTransform.BeginAnimation(TranslateTransform.YProperty, slideOut);
        }

        var fadeIn = new DoubleAnimation(0, 1, inDur)
        {
            EasingFunction = easeOut,
            BeginTime = inDelay,
            FillBehavior = FillBehavior.HoldEnd
        };
        var slideIn = new DoubleAnimation(startY, 0, inDur)
        {
            EasingFunction = easeOut,
            BeginTime = inDelay,
            FillBehavior = FillBehavior.HoldEnd
        };
        fadeIn.Completed += (s, e) =>
        {
            if (transitionVersion != _lyricsLineTransitionVersion) return;

            AnimatedLyricsLayer.OpacityMask = null;
        };
        // Keep the completed clocks at their final values. Detaching them here
        // changes the text's rendering state on the settling frame. The next
        // line transition (or hide) already clears both clocks before reuse.
        Timeline.SetDesiredFrameRate(fadeIn, fps);
        Timeline.SetDesiredFrameRate(slideIn, fps);

        incoming.BeginAnimation(OpacityProperty, fadeIn);
        inTransform.BeginAnimation(TranslateTransform.YProperty, slideIn);
    }

    private void ShowLyricsWidget()
    {
        bool alreadyActive = _isLyricsActive;
        _isLyricsActive = true;
        UpdateSpotifyCanvasPresentationContext();

        Dispatcher.Invoke(() =>
        {
            // Enforce CalendarWidget hidden and LyricsWidget visible to resolve
            // uncommitted visual states during in-flight animations.
            if (CalendarWidget.Visibility != Visibility.Collapsed || CalendarWidget.Opacity > 0.01)
            {
                CalendarWidget.BeginAnimation(OpacityProperty, null);
                if (!alreadyActive)
                {
                    // Animate fade-out when transitioning for the first time
                    var fadeOutCalendar = new DoubleAnimation(CalendarWidget.Opacity, 0, new Duration(TimeSpan.FromMilliseconds(250)))
                    {
                        EasingFunction = new ExponentialEase { Exponent = 4, EasingMode = EasingMode.EaseIn }
                    };
                    fadeOutCalendar.Completed += (s, e) =>
                    {
                        if (!_isLyricsActive) return;
                        CalendarWidget.Visibility = Visibility.Collapsed;
                        CalendarWidget.BeginAnimation(OpacityProperty, null);
                    };
                    System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(fadeOutCalendar, VNotch.Services.AnimationConfig.TargetFps);
                    CalendarWidget.BeginAnimation(OpacityProperty, fadeOutCalendar);
                }
                else
                {
                    // Already active — snap to collapsed immediately so it doesn't show through
                    CalendarWidget.Opacity = 0;
                    CalendarWidget.Visibility = Visibility.Collapsed;
                }
            }

            if (!alreadyActive)
            {
                var fadeOutGreeting = new DoubleAnimation(GreetingSection.Opacity, 0, new Duration(TimeSpan.FromMilliseconds(250)))
                {
                    EasingFunction = new ExponentialEase { Exponent = 4, EasingMode = EasingMode.EaseIn }
                };
                fadeOutGreeting.Completed += (s, e) =>
                {
                    if (!_isLyricsActive) return;
                    GreetingSection.Visibility = Visibility.Collapsed;
                    GreetingSection.BeginAnimation(OpacityProperty, null);
                };
                System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(fadeOutGreeting, VNotch.Services.AnimationConfig.TargetFps);
                GreetingSection.BeginAnimation(OpacityProperty, fadeOutGreeting);
            }

            // Always make LyricsWidget visible
            if (LyricsWidget.Visibility != Visibility.Visible)
            {
                LyricsWidget.Visibility = Visibility.Visible;

                if (!alreadyActive)
                {
                    LyricsWidget.BeginAnimation(OpacityProperty, null);
                    LyricsWidget.Opacity = 0;
                    var fadeIn = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(300)))
                    {
                        EasingFunction = new ExponentialEase { Exponent = 6, EasingMode = EasingMode.EaseOut }
                    };
                    fadeIn.Completed += (s, e) =>
                    {
                        LyricsWidget.BeginAnimation(OpacityProperty, null);
                        LyricsWidget.Opacity = 1.0;
                    };
                    System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(fadeIn, VNotch.Services.AnimationConfig.TargetFps);
                    LyricsWidget.BeginAnimation(OpacityProperty, fadeIn);
                }
                else
                {
                    LyricsWidget.Opacity = 1.0;
                }
            }
            else if (!alreadyActive)
            {
                // Visibility is already Visible, but transitioning from an inactive state
                var fadeIn = new DoubleAnimation(LyricsWidget.Opacity, 1, new Duration(TimeSpan.FromMilliseconds(250)))
                {
                    EasingFunction = new ExponentialEase { Exponent = 6, EasingMode = EasingMode.EaseOut }
                };
                fadeIn.Completed += (s, e) =>
                {
                    LyricsWidget.BeginAnimation(OpacityProperty, null);
                    LyricsWidget.Opacity = 1.0;
                };
                System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(fadeIn, VNotch.Services.AnimationConfig.TargetFps);
                LyricsWidget.BeginAnimation(OpacityProperty, fadeIn);
            }

            UpdateSpotifyCanvasPresentationContext();

            ResumeSpotifyCanvasLifecycle();
            ShowSpotifyCanvasBackgroundIfAvailable();

            FadeInLyricsBlurBackgroundIfActive();
        });
    }

    private void HideLyricsWidget()
    {
        _subtitleSearchController.Invalidate();
        if (!_isLyricsActive) return;
        _isLyricsActive = false;
        _isLyricsBlurFadeInProgress = false;
        UpdateSpotifyCanvasPresentationContext();
        _currentLyrics = null;
        _currentLyricIndex = -1;
        _lyricsProvider = "";

        Dispatcher.Invoke(() =>
        {
            HideLyricsSearchState(immediate: true);

            var fadeOutLyrics = new DoubleAnimation(LyricsWidget.Opacity, 0, new Duration(TimeSpan.FromMilliseconds(300)))
            {
                EasingFunction = new ExponentialEase { Exponent = 4, EasingMode = EasingMode.EaseIn }
            };
            fadeOutLyrics.Completed += (s, e) =>
            {
                if (_isLyricsActive) return;

                LyricsWidget.Visibility = Visibility.Collapsed;
                LyricsWidget.BeginAnimation(OpacityProperty, null);
                LyricsWidget.Opacity = 0;

                if (LyricTextA != null) LyricTextA.Text = "";
                if (LyricTextB != null) LyricTextB.Text = "";
                if (LyricsPlaceholderPanel != null)
                {
                    LyricsPlaceholderPanel.Visibility = Visibility.Collapsed;
                    LyricsPlaceholderPanel.BeginAnimation(OpacityProperty, null);
                    LyricsPlaceholderPanel.Opacity = 0;
                }
                if (LyricsSearchPanel != null)
                {
                    LyricsSearchPanel.Visibility = Visibility.Collapsed;
                    LyricsSearchPanel.BeginAnimation(OpacityProperty, null);
                    LyricsSearchPanel.Opacity = 0;
                }
                UpdateSpotifyCanvasPresentationContext();
            };
            System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(fadeOutLyrics, VNotch.Services.AnimationConfig.TargetFps);
            LyricsWidget.BeginAnimation(OpacityProperty, fadeOutLyrics);

            if (LyricsBlurBackground != null && LyricsBlurBackground.Visibility == Visibility.Visible)
            {
                _isLyricsBlurFadeInProgress = false;
                var fadeOutBlur = new DoubleAnimation(LyricsBlurBackground.Opacity, 0, new Duration(TimeSpan.FromMilliseconds(400)))
                {
                    EasingFunction = new ExponentialEase { Exponent = 4, EasingMode = EasingMode.EaseIn }
                };
                fadeOutBlur.Completed += (s, e) =>
                {
                    if (_isLyricsActive) return;
                    _isLyricsBlurFadeInProgress = false;
                    LyricsBlurBackground.Visibility = Visibility.Collapsed;
                    LyricsBlurBackground.BeginAnimation(OpacityProperty, null);
                    LyricsBlurBackground.Opacity = 0;
                };
                System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(fadeOutBlur, VNotch.Services.AnimationConfig.TargetFps);
                LyricsBlurBackground.BeginAnimation(OpacityProperty, fadeOutBlur);
            }

            SuspendSpotifyCanvasLifecycle();

            CalendarWidget.Visibility = Visibility.Visible;
            CalendarWidget.Opacity = 0;
            var fadeInCalendar = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(350)))
            {
                EasingFunction = new ExponentialEase { Exponent = 6, EasingMode = EasingMode.EaseOut },
                BeginTime = TimeSpan.FromMilliseconds(150)
            };
            System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(fadeInCalendar, VNotch.Services.AnimationConfig.TargetFps);
            CalendarWidget.BeginAnimation(OpacityProperty, fadeInCalendar);

            if (ShouldShowGreetingSection)
            {
                GreetingSection.Visibility = Visibility.Visible;
                GreetingSection.Opacity = 0;
                var fadeInGreeting = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(350)))
                {
                    EasingFunction = new ExponentialEase { Exponent = 6, EasingMode = EasingMode.EaseOut },
                    BeginTime = TimeSpan.FromMilliseconds(150)
                };
                System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(fadeInGreeting, VNotch.Services.AnimationConfig.TargetFps);
                GreetingSection.BeginAnimation(OpacityProperty, fadeInGreeting);
            }
            else
            {
                GreetingSection.Visibility = Visibility.Collapsed;
            }
        });
    }

    internal void CheckAndRetryYouTubeSubtitlesOnExpand()
    {
        if (!_settings.EnableYouTubeSubtitles || _settings.EnableLocalOnlyMode) return;
        if (_currentMediaInfo == null || !IsCurrentTrackYouTube(_currentMediaInfo)) return;
        if (_currentLyrics != null && _currentLyrics.Count > 0) return;

        FetchSubtitlesForTrack(_currentMediaInfo, force: true).SafeFireAndForget("SUBTITLES-EXPAND-RETRY");
    }

    private static bool IsCurrentTrackYouTube(MediaInfo info)
    {
        return !string.IsNullOrEmpty(info.YouTubeVideoId)
            || info.Platform == MediaPlatform.YouTube
            || (info.CurrentArtist != null && info.CurrentArtist.Contains("YouTube", StringComparison.OrdinalIgnoreCase))
            || MediaPlatformExtensions.ParsePlatform(info.CurrentArtist ?? "") == MediaPlatform.YouTube
            || MediaPlatformExtensions.ParsePlatform(info.MediaSource ?? "") is MediaPlatform.YouTube or MediaPlatform.Browser;
    }

    private void ClearLyrics()
    {
        _lyricsTrackKey = "";
        _subtitleFetchFailureCount = 0;
        _lastSubtitleFetchFailureTime = DateTime.MinValue;
        _syncedTextSource = SyncedTextSource.None;
        _lyricsService.Reset();
        _youtubeSubtitleService.Reset();
        ResetSpotifyCanvas();
        HideLyricsSearchState(immediate: true);
        HideLyricsWidget();
    }
}

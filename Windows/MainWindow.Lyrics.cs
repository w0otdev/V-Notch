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
    private Storyboard? _lyricsSearchShimmerStoryboard;
    private bool _isLyricsSearchVisible;
    private int _lyricsSearchTransitionVersion;
    private Brush? _lyricsLayerFadeMask;
    private int _lyricsLineTransitionVersion;
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
        if (!_settings.EnableSpotifyLyrics || !_settings.EnableOnlineLyrics || _settings.EnableLocalOnlyMode)
        {
            ResetSpotifyCanvas();
            HideLyricsWidget();
            return;
        }

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

        if (_isLyricsActive)
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

        if (trackKey != _lyricsTrackKey) return;

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
        if (string.IsNullOrEmpty(videoId) && !string.IsNullOrEmpty(info.CurrentTrack))
        {
            // Preserve resolved "yt:{id}" key and avoid overwriting with unresolved
                // fallback when MediaChanged fires with empty videoId after subtitle fetch.
            if (!force && _lyricsTrackKey.StartsWith("yt:", StringComparison.Ordinal)
                && !_lyricsTrackKey.StartsWith("yt-lrc:", StringComparison.Ordinal)
                && _currentLyrics != null && _currentLyrics.Count > 0)
            {
                return;
            }

            var lookup = await _mediaService.TryGetYouTubeVideoIdWithInfoAsync(info.CurrentTrack, info.CurrentArtist, System.Threading.CancellationToken.None);
            if (lookup != null && !string.IsNullOrEmpty(lookup.Id))
            {
                videoId = lookup.Id;
                info.YouTubeVideoId = videoId;
            }
        }

        string trackKey = !string.IsNullOrEmpty(videoId) ? $"yt:{videoId}" : $"yt-lrc:{info.CurrentTrack}|{info.CurrentArtist}";
        if (!force && trackKey == _lyricsTrackKey && (_currentLyrics != null && _currentLyrics.Count > 0 || !_isLyricsActive)) return;
        _lyricsTrackKey = trackKey;
        _syncedTextSource = SyncedTextSource.YouTubeSubtitles;

        if (_isLyricsActive)
        {
            ShowLyricsSearchState(isYouTube: true);
        }

        List<LyricLine>? subtitles = null;
        if (!string.IsNullOrEmpty(videoId))
        {
            subtitles = await _youtubeSubtitleService.FetchSubtitlesAsync(videoId, force: force);
        }

        if (trackKey != _lyricsTrackKey) return;

        if (subtitles != null && subtitles.Count > 0)
        {
            ApplySyncedLines(subtitles, info, provider: "YouTube");
            return;
        }

        // No YouTube subtitles found — do NOT fall back to LRCLIB in YouTube subtitle mode.
        ApplySyncedLines(null, info);
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
            if (_isLyricsActive)
                HideLyricsWidget();
            return;
        }

        _currentLyrics = lines;
        _currentLyricIndex = -1;
        _lyricsProvider = provider?.Trim() ?? "";

        // Always call ShowLyricsWidget — it now handles both the first-time and already-active cases,
        // ensuring CalendarWidget is collapsed and LyricsWidget is visible even after races.
        ShowLyricsWidget();

        Dispatcher.Invoke(() =>
        {
            bool transitionFromSearch =
                _isLyricsSearchVisible && LyricsSearchPanel.Visibility == Visibility.Visible;
            HideLyricsSearchState();

            int idx = FindCurrentLyricIndex();
            if (idx >= 0)
            {
                _currentLyricIndex = idx;
                HideLyricsPlaceholder();
                AnimateLyricLine(_currentLyrics[idx].Text, transitionFromSearch);
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
            ShowLyricsWidget();

            LyricTextA.BeginAnimation(OpacityProperty, null);
            LyricTextB.BeginAnimation(OpacityProperty, null);
            LyricTextA.Opacity = 0;
            LyricTextB.Opacity = 0;
            LyricTextA.Text = "";
            LyricTextB.Text = "";

            LyricsPlaceholderPanel.BeginAnimation(OpacityProperty, null);
            LyricsPlaceholderTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            LyricsPlaceholderPanel.Opacity = 0;
            LyricsPlaceholderPanel.Visibility = Visibility.Collapsed;

            LyricsSearchText.Text = Loc.Get(isYouTube ? "subtitles.searching" : "lyrics.searching");
            _isLyricsSearchVisible = true;
            _lyricsSearchTransitionVersion++;
            TranslateTransform searchTranslate = GetLyricsSearchTransform();
            LyricsSearchPanel.BeginAnimation(OpacityProperty, null);
            searchTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            LyricsSearchPanel.Visibility = Visibility.Visible;
            LyricsSearchPanel.Opacity = 1;
            searchTranslate.Y = 0;
            StartLyricsSearchShimmer();
        }

        if (Dispatcher.CheckAccess())
            ShowState();
        else
            Dispatcher.Invoke(ShowState);
    }

    private void HideLyricsSearchState()
    {
        if (!_isLyricsSearchVisible || LyricsSearchPanel.Visibility != Visibility.Visible)
        {
            StopLyricsSearchShimmer();
            return;
        }

        _isLyricsSearchVisible = false;
        int transitionVersion = ++_lyricsSearchTransitionVersion;
        TranslateTransform searchTranslate = GetLyricsSearchTransform();

        void FinishHide()
        {
            if (_isLyricsSearchVisible || transitionVersion != _lyricsSearchTransitionVersion)
                return;

            StopLyricsSearchShimmer();
            LyricsSearchPanel.BeginAnimation(OpacityProperty, null);
            searchTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            LyricsSearchPanel.Opacity = 0;
            searchTranslate.Y = 0;
            LyricsSearchPanel.Visibility = Visibility.Collapsed;
        }

        if (AnimationConfig.ReduceMotion)
        {
            FinishHide();
            return;
        }

        var duration = new Duration(TimeSpan.FromMilliseconds(240));
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

    private void ShowLyricsPlaceholder(
        string title,
        string artist,
        string provider,
        bool transitionFromSearch = false)
    {
        if (LyricsPlaceholderPanel == null) return;

        LyricsPlaceholderTitle.Text = title;
        LyricsPlaceholderArtist.Text = artist;
        LyricsPlaceholderProvider.Text = string.IsNullOrWhiteSpace(provider)
            ? ""
            : $"Provided by: {provider}";
        LyricsPlaceholderProvider.Visibility = string.IsNullOrWhiteSpace(provider)
            ? Visibility.Collapsed
            : Visibility.Visible;
        LyricsPlaceholderPanel.Visibility = Visibility.Visible;

        LyricTextA.BeginAnimation(OpacityProperty, null);
        LyricTextB.BeginAnimation(OpacityProperty, null);
        LyricTextA.Opacity = 0;
        LyricTextB.Opacity = 0;

        LyricsPlaceholderPanel.BeginAnimation(OpacityProperty, null);
        LyricsPlaceholderTranslate.BeginAnimation(TranslateTransform.YProperty, null);

        if (AnimationConfig.ReduceMotion)
        {
            LyricsPlaceholderPanel.Opacity = 1;
            LyricsPlaceholderTranslate.Y = 0;
            return;
        }

        var dur = new Duration(TimeSpan.FromMilliseconds(350));
        var ease = new ExponentialEase { Exponent = 6, EasingMode = EasingMode.EaseOut };
        TimeSpan? delay = transitionFromSearch ? TimeSpan.FromMilliseconds(75) : null;

        var fadeIn = new DoubleAnimation(0, 1, dur) { EasingFunction = ease, BeginTime = delay };
        var slideIn = new DoubleAnimation(transitionFromSearch ? 10 : 6, 0, dur)
        {
            EasingFunction = ease,
            BeginTime = delay
        };
        Timeline.SetDesiredFrameRate(fadeIn, VNotch.Services.AnimationConfig.TargetFps);
        Timeline.SetDesiredFrameRate(slideIn, VNotch.Services.AnimationConfig.TargetFps);

        LyricsPlaceholderPanel.BeginAnimation(OpacityProperty, fadeIn);
        LyricsPlaceholderTranslate.BeginAnimation(TranslateTransform.YProperty, slideIn);
    }

    private void HideLyricsPlaceholder()
    {
        if (LyricsPlaceholderPanel == null || LyricsPlaceholderPanel.Opacity < 0.01) return;

        if (AnimationConfig.ReduceMotion)
        {
            LyricsPlaceholderPanel.BeginAnimation(OpacityProperty, null);
            LyricsPlaceholderTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            LyricsPlaceholderPanel.Visibility = Visibility.Collapsed;
            LyricsPlaceholderPanel.Opacity = 0;
            LyricsPlaceholderTranslate.Y = 0;
            return;
        }

        var dur = new Duration(TimeSpan.FromMilliseconds(300));
        var ease = new ExponentialEase { Exponent = 4, EasingMode = EasingMode.EaseIn };

        var fadeOut = new DoubleAnimation(LyricsPlaceholderPanel.Opacity, 0, dur) { EasingFunction = ease };
        var slideOut = new DoubleAnimation(0, -6, dur) { EasingFunction = ease };
        Timeline.SetDesiredFrameRate(fadeOut, VNotch.Services.AnimationConfig.TargetFps);
        Timeline.SetDesiredFrameRate(slideOut, VNotch.Services.AnimationConfig.TargetFps);

        fadeOut.Completed += (s, e) =>
        {
            LyricsPlaceholderPanel.Visibility = Visibility.Collapsed;
            LyricsPlaceholderPanel.BeginAnimation(OpacityProperty, null);
            LyricsPlaceholderPanel.Opacity = 0;
        };

        LyricsPlaceholderPanel.BeginAnimation(OpacityProperty, fadeOut);
        LyricsPlaceholderTranslate.BeginAnimation(TranslateTransform.YProperty, slideOut);
    }

    private void UpdateLyricsDisplay()
    {
        if (!_isLyricsActive || _currentLyrics == null || _currentLyrics.Count == 0)
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
            if (_currentLyricIndex < 0)
                HideLyricsPlaceholder();

            _currentLyricIndex = newIndex;
            string lineText = _currentLyrics[newIndex].Text;
            AnimateLyricLine(lineText);
        }
        else if (newIndex < 0 && _currentLyricIndex >= 0)
        {
            _currentLyricIndex = -1;

            // Show track info during instrumental gaps — always use _currentMediaInfo for accurate data.
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

            ShowLyricsPlaceholder(gapTitle, gapArtist, _lyricsProvider);
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

    private void AnimateLyricLine(string newText, bool transitionFromSearch = false)
    {
        if (LyricTextA == null || LyricTextB == null) return;

        // Apply edge fade mask only during transitions and remove when settled
            // so resting text rows are not dimmed.
        _lyricsLayerFadeMask ??= AnimatedLyricsLayer.OpacityMask;
        int transitionVersion = ++_lyricsLineTransitionVersion;

        bool useA = LyricTextA.Opacity > 0.5;
        TextBlock outgoing = useA ? LyricTextA : LyricTextB;
        TextBlock incoming = useA ? LyricTextB : LyricTextA;
        TranslateTransform outTransform = useA ? LyricTranslateA : LyricTranslateB;
        TranslateTransform inTransform = useA ? LyricTranslateB : LyricTranslateA;

        incoming.Text = newText;

        outgoing.BeginAnimation(OpacityProperty, null);
        outTransform.BeginAnimation(TranslateTransform.YProperty, null);
        incoming.BeginAnimation(OpacityProperty, null);
        inTransform.BeginAnimation(TranslateTransform.YProperty, null);

        bool hasOutgoingText = !string.IsNullOrWhiteSpace(outgoing.Text);
        outgoing.Opacity = hasOutgoingText ? 1 : 0;
        outTransform.Y = 0;
        incoming.Opacity = 0;
        inTransform.Y = transitionFromSearch ? 10 : 14;

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
        var inDelay = TimeSpan.FromMilliseconds(transitionFromSearch ? 75 : 80);
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

        var fadeIn = new DoubleAnimation(0, 1, inDur) { EasingFunction = easeOut, BeginTime = inDelay };
        var slideIn = new DoubleAnimation(transitionFromSearch ? 10 : 14, 0, inDur)
        {
            EasingFunction = easeOut,
            BeginTime = inDelay
        };
        fadeIn.Completed += (s, e) =>
        {
            if (transitionVersion != _lyricsLineTransitionVersion) return;

            AnimatedLyricsLayer.OpacityMask = null;
            incoming.BeginAnimation(OpacityProperty, null);
            incoming.Opacity = 1;
            inTransform.BeginAnimation(TranslateTransform.YProperty, null);
            inTransform.Y = 0;
        };
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
                    GreetingSection.Visibility = Visibility.Collapsed;
                    GreetingSection.BeginAnimation(OpacityProperty, null);
                };
                System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(fadeOutGreeting, VNotch.Services.AnimationConfig.TargetFps);
                GreetingSection.BeginAnimation(OpacityProperty, fadeOutGreeting);
            }

            // Always make LyricsWidget visible
            if (LyricsWidget.Visibility != Visibility.Visible || LyricsWidget.Opacity < 0.99)
            {
                LyricsWidget.BeginAnimation(OpacityProperty, null);
                LyricsWidget.Visibility = Visibility.Visible;

                if (!alreadyActive)
                {
                    LyricsWidget.Opacity = 0;
                    var fadeIn = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(350)))
                    {
                        EasingFunction = new ExponentialEase { Exponent = 6, EasingMode = EasingMode.EaseOut },
                        BeginTime = TimeSpan.FromMilliseconds(100)
                    };
                    System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(fadeIn, VNotch.Services.AnimationConfig.TargetFps);
                    LyricsWidget.BeginAnimation(OpacityProperty, fadeIn);
                }
                else
                {
                    LyricsWidget.Opacity = 1.0;
                }
            }

            RestoreLyricsBlurFallback();

            ResumeSpotifyCanvasLifecycle();
            ShowSpotifyCanvasBackgroundIfAvailable();
            UpdateSpotifyCanvasPresentationContext();
        });
    }

    private void HideLyricsWidget()
    {
        if (!_isLyricsActive) return;
        _isLyricsActive = false;
        UpdateSpotifyCanvasPresentationContext();
        _currentLyrics = null;
        _currentLyricIndex = -1;
        _lyricsProvider = "";

        Dispatcher.Invoke(() =>
        {
            HideLyricsSearchState();

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
                UpdateSpotifyCanvasPresentationContext();
            };
            System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(fadeOutLyrics, VNotch.Services.AnimationConfig.TargetFps);
            LyricsWidget.BeginAnimation(OpacityProperty, fadeOutLyrics);

            if (LyricsBlurBackground != null && LyricsBlurBackground.Visibility == Visibility.Visible)
            {
                var fadeOutBlur = new DoubleAnimation(LyricsBlurBackground.Opacity, 0, new Duration(TimeSpan.FromMilliseconds(400)))
                {
                    EasingFunction = new ExponentialEase { Exponent = 4, EasingMode = EasingMode.EaseIn }
                };
                fadeOutBlur.Completed += (s, e) =>
                {
                    if (_isLyricsActive) return;
                    LyricsBlurBackground.Visibility = Visibility.Collapsed;
                    LyricsBlurBackground.BeginAnimation(OpacityProperty, null);
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

    private void ClearLyrics()
    {
        _lyricsTrackKey = "";
        _syncedTextSource = SyncedTextSource.None;
        _lyricsService.Reset();
        _youtubeSubtitleService.Reset();
        ResetSpotifyCanvas();
        HideLyricsWidget();
    }
}

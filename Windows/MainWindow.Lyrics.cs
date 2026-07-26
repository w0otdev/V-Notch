using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VNotch.Models;
using VNotch.Services;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch;

public partial class MainWindow
{
    private readonly LyricsService _lyricsService = new();
    private readonly YouTubeSubtitleService _youtubeSubtitleService = new();
    private SpotifyCanvasService? _spotifyCanvasService;
    private SpotifyCanvasService SpotifyCanvasService =>
        _spotifyCanvasService ??= new SpotifyCanvasService();
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
    private CancellationTokenSource? _spotifyCanvasCts;
    private string _spotifyCanvasTrackKey = "";
    private Uri? _spotifyCanvasUri;
    private bool _spotifyCanvasShouldPlay;
    private bool _isSpotifyCanvasMediaOpen;
    private bool _spotifyCanvasLookupCompleted;

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
        if (!_settings.EnableSpotifyLyrics)
        {
            ResetSpotifyCanvas();
            HideLyricsWidget();
            return;
        }

        string trackKey = $"{info.CurrentTrack}|{info.CurrentArtist}";

        if (trackKey == _lyricsTrackKey) return;
        _lyricsTrackKey = trackKey;
        _syncedTextSource = SyncedTextSource.SpotifyLyrics;

        if (info.Platform != MediaPlatform.Spotify)
        {
            ResetSpotifyCanvas();
            HideLyricsWidget();
            return;
        }

        StartSpotifyCanvasFetch(info, trackKey);

        _currentLyrics = null;
        _currentLyricIndex = -1;
        _lyricsProvider = "";
        ShowLyricsSearchState(isYouTube: false);

        int durationSec = (int)info.Duration.TotalSeconds;
        if (durationSec <= 0) durationSec = 240;

        LyricsResult? lyrics = await _lyricsService.FetchSyncedLyricsAsync(
            info.CurrentTrack, info.CurrentArtist, durationSec);

        if (trackKey != _lyricsTrackKey) return;

        ApplySyncedLines(lyrics?.Lines, info, provider: lyrics?.Provider);
    }

    private void StartSpotifyCanvasFetch(MediaInfo info, string trackKey)
    {
        CancelSpotifyCanvasFetch(retryWhenVisible: false);
        _spotifyCanvasTrackKey = trackKey;
        _spotifyCanvasUri = null;
        _spotifyCanvasLookupCompleted = false;

        if (Dispatcher.CheckAccess())
            HideSpotifyCanvasBackground(clearSource: true);
        else
            Dispatcher.BeginInvoke(new Action(() => HideSpotifyCanvasBackground(clearSource: true)));

        RuntimeLog.Debug("SPOTIFY-CANVAS", () =>
            $"Fetch gate: enabled={_settings.EnableSpotifyCanvas}, platform={info.Platform}, " +
            $"sessionStored={!string.IsNullOrWhiteSpace(_settings.SpotifySpDc)}");
        if (!_settings.EnableSpotifyCanvas || info.Platform != MediaPlatform.Spotify)
            return;

        TryStartSpotifyCanvasFetch(info, trackKey);
    }

    private void TryStartSpotifyCanvasFetch(MediaInfo info, string trackKey)
    {
        if (!_settings.EnableSpotifyCanvas ||
            info.Platform != MediaPlatform.Spotify ||
            !IsSpotifyCanvasSurfaceVisible ||
            _spotifyCanvasLookupCompleted ||
            _spotifyCanvasCts != null)
        {
            return;
        }

        var requestCts = new CancellationTokenSource();
        _spotifyCanvasCts = requestCts;
        FetchSpotifyCanvasAsync(info, trackKey, requestCts)
            .SafeFireAndForget("SPOTIFY-CANVAS");
    }

    private async Task FetchSpotifyCanvasAsync(
        MediaInfo info,
        string trackKey,
        CancellationTokenSource requestCts)
    {
        CancellationToken token = requestCts.Token;
        try
        {
            Uri? canvasUri = await SpotifyCanvasService.FetchCanvasAsync(
                info.CurrentTrack,
                info.CurrentArtist,
                info.Duration,
                _settings.SpotifySpDc,
                token);

            if (token.IsCancellationRequested ||
                canvasUri == null ||
                trackKey != _lyricsTrackKey ||
                trackKey != _spotifyCanvasTrackKey)
            {
                if (!token.IsCancellationRequested && canvasUri == null && trackKey == _spotifyCanvasTrackKey)
                    RuntimeLog.Debug("SPOTIFY-CANVAS", "No Canvas available; keeping normal lyrics background");
                return;
            }

            _spotifyCanvasUri = canvasUri;
            RuntimeLog.Debug("SPOTIFY-CANVAS", "Canvas is ready for the current lyrics view");
            await Dispatcher.InvokeAsync(() =>
            {
                if (!token.IsCancellationRequested &&
                    IsSpotifyCanvasSurfaceVisible &&
                    _syncedTextSource == SyncedTextSource.SpotifyLyrics &&
                    trackKey == _lyricsTrackKey)
                {
                    ShowSpotifyCanvasBackgroundIfAvailable();
                }
            });
        }
        finally
        {
            if (ReferenceEquals(_spotifyCanvasCts, requestCts))
            {
                _spotifyCanvasCts = null;
                if (!token.IsCancellationRequested && trackKey == _spotifyCanvasTrackKey)
                {
                    _spotifyCanvasLookupCompleted = true;
                }
            }

            requestCts.Dispose();
        }
    }

    private void RefreshSpotifyCanvasForCurrentTrack()
    {
        if (_currentMediaInfo == null ||
            _currentMediaInfo.Platform != MediaPlatform.Spotify ||
            string.IsNullOrWhiteSpace(_currentMediaInfo.CurrentTrack))
        {
            ResetSpotifyCanvas();
            return;
        }

        string trackKey = $"{_currentMediaInfo.CurrentTrack}|{_currentMediaInfo.CurrentArtist}";
        StartSpotifyCanvasFetch(_currentMediaInfo, trackKey);
    }

    private void ShowSpotifyCanvasBackgroundIfAvailable()
    {
        if (!_settings.EnableSpotifyCanvas ||
            !IsSpotifyCanvasSurfaceVisible ||
            _spotifyCanvasUri == null ||
            LyricsCanvasBackground == null ||
            LyricsCanvasVideo == null)
        {
            return;
        }

        try
        {
            LyricsCanvasBackground.BeginAnimation(OpacityProperty, null);
            LyricsCanvasBackground.Opacity = 0;
            LyricsCanvasBackground.Visibility = Visibility.Visible;

            ApplySpotifyCanvasBrightness();
            if (LyricsCanvasVideo.Source != _spotifyCanvasUri)
            {
                LyricsCanvasVideo.Stop();
                LyricsCanvasVideo.Source = _spotifyCanvasUri;
            }

            // Opening the media is required to display its first frame. The
            // MediaOpened handler immediately pauses it again when Spotify is paused.
            LyricsCanvasVideo.Play();
            if (_isSpotifyCanvasMediaOpen)
                FadeInSpotifyCanvasBackgroundIfReady();
            RuntimeLog.Debug("SPOTIFY-CANVAS", "Opening Canvas video in lyrics background");
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("SPOTIFY-CANVAS", $"Unable to start Canvas video: {ex.Message}");
            HideSpotifyCanvasBackground(clearSource: true);
        }
    }

    private void HideSpotifyCanvasBackground(bool clearSource, bool restoreFallback = true)
    {
        if (LyricsCanvasBackground == null || LyricsCanvasVideo == null)
            return;

        _isSpotifyCanvasMediaOpen = false;
        LyricsCanvasBackground.BeginAnimation(OpacityProperty, null);
        LyricsCanvasBackground.Opacity = 0;
        LyricsCanvasBackground.Visibility = Visibility.Collapsed;

        try
        {
            if (clearSource)
            {
                ReleaseSpotifyCanvasMediaElement();
            }
            else
            {
                LyricsCanvasVideo.Stop();
            }
        }
        catch
        {
            // MediaElement can throw while Windows is tearing down a failed codec.
        }

        if (restoreFallback)
        {
            RestoreLyricsBlurFallback();
        }
    }

    private void ReleaseSpotifyCanvasMediaElement()
    {
        _isSpotifyCanvasMediaOpen = false;
        if (LyricsCanvasVideo == null) return;

        try
        {
            LyricsCanvasVideo.Stop();
            LyricsCanvasVideo.Close();
            LyricsCanvasVideo.Source = null;
            LyricsCanvasVideo.Width = double.NaN;
            LyricsCanvasVideo.Height = double.NaN;
        }
        catch
        {
            try { LyricsCanvasVideo.Source = null; } catch { }
        }
    }

    private void CancelSpotifyCanvasFetch(bool retryWhenVisible)
    {
        var pending = _spotifyCanvasCts;
        _spotifyCanvasCts = null;
        if (pending == null) return;

        if (retryWhenVisible)
        {
            _spotifyCanvasLookupCompleted = false;
        }

        try { pending.Cancel(); } catch (ObjectDisposedException) { }
        pending.Dispose();
    }

    private void SuspendSpotifyCanvasLifecycle()
    {
        CancelSpotifyCanvasFetch(retryWhenVisible: true);
        if (Dispatcher.CheckAccess())
        {
            HideSpotifyCanvasBackground(clearSource: true, restoreFallback: false);
        }
        else
        {
            Dispatcher.BeginInvoke(new Action(() =>
                HideSpotifyCanvasBackground(clearSource: true, restoreFallback: false)));
        }
    }

    private void ResumeSpotifyCanvasLifecycle()
    {
        if (!IsSpotifyCanvasSurfaceVisible ||
            !_settings.EnableSpotifyCanvas ||
            _currentMediaInfo?.Platform != MediaPlatform.Spotify)
        {
            return;
        }

        if (_spotifyCanvasUri != null)
        {
            ShowSpotifyCanvasBackgroundIfAvailable();
            return;
        }

        string trackKey = $"{_currentMediaInfo.CurrentTrack}|{_currentMediaInfo.CurrentArtist}";
        if (trackKey == _lyricsTrackKey && trackKey == _spotifyCanvasTrackKey)
        {
            TryStartSpotifyCanvasFetch(_currentMediaInfo, trackKey);
        }
    }

    private void DisposeSpotifyCanvasLifecycle()
    {
        CancelSpotifyCanvasFetch(retryWhenVisible: false);
        _spotifyCanvasTrackKey = "";
        _spotifyCanvasUri = null;
        _spotifyCanvasShouldPlay = false;
        _spotifyCanvasLookupCompleted = false;
        ReleaseSpotifyCanvasMediaElement();
    }

    private void ApplySpotifyCanvasBrightness()
    {
        if (LyricsCanvasVideo != null)
        {
            LyricsCanvasVideo.BeginAnimation(UIElement.OpacityProperty, null);
            LyricsCanvasVideo.Opacity = 1.0;
        }

        if (LyricsCanvasBrightnessOverlay != null)
        {
            double brightness = Math.Clamp(_settings.SpotifyCanvasBrightness, 0.2, 1.0);
            LyricsCanvasBrightnessOverlay.BeginAnimation(UIElement.OpacityProperty, null);
            LyricsCanvasBrightnessOverlay.Opacity = 1.0 - brightness;
        }
    }

    private void HideLyricsBlurForCanvas()
    {
        if (LyricsBlurBackground == null)
            return;

        LyricsBlurBackground.BeginAnimation(OpacityProperty, null);
        LyricsBlurBackground.Opacity = 0;
        LyricsBlurBackground.Visibility = Visibility.Collapsed;
    }

    private void RestoreLyricsBlurFallback()
    {
        if (!_isLyricsActive || LyricsBlurBackground == null)
            return;

        if (_isSpotifyCanvasMediaOpen)
        {
            HideLyricsBlurForCanvas();
            return;
        }

        LyricsBlurBackground.BeginAnimation(OpacityProperty, null);
        if (_settings.EnableBlurEffects && !IsLiquidGlassEnabled)
        {
            LyricsBlurImage.BeginAnimation(OpacityProperty, null);
            LyricsBlurImage.Opacity = 1;
            LyricsBlurBackground.Visibility = Visibility.Visible;
            LyricsBlurBackground.Opacity = 0.55;
        }
        else
        {
            LyricsBlurBackground.Opacity = 0;
            LyricsBlurBackground.Visibility = Visibility.Collapsed;
        }
    }

    private void ResetSpotifyCanvas()
    {
        CancelSpotifyCanvasFetch(retryWhenVisible: false);
        _spotifyCanvasTrackKey = "";
        _spotifyCanvasUri = null;
        _spotifyCanvasLookupCompleted = false;

        if (Dispatcher.CheckAccess())
            HideSpotifyCanvasBackground(clearSource: true);
        else
            Dispatcher.BeginInvoke(new Action(() => HideSpotifyCanvasBackground(clearSource: true)));
    }

    private void UpdateSpotifyCanvasPlaybackState(MediaInfo info)
    {
        _spotifyCanvasShouldPlay = info.Platform == MediaPlatform.Spotify && info.IsPlaying;
        if (LyricsCanvasVideo == null ||
            LyricsCanvasBackground?.Visibility != Visibility.Visible ||
            LyricsCanvasVideo.Source == null)
        {
            return;
        }

        try
        {
            if (_spotifyCanvasShouldPlay)
                LyricsCanvasVideo.Play();
            else
                LyricsCanvasVideo.Pause();
        }
        catch (Exception ex)
        {
            RuntimeLog.Debug("SPOTIFY-CANVAS", () => $"Playback state update skipped: {ex.Message}");
        }
    }

    private void LyricsCanvasVideo_MediaOpened(object sender, RoutedEventArgs e)
    {
        if (!IsSpotifyCanvasSurfaceVisible ||
            _spotifyCanvasUri == null ||
            LyricsCanvasVideo.Source != _spotifyCanvasUri)
        {
            HideSpotifyCanvasBackground(clearSource: true);
            return;
        }

        UpdateSpotifyCanvasCrop();

        try
        {
            if (_spotifyCanvasShouldPlay)
                LyricsCanvasVideo.Play();
            else
                LyricsCanvasVideo.Pause();
        }
        catch { }

        RuntimeLog.Debug("SPOTIFY-CANVAS", "Canvas media opened successfully");

        _isSpotifyCanvasMediaOpen = true;
        ApplySpotifyCanvasBrightness();
        FadeInSpotifyCanvasBackgroundIfReady();
    }

    private void FadeInSpotifyCanvasBackgroundIfReady()
    {
        if (!_isSpotifyCanvasMediaOpen ||
            !_isLyricsActive ||
            !_isExpanded ||
            _isAnimating ||
            LyricsCanvasBackground == null)
        {
            if (LyricsCanvasBackground != null)
            {
                LyricsCanvasBackground.BeginAnimation(OpacityProperty, null);
                LyricsCanvasBackground.Opacity = 0;
            }
            return;
        }

        Uri? canvasUri = _spotifyCanvasUri;
        LyricsCanvasBackground.BeginAnimation(OpacityProperty, null);
        LyricsCanvasBackground.Visibility = Visibility.Visible;
        LyricsCanvasBackground.Opacity = 0;

        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250))
        {
            EasingFunction = new ExponentialEase { Exponent = 4, EasingMode = EasingMode.EaseOut }
        };
        fadeIn.Completed += (s, e) =>
        {
            if (_isSpotifyCanvasMediaOpen &&
                _isLyricsActive &&
                _isExpanded &&
                LyricsCanvasVideo.Source == canvasUri)
            {
                HideLyricsBlurForCanvas();
            }
        };
        Timeline.SetDesiredFrameRate(fadeIn, AnimationConfig.TargetFps);
        LyricsCanvasBackground.BeginAnimation(OpacityProperty, fadeIn);
    }

    private void LyricsCanvasBackground_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateSpotifyCanvasCrop();
    }

    private void UpdateSpotifyCanvasCrop()
    {
        if (LyricsCanvasVideo == null || LyricsCanvasViewport == null)
            return;

        int videoWidth = LyricsCanvasVideo.NaturalVideoWidth;
        int videoHeight = LyricsCanvasVideo.NaturalVideoHeight;
        double viewportWidth = LyricsCanvasViewport.ActualWidth;
        double viewportHeight = LyricsCanvasViewport.ActualHeight;

        if (videoWidth <= 0 || videoHeight <= 0 || viewportWidth <= 0 || viewportHeight <= 0)
            return;

        double scale = Math.Max(viewportWidth / videoWidth, viewportHeight / videoHeight);
        LyricsCanvasVideo.Width = videoWidth * scale;
        LyricsCanvasVideo.Height = videoHeight * scale;
    }

    private void LyricsCanvasVideo_MediaEnded(object sender, RoutedEventArgs e)
    {
        if (!IsSpotifyCanvasSurfaceVisible ||
            _spotifyCanvasUri == null ||
            LyricsCanvasVideo.Source != _spotifyCanvasUri)
        {
            return;
        }

        try
        {
            LyricsCanvasVideo.Position = TimeSpan.Zero;
            if (_spotifyCanvasShouldPlay)
                LyricsCanvasVideo.Play();
            else
                LyricsCanvasVideo.Pause();
        }
        catch (Exception ex)
        {
            RuntimeLog.Debug("SPOTIFY-CANVAS", () => $"Canvas loop failed: {ex.Message}");
        }
    }

    private void LyricsCanvasVideo_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        if (!IsSpotifyCanvasSurfaceVisible || LyricsCanvasVideo.Source == null)
        {
            return;
        }

        RuntimeLog.Warn("SPOTIFY-CANVAS", $"Canvas video failed; using lyrics fallback: {e.ErrorException?.Message}");
        _spotifyCanvasUri = null;
        HideSpotifyCanvasBackground(clearSource: true);
    }

    private void LyricsCanvasVideo_Unloaded(object sender, RoutedEventArgs e)
    {
        CancelSpotifyCanvasFetch(retryWhenVisible: true);
        ReleaseSpotifyCanvasMediaElement();
    }

    private async Task FetchSubtitlesForTrack(MediaInfo info, bool force = false)
    {
        if (!_settings.EnableYouTubeSubtitles)
        {
            HideLyricsWidget();
            return;
        }

        if (string.IsNullOrEmpty(info.YouTubeVideoId))
            return;

        _lastKnownYouTubeVideoId = info.YouTubeVideoId;

        string trackKey = $"yt:{info.YouTubeVideoId}";
        if (!force && trackKey == _lyricsTrackKey) return;
        _lyricsTrackKey = trackKey;
        _syncedTextSource = SyncedTextSource.YouTubeSubtitles;

        _currentLyrics = null;
        _currentLyricIndex = -1;
        _lyricsProvider = "";
        ShowLyricsSearchState(isYouTube: true);

        var subtitles = await _youtubeSubtitleService.FetchSubtitlesAsync(info.YouTubeVideoId, force: force);

        if (trackKey != _lyricsTrackKey) return;

        ApplySyncedLines(subtitles, info, isYouTube: true, provider: "YouTube");
    }

    private void ApplySyncedLines(
        List<LyricLine>? lines,
        MediaInfo info,
        bool isYouTube = false,
        string? provider = null)
    {
        if (lines == null || lines.Count == 0)
        {
            _lyricsTrackKey = "";
            ResetSpotifyCanvas();
            if (_isLyricsActive)
                HideLyricsWidget();
            return;
        }

        _currentLyrics = lines;
        _currentLyricIndex = -1;
        _lyricsProvider = provider?.Trim() ?? "";

        if (!_isLyricsActive)
        {
            ShowLyricsWidget();
        }

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
            else if (LyricsPlaceholderPanel.Visibility != Visibility.Visible || LyricsPlaceholderPanel.Opacity < 0.01)
            {
                string placeholderTitle = isYouTube ? info.CurrentArtist : info.CurrentTrack;
                string placeholderArtist = isYouTube ? "" : info.CurrentArtist;
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
            new PropertyPath($"(TextBlock.Foreground).(GradientBrush.GradientStops)[{gradientStopIndex}].(GradientStop.Offset)"));
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
            if (_syncedTextSource == SyncedTextSource.YouTubeSubtitles && _currentMediaInfo != null)
            {
                ShowLyricsPlaceholder(_currentMediaInfo.CurrentArtist, "", _lyricsProvider);
            }
            else
            {
                string trackKey = _lyricsTrackKey;
                string[] parts = trackKey.Split('|', 2);
                if (parts.Length == 2)
                    ShowLyricsPlaceholder(parts[0], parts[1], _lyricsProvider);
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

    private void AnimateLyricLine(string newText, bool transitionFromSearch = false)
    {
        if (LyricTextA == null || LyricTextB == null) return;

        // The edge fade mask exists only to soften lines sliding in/out. Keeping
        // it on while a line is at rest dims the first/last text rows whenever
        // layout hugs the text, so it is applied per-transition and removed once
        // the incoming line settles.
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
        if (_isLyricsActive) return;
        _isLyricsActive = true;

        Dispatcher.Invoke(() =>
        {
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

            LyricsWidget.BeginAnimation(OpacityProperty, null);
            LyricsWidget.Visibility = Visibility.Visible;
            LyricsWidget.Opacity = 0;

            var fadeIn = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(350)))
            {
                EasingFunction = new ExponentialEase { Exponent = 6, EasingMode = EasingMode.EaseOut },
                BeginTime = TimeSpan.FromMilliseconds(100)
            };
            System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(fadeIn, VNotch.Services.AnimationConfig.TargetFps);
            LyricsWidget.BeginAnimation(OpacityProperty, fadeIn);

            RestoreLyricsBlurFallback();

            ResumeSpotifyCanvasLifecycle();
            ShowSpotifyCanvasBackgroundIfAvailable();
        });
    }

    private void HideLyricsWidget()
    {
        if (!_isLyricsActive) return;
        _isLyricsActive = false;
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

            if (!IsNonCalendarWidgetMode && !_isLyricsActive)
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

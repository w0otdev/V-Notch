using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using VNotch.Services;

namespace VNotch.Presenters;

public sealed class SpotifyCanvasPresenter : ISpotifyCanvasPresenter
{
    private const string LogTag = "SPOTIFY-CANVAS-PRESENTER";

    private readonly SpotifyCanvasViewRefs _refs;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _loopTimer;

    private MediaElement _activeVideo;
    private MediaElement? _standbyVideo;

    private bool _isMediaOpen;
    private int _fadeGeneration;
    private bool _fadeInProgress;
    private Uri? _currentUri;
    private long _sourceVersion;
    private bool _shouldPlay;
    private SpotifyCanvasPresentationOptions? _options;
    private bool _disposed;
    private bool _isSwapping;

    public event EventHandler? MediaOpened;
    public event EventHandler? MediaEnded;
    public event EventHandler<string?>? MediaFailed;
    public event EventHandler? Unloaded;

    public bool IsMediaOpen => _isMediaOpen;
    public bool IsCanvasVisiblyShowing =>
        !_disposed &&
        _isMediaOpen &&
        _refs.Background != null &&
        _refs.Background.Visibility == Visibility.Visible &&
        _refs.Background.Opacity >= 0.95;
    public Uri? CurrentSource => _currentUri;
    public long CurrentSourceVersion => _sourceVersion;

    public SpotifyCanvasPresenter(SpotifyCanvasViewRefs refs, Dispatcher dispatcher)
    {
        _refs = refs ?? throw new ArgumentNullException(nameof(refs));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

        _activeVideo = _refs.Video;
        _standbyVideo = _refs.VideoAlt;

        _loopTimer = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(30)
        };
        _loopTimer.Tick += OnLoopTimerTick;

        _refs.Background.SizeChanged += OnBackgroundSizeChanged;
        HookVideoEvents(_refs.Video);
        if (_refs.VideoAlt != null)
        {
            HookVideoEvents(_refs.VideoAlt);
        }
    }

    private void HookVideoEvents(MediaElement video)
    {
        video.MediaOpened += OnVideoMediaOpened;
        video.MediaEnded += OnVideoMediaEnded;
        video.MediaFailed += OnVideoMediaFailed;
        video.Unloaded += OnVideoUnloaded;
    }

    private void UnhookVideoEvents(MediaElement video)
    {
        video.MediaOpened -= OnVideoMediaOpened;
        video.MediaEnded -= OnVideoMediaEnded;
        video.MediaFailed -= OnVideoMediaFailed;
        video.Unloaded -= OnVideoUnloaded;
    }

    public void SetPlaybackState(bool isPlaying)
    {
        if (_disposed) return;
        _shouldPlay = isPlaying;
        if (!_isMediaOpen || _refs.Background.Visibility != Visibility.Visible || _activeVideo.Source == null)
            return;

        try
        {
            if (_shouldPlay)
            {
                _activeVideo.Play();
                _loopTimer.Start();
            }
            else
            {
                _activeVideo.Pause();
                _loopTimer.Stop();
                if (_standbyVideo != null && _standbyVideo.Source != null)
                {
                    _standbyVideo.Pause();
                }
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Debug(LogTag, () => $"Playback state update skipped: {ex.Message}");
        }
    }

    public void SetSource(Uri? uri, long sourceVersion, bool autoPlay)
    {
        if (_disposed) return;
        if (uri == null)
        {
            Hide(clearSource: true);
            return;
        }

        try
        {
            _shouldPlay = autoPlay;
            _sourceVersion = sourceVersion;

            // Preserve current frame and fade on repeated Canvas requests instead of
            // blanking and restarting playback on every update.
            if (_currentUri == uri && (_refs.Video.Source == uri || (_refs.VideoAlt != null && _refs.VideoAlt.Source == uri)) && _refs.Background.Visibility == Visibility.Visible)
            {
                SetPlaybackState(autoPlay);
                if (_isMediaOpen) FadeInBackgroundIfReady();
                return;
            }

            _loopTimer.Stop();
            _isSwapping = false;

            ++_fadeGeneration;
            _fadeInProgress = false;
            _refs.Background.BeginAnimation(UIElement.OpacityProperty, null);
            _refs.Background.Opacity = 0;
            _refs.Background.Visibility = Visibility.Visible;

            _currentUri = uri;
            _isMediaOpen = false;

            // Reset roles: Primary Video is active, VideoAlt is standby
            _activeVideo = _refs.Video;
            _standbyVideo = _refs.VideoAlt;

            Panel.SetZIndex(_activeVideo, 2);
            _activeVideo.BeginAnimation(UIElement.OpacityProperty, null);
            _activeVideo.Opacity = 1.0;

            if (_standbyVideo != null)
            {
                Panel.SetZIndex(_standbyVideo, 0);
                _standbyVideo.BeginAnimation(UIElement.OpacityProperty, null);
                _standbyVideo.Opacity = 0.0;
            }

            _activeVideo.Stop();
            _activeVideo.Source = uri;
            if (autoPlay)
            {
                _activeVideo.Play();
                _loopTimer.Start();
            }
            else
            {
                _activeVideo.Pause();
            }

            if (_standbyVideo != null)
            {
                _standbyVideo.Stop();
                _standbyVideo.Source = uri;
                _standbyVideo.Pause();
            }

            RuntimeLog.Debug(LogTag, $"Opening Canvas video in lyrics background (version #{sourceVersion})");
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn(LogTag, $"Unable to start Canvas video: {ex.Message}");
            Hide(clearSource: true);
        }
    }

    public void ApplyPresentation(SpotifyCanvasPresentationOptions options)
    {
        if (_disposed) return;
        _options = options;

        _activeVideo.BeginAnimation(UIElement.OpacityProperty, null);
        _activeVideo.Opacity = 1.0;

        if (_standbyVideo != null && !_isSwapping)
        {
            _standbyVideo.BeginAnimation(UIElement.OpacityProperty, null);
            _standbyVideo.Opacity = 0.0;
            Panel.SetZIndex(_standbyVideo, 0);
        }

        double brightness = Math.Clamp(options.Brightness, 0.2, 1.0);
        _refs.BrightnessOverlay.BeginAnimation(UIElement.OpacityProperty, null);
        _refs.BrightnessOverlay.Opacity = 1.0 - brightness;

        if (options.CanFadeIn && _isMediaOpen)
        {
            FadeInBackgroundIfReady();
        }
        else if (!options.IsLyricsActive && _refs.Background.Visibility == Visibility.Visible)
        {
            _refs.Background.BeginAnimation(UIElement.OpacityProperty, null);
            _refs.Background.Opacity = 0;
            _refs.Background.Visibility = Visibility.Collapsed;
        }

        if (_isMediaOpen || IsCanvasVisiblyShowing)
        {
            HideBlurFallback();
        }
        else if (options.IsLyricsActive && options.BlurFallbackEnabled)
        {
            RestoreBlurFallback();
        }
    }

    public void Hide(bool clearSource, bool restoreFallback = true)
    {
        if (_disposed) return;
        _isMediaOpen = false;
        _loopTimer.Stop();
        _isSwapping = false;

        ++_fadeGeneration;
        _fadeInProgress = false;
        _refs.Background.BeginAnimation(UIElement.OpacityProperty, null);
        _refs.Background.Opacity = 0;
        _refs.Background.Visibility = Visibility.Collapsed;

        try
        {
            if (clearSource)
            {
                Release();
            }
            else
            {
                _refs.Video.Stop();
                _refs.VideoAlt?.Stop();
            }
        }
        catch
        {
            // MediaElement can throw while Windows is tearing down a failed codec.
        }

        if (restoreFallback)
        {
            RestoreBlurFallback();
        }
    }

    public void Release()
    {
        _isMediaOpen = false;
        _loopTimer.Stop();
        _isSwapping = false;

        ++_fadeGeneration;
        _fadeInProgress = false;
        _currentUri = null;

        ReleaseVideo(_refs.Video);
        if (_refs.VideoAlt != null)
        {
            ReleaseVideo(_refs.VideoAlt);
        }

        _activeVideo = _refs.Video;
        _standbyVideo = _refs.VideoAlt;
    }

    private static void ReleaseVideo(MediaElement video)
    {
        try
        {
            video.Stop();
            video.Close();
            video.Source = null;
            video.Width = double.NaN;
            video.Height = double.NaN;
        }
        catch
        {
            try
            {
                video.Source = null;
            }
            catch
            {
                // Ignore failure when resetting media element source
            }
        }
    }

    public void FadeInBackgroundIfReady()
    {
        if (_disposed) return;
        if (!_isMediaOpen)
        {
            ++_fadeGeneration;
            _fadeInProgress = false;
            _refs.Background.BeginAnimation(UIElement.OpacityProperty, null);
            _refs.Background.Opacity = 0;
            return;
        }

        bool canFadeIn = _options?.CanFadeIn ?? true;
        if (!canFadeIn)
        {
            // If already fully visible, do not wipe to 0 during transient window animations or hover
            if (_refs.Background.Visibility == Visibility.Visible && _refs.Background.Opacity >= 0.95)
                return;

            ++_fadeGeneration;
            _fadeInProgress = false;
            _refs.Background.BeginAnimation(UIElement.OpacityProperty, null);
            _refs.Background.Opacity = 0;
            return;
        }

        if (_fadeInProgress) return;
        if (_refs.Background.Visibility == Visibility.Visible && _refs.Background.Opacity >= 0.999)
        {
            HideBlurFallback();
            return;
        }

        Uri? canvasUri = _currentUri;
        long sourceVersion = _sourceVersion;
        int fadeGeneration = ++_fadeGeneration;
        double fromOpacity = Math.Clamp(_refs.Background.Opacity, 0, 1);
        _fadeInProgress = true;
        _refs.Background.BeginAnimation(UIElement.OpacityProperty, null);
        _refs.Background.Opacity = fromOpacity;
        _refs.Background.Visibility = Visibility.Visible;

        var fadeIn = new DoubleAnimation(fromOpacity, 1, TimeSpan.FromMilliseconds(250))
        {
            EasingFunction = new ExponentialEase { Exponent = 4, EasingMode = EasingMode.EaseOut }
        };
        fadeIn.Completed += (s, e) =>
        {
            if (fadeGeneration != _fadeGeneration) return;
            _fadeInProgress = false;
            _refs.Background.Opacity = 1;
            _refs.Background.BeginAnimation(UIElement.OpacityProperty, null);
            if (_isMediaOpen && (_options?.CanFadeIn ?? true) && _activeVideo.Source == canvasUri && _sourceVersion == sourceVersion)
            {
                HideBlurFallback();
            }
        };
        Timeline.SetDesiredFrameRate(fadeIn, AnimationConfig.TargetFps);
        _refs.Background.BeginAnimation(UIElement.OpacityProperty, fadeIn);
    }

    public void UpdateCrop()
    {
        if (_disposed) return;
        double viewportWidth = _refs.Viewport.ActualWidth;
        double viewportHeight = _refs.Viewport.ActualHeight;

        if (viewportWidth <= 0 || viewportHeight <= 0)
            return;

        UpdateVideoCrop(_refs.Video, viewportWidth, viewportHeight);
        if (_refs.VideoAlt != null)
        {
            UpdateVideoCrop(_refs.VideoAlt, viewportWidth, viewportHeight);
            if (double.IsNaN(_refs.VideoAlt.Width) && !double.IsNaN(_refs.Video.Width))
            {
                _refs.VideoAlt.Width = _refs.Video.Width;
                _refs.VideoAlt.Height = _refs.Video.Height;
            }
        }
    }

    private static void UpdateVideoCrop(MediaElement video, double viewportWidth, double viewportHeight)
    {
        int videoWidth = video.NaturalVideoWidth;
        int videoHeight = video.NaturalVideoHeight;
        if (videoWidth <= 0 || videoHeight <= 0)
            return;

        double scale = Math.Max(viewportWidth / videoWidth, viewportHeight / videoHeight);
        video.Width = videoWidth * scale;
        video.Height = videoHeight * scale;
    }

    public void HideBlurFallback()
    {
        if (_refs.BlurFallbackBackground == null)
            return;

        _refs.BlurFallbackBackground.BeginAnimation(UIElement.OpacityProperty, null);
        _refs.BlurFallbackBackground.Opacity = 0;
        _refs.BlurFallbackBackground.Visibility = Visibility.Collapsed;
    }

    public void RestoreBlurFallback()
    {
        if (_options != null && !_options.IsLyricsActive)
            return;
        if (_refs.BlurFallbackBackground == null)
            return;

        // If Spotify Canvas media is loaded, Canvas is the active visual; do not show fallback
        if (_isMediaOpen)
        {
            HideBlurFallback();
            return;
        }

        if (_options?.BlurFallbackEnabled ?? true)
        {
            if (_refs.BlurFallbackImage != null)
            {
                _refs.BlurFallbackImage.BeginAnimation(UIElement.OpacityProperty, null);
                _refs.BlurFallbackImage.Opacity = 1;
            }

            double currentOpacity = _refs.BlurFallbackBackground.Visibility == Visibility.Visible
                ? _refs.BlurFallbackBackground.Opacity
                : 0;

            if (_refs.BlurFallbackBackground.Visibility != Visibility.Visible || currentOpacity < 0.5)
            {
                _refs.BlurFallbackBackground.Visibility = Visibility.Visible;
                _refs.BlurFallbackBackground.BeginAnimation(UIElement.OpacityProperty, null);
                var fadeIn = new DoubleAnimation(currentOpacity, 0.55, new Duration(TimeSpan.FromMilliseconds(350)))
                {
                    EasingFunction = new ExponentialEase { Exponent = 4, EasingMode = EasingMode.EaseOut }
                };
                Timeline.SetDesiredFrameRate(fadeIn, AnimationConfig.TargetFps);
                _refs.BlurFallbackBackground.BeginAnimation(UIElement.OpacityProperty, fadeIn);
            }
        }
        else
        {
            _refs.BlurFallbackBackground.BeginAnimation(UIElement.OpacityProperty, null);
            _refs.BlurFallbackBackground.Opacity = 0;
            _refs.BlurFallbackBackground.Visibility = Visibility.Collapsed;
        }
    }

    private void OnBackgroundSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        UpdateCrop();
    }

    private void OnVideoMediaOpened(object? sender, RoutedEventArgs e)
    {
        if (_disposed || _currentUri == null)
            return;

        var video = sender as MediaElement;
        if (video == null || video.Source != _currentUri)
            return;

        UpdateCrop();

        if (ReferenceEquals(video, _activeVideo))
        {
            try
            {
                if (_shouldPlay)
                {
                    _activeVideo.Play();
                    _loopTimer.Start();
                }
                else
                {
                    _activeVideo.Pause();
                    _loopTimer.Stop();
                }
            }
            catch
            {
                // Ignore playback state transition errors on newly opened media
            }

            RuntimeLog.Debug(LogTag, "Canvas media opened successfully (active video)");

            _isMediaOpen = true;
            if (_options != null)
            {
                ApplyPresentation(_options);
            }
            FadeInBackgroundIfReady();

            MediaOpened?.Invoke(this, EventArgs.Empty);
        }
        else if (ReferenceEquals(video, _standbyVideo))
        {
            try
            {
                _standbyVideo.Pause();
                _standbyVideo.Position = TimeSpan.Zero;
                _standbyVideo.Opacity = 0.0;
                Panel.SetZIndex(_standbyVideo, 0);
            }
            catch
            {
                // Ignore standby prep errors
            }

            RuntimeLog.Debug(LogTag, "Canvas standby media opened and cued to zero");
        }
    }

    private void OnLoopTimerTick(object? sender, EventArgs e)
    {
        if (_disposed || !_shouldPlay || !_isMediaOpen || _isSwapping)
            return;

        // Check if active video is within 300ms of the end; if so, pre-roll the standby video
        // in the background so its codec produces real frames before we crossfade it in.
        if (_activeVideo.NaturalDuration.HasTimeSpan)
        {
            var duration = _activeVideo.NaturalDuration.TimeSpan;
            var pos = _activeVideo.Position;
            if (duration > TimeSpan.FromMilliseconds(600) && pos >= duration - TimeSpan.FromMilliseconds(300))
            {
                RuntimeLog.Debug(LogTag, $"Pre-rolling canvas loop (pos={pos.TotalSeconds:F2}s, dur={duration.TotalSeconds:F2}s)");
                StartPreRollLoop();
            }
        }
    }

    private void OnVideoMediaEnded(object? sender, RoutedEventArgs e)
    {
        if (_disposed || _currentUri == null)
            return;

        var endingVideo = sender as MediaElement;
        if (endingVideo == null || endingVideo.Source != _currentUri)
            return;

        if (ReferenceEquals(endingVideo, _activeVideo))
        {
            RuntimeLog.Debug(LogTag, "Canvas media ended on active video");
            if (!_isSwapping)
            {
                StartPreRollLoop();
            }
            MediaEnded?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            endingVideo.Pause();
            endingVideo.Position = TimeSpan.Zero;
        }
    }

    private void StartPreRollLoop()
    {
        if (_disposed || _currentUri == null || _isSwapping)
            return;

        if (_standbyVideo == null || _standbyVideo.Source != _currentUri)
        {
            FallbackSeekToZero(_activeVideo);
            return;
        }

        _isSwapping = true;
        var outgoing = _activeVideo;
        var incoming = _standbyVideo;

        try
        {
            // Stage 1: Pre-roll incoming video UNDERNEATH outgoing while incoming is completely hidden.
            // Any EVR blank/black surface initialization happens while incoming is at opacity 0 under outgoing.
            Panel.SetZIndex(outgoing, 2);
            Panel.SetZIndex(incoming, 0);

            incoming.Position = TimeSpan.Zero;
            incoming.BeginAnimation(UIElement.OpacityProperty, null);
            incoming.Opacity = 0.0;
            if (_shouldPlay)
            {
                incoming.Play();
            }

            // Stage 2: After 150ms of playback, incoming has decoded and buffered its first frames.
            // Now cross-fade incoming over outgoing.
            var crossfadeTimer = new DispatcherTimer(DispatcherPriority.Render, _dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(150)
            };
            crossfadeTimer.Tick += (s, e) =>
            {
                crossfadeTimer.Stop();
                if (_disposed || !_isSwapping) return;

                try
                {
                    // Move incoming to top now that it has real decoded video frames
                    Panel.SetZIndex(incoming, 2);
                    Panel.SetZIndex(outgoing, 1);

                    var fadeIn = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(120))
                    {
                        EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
                    };

                    fadeIn.Completed += (_, _) =>
                    {
                        try
                        {
                            incoming.BeginAnimation(UIElement.OpacityProperty, null);
                            incoming.Opacity = 1.0;

                            // Stage 3: Outgoing is now completely covered by incoming's running video.
                            // Quietly pause outgoing, rewind to 0, and hide it at opacity 0 under incoming.
                            outgoing.Pause();
                            outgoing.Position = TimeSpan.Zero;
                            outgoing.BeginAnimation(UIElement.OpacityProperty, null);
                            outgoing.Opacity = 0.0;
                            Panel.SetZIndex(outgoing, 0);

                            // Swap roles
                            _activeVideo = incoming;
                            _standbyVideo = outgoing;
                        }
                        catch (Exception ex)
                        {
                            RuntimeLog.Debug(LogTag, () => $"Outgoing cleanup error: {ex.Message}");
                        }
                        finally
                        {
                            _isSwapping = false;
                        }
                    };

                    Timeline.SetDesiredFrameRate(fadeIn, AnimationConfig.TargetFps);
                    incoming.BeginAnimation(UIElement.OpacityProperty, fadeIn);
                }
                catch (Exception ex)
                {
                    _isSwapping = false;
                    RuntimeLog.Warn(LogTag, $"Crossfade trigger error: {ex.Message}");
                }
            };
            crossfadeTimer.Start();
        }
        catch (Exception ex)
        {
            _isSwapping = false;
            RuntimeLog.Warn(LogTag, $"Pre-roll loop failed: {ex.Message}");
            FallbackSeekToZero(outgoing);
        }
    }

    private void FallbackSeekToZero(MediaElement video)
    {
        try
        {
            video.Position = TimeSpan.Zero;
            if (_shouldPlay)
                video.Play();
            else
                video.Pause();
        }
        catch (Exception ex)
        {
            RuntimeLog.Debug(LogTag, () => $"Canvas loop failed: {ex.Message}");
        }
    }

    private void OnVideoMediaFailed(object? sender, ExceptionRoutedEventArgs e)
    {
        if (_disposed) return;
        var video = sender as MediaElement;
        if (video == null || video.Source == null) return;

        string? error = e.ErrorException?.Message;
        RuntimeLog.Warn(LogTag, $"Canvas video failed; using lyrics fallback: {error}");

        if (ReferenceEquals(video, _activeVideo) || _standbyVideo == null)
        {
            _currentUri = null;
            Hide(clearSource: true);
            MediaFailed?.Invoke(this, error);
        }
        else
        {
            try
            {
                _standbyVideo.Stop();
                _standbyVideo.Close();
                _standbyVideo.Source = null;
            }
            catch { }
        }
    }

    private void OnVideoUnloaded(object? sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, _activeVideo))
        {
            Release();
            Unloaded?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _loopTimer.Stop();
        _loopTimer.Tick -= OnLoopTimerTick;

        _refs.Background.SizeChanged -= OnBackgroundSizeChanged;
        UnhookVideoEvents(_refs.Video);
        if (_refs.VideoAlt != null)
        {
            UnhookVideoEvents(_refs.VideoAlt);
        }
        Release();
    }
}

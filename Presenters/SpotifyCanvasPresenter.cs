using System;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using VNotch.Services;

namespace VNotch.Presenters;

public sealed class SpotifyCanvasPresenter : ISpotifyCanvasPresenter
{
    private const string LogTag = "SPOTIFY-CANVAS-PRESENTER";

    private readonly SpotifyCanvasViewRefs _refs;
    private readonly Dispatcher _dispatcher;

    private bool _isMediaOpen;
    private int _fadeGeneration;
    private bool _fadeInProgress;
    private Uri? _currentUri;
    private long _sourceVersion;
    private bool _shouldPlay;
    private SpotifyCanvasPresentationOptions? _options;
    private bool _disposed;

    public event EventHandler? MediaOpened;
    public event EventHandler? MediaEnded;
    public event EventHandler<string?>? MediaFailed;
    public event EventHandler? Unloaded;

    public bool IsMediaOpen => _isMediaOpen;
    public Uri? CurrentSource => _currentUri;
    public long CurrentSourceVersion => _sourceVersion;

    public SpotifyCanvasPresenter(SpotifyCanvasViewRefs refs, Dispatcher dispatcher)
    {
        _refs = refs ?? throw new ArgumentNullException(nameof(refs));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

        _refs.Background.SizeChanged += OnBackgroundSizeChanged;
        _refs.Video.MediaOpened += OnVideoMediaOpened;
        _refs.Video.MediaEnded += OnVideoMediaEnded;
        _refs.Video.MediaFailed += OnVideoMediaFailed;
        _refs.Video.Unloaded += OnVideoUnloaded;
    }

    public void SetPlaybackState(bool isPlaying)
    {
        if (_disposed) return;
        _shouldPlay = isPlaying;
        if (!_isMediaOpen || _refs.Background.Visibility != Visibility.Visible || _refs.Video.Source == null)
            return;

        try
        {
            if (_shouldPlay)
                _refs.Video.Play();
            else
                _refs.Video.Pause();
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
            if (_refs.Video.Source == uri && _currentUri == uri && _refs.Background.Visibility == Visibility.Visible)
            {
                SetPlaybackState(autoPlay);
                if (_isMediaOpen) FadeInBackgroundIfReady();
                return;
            }

            ++_fadeGeneration;
            _fadeInProgress = false;
            _refs.Background.BeginAnimation(UIElement.OpacityProperty, null);
            _refs.Background.Opacity = 0;
            _refs.Background.Visibility = Visibility.Visible;

            _currentUri = uri;

            if (_refs.Video.Source != uri)
            {
                _isMediaOpen = false;
                _refs.Video.Stop();
                _refs.Video.Source = uri;
            }

            if (autoPlay)
                _refs.Video.Play();
            else
                _refs.Video.Pause();

            if (_isMediaOpen)
                FadeInBackgroundIfReady();

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

        _refs.Video.BeginAnimation(UIElement.OpacityProperty, null);
        _refs.Video.Opacity = 1.0;

        double brightness = Math.Clamp(options.Brightness, 0.2, 1.0);
        _refs.BrightnessOverlay.BeginAnimation(UIElement.OpacityProperty, null);
        _refs.BrightnessOverlay.Opacity = 1.0 - brightness;

        if (options.CanFadeIn && _isMediaOpen)
        {
            FadeInBackgroundIfReady();
        }
        else if (!options.CanFadeIn && _refs.Background.Visibility == Visibility.Visible)
        {
            _refs.Background.BeginAnimation(UIElement.OpacityProperty, null);
            _refs.Background.Opacity = 0;
        }

        bool canvasIsVisiblyShowing = _isMediaOpen && options.CanFadeIn && _refs.Background.Visibility == Visibility.Visible && _refs.Background.Opacity >= 0.99;

        if (canvasIsVisiblyShowing)
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
        ++_fadeGeneration;
        _fadeInProgress = false;
        _currentUri = null;

        try
        {
            _refs.Video.Stop();
            _refs.Video.Close();
            _refs.Video.Source = null;
            _refs.Video.Width = double.NaN;
            _refs.Video.Height = double.NaN;
        }
        catch
        {
            try
            {
                _refs.Video.Source = null;
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
        bool canFadeIn = _options?.CanFadeIn ?? true;
        if (!canFadeIn || !_isMediaOpen)
        {
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
            if (_isMediaOpen && (_options?.CanFadeIn ?? true) && _refs.Video.Source == canvasUri && _sourceVersion == sourceVersion)
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
        int videoWidth = _refs.Video.NaturalVideoWidth;
        int videoHeight = _refs.Video.NaturalVideoHeight;
        double viewportWidth = _refs.Viewport.ActualWidth;
        double viewportHeight = _refs.Viewport.ActualHeight;

        if (videoWidth <= 0 || videoHeight <= 0 || viewportWidth <= 0 || viewportHeight <= 0)
            return;

        double scale = Math.Max(viewportWidth / videoWidth, viewportHeight / videoHeight);
        _refs.Video.Width = videoWidth * scale;
        _refs.Video.Height = videoHeight * scale;
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

        if (_isMediaOpen)
        {
            HideBlurFallback();
            return;
        }

        _refs.BlurFallbackBackground.BeginAnimation(UIElement.OpacityProperty, null);
        if (_options?.BlurFallbackEnabled ?? true)
        {
            if (_refs.BlurFallbackImage != null)
            {
                _refs.BlurFallbackImage.BeginAnimation(UIElement.OpacityProperty, null);
                _refs.BlurFallbackImage.Opacity = 1;
            }
            _refs.BlurFallbackBackground.Visibility = Visibility.Visible;
            _refs.BlurFallbackBackground.Opacity = 0.55;
        }
        else
        {
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
        if (_disposed || _currentUri == null || _refs.Video.Source != _currentUri)
        {
            Hide(clearSource: true);
            return;
        }

        UpdateCrop();

        try
        {
            if (_shouldPlay)
                _refs.Video.Play();
            else
                _refs.Video.Pause();
        }
        catch
        {
            // Ignore playback state transition errors on newly opened media
        }

        RuntimeLog.Debug(LogTag, "Canvas media opened successfully");

        _isMediaOpen = true;
        if (_options != null)
        {
            ApplyPresentation(_options);
        }
        FadeInBackgroundIfReady();

        MediaOpened?.Invoke(this, EventArgs.Empty);
    }

    private void OnVideoMediaEnded(object? sender, RoutedEventArgs e)
    {
        if (_disposed || _currentUri == null || _refs.Video.Source != _currentUri)
            return;

        try
        {
            _refs.Video.Position = TimeSpan.Zero;
            if (_shouldPlay)
                _refs.Video.Play();
            else
                _refs.Video.Pause();
        }
        catch (Exception ex)
        {
            RuntimeLog.Debug(LogTag, () => $"Canvas loop failed: {ex.Message}");
        }

        MediaEnded?.Invoke(this, EventArgs.Empty);
    }

    private void OnVideoMediaFailed(object? sender, ExceptionRoutedEventArgs e)
    {
        if (_disposed || _refs.Video.Source == null)
            return;

        string? error = e.ErrorException?.Message;
        RuntimeLog.Warn(LogTag, $"Canvas video failed; using lyrics fallback: {error}");
        _currentUri = null;
        Hide(clearSource: true);

        MediaFailed?.Invoke(this, error);
    }

    private void OnVideoUnloaded(object? sender, RoutedEventArgs e)
    {
        Release();
        Unloaded?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _refs.Background.SizeChanged -= OnBackgroundSizeChanged;
        _refs.Video.MediaOpened -= OnVideoMediaOpened;
        _refs.Video.MediaEnded -= OnVideoMediaEnded;
        _refs.Video.MediaFailed -= OnVideoMediaFailed;
        _refs.Video.Unloaded -= OnVideoUnloaded;
        Release();
    }
}

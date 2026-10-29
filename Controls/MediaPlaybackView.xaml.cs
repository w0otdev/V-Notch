using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using VNotch.Models;
using VNotch.Services;
using VNotch.ViewModels;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch.Controls;

public sealed partial class MediaPlaybackView : UserControl, IDisposable
{
    private MediaViewModel? _viewModel;
    private IMediaDetectionService _mediaService = null!;
    private MediaInfo? _currentMediaInfo;
    private bool _disposed;
    public bool IsMusicExpanded { get; set; }
    public bool EnableBlurEffects { get; set; }
    internal ProgressEngine Progress => _progressEngine;
    internal double DisplayRatio { get; set; }
    internal int LastDisplayedSecond { get; set; } = -1;
    public event Action<bool>? BufferingChanged;

    public MediaPlaybackView()
    {
        InitializeComponent();
        ProgressBarContainer.LostMouseCapture += ProgressBar_LostMouseCapture;
        IsVisibleChanged += (_, _) =>
        {
            UpdateProgressTimerState();
            if (!IsVisible)
            {
                StopSpringRenderLoop();
                StopCatchUpAnimation();
                CancelRewindForNavigation();
                StopRewindTextAnimation();
                _marqueeController?.Stop();
                _isDraggingProgress = false;
                _isClickSeekPending = false;
                if (ProgressBarContainer.IsMouseCaptured) ProgressBarContainer.ReleaseMouseCapture();
                IndeterminateProgress.BeginAnimation(OpacityProperty, null);
                BufferingChanged?.Invoke(false);
            }
            else
            {
                RefreshMediaMarquee();
                RenderProgressBar();
            }
        };
    }

    public void Attach(MediaViewModel viewModel, IMediaDetectionService service)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(service);
        if (_viewModel != null) _viewModel.PropertyChanged -= OnMediaPropertyChanged;
        _viewModel = viewModel;
        _viewModel.PropertyChanged += OnMediaPropertyChanged;
        DataContext = viewModel;
        PresentPlayback(viewModel.IsPlaying);
        _mediaService = service;
    }

    internal void ResetHoverBlur()
    {
        _mediaControlsHoverBlur?.BeginAnimation(BlurEffect.RadiusProperty, null);
        if (_mediaControlsHoverBlur != null) _mediaControlsHoverBlur.Radius = 0;
        MediaControls.Effect = null;
    }

    internal void CancelRewindForNavigation()
    {
        if (!_isRewindAnimating) return;
        _isRewindAnimating = false;
        StopRewindTextAnimation();
        ProgressBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        ProgressBarScale.ScaleX = 0;
        DisplayRatio = 0;
    }

    private DateTime _lastPrevRewindClickTime = DateTime.MinValue;

    internal void PrepareForPreviousTrackRequest()
    {
        try
        {
            var frame = _progressEngine.GetUiFrame();
            bool isConsecutive = (DateTime.UtcNow - _lastPrevRewindClickTime).TotalSeconds < 3.0;

            if (!isConsecutive && frame.Duration.TotalSeconds > 0 && frame.Position.TotalSeconds > 3.0)
            {
                _lastPrevRewindClickTime = DateTime.UtcNow;
                _allowProgressBackwardRenderUntil = DateTime.UtcNow.AddSeconds(5);
                _blockBackwardAfterSeekUntil = DateTime.UtcNow.AddSeconds(5);
                _suppressExternalSeekDetectionUntil = DateTime.UtcNow.AddSeconds(5);

                _progressEngine.NotifyUserSeek(TimeSpan.Zero, isPlaying: _currentMediaInfo?.IsPlaying);
                _progressEngine.NotifyPreviousTrackRequested();

                AnimateSeekProgressTo(0);
                CurrentTimeText.Text = FormatTime(TimeSpan.Zero);
                LastDisplayedSecond = 0;
                UpdateProgressTimerState();

                RuntimeLog.Log("PROGRESS-PREV-PREP", $"Rewound UI to 0 (was at {frame.Position.TotalSeconds:F1}s)");
            }
            else
            {
                _lastPrevRewindClickTime = DateTime.MinValue;
                _allowProgressBackwardRenderUntil = DateTime.UtcNow.AddSeconds(5);
                _suppressExternalSeekDetectionUntil = DateTime.UtcNow.AddSeconds(5);
                _progressEngine.NotifyPreviousTrackRequested();

                RuntimeLog.Log("PROGRESS-PREV-PREP", $"Skip to previous track (pos={frame.Position.TotalSeconds:F1}s, consecutive={isConsecutive})");
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Log("PROGRESS-PREV-PREP", ex.Message);
        }
    }

    internal void OptimisticPrepareForNextTrack()
    {
        try
        {
            _lastPrevRewindClickTime = DateTime.MinValue;
            var frame = _progressEngine.GetUiFrame();
            _progressEngine.NotifyUserSeek(frame.Position, isPlaying: false);
            _suppressExternalSeekDetectionUntil = DateTime.UtcNow.AddSeconds(3);
        }
        catch (Exception ex)
        {
            RuntimeLog.Log("PROGRESS-NEXT-PREP", ex.Message);
        }
    }


    internal async void PlayPauseButton_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_disposed || _viewModel == null) return;
        try
        {
            ButtonFeedback.PlayButtonPressAnimation(PlayPauseButton);
            _progressEngine.NotifyUserPlayPause(!_viewModel.IsPlaying);
            RenderProgressBar();
            UpdateProgressTimerState();
            await _viewModel.PlayPauseCommand.ExecuteAsync(null);
        }
        catch (Exception ex) { RuntimeLog.Error("MEDIA-CTRL", ex, "PlayPause failed"); }
    }

    internal async void NextButton_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_disposed || _viewModel == null) return;
        try
        {
            ButtonFeedback.PlayButtonPressAnimation(NextButton);
            ButtonFeedback.PlayNextSkipAnimation(NextArrow0, NextArrow1);
            OptimisticPrepareForNextTrack();
            await _viewModel.NextTrackCommand.ExecuteAsync(null);
        }
        catch (Exception ex) { RuntimeLog.Error("MEDIA-CTRL", ex, "NextTrack failed"); }
    }

    internal async void PrevButton_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_disposed || _viewModel == null) return;
        try
        {
            ButtonFeedback.PlayButtonPressAnimation(PrevButton);
            ButtonFeedback.PlayPrevSkipAnimation(PrevArrow0, PrevArrow2);
            PrepareForPreviousTrackRequest();
            await _viewModel.PreviousTrackCommand.ExecuteAsync(null);
        }
        catch (Exception ex) { RuntimeLog.Error("MEDIA-CTRL", ex, "PreviousTrack failed"); }
    }

    private void MediaButton_MouseEnter(object sender, MouseEventArgs e) => AnimateTransportHover(sender, true);
    private void MediaButton_MouseLeave(object sender, MouseEventArgs e) => AnimateTransportHover(sender, false);
    private static void AnimateTransportHover(object sender, bool hovered)
    {
        if (sender is not Border button || button.RenderTransform is not ScaleTransform transform) return;
        if (hovered) button.CacheMode ??= new BitmapCache(1.5);
        var animation = new DoubleAnimation(hovered ? 1.18 : 1.0, hovered ? _dur150 : _dur200)
        {
            EasingFunction = _easeQuadOut
        };
        Timeline.SetDesiredFrameRate(animation, AnimationConfig.TargetFps);
        if (!hovered) animation.Completed += (_, _) => { if (!button.IsMouseOver) button.CacheMode = null; };
        transform.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        transform.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }

    private bool? _presentedPlaying;

    internal void PresentPlayback(bool playing)
    {
        System.Windows.Automation.AutomationProperties.SetName(PlayPauseButton, Loc.Get(playing ? "media.pause" : "media.play"));
        if (_presentedPlaying == playing) return;
        _presentedPlaying = playing;
        var from = playing ? PlayIcon : PauseIcon;
        var to = playing ? PauseIcon : PlayIcon;
        ButtonFeedback.AnimateIconSwitch(from, to, TimeSpan.FromMilliseconds(180), _easeQuadInOut);
    }

    private void OnMediaPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_disposed || e.PropertyName != nameof(MediaViewModel.IsPlaying)) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => { if (!_disposed) PresentPlayback(_viewModel!.IsPlaying); }));
            return;
        }
        PresentPlayback(_viewModel!.IsPlaying);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_viewModel != null) _viewModel.PropertyChanged -= OnMediaPropertyChanged;
        SetProgressRenderingEnabled(false);
        StopSpringRenderLoop();
        StopCatchUpAnimation();
        CancelRewindForNavigation();
        StopRewindTextAnimation();
        IndeterminateProgress.BeginAnimation(OpacityProperty, null);
        _marqueeController?.Dispose();
        if (ProgressBarContainer.IsMouseCaptured) ProgressBarContainer.ReleaseMouseCapture();
        BufferingChanged = null;
    }

    public void UpdateProgressSectionLayout()
    {
        if (ProgressSection == null || ProgressBarContainer == null || MediaInfoSection == null)
            return;

        bool useCompactLayout = !IsMusicExpanded;
        double fallbackWidth = useCompactLayout ? 208 : 340;

        double containerHeight = useCompactLayout ? 10 : 14;
        double barHeight = 4;
        double barRadius = 2.0;
        double timeTopMargin = useCompactLayout ? 4 : 2;
        double timeSideMargin = useCompactLayout ? 4 : 6;
        double progressRightInset = 0;
        double targetWidth = Math.Max(0, fallbackWidth - 29);

        Grid.SetColumnSpan(this, useCompactLayout ? 2 : 1);

        ProgressSection.HorizontalAlignment = HorizontalAlignment.Left;
        ProgressSection.Width = targetWidth;
        ProgressSection.Margin = new Thickness(0, useCompactLayout ? 8 : 10, progressRightInset, 0);
        ProgressBarContainer.Margin = new Thickness(0);

        ProgressBarContainer.Height = containerHeight;
        ProgressBarBg.Height = barHeight;
        ProgressBar.Height = barHeight;
        IndeterminateProgress.Height = barHeight;

        var cornerRadius = new CornerRadius(barRadius);
        ProgressBarBg.CornerRadius = cornerRadius;
        ProgressBar.CornerRadius = new CornerRadius(0);
        IndeterminateProgress.CornerRadius = cornerRadius;

        if (ProgressBarClip != null)
        {
            ProgressBarClip.RadiusX = barRadius;
            ProgressBarClip.RadiusY = barRadius;
        }

        CurrentTimeText.Margin = new Thickness(0, timeTopMargin, timeSideMargin, 0);
        RemainingTimeText.Margin = new Thickness(timeSideMargin, timeTopMargin, 0, 0);
    }

    private void MediaInfoSection_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateProgressSectionLayout();
    }

    private void ProgressBarBg_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ProgressBarClip != null)
        {
            ProgressBarClip.Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height);
        }
        double radius = e.NewSize.Height / 2.0;
        ProgressBarBg.CornerRadius = new CornerRadius(radius);
    }

    private void MediaWidgetContainer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        double fadeEndX = GetVisibleMediaTextWidth(340);

        if (TitleScrollContainer != null)
        {
            TitleScrollContainer.Width = fadeEndX;
        }

        if (ArtistScrollContainer != null)
        {
            ArtistScrollContainer.Width = fadeEndX;
        }

        RefreshMediaMarquee();

        if (TextFadeBrush != null)
        {
            TextFadeBrush.EndPoint = new Point(fadeEndX, 0);
            TextFadeBrush.GradientStops[0].Offset = 0;
            double fadeStartX = Math.Max(0, fadeEndX - 20);
            TextFadeBrush.GradientStops[1].Offset = fadeEndX > 0 ? fadeStartX / fadeEndX : 0.8;
            TextFadeBrush.GradientStops[2].Offset = 1;
        }

        UpdateProgressSectionLayout();
    }

    internal double GetVisibleMediaTextWidth(double fallbackWidth)
    {
        double widgetWidth = ActualWidth;
        if (widgetWidth > 0)
        {
            double availableWidth = widgetWidth;
            return Math.Max(0, Math.Min(340, availableWidth));
        }

        double infoWidth = MediaInfoSection?.ActualWidth > 0 ? MediaInfoSection.ActualWidth : 0;
        if (infoWidth > 0)
        {
            return Math.Max(0, Math.Min(340, infoWidth));
        }

        return fallbackWidth;
    }


    private BlurEffect? _mediaControlsHoverBlur;
    private TransformGroup? _currentTimeHoverTransform;
    private TransformGroup? _remainingTimeHoverTransform;
    private ScaleTransform? _currentTimeHoverScale;
    private ScaleTransform? _remainingTimeHoverScale;
    private TranslateTransform? _currentTimeHoverTranslate;
    private TranslateTransform? _remainingTimeHoverTranslate;

    internal void AnimateProgressBarHover(bool isHovered)
    {
        double scaleX = isHovered ? 1.04 : 1.0;
        double blurRadius = EnableBlurEffects && isHovered ? 4.0 : 0.0;
        double surroundOpacity = isHovered ? 0.45 : 1.0;
        double timeScale = isHovered ? 1.22 : 1.0;
        double timeTranslateY = isHovered ? 3.0 : 0.0;

        var duration = TimeSpan.FromMilliseconds(isHovered ? 350 : 250);
        var easing = (IEasingFunction)(isHovered
            ? new ExponentialEase { Exponent = 6, EasingMode = EasingMode.EaseOut }
            : new CubicEase { EasingMode = EasingMode.EaseOut });
        int fps = VNotch.Services.AnimationConfig.TargetFps;

        ProgressBarContainer.BeginAnimation(MarginProperty, null);
        double barHeight = isHovered ? 10 : 4;
        var heightAnim = new DoubleAnimation { To = barHeight, Duration = duration, EasingFunction = easing };
        var scaleXAnim = new DoubleAnimation { To = scaleX, Duration = duration, EasingFunction = easing };
        Timeline.SetDesiredFrameRate(heightAnim, fps);
        Timeline.SetDesiredFrameRate(scaleXAnim, fps);
        ProgressBarBg.BeginAnimation(HeightProperty, heightAnim);
        ProgressBar.BeginAnimation(HeightProperty, heightAnim);
        ProgressBarMainScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleXAnim);

        double barRadius = barHeight / 2.0;
        var clipRadiusAnim = new DoubleAnimation { To = barRadius, Duration = duration, EasingFunction = easing };
        Timeline.SetDesiredFrameRate(clipRadiusAnim, fps);
        ProgressBarClip.BeginAnimation(RectangleGeometry.RadiusXProperty, clipRadiusAnim);
        ProgressBarClip.BeginAnimation(RectangleGeometry.RadiusYProperty, clipRadiusAnim);

        if (_currentTimeHoverTransform == null)
        {
            _currentTimeHoverScale = new ScaleTransform(1, 1);
            _currentTimeHoverTranslate = new TranslateTransform(0, 0);
            _currentTimeHoverTransform = new TransformGroup();
            _currentTimeHoverTransform.Children.Add(_currentTimeHoverScale);
            _currentTimeHoverTransform.Children.Add(_currentTimeHoverTranslate);
            CurrentTimeText.RenderTransformOrigin = new Point(0, 0.5);
            CurrentTimeText.RenderTransform = _currentTimeHoverTransform;
        }
        if (_remainingTimeHoverTransform == null)
        {
            _remainingTimeHoverScale = new ScaleTransform(1, 1);
            _remainingTimeHoverTranslate = new TranslateTransform(0, 0);
            _remainingTimeHoverTransform = new TransformGroup();
            _remainingTimeHoverTransform.Children.Add(_remainingTimeHoverScale);
            _remainingTimeHoverTransform.Children.Add(_remainingTimeHoverTranslate);
            RemainingTimeText.RenderTransformOrigin = new Point(1, 0.5);
            RemainingTimeText.RenderTransform = _remainingTimeHoverTransform;
        }

        var timeScaleAnim = new DoubleAnimation { To = timeScale, Duration = duration, EasingFunction = easing };
        var timeTranslateAnim = new DoubleAnimation { To = timeTranslateY, Duration = duration, EasingFunction = easing };
        Timeline.SetDesiredFrameRate(timeScaleAnim, fps);
        Timeline.SetDesiredFrameRate(timeTranslateAnim, fps);

        _currentTimeHoverScale!.BeginAnimation(ScaleTransform.ScaleXProperty, timeScaleAnim);
        _currentTimeHoverScale.BeginAnimation(ScaleTransform.ScaleYProperty, timeScaleAnim);
        _currentTimeHoverTranslate!.BeginAnimation(TranslateTransform.YProperty, timeTranslateAnim);
        _remainingTimeHoverScale!.BeginAnimation(ScaleTransform.ScaleXProperty, timeScaleAnim);
        _remainingTimeHoverScale.BeginAnimation(ScaleTransform.ScaleYProperty, timeScaleAnim);
        _remainingTimeHoverTranslate!.BeginAnimation(TranslateTransform.YProperty, timeTranslateAnim);

        var blurAnim = new DoubleAnimation { To = blurRadius, Duration = duration, EasingFunction = easing };
        var dimAnim = new DoubleAnimation { To = surroundOpacity, Duration = duration, EasingFunction = easing };
        Timeline.SetDesiredFrameRate(blurAnim, fps);
        Timeline.SetDesiredFrameRate(dimAnim, fps);

        if (EnableBlurEffects)
        {
            _mediaControlsHoverBlur ??= new BlurEffect { Radius = 0, RenderingBias = RenderingBias.Performance };
            MediaControls.Effect = _mediaControlsHoverBlur;
            _mediaControlsHoverBlur.BeginAnimation(BlurEffect.RadiusProperty, blurAnim);
        }
        else
        {
            _mediaControlsHoverBlur?.BeginAnimation(BlurEffect.RadiusProperty, null);
            MediaControls.Effect = null;
        }
        MediaControls.BeginAnimation(OpacityProperty, dimAnim);
    }


    private MarqueeController? _marqueeController;

    private MarqueeController Marquee => _marqueeController ??= new MarqueeController(
        title: new MarqueeTargets(
            A: new MarqueeLayer(TrackTitleLayer, TrackTitle, TitleMarqueeTranslate, TitleMorphTranslate),
            B: new MarqueeLayer(TrackTitleNextLayer, TrackTitleNext, TitleMarqueeTranslateNext, TitleMorphTranslateNext)),
        artist: new MarqueeTargets(
            A: new MarqueeLayer(TrackArtistLayer, TrackArtist, ArtistMarqueeTranslate, ArtistMorphTranslate),
            B: new MarqueeLayer(TrackArtistNextLayer, TrackArtistNext, ArtistMarqueeTranslateNext, ArtistMorphTranslateNext)),
        GetVisibleMediaTextWidth);

    internal void RefreshMediaMarquee() => Marquee.RefreshMediaMarquee();
    internal void TransitionTrackText(string title, string artist) => Marquee.TransitionTrackText(title, artist);
    internal void UpdateTitleText(string newText) => Marquee.UpdateTitleText(newText);
    internal void UpdateArtistText(string newText) => Marquee.UpdateArtistText(newText);

    private bool _isDraggingProgress = false;
    private bool _isProgressBarExpanded = false;

    private TimeSpan _dragSeekPosition = TimeSpan.Zero;

    private readonly ProgressEngine _progressEngine = new ProgressEngine();
    private long _progressSnapshotSequence = 0;


    private double _progressTargetRatio = 0;
    private double _progressSpringTargetRatio = 0;
    private double _progressVelocity = 0;
    private bool _isSeekSpringActive = false;
    private int _springSettleFrames = 0;
    private bool _isClickSeekPending = false;
    private DateTime _allowProgressBackwardRenderUntil = DateTime.MinValue;
    private DateTime _blockBackwardAfterSeekUntil = DateTime.MinValue;
    private Point _mouseDownPoint;
    private const double DRAG_THRESHOLD = 3.0;
    private DateTime _suppressExternalSeekDetectionUntil = DateTime.MinValue;
    private DateTime _protectSpringTargetUntil = DateTime.MinValue;

    private ProgressSpringRenderer? _springRenderer;
    private ProgressSpringRenderer Spring => _springRenderer ??= new ProgressSpringRenderer(
        applyRatio: r =>
        {
            DisplayRatio = r;
            ProgressBarScale.ScaleX = r;
        },
        shouldRender: () => !_isDraggingProgress,
        getPlaybackRate: () => _currentMediaInfo?.PlaybackRate ?? 1.0);

    private const string ProgressSeekLogTag = "PROGRESS-SEEK";
    private const string ProgressRenderBackwardLogTag = "PROGRESS-RENDER-BACKWARD";
    private const double SOURCE_SMOOTH_SECONDS = 2.0;

    private static readonly TimeSpan EndSeekSafetyMargin = TimeSpan.FromMilliseconds(1500);

    internal static TimeSpan ClampSeekTarget(TimeSpan target, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) return target < TimeSpan.Zero ? TimeSpan.Zero : target;

        TimeSpan maxSeek = duration - EndSeekSafetyMargin;
        if (maxSeek < TimeSpan.Zero) maxSeek = TimeSpan.Zero;

        if (target > maxSeek) target = maxSeek;
        if (target < TimeSpan.Zero) target = TimeSpan.Zero;
        return target;
    }

    private void StartSpringRenderLoop()
    {
        Spring.DisplayRatio = DisplayRatio;
        Spring.TargetRatio = _progressTargetRatio;
        Spring.SpringTargetRatio = _progressSpringTargetRatio;
        Spring.Velocity = _progressVelocity;
        Spring.SettleFrames = _springSettleFrames;
        Spring.Start();
        UpdateProgressTimerState();
    }

    internal void StopSpringRenderLoop()
    {
        if (_springRenderer == null) return;
        bool wasActive = Spring.IsActive;
        Spring.Stop();
        // A completed spring can predate a later seek or normal playback.
        // Stopping an idle renderer must not restore its old display position.
        if (wasActive) DisplayRatio = Spring.DisplayRatio;
        _progressVelocity = Spring.Velocity;
        _springSettleFrames = Spring.SettleFrames;
        _isSeekSpringActive = Spring.IsActive;
        UpdateProgressTimerState();
    }

    private bool _progressRenderingActive;
    private TimeSpan _lastProgressRenderTime = TimeSpan.MinValue;

    private void SetProgressRenderingEnabled(bool enabled)
    {
        if (_progressRenderingActive == enabled) return;
        _progressRenderingActive = enabled;
        _lastProgressRenderTime = TimeSpan.MinValue;
        if (enabled) CompositionTarget.Rendering += OnProgressRendering;
        else CompositionTarget.Rendering -= OnProgressRendering;
    }

    private void OnProgressRendering(object? sender, EventArgs e)
    {
        if (e is RenderingEventArgs frame)
        {
            if (_lastProgressRenderTime == frame.RenderingTime) return;
            _lastProgressRenderTime = frame.RenderingTime;
        }
        if (IsVisible && _currentMediaInfo != null)
        {
            RenderProgressBar();
        }
    }

    private string _lastSessionId = "";
    private string _lastProgressSessionInstanceKey = "";
    private string _lastProgressSignature = "";
    private string _lastProgressTimelineKey = "";
    private DateTimeOffset _lastProgressTimelineUpdated = DateTimeOffset.MinValue;
    private bool _lastProgressIsPlaying;
    private long _trackChangeSequence = 0;

    internal void UpdateProgressTracking(MediaInfo info)
    {
        if (info.IsThumbnailOnlyUpdate)
        {
            if (_currentMediaInfo != null)
                _currentMediaInfo.Thumbnail = info.Thumbnail;
            else
                _currentMediaInfo = info;
            return;
        }
        _currentMediaInfo = info;
        NormalizeStartupSnapshotTimestamp(info);

        ProgressSection.Visibility = Visibility.Visible;
        ProgressSection.Opacity = 1;

        bool hasRealTrack = !string.IsNullOrWhiteSpace(info.CurrentTrack) &&
                            !string.Equals(info.CurrentTrack, "No media playing", StringComparison.OrdinalIgnoreCase);
        if (hasRealTrack)
        {
            if (_isDraggingProgress && info.HasTimeline)
            {
                if (!info.IsPlaying)
                {
                    _progressEngine.NotifyUserPlayPause(false);
                }
                return;
            }

            string newSignature = $"{info.SourceAppId}|{info.CurrentTrack}|{info.CurrentArtist}";
            bool isTrackChanged = newSignature != _lastProgressSignature;
            bool isSessionSwitch = MediaProgressHelpers.HasLogicalSessionChanged(
                _lastSessionId,
                _lastProgressSessionInstanceKey,
                info.SourceAppId,
                info.SessionInstanceKey);
            bool isFirstEverTrack = string.IsNullOrEmpty(_lastProgressSignature);

            if (isTrackChanged && !isFirstEverTrack && !isSessionSwitch && info.IsPlaying && info.Position > TimeSpan.Zero)
            {
                string trackPrefix = $"{info.SourceAppId}|{info.CurrentTrack}|";
                if (_lastProgressSignature.StartsWith(trackPrefix, StringComparison.Ordinal))
                {
                    string oldArtist = _lastProgressSignature.Substring(trackPrefix.Length);
                    _lastProgressSignature = newSignature;
                    isTrackChanged = false;
                    RuntimeLog.Debug("PROGRESS-TRACK", () =>
                        $"Artist-only stabilization, skipping reset: '{oldArtist}' → '{info.CurrentArtist}' pos={info.Position.TotalSeconds:F1}s");
                }
            }

            if (isTrackChanged)
            {
                _lastProgressSignature = newSignature;
                double visibleRatio = Math.Clamp(ProgressBarScale.ScaleX, 0, 1);

                _trackChangeSequence++;

                _progressEngine.Reset();
                StopCatchUpAnimation();
                StopRewindTextAnimation();

                _isRewindAnimating = false;
                ProgressBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                _progressVelocity = 0;
                _springSettleFrames = 0;
                _isSeekSpringActive = false;

                LastDisplayedSecond = -1;
                _progressSnapshotSequence = 0;
                _lastProgressTimelineUpdated = DateTimeOffset.MinValue;
                _lastProgressIsPlaying = false;
                StopSpringRenderLoop();

                double fromRatio = visibleRatio;
                DisplayRatio = fromRatio;
                _progressTargetRatio = 0;
                _progressSpringTargetRatio = 0;
                _lastRenderedRatio = 0;
                RemainingTimeText.Text = FormatDuration(info.Duration);

                if (isFirstEverTrack)
                {
                    if (info.IsPlaying && info.Position > TimeSpan.Zero && info.HasTimeline)
                    {
                        double initialRatio = Math.Clamp(info.Position.TotalSeconds / info.Duration.TotalSeconds, 0, 1);
                        DisplayRatio = initialRatio;
                        _progressTargetRatio = initialRatio;
                        _progressSpringTargetRatio = initialRatio;
                        _lastRenderedRatio = initialRatio;
                        ProgressBarScale.ScaleX = initialRatio;
                        CurrentTimeText.Text = FormatTime(info.Position);
                        RemainingTimeText.Text = FormatDuration(info.Duration);
                        RuntimeLog.Debug("PROGRESS-BOOT", () =>
                            $"First track init at pos={info.Position.TotalSeconds:F1}s ratio={initialRatio:F4}");
                    }
                    else
                    {
                        DisplayRatio = 0;
                        ProgressBarScale.ScaleX = 0;
                        CurrentTimeText.Text = "0:00";
                    }
                }
                else if (IsVisible && info.HasTimeline)
                {
                    AnimateTrackChangeRewindToZero(fromRatio, info.Duration);
                }
                else
                {
                    DisplayRatio = 0;
                    ProgressBarScale.ScaleX = 0;
                    CurrentTimeText.Text = "0:00";
                }

                _suppressExternalSeekDetectionUntil = DateTime.UtcNow.AddSeconds(2);
            }
            else if (isSessionSwitch)
            {
                _progressEngine.Reset();
                _progressSnapshotSequence = 0;
                _lastProgressTimelineUpdated = DateTimeOffset.MinValue;
                _lastProgressIsPlaying = false;
                _allowProgressBackwardRenderUntil = DateTime.UtcNow.AddSeconds(3);
                RuntimeLog.Debug("PROGRESS-SESSION", () =>
                    $"Logical session changed for same track: " +
                    $"'{_lastProgressSessionInstanceKey}' -> '{info.SessionInstanceKey}'");
            }

            _lastSessionId = info.SourceAppId ?? "";
            _lastProgressSessionInstanceKey = info.SessionInstanceKey ?? "";

            if (info.IsTimelineIndeterminate)
            {
                StopCatchUpAnimation();
                CancelRewindForNavigation();
                StopSpringRenderLoop();
                ProgressBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                _isDraggingProgress = false;
                _isClickSeekPending = false;
                if (ProgressBarContainer.IsMouseCaptured) ProgressBarContainer.ReleaseMouseCapture();
                BufferingChanged?.Invoke(false);
            }

            string timelineKey = $"{info.SourceAppId}|{info.SessionInstanceKey}|{info.CurrentTrack}|{info.CurrentArtist}|{info.IsTimelineIndeterminate}";
            if (timelineKey != _lastProgressTimelineKey)
            {
                _lastProgressTimelineKey = timelineKey;
                _lastProgressTimelineUpdated = DateTimeOffset.MinValue;
                _lastProgressIsPlaying = false;
            }

            if (_lastProgressTimelineUpdated != DateTimeOffset.MinValue &&
                timelineKey == _lastProgressTimelineKey &&
                info.IsPlaying &&
                _lastProgressIsPlaying)
            {

                var infoUpdatedUtc = info.LastUpdated.ToUniversalTime();
                var lastUpdatedUtc = _lastProgressTimelineUpdated.ToUniversalTime();

                if (infoUpdatedUtc < lastUpdatedUtc)
                {
                    UpdateProgressTimerState();
                    if (IsVisible) RenderProgressBar();
                    return;
                }
            }

            var snapshot = new ProgressSnapshot
            {
                Position = info.Position,
                Duration = info.Duration,
                IsPlaying = info.IsPlaying,

                IsYouTube = IsLikelyBrowserProgressSource(info),
                PlaybackRate = info.PlaybackRate,
                IsSeekEnabled = info.IsSeekEnabled,
                IsIndeterminate = info.IsTimelineIndeterminate,
                Timestamp = info.LastUpdated.UtcDateTime,
                SequenceNumber = System.Threading.Interlocked.Increment(ref _progressSnapshotSequence)
            };

            _progressEngine.OnMediaSnapshot(snapshot);
            if (info.LastUpdated > _lastProgressTimelineUpdated || info.IsPlaying != _lastProgressIsPlaying)
            {
                _lastProgressTimelineUpdated = info.LastUpdated;
            }
            _lastProgressIsPlaying = info.IsPlaying;

            bool canSeek = info.CanSeekTimeline;
            ProgressBarContainer.Cursor = canSeek ? Cursors.Hand : Cursors.Arrow;

            UpdateProgressTimerState();

            if (IsVisible)
            {
                RenderProgressBar();
                Dispatcher.BeginInvoke(new Action(RenderProgressBar), DispatcherPriority.Render);
            }
        }
        else
        {
            UpdateProgressTimerState();
            _lastSessionId = "";
            _lastProgressSessionInstanceKey = "";

            IndeterminateProgress.Visibility = Visibility.Collapsed;
            IndeterminateProgress.BeginAnimation(OpacityProperty, null);
            ProgressBar.Visibility = Visibility.Visible;
            ProgressBarContainer.Cursor = Cursors.Arrow;

            _progressEngine.Reset();
            _lastProgressTimelineKey = "";
            _lastProgressTimelineUpdated = DateTimeOffset.MinValue;

            double fromRatio = Math.Clamp(ProgressBarScale.ScaleX, 0, 1);
            if (fromRatio <= 0.005) fromRatio = Math.Clamp(DisplayRatio, 0, 1);

            if (fromRatio > 0.005 && IsVisible)
            {
                if (!_isRewindAnimating)
                {
                    _trackChangeSequence++;
                    StopCatchUpAnimation();
                    StopRewindTextAnimation();

                    _progressVelocity = 0;
                    _springSettleFrames = 0;
                    _isSeekSpringActive = false;

                    LastDisplayedSecond = -1;
                    _progressSnapshotSequence = 0;
                    StopSpringRenderLoop();

                    DisplayRatio = fromRatio;
                    _progressTargetRatio = 0;
                    _progressSpringTargetRatio = 0;
                    _lastRenderedRatio = 0;
                    RemainingTimeText.Text = "0:00";

                    _lastProgressSignature = "";
                    AnimateTrackChangeRewindToZero(fromRatio, TimeSpan.Zero);
                }
            }
            else if (!_isRewindAnimating)
            {
                _lastProgressSignature = "";
                ResetProgressUI();
            }

            if (IsVisible && !_isRewindAnimating) RenderProgressBar();
        }
    }

    private static void NormalizeStartupSnapshotTimestamp(MediaInfo info) =>
        MediaProgressHelpers.NormalizeStartupSnapshotTimestamp(info);

    internal static bool IsLikelyBrowserProgressSource(MediaInfo info) =>
        MediaProgressHelpers.IsLikelyBrowserProgressSource(info);

    internal void StartIndeterminateAnimation()
    {
        if (!IsVisible || IndeterminateProgress.Visibility != Visibility.Visible || IndeterminateProgress.HasAnimatedProperties) return;

        var anim = new DoubleAnimation(0.3, 0.8, TimeSpan.FromSeconds(1))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        };
        Timeline.SetDesiredFrameRate(anim, VNotch.Services.AnimationConfig.TargetFps);
        IndeterminateProgress.BeginAnimation(OpacityProperty, anim);
    }

    private void PresentTimelineMode(bool isIndeterminate, bool playing)
    {
        IndeterminateProgress.Visibility = isIndeterminate ? Visibility.Visible : Visibility.Collapsed;
        ProgressBar.Visibility = isIndeterminate ? Visibility.Collapsed : Visibility.Visible;
        ProgressBarBg.Visibility = Visibility.Visible;
        if (isIndeterminate && playing && IsVisible)
        {
            StartIndeterminateAnimation();
        }
        else
        {
            if (IndeterminateProgress.HasAnimatedProperties)
                IndeterminateProgress.BeginAnimation(OpacityProperty, null);
            IndeterminateProgress.Opacity = 0.55;
        }
    }

    internal void ResetProgressUI()
    {
        _trackChangeSequence++;
        _isRewindAnimating = false;
        ProgressBarContainer.BeginAnimation(OpacityProperty, null);
        ProgressBarContainer.Opacity = 1;
        ProgressBarMainScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        ProgressBarMainScale.ScaleY = 1;
        ProgressBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        ProgressBarScale.ScaleX = 0;
        DisplayRatio = 0;
        _progressTargetRatio = 0;
        _progressSpringTargetRatio = 0;
        _progressVelocity = 0;
        _springSettleFrames = 0;
        _isSeekSpringActive = false;
        _blockBackwardAfterSeekUntil = DateTime.MinValue;

        LastDisplayedSecond = -1;
        CurrentTimeText.Text = "0:00";
        RemainingTimeText.Text = "0:00";
        IndeterminateProgress.BeginAnimation(OpacityProperty, null);
        IndeterminateProgress.Visibility = Visibility.Collapsed;
    }


    private double _lastRenderedRatio = 0;
    private DateTime _lastProgressMovementUtc = DateTime.UtcNow;
    private double _lastProgressMovementRatio = -1;

    private string? _progressSourceAppId;
    private MediaPlatform _progressSourcePlatform;
    private bool _progressSourceIsBrowser;

    private bool IsBrowserProgressSource()
    {
        if (_currentMediaInfo == null) return false;
        if (_progressSourceAppId == _currentMediaInfo.SourceAppId &&
            _progressSourcePlatform == _currentMediaInfo.Platform)
            return _progressSourceIsBrowser;
        _progressSourceAppId = _currentMediaInfo.SourceAppId;
        _progressSourcePlatform = _currentMediaInfo.Platform;
        _progressSourceIsBrowser = _currentMediaInfo.Platform is MediaPlatform.Browser or MediaPlatform.YouTube or MediaPlatform.SoundCloud ||
                                   PlatformDetector.IsBrowserApp(_currentMediaInfo.SourceAppId);
        return _progressSourceIsBrowser;
    }

    internal void RenderProgressBar()
    {
        if (_isDraggingProgress || _currentMediaInfo == null) return;
        if (_isRewindAnimating) return;

        bool hasRealTrack = !string.IsNullOrWhiteSpace(_currentMediaInfo.CurrentTrack) &&
                            !string.Equals(_currentMediaInfo.CurrentTrack, "No media playing", StringComparison.OrdinalIgnoreCase);
        if (!hasRealTrack)
        {
            CurrentTimeText.Text = "0:00";
            RemainingTimeText.Text = "0:00";
            ProgressBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            ProgressBarScale.ScaleX = 0;
            DisplayRatio = 0;
            _progressTargetRatio = 0;
            _progressSpringTargetRatio = 0;
            _progressVelocity = 0;
            _springSettleFrames = 0;
            _isSeekSpringActive = false;
            StopSpringRenderLoop();

            LastDisplayedSecond = -1;
            return;
        }

        DateTime renderNow = DateTime.UtcNow;
        var frame = _progressEngine.GetUiFrame();
        if (frame.ShowIndeterminate || IndeterminateProgress.Visibility == Visibility.Visible)
            PresentTimelineMode(frame.ShowIndeterminate, frame.State == ProgressState.Playing);

        if (frame.ShowIndeterminate || frame.Duration <= TimeSpan.Zero)
        {
            if (frame.ShowIndeterminate)
            {
                CurrentTimeText.Text = "";
                RemainingTimeText.Text = _currentMediaInfo.IsTimelinePending ? "--:--" : "LIVE";
            }
            else
            {
                CurrentTimeText.Text = "0:00";
                RemainingTimeText.Text = "0:00";
            }
            StopSpringRenderLoop();
            ProgressBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            ProgressBarScale.ScaleX = 0;
            DisplayRatio = 0;
            _progressTargetRatio = 0;
            _progressSpringTargetRatio = 0;
            _progressVelocity = 0;
            _springSettleFrames = 0;
            _isSeekSpringActive = false;

            LastDisplayedSecond = -1;
            UpdateProgressTimerState();
            return;
        }

        double engineRatio = 0;
        if (frame.Duration.TotalSeconds > 0)
        {
            engineRatio = frame.Position.TotalSeconds / frame.Duration.TotalSeconds;
            engineRatio = Math.Clamp(engineRatio, 0, 1);
        }

        if (_isCatchUpAnimating)
        {
            double ratioDiff = Math.Abs(engineRatio - _lastRenderedRatio);
            if (ratioDiff > 0.01)
            {
                StopCatchUpAnimation();

                _progressTargetRatio = engineRatio;
                _progressSpringTargetRatio = DisplayRatio;
                _progressVelocity = 0;
                _springSettleFrames = 0;
                _isSeekSpringActive = true;
                _protectSpringTargetUntil = renderNow.AddSeconds(1.5);
                StartSpringRenderLoop();
                CurrentTimeText.Text = FormatTime(frame.Position);
                _lastRenderedRatio = engineRatio;
            }
            else
            {
                _lastRenderedRatio = engineRatio;
                return;
            }
        }

        _lastRenderedRatio = engineRatio;

        if (frame.DurationJustChanged)
        {
            ProgressBarBg.Visibility = frame.Duration.TotalSeconds > 0 ? Visibility.Visible : Visibility.Collapsed;

            _isSeekSpringActive = false;
            _springSettleFrames = 0;
            _progressVelocity = 0;
            StopSpringRenderLoop();

            DisplayRatio = engineRatio;
            _progressTargetRatio = engineRatio;
            _progressSpringTargetRatio = engineRatio;
            ProgressBarScale.ScaleX = engineRatio;

        }
        else if (_isSeekSpringActive)
        {
            if (!Spring.IsActive)
            {
                _isSeekSpringActive = false;
                DisplayRatio = Spring.DisplayRatio;
                _progressVelocity = 0;
                _springSettleFrames = 0;
                UpdateProgressTimerState();
                RuntimeLog.Debug("PROGRESS-SPRING", () => $"settled at ratio={DisplayRatio:F4} engineRatio={engineRatio:F4}");

                if (_isClickSeekPending)
                {
                    DisplayRatio = _progressTargetRatio;
                    ProgressBarScale.ScaleX = _progressTargetRatio;
                    CurrentTimeText.Text = FormatTime(_dragSeekPosition);
                    return;
                }

                DisplayRatio = engineRatio;
                ProgressBarScale.ScaleX = engineRatio;
                _suppressExternalSeekDetectionUntil = renderNow.AddSeconds(1.5);
            }
            else
            {
                if (!Spring.IsHooked)
                {
                    StartSpringRenderLoop();
                }

                if (!_isClickSeekPending && renderNow >= _protectSpringTargetUntil)
                {
                    _progressTargetRatio = engineRatio;
                }
                if (_isClickSeekPending)
                {
                    CurrentTimeText.Text = FormatTime(_dragSeekPosition);
                }
                else if (frame.Duration.TotalSeconds > 0)
                {
                    var pos = TimeSpan.FromSeconds(DisplayRatio * frame.Duration.TotalSeconds);
                    int sec = (int)pos.TotalSeconds;
                    if (sec != LastDisplayedSecond)
                    {
                        LastDisplayedSecond = sec;
                        CurrentTimeText.Text = FormatTime(pos);
                        RemainingTimeText.Text = FormatDuration(frame.Duration);
                    }
                }
                return;
            }
        }

        if (_isClickSeekPending)
        {
            CurrentTimeText.Text = FormatTime(_dragSeekPosition);
            return;
        }

        if (!_isSeekSpringActive)
        {



            bool isRealtimeProgressing = frame.State == ProgressState.Playing || frame.State == ProgressState.Seeking;
            double rawTargetRatio = engineRatio;
            double rawRatioDiff = Math.Abs(rawTargetRatio - DisplayRatio);
            double rawDiffSeconds = rawRatioDiff * frame.Duration.TotalSeconds;

            if (!_isDraggingProgress &&
                !_isRewindAnimating &&
                !_isCatchUpAnimating &&
                !_isClickSeekPending &&
                !_isSeekSpringActive &&
                frame.Duration.TotalSeconds > 0 &&
                DisplayRatio > 0.005 &&
                renderNow >= _suppressExternalSeekDetectionUntil)
            {
                bool playing = frame.State == ProgressState.Playing ||
                               frame.State == ProgressState.Seeking;
                bool forwardJump = rawTargetRatio > DisplayRatio &&
                                   rawDiffSeconds >= 1.2;
                bool backwardJump = rawTargetRatio < DisplayRatio &&
                                    rawDiffSeconds >= 0.6;

                bool isLikelyTrackSkip = DisplayRatio > 0.92 &&
                                         rawTargetRatio < 0.05;

                bool isFalseZeroReport = rawTargetRatio < 0.001 &&
                                         DisplayRatio > 0.05 &&
                                         playing;

                bool isSuspiciousBackward = backwardJump && playing &&
                                            rawDiffSeconds > 30.0 &&
                                            DisplayRatio > 0.10;

                if (rawDiffSeconds >= 0.6)
                {
                    RuntimeLog.Log(ProgressSeekLogTag,
                        $"check from={DisplayRatio:F4} to={rawTargetRatio:F4} " +
                        $"diffSec={rawDiffSeconds:F2} state={frame.State} playing={playing} " +
                        $"forward={forwardJump} backward={backwardJump} likelySkip={isLikelyTrackSkip} " +
                        $"falseZero={isFalseZeroReport} suspiciousBack={isSuspiciousBackward}");
                }

                if (playing && (forwardJump || backwardJump) && !isLikelyTrackSkip && !isFalseZeroReport && !isSuspiciousBackward)
                {
                    AnimateExternalSeekTo(rawTargetRatio);
                    _suppressExternalSeekDetectionUntil = renderNow.AddMilliseconds(800);

                    return;
                }

                if (isFalseZeroReport || isSuspiciousBackward)
                {

                    return;
                }
            }

            if (rawDiffSeconds >= SOURCE_SMOOTH_SECONDS)
            {
                RuntimeLog.Debug("PROGRESS-RENDER-JUMP", () =>
                    $"*** LARGE-RENDER-SNAP: display={DisplayRatio:F4} -> target={rawTargetRatio:F4} " +
                    $"diffSec={rawDiffSeconds:F2}s direction={(rawTargetRatio < DisplayRatio ? "BACKWARD" : "forward")} " +
                    $"state={frame.State} pos={frame.Position.TotalSeconds:F2}s dur={frame.Duration.TotalSeconds:F1}s");
                DisplayRatio = rawTargetRatio;
                _progressTargetRatio = rawTargetRatio;
                _progressSpringTargetRatio = rawTargetRatio;
                _progressVelocity = 0;
                ProgressBarScale.ScaleX = DisplayRatio;
            }
            else
            {
                _progressTargetRatio = rawTargetRatio;

                double backwardThreshold = 0.5;
                if (_currentMediaInfo != null)
                {
                    bool isBrowserSource = IsBrowserProgressSource();

                    if (isBrowserSource)
                    {
                        backwardThreshold = 1.5;
                    }
                }

                bool isUserSeekWindow = frame.State == ProgressState.Seeking || renderNow < _allowProgressBackwardRenderUntil;
                bool isPostSeekStabilization = renderNow < _blockBackwardAfterSeekUntil;

                if (_progressTargetRatio < DisplayRatio)
                {
                    double backwardSeconds = (DisplayRatio - _progressTargetRatio) * frame.Duration.TotalSeconds;

                    if (isUserSeekWindow)
                    {
                        RuntimeLog.Debug(ProgressRenderBackwardLogTag, () =>
                            $"allowed (user-seek): display={DisplayRatio:F4} target={_progressTargetRatio:F4} backSec={backwardSeconds:F2}s");
                    }
                    else if (isPostSeekStabilization)
                    {
                        _progressTargetRatio = DisplayRatio;
                    }
                    else if (isRealtimeProgressing)
                    {
                        bool isBrowserSourceForBackward = _currentMediaInfo != null &&
                            _currentMediaInfo.Platform is MediaPlatform.Browser or MediaPlatform.YouTube or MediaPlatform.SoundCloud;

                        double allowedBackward = isBrowserSourceForBackward ? 0.5 : 0.08;
                        if (backwardSeconds > allowedBackward)
                        {
                            RuntimeLog.Debug(ProgressRenderBackwardLogTag, () =>
                                $"BLOCKED (playback): display={DisplayRatio:F4} target={_progressTargetRatio:F4} " +
                                $"backSec={backwardSeconds:F2}s > allowed={allowedBackward:F2}s isBrowser={isBrowserSourceForBackward}");
                            _progressTargetRatio = DisplayRatio;
                        }
                        else
                        {
                            RuntimeLog.Debug(ProgressRenderBackwardLogTag, () =>
                                $"allowed (small): display={DisplayRatio:F4} target={_progressTargetRatio:F4} " +
                                $"backSec={backwardSeconds:F2}s <= allowed={allowedBackward:F2}s");
                        }
                    }
                    else if (backwardSeconds > backwardThreshold)
                    {
                        double maxBackwardStepSeconds = 0.22;
                        double maxBackwardRatioStep = maxBackwardStepSeconds / frame.Duration.TotalSeconds;
                        double cappedTarget = Math.Max(_progressTargetRatio, DisplayRatio - maxBackwardRatioStep);
                        RuntimeLog.Debug(ProgressRenderBackwardLogTag, () =>
                            $"capped (paused): display={DisplayRatio:F4} target={_progressTargetRatio:F4} " +
                            $"backSec={backwardSeconds:F2}s capped={cappedTarget:F4}");
                        _progressTargetRatio = cappedTarget;
                    }
                }

                DisplayRatio = _progressTargetRatio;
            }

            DisplayRatio = Math.Clamp(DisplayRatio, 0, 1);
            ProgressBarScale.ScaleX = DisplayRatio;
        }

        if (!_isDraggingProgress && frame.Duration.TotalSeconds > 0 && frame.State == ProgressState.Playing)
        {
            if (_lastProgressMovementRatio < 0 || Math.Abs(DisplayRatio - _lastProgressMovementRatio) > 0.0015)
            {
                _lastProgressMovementRatio = DisplayRatio;
                _lastProgressMovementUtc = renderNow;
            }
            else
            {
                var stuckFor = renderNow - _lastProgressMovementUtc;
                var visualDrift = Math.Abs(engineRatio - DisplayRatio);
                if (stuckFor.TotalMilliseconds > 900 && visualDrift > 0.02)
                {
                    _lastProgressMovementUtc = renderNow;
                }
            }
        }

        int currentSecond = (int)frame.Position.TotalSeconds;
        if (currentSecond != LastDisplayedSecond)
        {
            LastDisplayedSecond = currentSecond;
            CurrentTimeText.Text = FormatTime(frame.Position);
            RemainingTimeText.Text = FormatDuration(frame.Duration);
        }
    }

    private static string FormatTime(TimeSpan time) => MediaProgressHelpers.FormatTime(time);
    private static string FormatDuration(TimeSpan duration) => duration.TotalSeconds > 0 ? FormatTime(duration) : "--:--";

    internal static TimeSpan ParseCurrentTime(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return TimeSpan.Zero;
        var parts = text.Trim().Split(':');
        if (parts.Length == 2 &&
            int.TryParse(parts[0], out int m) &&
            double.TryParse(parts[1], out double s))
        {
            return TimeSpan.FromSeconds(m * 60 + s);
        }
        if (parts.Length == 3 &&
            int.TryParse(parts[0], out int h) &&
            int.TryParse(parts[1], out int m2) &&
            double.TryParse(parts[2], out double s2))
        {
            return TimeSpan.FromSeconds(h * 3600 + m2 * 60 + s2);
        }
        return TimeSpan.Zero;
    }

    #region Progress Bar Click and Drag to Seek

    private void ProgressBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_currentMediaInfo?.CanSeekTimeline != true) return;

        e.Handled = true;

        StopCatchUpAnimation();

        _isClickSeekPending = true;
        _mouseDownPoint = e.GetPosition(ProgressBarContainer);
        ProgressBarContainer.CaptureMouse();

        var duration = _progressEngine.GetUiFrame().Duration;
        if (duration.TotalSeconds <= 0) return;

        double ratio = _mouseDownPoint.X / ProgressBarContainer.ActualWidth;
        ratio = Math.Clamp(ratio, 0, 1);

        _dragSeekPosition = TimeSpan.FromSeconds(duration.TotalSeconds * ratio);
        _progressTargetRatio = ratio;
        if (!_isSeekSpringActive)
        {
            _progressSpringTargetRatio = DisplayRatio;
            _progressVelocity = 0;
        }
        _springSettleFrames = 0;
        _isSeekSpringActive = true;
        _protectSpringTargetUntil = DateTime.UtcNow.AddSeconds(1.5);
        StartSpringRenderLoop();

        CurrentTimeText.Text = FormatTime(_dragSeekPosition);
    }

    private void ProgressBar_MouseMove(object sender, MouseEventArgs e)
    {
        e.Handled = true;

        if (e.LeftButton != MouseButtonState.Pressed) return;
        if (!_isClickSeekPending && !_isDraggingProgress) return;

        var currentPos = e.GetPosition(ProgressBarContainer);

        if (_isClickSeekPending && !_isDraggingProgress)
        {
            double dist = Math.Abs(currentPos.X - _mouseDownPoint.X);
            double threshold = _isSeekSpringActive ? 25.0 : DRAG_THRESHOLD;
            if (dist < threshold) return;

            _isClickSeekPending = false;
            _isDraggingProgress = true;
            _isSeekSpringActive = false;
            _progressVelocity = 0;
            _springSettleFrames = 0;
            StopSpringRenderLoop();
            StopCatchUpAnimation();
            BufferingChanged?.Invoke(true);
        }

        if (_isDraggingProgress)
        {
            UpdateProgressFromMouse(e);
        }
    }

    private async void ProgressBar_MouseUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;

        bool wasDragging = _isDraggingProgress;
        bool wasClickSeek = _isClickSeekPending;
        bool prevExpanded = _isProgressBarExpanded;
        BufferingChanged?.Invoke(false);

        _isDraggingProgress = false;
        _isClickSeekPending = false;

        ProgressBarContainer.ReleaseMouseCapture();

        _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, (System.Action)(() =>
        {

            if (prevExpanded && !ProgressBarContainer.IsMouseOver)
            {

                _isProgressBarExpanded = false;
                AnimateProgressBarHover(false);
            }

        }));

        if (wasDragging || wasClickSeek)
        {
            await SeekToPosition(_dragSeekPosition);
        }
    }

    private void ProgressBar_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (!_isDraggingProgress && !_isClickSeekPending) return;
        _isDraggingProgress = false;
        _isClickSeekPending = false;
        BufferingChanged?.Invoke(false);
        if (_isProgressBarExpanded && !ProgressBarContainer.IsMouseOver)
        {
            _isProgressBarExpanded = false;
            AnimateProgressBarHover(false);
        }
        UpdateProgressTimerState();
    }

    private void UpdateProgressFromMouse(MouseEventArgs e)
    {
        if (_currentMediaInfo?.CanSeekTimeline != true) return;
        var duration = _progressEngine.GetUiFrame().Duration;
        if (duration.TotalSeconds <= 0) return;

        var position = e.GetPosition(ProgressBarContainer);
        double ratio = position.X / ProgressBarContainer.ActualWidth;
        ratio = Math.Clamp(ratio, 0, 1);

        ProgressBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        DisplayRatio = ratio;
        _progressTargetRatio = ratio;
        _progressSpringTargetRatio = ratio;
        _progressVelocity = 0;
        _springSettleFrames = 0;
        ProgressBarScale.ScaleX = ratio;

        _dragSeekPosition = TimeSpan.FromSeconds(duration.TotalSeconds * ratio);

        CurrentTimeText.Text = FormatTime(_dragSeekPosition);
    }

    internal void AnimateSeekProgressTo(double targetRatio)
    {
        targetRatio = Math.Clamp(targetRatio, 0, 1);

        _progressTargetRatio = targetRatio;
        if (!_isSeekSpringActive)
        {
            _progressSpringTargetRatio = DisplayRatio;
            _progressVelocity = 0;
        }
        _springSettleFrames = 0;
        _isSeekSpringActive = true;
        _protectSpringTargetUntil = DateTime.UtcNow.AddSeconds(1.5);
        StartSpringRenderLoop();
    }

    private UiScheduledTimer? _rewindTextTimer;
    private bool _isRewindAnimating = false;

    internal void StopRewindTextAnimation()
    {
        if (_rewindTextTimer != null)
        {
            _rewindTextTimer.Stop();
            _rewindTextTimer.Dispose();
            _rewindTextTimer = null;
        }
    }

    internal TimeSpan GetPositionForRatio(double ratio)
    {
        var frame = _progressEngine.GetUiFrame();
        var duration = frame.Duration;
        if (frame.ShowIndeterminate || duration.TotalSeconds <= 0) return TimeSpan.Zero;
        return TimeSpan.FromSeconds(Math.Clamp(ratio, 0, 1) * duration.TotalSeconds);
    }

    internal async Task SeekToPosition(TimeSpan newPos)
    {
        var frame = _progressEngine.GetUiFrame();
        var duration = frame.Duration;
        if (_currentMediaInfo?.CanSeekTimeline != true || frame.ShowIndeterminate || duration.TotalSeconds <= 0) return;

        newPos = ClampSeekTarget(newPos, duration);

        try
        {
            _allowProgressBackwardRenderUntil = DateTime.UtcNow.AddSeconds(3);
            _blockBackwardAfterSeekUntil = DateTime.UtcNow.AddSeconds(3.5);
            _suppressExternalSeekDetectionUntil = DateTime.UtcNow.AddSeconds(3);
            _progressEngine.NotifyUserSeek(newPos, isPlaying: _currentMediaInfo?.IsPlaying);

            double targetRatio = newPos.TotalSeconds / duration.TotalSeconds;
            targetRatio = Math.Clamp(targetRatio, 0, 1);
            AnimateSeekProgressTo(targetRatio);
            CurrentTimeText.Text = FormatTime(newPos);
            LastDisplayedSecond = (int)newPos.TotalSeconds;

            await _mediaService.SeekAsync(newPos);

            UpdateProgressTimerState();
        }
        catch (Exception ex)
        {
            RuntimeLog.Error(ProgressSeekLogTag, ex.ToString());
        }
    }

    private void ProgressBar_MouseEnter(object sender, MouseEventArgs e)
    {
        _isProgressBarExpanded = true;
        AnimateProgressBarHover(true);
    }

    private void ProgressBar_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!_isDraggingProgress && !_isClickSeekPending)
        {
            _isProgressBarExpanded = false;
            AnimateProgressBarHover(false);
        }
    }

    #endregion

    #region Progress Bar Animation on Expand

    internal void UpdateProgressTimerState()
    {
        bool springActive = _isSeekSpringActive || (_springRenderer != null && Spring.IsActive);
        bool shouldRender = !_disposed && IsVisible && _currentMediaInfo?.HasTimeline == true &&
            (_progressEngine.RequiresPositionUpdates || springActive || _isRewindAnimating || _isCatchUpAnimating);
        SetProgressRenderingEnabled(shouldRender);
    }

    #endregion

    #region Progress Catch-Up Animation

    private bool _isCatchUpAnimating = false;

    internal void StartProgressCatchUpAnimation()
    {
        if (_currentMediaInfo == null) return;

        var frame = _progressEngine.GetUiFrame();
        if (frame.ShowIndeterminate || frame.Duration.TotalSeconds <= 0) return;

        double targetRatio = frame.Position.TotalSeconds / frame.Duration.TotalSeconds;
        targetRatio = Math.Clamp(targetRatio, 0, 1);

        if (_isRewindAnimating)
        {
            _isRewindAnimating = false;
            StopRewindTextAnimation();
            ProgressBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        }

        if (_isSeekSpringActive && Spring.IsActive)
        {
            return;
        }

        double fromRatio = Math.Clamp(DisplayRatio, 0, 1);

        if (Math.Abs(fromRatio - targetRatio) < 0.005)
        {
            _isCatchUpAnimating = false;
            ProgressBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            ProgressBarScale.ScaleX = targetRatio;
            DisplayRatio = targetRatio;
            _progressTargetRatio = targetRatio;
            _progressSpringTargetRatio = targetRatio;
            _lastRenderedRatio = targetRatio;
            _progressVelocity = 0;
            CurrentTimeText.Text = FormatTime(frame.Position);
            RemainingTimeText.Text = FormatDuration(frame.Duration);
            UpdateProgressTimerState();
            return;
        }

        _isCatchUpAnimating = true;
        UpdateProgressTimerState();
        _progressTargetRatio = targetRatio;
        _progressSpringTargetRatio = targetRatio;
        _progressVelocity = 0;
        ProgressBarScale.ScaleX = fromRatio;

        double gapRatio = Math.Abs(targetRatio - fromRatio);
        int durationMs = (int)Math.Clamp(400 + gapRatio * 300, 400, 650);
        var catchUpDuration = TimeSpan.FromMilliseconds(durationMs);
        var catchUpAnim = new DoubleAnimation(fromRatio, targetRatio, new Duration(catchUpDuration))
        {
            EasingFunction = _easeExpOut6,
            FillBehavior = FillBehavior.Stop
        };
        Timeline.SetDesiredFrameRate(catchUpAnim, VNotch.Services.AnimationConfig.TargetFps);

        long seqAtStart = _trackChangeSequence;

        catchUpAnim.Completed += (s, e) =>
        {
            if (_trackChangeSequence != seqAtStart || (!_isCatchUpAnimating && !_isRewindAnimating)) return;

            _isCatchUpAnimating = false;
            ProgressBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            ProgressBarScale.ScaleX = targetRatio;
            DisplayRatio = targetRatio;
            _lastRenderedRatio = targetRatio;
            UpdateProgressTimerState();
        };

        ProgressBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, catchUpAnim);
        CurrentTimeText.Text = FormatTime(frame.Position);
        RemainingTimeText.Text = FormatDuration(frame.Duration);
    }

    internal void StopCatchUpAnimation()
    {
        if (!_isCatchUpAnimating) return;
        _isCatchUpAnimating = false;
        UpdateProgressTimerState();
    }

    internal void AnimateTrackChangeRewindToZero(double fromRatio, TimeSpan newDuration)
    {
        fromRatio = Math.Clamp(fromRatio, 0, 1);
        double targetRatio = 0;

        _isSeekSpringActive = false;
        _springSettleFrames = 0;
        _progressVelocity = 0;
        StopSpringRenderLoop();
        _isRewindAnimating = true;
        UpdateProgressTimerState();

        long seqAtStart = _trackChangeSequence;
        var duration = TimeSpan.FromMilliseconds(Math.Clamp(280 + fromRatio * 180, 300, 450));

        ProgressBar.Visibility = Visibility.Visible;
        ProgressBarBg.Visibility = Visibility.Visible;
        if (IndeterminateProgress.Visibility == Visibility.Visible)
        {
            IndeterminateProgress.BeginAnimation(OpacityProperty, null);
            IndeterminateProgress.Visibility = Visibility.Collapsed;
        }

        ProgressBarContainer.BeginAnimation(OpacityProperty, null);
        ProgressBarContainer.Opacity = 1;
        ProgressBarMainScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        ProgressBarMainScale.ScaleY = 1;

        var anim = new DoubleAnimation(fromRatio, targetRatio, new Duration(duration))
        {
            EasingFunction = _easeExpOut6,
            FillBehavior = FillBehavior.Stop
        };
        Timeline.SetDesiredFrameRate(anim, VNotch.Services.AnimationConfig.TargetFps);

        anim.Completed += (s, e) =>
        {
            if (_trackChangeSequence != seqAtStart || (!_isCatchUpAnimating && !_isRewindAnimating)) return;

            ProgressBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            ProgressBarScale.ScaleX = 0;
            DisplayRatio = 0;
            _progressTargetRatio = 0;
            _progressSpringTargetRatio = 0;
            _lastRenderedRatio = 0;
            LastDisplayedSecond = 0;
            _isSeekSpringActive = false;
            _isClickSeekPending = false;
            _progressVelocity = 0;
            _springSettleFrames = 0;
            StopSpringRenderLoop();
            CurrentTimeText.Text = "0:00";
            RemainingTimeText.Text = newDuration > TimeSpan.Zero ? FormatDuration(newDuration) : "0:00";
            StopRewindTextAnimation();
            _isRewindAnimating = false;

            UpdateProgressTimerState();
        };

        _progressTargetRatio = 0;
        _progressSpringTargetRatio = 0;
        DisplayRatio = 0;

        ProgressBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        ProgressBarScale.ScaleX = fromRatio;
        ProgressBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);

        double startSeconds = ParseCurrentTime(CurrentTimeText.Text).TotalSeconds;
        if (startSeconds <= 0 && fromRatio > 0 && newDuration.TotalSeconds > 0)
        {
            startSeconds = newDuration.TotalSeconds * fromRatio;
        }

        StopRewindTextAnimation();
        if (startSeconds > 0)
        {
            var startTime = DateTime.UtcNow;
            var totalMs = duration.TotalMilliseconds;
            _rewindTextTimer = new UiScheduledTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(16)
            };
            _rewindTextTimer.Tick += (s, e) =>
            {
                if (_trackChangeSequence != seqAtStart)
                {
                    StopRewindTextAnimation();
                    return;
                }
                double t = (DateTime.UtcNow - startTime).TotalMilliseconds / totalMs;
                if (t >= 1.0)
                {
                    CurrentTimeText.Text = "0:00";
                    StopRewindTextAnimation();
                    return;
                }
                double eased = 1.0 - Math.Pow(2.0, -6.0 * t);
                double currentSec = Math.Max(0, startSeconds * (1.0 - eased));
                CurrentTimeText.Text = FormatTime(TimeSpan.FromSeconds(currentSec));
            };
            _rewindTextTimer.Start();
        }
        else
        {
            CurrentTimeText.Text = "0:00";
        }
    }

    internal void AnimateExternalSeekTo(double targetRatio)
    {
        targetRatio = Math.Clamp(targetRatio, 0, 1);
        double fromRatio = Math.Clamp(DisplayRatio, 0, 1);
        double delta = Math.Abs(fromRatio - targetRatio);

        StopRewindTextAnimation();

        if (delta <= 0.005)
        {
            RuntimeLog.Log(ProgressSeekLogTag, $"animate skipped (delta={delta:F4})");
            ProgressBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            ProgressBarScale.ScaleX = targetRatio;
            DisplayRatio = targetRatio;
            _progressTargetRatio = targetRatio;
            _progressSpringTargetRatio = targetRatio;
            _progressVelocity = 0;
            _springSettleFrames = 0;
            _isSeekSpringActive = false;
            CurrentTimeText.Text = FormatTime(GetPositionForRatio(targetRatio));
            _lastRenderedRatio = targetRatio;
            UpdateProgressTimerState();
            return;
        }

        _isSeekSpringActive = false;
        _springSettleFrames = 0;
        _progressVelocity = 0;
        StopSpringRenderLoop();
        _isRewindAnimating = true;
        UpdateProgressTimerState();

        var duration = TimeSpan.FromMilliseconds(Math.Clamp(220 + delta * 320, 240, 420));

        RuntimeLog.Log(ProgressSeekLogTag,
            $"animate start from={fromRatio:F4} to={targetRatio:F4} delta={delta:F4} dur={duration.TotalMilliseconds:F0}ms");

        var anim = new DoubleAnimation(fromRatio, targetRatio, new Duration(duration))
        {
            EasingFunction = _easeExpOut6,
            FillBehavior = FillBehavior.Stop
        };
        Timeline.SetDesiredFrameRate(anim, VNotch.Services.AnimationConfig.TargetFps);

        long seqAtStart = _trackChangeSequence;

        anim.Completed += (s, e) =>
        {
            if (_trackChangeSequence != seqAtStart || (!_isCatchUpAnimating && !_isRewindAnimating)) return;

            RuntimeLog.Log(ProgressSeekLogTag, $"animate completed -> {targetRatio:F4}");
            ProgressBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            ProgressBarScale.ScaleX = targetRatio;
            DisplayRatio = targetRatio;
            _progressTargetRatio = targetRatio;
            _progressSpringTargetRatio = targetRatio;
            _lastRenderedRatio = targetRatio;
            LastDisplayedSecond = (int)GetPositionForRatio(targetRatio).TotalSeconds;
            CurrentTimeText.Text = FormatTime(GetPositionForRatio(targetRatio));
            StopRewindTextAnimation();
            _isRewindAnimating = false;

            _blockBackwardAfterSeekUntil = DateTime.UtcNow.AddSeconds(3.5);
            UpdateProgressTimerState();
        };

        _progressTargetRatio = targetRatio;
        _progressSpringTargetRatio = targetRatio;

        ProgressBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        ProgressBarScale.ScaleX = fromRatio;
        ProgressBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);

        var startTime = DateTime.UtcNow;
        var totalMs = duration.TotalMilliseconds;
        _rewindTextTimer = new UiScheduledTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _rewindTextTimer.Tick += (s, e) =>
        {
            if (_trackChangeSequence != seqAtStart)
            {
                StopRewindTextAnimation();
                return;
            }
            double t = (DateTime.UtcNow - startTime).TotalMilliseconds / totalMs;
            if (t >= 1.0)
            {
                CurrentTimeText.Text = FormatTime(GetPositionForRatio(targetRatio));
                StopRewindTextAnimation();
                return;
            }
            double eased = 1.0 - Math.Pow(2.0, -6.0 * t);
            double current = fromRatio + (targetRatio - fromRatio) * eased;
            CurrentTimeText.Text = FormatTime(GetPositionForRatio(current));
        };
        _rewindTextTimer.Start();
    }

    #endregion
}

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using VNotch.Services;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch;

public partial class MainWindow
{
    private bool _localAudioView;
    private bool _isAudioView
    {
        get => _localAudioView;
        set
        {
            _localAudioView = value;
            _notchState.IsAudioView = value;
        }
    }
    private const double _audioViewWidth = 720;

    private const double _audioViewMaxHeight = 378;
    private const double _audioViewMinHeight = 150;
    private const double _audioViewChrome = 66;
    private double _audioViewHeight = _audioViewMaxHeight;
    private int _audioNotchHeightGeneration;

    private AudioMixerService? _audioMixerServiceCached;
    private AudioMixerService AudioMixer =>
        _audioMixerServiceCached ??= (AudioMixerService)App.Services.GetService(typeof(AudioMixerService))!;

    private System.Windows.Threading.DispatcherTimer? _audioPollTimer;

    private void StartAudioPoll()
    {
        if (_audioPollTimer == null)
        {
            _audioPollTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(600)
            };
            _audioPollTimer.Tick += (_, _) => PollAudioVolumes();
        }
        _audioPollTimer.Start();
    }

    private void StopAudioPoll() => _audioPollTimer?.Stop();

    private bool _audioTopFadeShown;
    private bool _audioBottomFadeShown;

    private void AudioScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
        => UpdateAudioScrollFades();

    private void UpdateAudioScrollFades()
    {
        if (AudioScrollViewer == null || AudioFadeTopStop == null || AudioFadeBottomStop == null) return;

        double offset = AudioScrollViewer.VerticalOffset;
        double scrollable = AudioScrollViewer.ScrollableHeight;

        bool showTop = offset > 1.0;
        bool showBottom = scrollable > 1.0 && offset < scrollable - 1.0;

        if (showTop != _audioTopFadeShown)
        {
            _audioTopFadeShown = showTop;
            AnimateAudioFadeStop(AudioFadeTopStop, showTop);
        }
        if (showBottom != _audioBottomFadeShown)
        {
            _audioBottomFadeShown = showBottom;
            AnimateAudioFadeStop(AudioFadeBottomStop, showBottom);
        }
    }

    private void AnimateAudioFadeStop(GradientStop stop, bool fade)
    {
        var to = fade ? Colors.Transparent : Colors.Black;
        var anim = new ColorAnimation(to, new Duration(TimeSpan.FromMilliseconds(180)))
        {
            EasingFunction = new System.Windows.Media.Animation.QuadraticEase
            {
                EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
            }
        };
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(anim, VNotch.Services.AnimationConfig.TargetFps);
        stop.BeginAnimation(GradientStop.ColorProperty, anim);
    }

    private void AudioIconButton_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_isAudioView || _isAnimating) return;
        if (_isTimerView)
        {
            SwitchFromTimerToAudioView();
        }
        else if (_isSecondaryView)
        {
            SwitchFromSecondaryToAudioView();
        }
        else
        {
            SwitchToAudioView();
        }
    }

    private void SwitchFromSecondaryToAudioView(long? transitionId = null)
    {
        if (transitionId == null)
        {
            _transitionCoordinator.RequestView(VNotch.Models.NotchView.AudioMixer, "SwitchFromSecondaryToAudioView");
            return;
        }

        SwitchToAudioViewCore(transitionId.Value, fromSecondary: true, fromTimer: false);
    }

    private void SwitchFromTimerToAudioView(long? transitionId = null)
    {
        if (transitionId == null)
        {
            _transitionCoordinator.RequestView(VNotch.Models.NotchView.AudioMixer, "SwitchFromTimerToAudioView");
            return;
        }

        SwitchToAudioViewCore(transitionId.Value, fromSecondary: false, fromTimer: true);
    }

    private void SwitchToAudioView(long? transitionId = null)
    {
        if (transitionId == null)
        {
            _transitionCoordinator.RequestView(VNotch.Models.NotchView.AudioMixer, "SwitchToAudioView");
            return;
        }

        SwitchToAudioViewCore(transitionId.Value, fromSecondary: _isSecondaryView, fromTimer: _isTimerView);
    }

    private void SwitchToAudioViewCore(long transitionId, bool fromSecondary, bool fromTimer)
    {
        int generation = (int)transitionId;
        _viewTransitionGeneration = generation;
        CancelTimerEditingInstant();

        FrameworkElement outgoing;
        bool fromPrimary = !fromSecondary && !fromTimer;
        if (fromTimer) outgoing = TimerContent;
        else if (fromSecondary) outgoing = SecondaryContent;
        else outgoing = ExpandedContent;

        _isAudioView = true;
        _isSecondaryView = false;
        _isTimerView = false;
        _isAnimating = true;
        SuppressPrivacyDot();
        SuspendSpotifyCanvasLifecycle();
        _lastViewSwitchUtc = DateTime.UtcNow;
        _isScrollSessionLocked = true;

        if (fromSecondary)
        {
            if (IsCameraPreviewLifecycleActive)
            {
                StopCameraPreviewForViewExit();
            }
            else
            {
                ResetCameraSectionLayoutInstant();
            }
            DisableKeyboardInput();
        }
        if (fromTimer)
        {
            RestoreTimerContentOpacity();
        }
        if (fromPrimary)
        {
            HideMediaBackground();
            if (LyricsBlurBackground != null && LyricsBlurBackground.Visibility == Visibility.Visible)
            {
                LyricsBlurBackground.BeginAnimation(OpacityProperty, null);
                LyricsBlurBackground.Opacity = 0;
                LyricsBlurBackground.Visibility = Visibility.Collapsed;
            }
        }

        UpdateNavIconsActiveState();
        NavIconsPanel.Visibility = Visibility.Visible;
        NavIconsPanel.Opacity = 1;

        if (fromPrimary)
        {
            NavIconsBackground.BeginAnimation(OpacityProperty, null);
            NavIconsBackground.Opacity = 0;
            NavIconsBackground.Visibility = Visibility.Visible;
            var navBgFadeIn = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(300)))
            {
                EasingFunction = _easePowerOut3,
                BeginTime = TimeSpan.FromMilliseconds(200)
            };
            Timeline.SetDesiredFrameRate(navBgFadeIn, AnimationConfig.TargetFps);
            NavIconsBackground.BeginAnimation(OpacityProperty, navBgFadeIn);
        }
        else
        {
            NavIconsBackground.BeginAnimation(OpacityProperty, null);
            NavIconsBackground.Opacity = 1;
            NavIconsBackground.Visibility = Visibility.Visible;
        }

        bool hadSnapshot = _lastAudioSnapshot != null;
        if (!hadSnapshot)
        {
            var quickSnap = SafeCall(() => ReadAudioSnapshot(includeIcons: false));
            if (quickSnap != null)
            {
                _lastAudioSnapshot = quickSnap;
                hadSnapshot = true;
            }
        }

        if (hadSnapshot)
        {
            SetAudioLoadingState(false);
            EnsureAudioUIBuilt(_lastAudioSnapshot!);
        }
        else
        {
            SetAudioLoadingState(true);
            _audioViewHeight = _audioViewMaxHeight;
        }

        double fromW;
        double fromH;
        if (fromTimer)
        {
            fromW = NotchBorder.ActualWidth > 0 ? NotchBorder.ActualWidth : _clockViewWidth;
            fromH = NotchBorder.ActualHeight > 0 ? NotchBorder.ActualHeight : _clockViewHeight;
        }
        else
        {
            fromW = NotchBorder.ActualWidth > 0 ? NotchBorder.ActualWidth : _expandedWidth;
            fromH = NotchBorder.ActualHeight > 0 ? NotchBorder.ActualHeight : _expandedHeight;
        }

        double openWindowHeight = Math.Max(fromH, _audioViewHeight);
        ResizeHostWindowHeight(openWindowHeight);

        AnimateAudioViewSwap(
            outgoing, AudioContent,
            new Size(fromW, fromH), new Size(_audioViewWidth, _audioViewHeight),
            prepIncoming: () =>
            {
                if (AudioScrollViewer != null)
                {
                    AudioScrollViewer.Width = _audioViewWidth - 38;
                    AudioScrollViewer.HorizontalAlignment = HorizontalAlignment.Left;
                    AudioScrollViewer.VerticalAlignment = VerticalAlignment.Top;
                    AudioScrollViewer.BeginAnimation(OpacityProperty, null);
                    AudioScrollViewer.Visibility = Visibility.Visible;
                    AudioScrollViewer.Opacity = 1;
                    AudioScrollViewer.ScrollToTop();
                    VNotch.Presenters.NotchContentTransitionPresenter.ClearTransformAndEffects(AudioScrollViewer);
                }
            },
            onComplete: () =>
            {
                if (openWindowHeight > _audioViewHeight)
                    ResizeHostWindowHeight(_audioViewHeight);
                if (!ApplyPendingAudioSnapshot())
                    SettleAudioNotchToFit();
                StartAudioPoll();
            },
            generation: generation);

        RefreshAudioData(SettleAudioNotchToFit);
    }

    private void SwitchFromAudioToPrimaryView(long? transitionId = null)
    {
        if (transitionId == null)
        {
            _transitionCoordinator.RequestView(VNotch.Models.NotchView.Media, "SwitchFromAudioToPrimaryView");
            return;
        }

        int generation = (int)transitionId;
        _viewTransitionGeneration = generation;
        _isAudioView = false;
        StopAudioPoll();
        _audioMixerServiceCached?.ReleaseSessionCache();
        _isAnimating = true;
        _lastViewSwitchUtc = DateTime.UtcNow;
        _isScrollSessionLocked = true;

        UpdateNavIconsActiveState();
        NavIconsBackground.BeginAnimation(OpacityProperty, null);
        NavIconsBackground.Opacity = 0;
        NavIconsBackground.Visibility = Visibility.Collapsed;

        MusicCompactContent.BeginAnimation(OpacityProperty, null);
        MusicCompactContent.Opacity = 0;
        MusicCompactContent.Visibility = Visibility.Collapsed;
        CollapsedContent.BeginAnimation(OpacityProperty, null);
        CollapsedContent.Opacity = 0;
        CollapsedContent.Visibility = Visibility.Collapsed;

        double fromW = NotchBorder.ActualWidth > 0 ? NotchBorder.ActualWidth : _audioViewWidth;
        double fromH = NotchBorder.ActualHeight > 0 ? NotchBorder.ActualHeight : _audioViewHeight;

        AnimateAudioViewSwap(
            AudioContent, ExpandedContent,
            new Size(fromW, fromH), new Size(_expandedWidth, _expandedHeight),
            prepIncoming: () =>
            {
                ExpandedContent.Effect = null;
                ExpandedContent.Width = _expandedWidth - 16;
                ExpandedContent.Height = _expandedHeight - 10;
            },
            onComplete: () =>
            {
                RestoreExpandedWindowSize();
                ResumeSpotifyCanvasLifecycle();
                ShowMediaBackground();
                UpdateProgressSectionLayout();
                RefreshMediaMarquee();
                FadeInLyricsBlurBackgroundIfActive();
            },
            generation: generation);
    }

    private void SwitchFromAudioToSecondaryView(long? transitionId = null)
    {
        if (transitionId == null)
        {
            _transitionCoordinator.RequestView(VNotch.Models.NotchView.Secondary, "SwitchFromAudioToSecondaryView");
            return;
        }

        int generation = (int)transitionId;
        _viewTransitionGeneration = generation;
        _isAudioView = false;
        StopAudioPoll();
        _audioMixerServiceCached?.ReleaseSessionCache();
        _isSecondaryView = true;
        _isAnimating = true;
        _lastViewSwitchUtc = DateTime.UtcNow;
        _isScrollSessionLocked = true;

        UpdateNavIconsActiveState();
        UpdateShelfCapacityIndicator();

        double fromW = NotchBorder.ActualWidth > 0 ? NotchBorder.ActualWidth : _audioViewWidth;
        double fromH = NotchBorder.ActualHeight > 0 ? NotchBorder.ActualHeight : _audioViewHeight;

        AnimateAudioViewSwap(
            AudioContent, SecondaryContent,
            new Size(fromW, fromH), new Size(_expandedWidth, _expandedHeight),
            prepIncoming: () =>
            {
                EnableKeyboardInput();
                SecondaryContent.HorizontalAlignment = HorizontalAlignment.Center;
                SecondaryContent.VerticalAlignment = VerticalAlignment.Top;
                SecondaryContent.Width = _expandedWidth
                    - SecondaryContent.Margin.Left - SecondaryContent.Margin.Right;
            },
            onComplete: () =>
            {
                SecondaryContent.HorizontalAlignment = HorizontalAlignment.Center;
                SecondaryContent.Width = _expandedWidth
                    - SecondaryContent.Margin.Left - SecondaryContent.Margin.Right;
                SecondaryContent.UpdateLayout();
                RestoreExpandedWindowSize();
                ResetCameraSectionLayoutInstant();
            },
            generation: generation);
    }

    private void SwitchFromAudioToTimerView(long? transitionId = null)
    {
        if (transitionId == null)
        {
            _transitionCoordinator.RequestView(VNotch.Models.NotchView.Timer, "SwitchFromAudioToTimerView");
            return;
        }

        int generation = (int)transitionId;
        _viewTransitionGeneration = generation;
        _isAudioView = false;
        StopAudioPoll();
        _audioMixerServiceCached?.ReleaseSessionCache();
        _isTimerView = true;
        _isAnimating = true;
        _lastViewSwitchUtc = DateTime.UtcNow;
        _isScrollSessionLocked = true;

        UpdateTimerNavIconsState();

        double fromW = NotchBorder.ActualWidth > 0 ? NotchBorder.ActualWidth : _audioViewWidth;
        double fromH = NotchBorder.ActualHeight > 0 ? NotchBorder.ActualHeight : _audioViewHeight;

        AnimateAudioViewSwap(
            AudioContent, TimerContent,
            new Size(fromW, fromH), new Size(_clockViewWidth, _clockViewHeight),
            prepIncoming: () =>
            {
                ApplyClockViewWindowSize();
                PrepareClockViewContentSize();
                RefreshClockView();
                RestoreTimerContentOpacity();
            },
            onComplete: () => UpdateTimerDisplay(),
            generation: generation);
    }

    private void AnimateAudioViewSwap(
        FrameworkElement outgoing, FrameworkElement incoming,
        Size notchFrom, Size notchTo,
        Action? prepIncoming, Action? onComplete, int? generation = null)
    {
        double notchFromW = notchFrom.Width;
        double notchFromH = notchFrom.Height;
        double notchToW = notchTo.Width;
        double notchToH = notchTo.Height;
        int activeGen = generation ?? _viewTransitionGeneration;
        NotchBorder.IsHitTestVisible = false;

        var durOut = new Duration(TimeSpan.FromMilliseconds(170));
        var durIn = new Duration(TimeSpan.FromMilliseconds(440));
        var inDelay = TimeSpan.FromMilliseconds(40);
        int fps = AnimationConfig.TargetFps;

        bool outIsAudio = ReferenceEquals(outgoing, AudioContent);
        bool inIsAudio = ReferenceEquals(incoming, AudioContent);

        if (outIsAudio)
        {
            var closeTranslate = new TranslateTransform(0, 0);
            outgoing.RenderTransform = closeTranslate;

            var aFade = MakeAnim(1, 0, durOut, _easeAppleIn);
            var aSlide = MakeAnim(0, 10, durOut, _easeAppleIn);
            Timeline.SetDesiredFrameRate(aSlide, fps);

            bool useContentBlur = _settings.EnableBlurEffects && !IsLiquidGlassEnabled;
            BlurEffect? outBlur = null;
            DoubleAnimation? blurOutAnim = null;
            if (useContentBlur)
            {
                outBlur = outgoing.Effect as BlurEffect ?? new BlurEffect { Radius = 0, RenderingBias = RenderingBias.Performance };
                outgoing.Effect = outBlur;
                blurOutAnim = MakeAnim(0, 6, durOut, _easeAppleIn);
            }

            aFade.Completed += (s, e) =>
            {
                if (activeGen != _viewTransitionGeneration) return;
                outgoing.Visibility = Visibility.Collapsed;
                outgoing.RenderTransform = null;
                outgoing.Effect = null;
                if (outBlur != null) outBlur.Radius = 0;
                outgoing.BeginAnimation(OpacityProperty, null);
                outgoing.Opacity = 1;
                RestoreAudioRootChildrenVisualState();
            };
            outgoing.BeginAnimation(OpacityProperty, aFade);
            closeTranslate.BeginAnimation(TranslateTransform.YProperty, aSlide);
            if (outBlur != null && blurOutAnim != null)
                outBlur.BeginAnimation(BlurEffect.RadiusProperty, blurOutAnim);
        }
        else
        {
            double outRestY = ReferenceEquals(outgoing, ExpandedContent) ? ExpandedContentRestY : 0;

            var outTranslate = new TranslateTransform(0, outRestY);
            outgoing.RenderTransform = outTranslate;

            var fadeOut = MakeAnim(1, 0, durOut, _easeAppleIn);
            var slideUp = MakeAnim(outRestY, outRestY - 10, durOut, _easeAppleIn);
            Timeline.SetDesiredFrameRate(slideUp, fps);

            bool useContentBlur = _settings.EnableBlurEffects && !IsLiquidGlassEnabled;
            BlurEffect? outBlur = null;
            DoubleAnimation? blurOutAnim = null;
            if (useContentBlur)
            {
                outBlur = outgoing.Effect as BlurEffect ?? new BlurEffect { Radius = 0, RenderingBias = RenderingBias.Performance };
                outgoing.Effect = outBlur;
                blurOutAnim = MakeAnim(0, 6, durOut, _easeAppleIn);
            }

            fadeOut.Completed += (s, e) =>
            {
                if (activeGen != _viewTransitionGeneration) return;
                outgoing.Visibility = Visibility.Collapsed;
                outgoing.RenderTransform = null;
                outgoing.Effect = null;
                if (outBlur != null) outBlur.Radius = 0;
            };

            outgoing.BeginAnimation(OpacityProperty, fadeOut);
            outTranslate.BeginAnimation(TranslateTransform.YProperty, slideUp);
            if (outBlur != null && blurOutAnim != null)
                outBlur.BeginAnimation(BlurEffect.RadiusProperty, blurOutAnim);
        }

        prepIncoming?.Invoke();

        AnimateClockViewNotchResize(notchFromW, notchFromH, notchToW, notchToH, durIn, inDelay, generation: activeGen);

        incoming.Visibility = Visibility.Visible;
        incoming.BeginAnimation(OpacityProperty, null);
        incoming.Opacity = 0;

        if (inIsAudio)
        {
            if (AudioScrollViewer != null)
            {
                AudioScrollViewer.Width = _audioViewWidth - 38;
                AudioScrollViewer.HorizontalAlignment = HorizontalAlignment.Left;
                AudioScrollViewer.VerticalAlignment = VerticalAlignment.Top;
                AudioScrollViewer.BeginAnimation(OpacityProperty, null);
                AudioScrollViewer.Visibility = Visibility.Visible;
                AudioScrollViewer.Opacity = 1;
                AudioScrollViewer.ScrollToTop();
                VNotch.Presenters.NotchContentTransitionPresenter.ClearTransformAndEffects(AudioScrollViewer);
            }
            incoming.InvalidateMeasure();
            incoming.InvalidateArrange();

            var inTranslate = new TranslateTransform(0, 16);
            incoming.RenderTransform = inTranslate;

            var aFadeIn = MakeAnim(0, 1, durIn, _easeAppleOut, inDelay);
            var springSlide = MakeAnim(16, 0, durIn, _easeAppleOut, inDelay);
            Timeline.SetDesiredFrameRate(aFadeIn, fps);
            Timeline.SetDesiredFrameRate(springSlide, fps);

            bool useContentBlur = _settings.EnableBlurEffects && !IsLiquidGlassEnabled;
            BlurEffect? inBlur = null;
            DoubleAnimation? blurInAnim = null;
            if (useContentBlur)
            {
                inBlur = incoming.Effect as BlurEffect ?? new BlurEffect { Radius = 6, RenderingBias = RenderingBias.Performance };
                incoming.Effect = inBlur;
                blurInAnim = MakeAnim(6, 0, durIn, _easeAppleOut, inDelay);
            }

            aFadeIn.Completed += (s, e) =>
            {
                if (activeGen != _viewTransitionGeneration) return;
                _isAnimating = false;
                _isScrollSessionLocked = false;
                NotchBorder.IsHitTestVisible = true;
                incoming.Opacity = 1;
                incoming.BeginAnimation(OpacityProperty, null);
                incoming.RenderTransform = null;
                incoming.Effect = null;
                if (inBlur != null) inBlur.Radius = 0;
                if (AudioScrollViewer != null)
                {
                    AudioScrollViewer.BeginAnimation(OpacityProperty, null);
                    AudioScrollViewer.Visibility = Visibility.Visible;
                    AudioScrollViewer.Opacity = 1;
                    VNotch.Presenters.NotchContentTransitionPresenter.ClearTransformAndEffects(AudioScrollViewer);
                }
                _transitionCoordinator.CompleteTransition(activeGen);
                onComplete?.Invoke();
            };

            incoming.BeginAnimation(OpacityProperty, aFadeIn);
            inTranslate.BeginAnimation(TranslateTransform.YProperty, springSlide);
            if (inBlur != null && blurInAnim != null)
                inBlur.BeginAnimation(BlurEffect.RadiusProperty, blurInAnim);

            StaggerAudioMixerReveal(inDelay + TimeSpan.FromMilliseconds(20));
        }
        else
        {
            if (ReferenceEquals(incoming, ExpandedContent))
                PrepareExpandedContentLayoutForReveal();
            else
            {
                incoming.InvalidateMeasure();
                incoming.InvalidateArrange();
            }

            double restY = ReferenceEquals(incoming, ExpandedContent) ? ExpandedContentRestY : 0;

            var inTranslate = new TranslateTransform(0, 16 + restY);
            incoming.RenderTransform = inTranslate;

            var fadeIn = MakeAnim(0, 1, durIn, _easeAppleOut, inDelay);
            var springSlide = MakeAnim(16 + restY, restY, durIn, _easeAppleOut, inDelay);
            Timeline.SetDesiredFrameRate(fadeIn, fps);
            Timeline.SetDesiredFrameRate(springSlide, fps);

            fadeIn.Completed += (s, e) =>
            {
                if (activeGen != _viewTransitionGeneration) return;
                _isAnimating = false;
                _isScrollSessionLocked = false;
                NotchBorder.IsHitTestVisible = true;
                incoming.Opacity = 1;
                incoming.BeginAnimation(OpacityProperty, null);
                if (ReferenceEquals(incoming, ExpandedContent))
                    RestoreExpandedContentRestLayout();
                else
                    incoming.RenderTransform = null;
                if (outIsAudio)
                    RestorePrivacyDotVisibility();
                _transitionCoordinator.CompleteTransition(activeGen);
                onComplete?.Invoke();
            };

            incoming.BeginAnimation(OpacityProperty, fadeIn);
            inTranslate.BeginAnimation(TranslateTransform.YProperty, springSlide);
        }
    }

    private void SettleAudioNotchToFit()
        => AnimateAudioNotchHeight(_audioViewHeight, new Duration(TimeSpan.FromMilliseconds(300)), _easeExpOut6);

    private void AnimateAudioNotchHeight(double target, Duration dur, IEasingFunction ease)
    {
        int generation = ++_audioNotchHeightGeneration;
        if (!_isAudioView || _isAnimating || AudioScrollViewer == null) return;

        double current = NotchBorder.ActualHeight > 0 ? NotchBorder.ActualHeight : target;
        double currentScrollHeight = AudioScrollViewer.ActualHeight > 0
            ? AudioScrollViewer.ActualHeight
            : Math.Max(0.0, current - _audioViewChrome);
        if (Math.Abs(current - target) < 0.5)
        {
            NotchBorder.BeginAnimation(HeightProperty, null);
            NotchBorder.Height = target;
            AudioScrollViewer.BeginAnimation(HeightProperty, null);
            AudioScrollViewer.Height = target - _audioViewChrome;
            ResizeHostWindowHeight(target);
            return;
        }

        int fps = AnimationConfig.TargetFps;
        bool growing = target > current;
        if (growing) ResizeHostWindowHeight(target);

        NotchBorder.BeginAnimation(HeightProperty, null);
        AudioScrollViewer.BeginAnimation(HeightProperty, null);
        NotchBorder.Height = current;
        AudioScrollViewer.Height = currentScrollHeight;

        _isAnimating = true;

        var notchAnim = MakeAnim(current, target, dur, ease);
        var scrollAnim = MakeAnim(currentScrollHeight, Math.Max(0.0, target - _audioViewChrome), dur, ease);
        Timeline.SetDesiredFrameRate(notchAnim, fps);
        Timeline.SetDesiredFrameRate(scrollAnim, fps);

        notchAnim.Completed += (_, _) =>
        {
            if (generation != _audioNotchHeightGeneration || !_isAudioView)
                return;

            _isAnimating = false;
            NotchBorder.BeginAnimation(HeightProperty, null);
            NotchBorder.Height = target;
            AudioScrollViewer.BeginAnimation(HeightProperty, null);
            AudioScrollViewer.Height = target - _audioViewChrome;
            if (!growing) ResizeHostWindowHeight(target);
        };

        NotchBorder.BeginAnimation(HeightProperty, notchAnim, HandoffBehavior.SnapshotAndReplace);
        AudioScrollViewer.BeginAnimation(HeightProperty, scrollAnim, HandoffBehavior.SnapshotAndReplace);
    }

    private void PrewarmAudioSnapshot()
    {
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var quick = ReadAudioSnapshot(includeIcons: false);
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_lastAudioSnapshot == null)
                    {
                        _lastAudioSnapshot = quick;
                    }
                }), System.Windows.Threading.DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                RuntimeLog.Log("AUDIOMIXER-PREWARM", ex.Message);
            }
        });
    }
}

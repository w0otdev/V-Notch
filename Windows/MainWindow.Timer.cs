using System;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using VNotch.Services;
using static VNotch.Services.AnimationPrimitives;
using static VNotch.Services.Win32Interop;

namespace VNotch;

public partial class MainWindow
{
    private bool _isTimerView
    {
        get => _notchState.IsTimerView;
        set
        {
            _notchState.IsTimerView = value;
            if (value) _viewModel.SetView(VNotch.Models.NotchView.Timer);
            else if (_viewModel.CurrentView == VNotch.Models.NotchView.Timer)
                _viewModel.SetView(VNotch.Models.NotchView.Media);
        }
    }
    private const double _timerViewHeight = 108;
    private const double _countdownCompleteWidthInset = 28;
    private double CountdownCompleteViewWidth => Math.Max(_collapsedWidth, _expandedWidth - _countdownCompleteWidthInset);

    // ponytail: aliases keep animation code stable; TimerViewModel owns countdown state.
    private TimeSpan _countdownDuration { get => _viewModel.Timer.Duration; set => _viewModel.Timer.Duration = value; }
    private TimeSpan _countdownRemaining { get => _viewModel.Timer.Remaining; set => _viewModel.Timer.Remaining = value; }
    private bool _isCountdownRunning { get => _viewModel.Timer.IsRunning; set => _viewModel.Timer.IsRunning = value; }
    private DispatcherTimer? _countdownTimer;

    private DispatcherTimer? _countdownRepeatTimer;
    private int _countdownRepeatDirection;
    private int _countdownRepeatCount;
    private const int RepeatInitialDelayMs = 400;
    private const int RepeatFastIntervalMs = 80;
    private const int RepeatAccelerateAfter = 4;

    private static readonly Geometry _countdownPlayGeometry = CreateFrozenGeometry(
        "M133,440a35.37,35.37,0,0,1-17.5-4.67c-12-6.8-17.46-20-17.46-41.73V118.4c0-21.74,5.48-34.93,17.46-41.73a35.13,35.13,0,0,1,35.77.45L399.68,225.11a38.19,38.19,0,0,1,0,61.78L151.23,435a35.77,35.77,0,0,1-18.27,5Z");
    private static readonly Geometry _countdownPauseGeometry = CreateFrozenGeometry(
        "M224,320a16,16,0,0,1-32,0V192a16,16,0,0,1,32,0Zm96,0a16,16,0,0,1-32,0V192a16,16,0,0,1,32,0Z");
    private static readonly Brush _countdownStartIdleBrush = CreateFrozenVerticalGradient(
        Color.FromRgb(0xFF, 0xA0, 0x33), Color.FromRgb(0xFF, 0x7A, 0x00));
    private static readonly Brush _countdownStartRunningBrush = CreateFrozenVerticalGradient(
        Color.FromRgb(0xE0, 0x8A, 0x1E), Color.FromRgb(0xC2, 0x64, 0x00));

    private static readonly Color _countdownBorderIdleColor = Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF);
    private static readonly Color _countdownBorderEditingColor = Color.FromArgb(0x8C, 0xFF, 0x8C, 0x00);
    private static readonly Color _countdownBorderFlashColor = Color.FromArgb(0x70, 0xFF, 0x8C, 0x00);
    private static readonly Color _countdownBorderErrorColor = Color.FromArgb(0xB4, 0xFF, 0x45, 0x3A);
    private static readonly Color _countdownDigitsRestColor = Color.FromRgb(0xFF, 0xFF, 0xFF);
    private static readonly Color _countdownDigitsFlashColor = Color.FromRgb(0xFF, 0xC9, 0x85);

    private static Geometry CreateFrozenGeometry(string data)
    {
        var geometry = Geometry.Parse(data);
        geometry.Freeze();
        return geometry;
    }

    private static Brush CreateFrozenVerticalGradient(Color top, Color bottom)
    {
        var brush = new LinearGradientBrush(top, bottom, new Point(0, 0), new Point(0, 1));
        brush.Freeze();
        return brush;
    }

    private void SetCountdownStartVisual(bool running)
    {
        CountdownStartIcon.Data = running ? _countdownPauseGeometry : _countdownPlayGeometry;
        CountdownStartBtn.Background = running ? _countdownStartRunningBrush : _countdownStartIdleBrush;

        // Steppers only make sense while paused; fade them out of reach during a run.
        CountdownStepperCapsule.IsHitTestVisible = !running;
        var stepperFade = MakeAnim(running ? 0.4 : 1.0, _dur200, _easeQuadOut);
        CountdownStepperCapsule.BeginAnimation(OpacityProperty, stepperFade);
    }

    #region Timer View Navigation

    private void TimerIconButton_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_isAudioView && !_isAnimating)
        {
            SwitchFromAudioToTimerView();
            return;
        }
        if (!_isTimerView && !_isAnimating)
        {
            if (_isSecondaryView)
            {
                StopCameraPreviewForViewExit();
                SwitchFromSecondaryToTimerView();
            }
            else
            {
                SwitchToTimerView();
            }
        }
    }

    private void SwitchToTimerView()
    {
        if (_isTimerView || _isAnimating) return;
        _isTimerView = true;
        _isAnimating = true;
        SuspendSpotifyCanvasLifecycle();
        _lastViewSwitchUtc = DateTime.UtcNow;
        _isScrollSessionLocked = true;

        HideMediaBackgroundOverlay();
        if (LyricsBlurBackground != null && LyricsBlurBackground.Visibility == Visibility.Visible)
        {
            LyricsBlurBackground.BeginAnimation(OpacityProperty, null);
            LyricsBlurBackground.Opacity = 0;
            LyricsBlurBackground.Visibility = Visibility.Collapsed;
        }

        UpdateTimerNavIconsState();
        NavIconsPanel.Visibility = Visibility.Visible;
        NavIconsPanel.Opacity = 1;

        NavIconsBackground.BeginAnimation(OpacityProperty, null);
        NavIconsBackground.Opacity = 0;
        NavIconsBackground.Visibility = Visibility.Visible;
        var navBgFadeIn = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(300)))
        {
            EasingFunction = _easePowerOut3,
            BeginTime = TimeSpan.FromMilliseconds(200)
        };
        Timeline.SetDesiredFrameRate(navBgFadeIn, VNotch.Services.AnimationConfig.TargetFps);
        NavIconsBackground.BeginAnimation(OpacityProperty, navBgFadeIn);

        NotchBorder.IsHitTestVisible = false;

        ApplyClockViewWindowSize();
        PrepareClockViewContentSize();
        RefreshClockView();

        var durOut = new Duration(TimeSpan.FromMilliseconds(170));
        var durIn = new Duration(TimeSpan.FromMilliseconds(440));
        var inDelay = TimeSpan.FromMilliseconds(40);
        int fps = VNotch.Services.AnimationConfig.TargetFps;

        var primaryGroup = new TransformGroup();
        var primaryScale = new ScaleTransform(1, 1);
        var primaryTranslate = new TranslateTransform(0, ExpandedContentRestY);
        primaryGroup.Children.Add(primaryScale);
        primaryGroup.Children.Add(primaryTranslate);
        ExpandedContent.RenderTransform = primaryGroup;
        ExpandedContent.RenderTransformOrigin = new Point(0.5, 0.5);

        var fadeOut = MakeAnim(1, 0, durOut, _easeAppleIn);
        var slideUp = MakeAnim(ExpandedContentRestY, ExpandedContentRestY - 10, durOut, _easeAppleIn);
        var scaleDownX = MakeAnim(1, 0.96, durOut, _easeAppleIn);
        var scaleDownY = MakeAnim(1, 0.96, durOut, _easeAppleIn);
        Timeline.SetDesiredFrameRate(slideUp, fps);
        Timeline.SetDesiredFrameRate(scaleDownX, fps);
        Timeline.SetDesiredFrameRate(scaleDownY, fps);

        bool useContentBlur = _settings.EnableBlurEffects && !IsLiquidGlassEnabled;
        BlurEffect? expandedBlur = null;
        DoubleAnimation? blurOutAnim = null;
        if (useContentBlur)
        {
            expandedBlur = ExpandedContent.Effect as BlurEffect ?? new BlurEffect { Radius = 0, RenderingBias = RenderingBias.Performance };
            ExpandedContent.Effect = expandedBlur;
            blurOutAnim = MakeAnim(0, 6, durOut, _easeAppleIn);
        }

        fadeOut.Completed += (s, ev) =>
        {
            ExpandedContent.Visibility = Visibility.Collapsed;
            ExpandedContent.RenderTransform = null;
            ExpandedContent.Effect = null;
            if (expandedBlur != null) expandedBlur.Radius = 0;
        };

        ExpandedContent.BeginAnimation(OpacityProperty, fadeOut);
        primaryTranslate.BeginAnimation(TranslateTransform.YProperty, slideUp);
        primaryScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleDownX);
        primaryScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleDownY);
        if (expandedBlur != null && blurOutAnim != null)
            expandedBlur.BeginAnimation(BlurEffect.RadiusProperty, blurOutAnim);

        AnimateClockViewNotchResize(
            NotchBorder.ActualWidth > 0 ? NotchBorder.ActualWidth : _expandedWidth,
            NotchBorder.ActualHeight > 0 ? NotchBorder.ActualHeight : _expandedHeight,
            _clockViewWidth, _clockViewHeight, durIn, inDelay);

        PlayTimerViewEntrance(durIn, inDelay);

        UpdateTimerDisplay();
    }

    // The clock view "unfolds" out of the notch: the panel drops from the notch
    // lip while the header falls in, the analog clock springs to size and the
    // control bar rises from below — three staggered layers.
    private void PlayTimerViewEntrance(Duration durIn, TimeSpan inDelay)
    {
        int fps = VNotch.Services.AnimationConfig.TargetFps;

        TimerContent.Visibility = Visibility.Visible;
        TimerContent.BeginAnimation(OpacityProperty, null);
        TimerContent.Opacity = 0;

        var timerGroup = new TransformGroup();
        var timerScale = new ScaleTransform(0.94, 0.94);
        var timerTranslate = new TranslateTransform(0, -22);
        timerGroup.Children.Add(timerScale);
        timerGroup.Children.Add(timerTranslate);
        TimerContent.RenderTransform = timerGroup;
        TimerContent.RenderTransformOrigin = new Point(0.5, 0.0);

        var fadeIn = MakeAnim(0, 1, durIn, _easeAppleOut, inDelay);
        var dropIn = MakeAnim(-22, 0, durIn, _easeExpOut6, inDelay);
        var growX = MakeAnim(0.94, 1, durIn, _easeAppleOut, inDelay);
        var growY = MakeAnim(0.94, 1, durIn, _easeAppleOut, inDelay);
        Timeline.SetDesiredFrameRate(fadeIn, fps);
        Timeline.SetDesiredFrameRate(dropIn, fps);
        Timeline.SetDesiredFrameRate(growX, fps);
        Timeline.SetDesiredFrameRate(growY, fps);

        fadeIn.Completed += (s, ev) =>
        {
            _isAnimating = false;
            _isScrollSessionLocked = false;
            NotchBorder.IsHitTestVisible = true;
            TimerContent.Opacity = 1;
            TimerContent.BeginAnimation(OpacityProperty, null);
            TimerContent.RenderTransform = null;
            RestoreTimerContentOpacity();
        };

        RestoreTimerContentOpacity();
        TimerContent.UpdateLayout();

        TimerContent.BeginAnimation(OpacityProperty, fadeIn);
        timerTranslate.BeginAnimation(TranslateTransform.YProperty, dropIn);
        timerScale.BeginAnimation(ScaleTransform.ScaleXProperty, growX);
        timerScale.BeginAnimation(ScaleTransform.ScaleYProperty, growY);

        PlayClockViewUnfoldIn(inDelay);
    }

    private void PlayClockViewUnfoldIn(TimeSpan baseDelay)
    {
        int fps = VNotch.Services.AnimationConfig.TargetFps;

        // Header (analog clock + calendar) falls in from the notch lip.
        var headerTranslate = new TranslateTransform(0, -14);
        ClockViewHeader.RenderTransform = headerTranslate;
        ClockViewHeader.BeginAnimation(OpacityProperty, null);
        ClockViewHeader.Opacity = 0;

        var headerDelay = baseDelay + TimeSpan.FromMilliseconds(50);
        var headerFade = MakeAnim(0, 1, new Duration(TimeSpan.FromMilliseconds(360)), _easeAppleOut, headerDelay);
        var headerDrop = MakeAnim(-14, 0, new Duration(TimeSpan.FromMilliseconds(470)), _easeExpOut6, headerDelay);
        Timeline.SetDesiredFrameRate(headerFade, fps);
        Timeline.SetDesiredFrameRate(headerDrop, fps);

        headerFade.Completed += (_, _) =>
        {
            ClockViewHeader.BeginAnimation(OpacityProperty, null);
            ClockViewHeader.Opacity = 1;
            if (ReferenceEquals(ClockViewHeader.RenderTransform, headerTranslate))
                ClockViewHeader.RenderTransform = null;
        };
        ClockViewHeader.BeginAnimation(OpacityProperty, headerFade);
        headerTranslate.BeginAnimation(TranslateTransform.YProperty, headerDrop);

        // Analog clock springs to full size on top of the header drop.
        var clockScale = new ScaleTransform(0.84, 0.84);
        ClockViewClock.RenderTransform = clockScale;
        ClockViewClock.RenderTransformOrigin = new Point(0.5, 0.5);

        var clockDelay = baseDelay + TimeSpan.FromMilliseconds(100);
        var clockPop = MakeAnim(0.84, 1, new Duration(TimeSpan.FromMilliseconds(600)), _easeSoftSpring, clockDelay);
        Timeline.SetDesiredFrameRate(clockPop, fps);
        clockPop.Completed += (_, _) =>
        {
            if (ReferenceEquals(ClockViewClock.RenderTransform, clockScale))
                ClockViewClock.RenderTransform = null;
        };
        clockScale.BeginAnimation(ScaleTransform.ScaleXProperty, clockPop);
        clockScale.BeginAnimation(ScaleTransform.ScaleYProperty, clockPop);

        // Control bar rises to meet the header from below.
        var barGroup = new TransformGroup();
        var barScale = new ScaleTransform(0.96, 0.96);
        var barTranslate = new TranslateTransform(0, 18);
        barGroup.Children.Add(barScale);
        barGroup.Children.Add(barTranslate);
        TimerControlBar.RenderTransform = barGroup;
        TimerControlBar.RenderTransformOrigin = new Point(0.5, 1.0);
        TimerControlBar.BeginAnimation(OpacityProperty, null);
        TimerControlBar.Opacity = 0;

        var barDelay = baseDelay + TimeSpan.FromMilliseconds(140);
        var barFade = MakeAnim(0, 1, new Duration(TimeSpan.FromMilliseconds(340)), _easeAppleOut, barDelay);
        var barRise = MakeAnim(18, 0, new Duration(TimeSpan.FromMilliseconds(460)), _easeExpOut6, barDelay);
        var barGrow = MakeAnim(0.96, 1, new Duration(TimeSpan.FromMilliseconds(460)), _easeAppleOut, barDelay);
        Timeline.SetDesiredFrameRate(barFade, fps);
        Timeline.SetDesiredFrameRate(barRise, fps);
        Timeline.SetDesiredFrameRate(barGrow, fps);

        barFade.Completed += (_, _) =>
        {
            TimerControlBar.BeginAnimation(OpacityProperty, null);
            TimerControlBar.Opacity = 1;
            if (ReferenceEquals(TimerControlBar.RenderTransform, barGroup))
                TimerControlBar.RenderTransform = null;
        };
        TimerControlBar.BeginAnimation(OpacityProperty, barFade);
        barTranslate.BeginAnimation(TranslateTransform.YProperty, barRise);
        barScale.BeginAnimation(ScaleTransform.ScaleXProperty, barGrow);
        barScale.BeginAnimation(ScaleTransform.ScaleYProperty, barGrow);
    }

    private void ResetClockViewChildVisuals()
    {
        foreach (var el in new FrameworkElement[] { ClockViewHeader, ClockViewClock, TimerControlBar })
        {
            el.BeginAnimation(OpacityProperty, null);
            el.Opacity = 1;
            el.RenderTransform = null;
        }
    }

    private void SwitchFromSecondaryToTimerView()
    {
        if (_isTimerView || _isAnimating) return;
        _isTimerView = true;
        _isSecondaryView = false;
        _isAnimating = true;
        _lastViewSwitchUtc = DateTime.UtcNow;
        _isScrollSessionLocked = true;

        if (IsCameraPreviewLifecycleActive)
        {
            StopCameraPreviewForViewExit();
        }
        else
        {
            ResetCameraSectionLayoutInstant();
        }

        UpdateTimerNavIconsState();
        NotchBorder.IsHitTestVisible = false;

        ApplyClockViewWindowSize();
        PrepareClockViewContentSize();
        RefreshClockView();

        var durOut = new Duration(TimeSpan.FromMilliseconds(170));
        var durIn = new Duration(TimeSpan.FromMilliseconds(440));
        var inDelay = TimeSpan.FromMilliseconds(40);
        int fps = VNotch.Services.AnimationConfig.TargetFps;

        var secondaryGroup = new TransformGroup();
        var secondaryScale = new ScaleTransform(1, 1);
        var secondaryTranslate = new TranslateTransform(0, 0);
        secondaryGroup.Children.Add(secondaryScale);
        secondaryGroup.Children.Add(secondaryTranslate);
        SecondaryContent.RenderTransform = secondaryGroup;
        SecondaryContent.RenderTransformOrigin = new Point(0.5, 0.5);

        var fadeOut = MakeAnim(1, 0, durOut, _easeAppleIn);
        var slideUp = MakeAnim(0, -10, durOut, _easeAppleIn);
        var scaleDownX = MakeAnim(1, 0.96, durOut, _easeAppleIn);
        var scaleDownY = MakeAnim(1, 0.96, durOut, _easeAppleIn);
        Timeline.SetDesiredFrameRate(slideUp, fps);
        Timeline.SetDesiredFrameRate(scaleDownX, fps);
        Timeline.SetDesiredFrameRate(scaleDownY, fps);

        bool useContentBlur = _settings.EnableBlurEffects && !IsLiquidGlassEnabled;
        BlurEffect? secondaryBlur = null;
        DoubleAnimation? blurOutAnim = null;
        if (useContentBlur)
        {
            secondaryBlur = SecondaryContent.Effect as BlurEffect ?? new BlurEffect { Radius = 0, RenderingBias = RenderingBias.Performance };
            SecondaryContent.Effect = secondaryBlur;
            blurOutAnim = MakeAnim(0, 6, durOut, _easeAppleIn);
        }

        fadeOut.Completed += (s, ev) =>
        {
            SecondaryContent.Visibility = Visibility.Collapsed;
            SecondaryContent.RenderTransform = null;
            SecondaryContent.Effect = null;
            if (secondaryBlur != null) secondaryBlur.Radius = 0;
        };

        SecondaryContent.BeginAnimation(OpacityProperty, fadeOut);
        secondaryTranslate.BeginAnimation(TranslateTransform.YProperty, slideUp);
        secondaryScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleDownX);
        secondaryScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleDownY);
        if (secondaryBlur != null && blurOutAnim != null)
            secondaryBlur.BeginAnimation(BlurEffect.RadiusProperty, blurOutAnim);

        TimerContent.Visibility = Visibility.Visible;
        TimerContent.BeginAnimation(OpacityProperty, null);
        TimerContent.Opacity = 0;

        var timerGroup = new TransformGroup();
        var timerScale = new ScaleTransform(0.96, 0.96);
        var timerTranslate = new TranslateTransform(0, 16);
        timerGroup.Children.Add(timerScale);
        timerGroup.Children.Add(timerTranslate);
        TimerContent.RenderTransform = timerGroup;
        TimerContent.RenderTransformOrigin = new Point(0.5, 0.5);

        var fadeIn = MakeAnim(0, 1, durIn, _easeAppleOut, inDelay);
        var springSlide = MakeAnim(16, 0, durIn, _easeAppleOut, inDelay);
        var springScaleX = MakeAnim(0.96, 1, durIn, _easeAppleOut, inDelay);
        var springScaleY = MakeAnim(0.96, 1, durIn, _easeAppleOut, inDelay);
        Timeline.SetDesiredFrameRate(fadeIn, fps);
        Timeline.SetDesiredFrameRate(springSlide, fps);
        Timeline.SetDesiredFrameRate(springScaleX, fps);
        Timeline.SetDesiredFrameRate(springScaleY, fps);

        fadeIn.Completed += (s, ev) =>
        {
            _isAnimating = false;
            _isScrollSessionLocked = false;
            NotchBorder.IsHitTestVisible = true;
            TimerContent.Opacity = 1;
            TimerContent.BeginAnimation(OpacityProperty, null);
            TimerContent.RenderTransform = null;
            RestoreTimerContentOpacity();
        };

        AnimateClockViewNotchResize(
            NotchBorder.ActualWidth > 0 ? NotchBorder.ActualWidth : _expandedWidth,
            NotchBorder.ActualHeight > 0 ? NotchBorder.ActualHeight : _expandedHeight,
            _clockViewWidth, _clockViewHeight, durIn, inDelay);

        RestoreTimerContentOpacity();
        TimerContent.UpdateLayout();

        TimerContent.BeginAnimation(OpacityProperty, fadeIn);
        timerTranslate.BeginAnimation(TranslateTransform.YProperty, springSlide);
        timerScale.BeginAnimation(ScaleTransform.ScaleXProperty, springScaleX);
        timerScale.BeginAnimation(ScaleTransform.ScaleYProperty, springScaleY);

        UpdateTimerDisplay();
    }

    private void SwitchFromTimerToPrimaryView()
    {
        if (!_isTimerView || _isAnimating) return;
        CancelTimerEditingInstant();
        _isTimerView = false;
        _isAnimating = true;
        _lastViewSwitchUtc = DateTime.UtcNow;
        _isScrollSessionLocked = true;

        UpdateNavIconsActiveState();
        NavIconsBackground.BeginAnimation(OpacityProperty, null);
        NavIconsBackground.Opacity = 0;
        NavIconsBackground.Visibility = Visibility.Collapsed;

        NotchBorder.IsHitTestVisible = false;

        var durScroll = new Duration(TimeSpan.FromMilliseconds(420));
        var durIn = new Duration(TimeSpan.FromMilliseconds(440));
        var inDelay = TimeSpan.FromMilliseconds(30);
        int fps = VNotch.Services.AnimationConfig.TargetFps;

        var timerTranslate = new TranslateTransform(0, 0);
        TimerContent.RenderTransform = timerTranslate;
        TimerContent.RenderTransformOrigin = new Point(0.5, 0.5);

        var timerSlideDown = MakeAnim(0, 40, durScroll, _easeExpOut6);
        var timerFadeOut = MakeAnim(1, 0, new Duration(TimeSpan.FromMilliseconds(200)), _easeQuadIn);
        Timeline.SetDesiredFrameRate(timerSlideDown, fps);
        Timeline.SetDesiredFrameRate(timerFadeOut, fps);

        timerFadeOut.Completed += (s, ev) =>
        {
            TimerContent.Visibility = Visibility.Collapsed;
            TimerContent.RenderTransform = null;
            TimerContent.BeginAnimation(OpacityProperty, null);
            TimerContent.Opacity = 0;
        };

        TimerContent.BeginAnimation(OpacityProperty, timerFadeOut);
        timerTranslate.BeginAnimation(TranslateTransform.YProperty, timerSlideDown);

        double currentH = NotchBorder.ActualHeight > 0 ? NotchBorder.ActualHeight : _clockViewHeight;
        double currentWidthExit = NotchBorder.ActualWidth > 0 ? NotchBorder.ActualWidth : _clockViewWidth;
        AnimateClockViewNotchResize(currentWidthExit, currentH, _expandedWidth, _expandedHeight, durIn, inDelay, RestoreExpandedWindowSize);

        ExpandedContent.Visibility = Visibility.Visible;
        ExpandedContent.BeginAnimation(OpacityProperty, null);
        ExpandedContent.Opacity = 0;
        ExpandedContent.Effect = null;
        ExpandedContent.Width = _expandedWidth - 16;
        ExpandedContent.Height = _expandedHeight - 10;
        ExpandedContent.HorizontalAlignment = HorizontalAlignment.Right;
        ExpandedContent.UseLayoutRounding = false;
        ExpandedContent.UpdateLayout();

        PrepareExpandedContentLayoutForReveal();

        if (_currentMediaInfo?.Thumbnail != null && _currentMediaInfo.IsAnyMediaPlaying)
        {
            var palette = DynamicIslandColorExtractor.GetDynamicIslandPalette(_currentMediaInfo.Thumbnail);
            var subColor = LiftDarkColor(palette.Sub);
            var vibrantColor = Color.FromRgb(subColor.R, subColor.G, subColor.B);
            var darkColor = Color.FromArgb(vibrantColor.A,
                (byte)(vibrantColor.R * 0.65),
                (byte)(vibrantColor.G * 0.65),
                (byte)(vibrantColor.B * 0.65));

            ProgressBarGradientStart.BeginAnimation(GradientStop.ColorProperty, null);
            ProgressBarGradientEnd.BeginAnimation(GradientStop.ColorProperty, null);
            ProgressBarGradientStart.Color = vibrantColor;
            ProgressBarGradientEnd.Color = darkColor;
        }

        var primaryTranslate = new TranslateTransform(0, ExpandedContentRestY - 16);
        ExpandedContent.RenderTransform = primaryTranslate;
        ExpandedContent.RenderTransformOrigin = new Point(0.5, 0.5);

        var primarySlideDown = MakeAnim(ExpandedContentRestY - 16, ExpandedContentRestY, durIn, _easeAppleOut, inDelay);
        var primaryFadeIn = MakeAnim(0, 1, durIn, _easeAppleOut, inDelay);
        Timeline.SetDesiredFrameRate(primarySlideDown, fps);
        Timeline.SetDesiredFrameRate(primaryFadeIn, fps);

        primaryFadeIn.Completed += (s, ev) =>
        {
            _isAnimating = false;
            _isScrollSessionLocked = false;
            NotchBorder.IsHitTestVisible = true;
            ExpandedContent.Opacity = 1;
            ExpandedContent.BeginAnimation(OpacityProperty, null);
            RestoreExpandedContentRestLayout();
            ResumeSpotifyCanvasLifecycle();

            ShowMediaBackground();

            if (_settings.EnableBlurEffects && !IsLiquidGlassEnabled && _isLyricsActive && !_isSpotifyCanvasMediaOpen && LyricsBlurBackground != null)
            {
                LyricsBlurImage.BeginAnimation(OpacityProperty, null);
                LyricsBlurImage.Opacity = 1;
                LyricsBlurBackground.Visibility = Visibility.Visible;
                LyricsBlurBackground.BeginAnimation(OpacityProperty, null);
                var lyricsBlurFadeIn = new DoubleAnimation(0, 0.55, new Duration(TimeSpan.FromMilliseconds(250)))
                {
                    EasingFunction = new ExponentialEase { Exponent = 4, EasingMode = EasingMode.EaseOut }
                };
                System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(lyricsBlurFadeIn, VNotch.Services.AnimationConfig.TargetFps);
                LyricsBlurBackground.BeginAnimation(OpacityProperty, lyricsBlurFadeIn);
            }
        };

        ExpandedContent.BeginAnimation(OpacityProperty, primaryFadeIn);
        primaryTranslate.BeginAnimation(TranslateTransform.YProperty, primarySlideDown);
    }

    private void UpdateTimerNavIconsState()
    {
        HomeIconButton.Opacity = 0.4;
        FileShelfIconButton.Opacity = 0.4;
        TimerIconButton.Opacity = 1.0;
        AudioIconButton.Opacity = 0.4;
        if (!_isAnimating)
        {
            ShelfCountBadge.Visibility = Visibility.Collapsed;
        }
    }

    private void SwitchFromTimerToSecondaryView()
    {
        if (!_isTimerView || _isAnimating) return;
        CancelTimerEditingInstant();
        _isTimerView = false;
        _isSecondaryView = true;
        _isAnimating = true;
        _lastViewSwitchUtc = DateTime.UtcNow;
        _isScrollSessionLocked = true;

        UpdateNavIconsActiveState();
        NotchBorder.IsHitTestVisible = false;

        var durOut = new Duration(TimeSpan.FromMilliseconds(170));
        var durIn = new Duration(TimeSpan.FromMilliseconds(440));
        var inDelay = TimeSpan.FromMilliseconds(40);
        int fps = VNotch.Services.AnimationConfig.TargetFps;

        var timerGroup = new TransformGroup();
        var timerScale = new ScaleTransform(1, 1);
        var timerTranslate = new TranslateTransform(0, 0);
        timerGroup.Children.Add(timerScale);
        timerGroup.Children.Add(timerTranslate);
        TimerContent.RenderTransform = timerGroup;
        TimerContent.RenderTransformOrigin = new Point(0.5, 0.5);

        var fadeOut = MakeAnim(1, 0, durOut, _easeAppleIn);
        var slideUp = MakeAnim(0, -10, durOut, _easeAppleIn);
        var scaleDownX = MakeAnim(1, 0.96, durOut, _easeAppleIn);
        var scaleDownY = MakeAnim(1, 0.96, durOut, _easeAppleIn);
        Timeline.SetDesiredFrameRate(slideUp, fps);
        Timeline.SetDesiredFrameRate(scaleDownX, fps);
        Timeline.SetDesiredFrameRate(scaleDownY, fps);

        var timerBlur = TimerContent.Effect as BlurEffect ?? new BlurEffect { Radius = 0, RenderingBias = RenderingBias.Performance };
        TimerContent.Effect = timerBlur;
        var blurOutAnim = MakeAnim(0, _settings.EnableBlurEffects ? 6 : 0, durOut, _easeAppleIn);

        fadeOut.Completed += (s, ev) =>
        {
            TimerContent.Visibility = Visibility.Collapsed;
            TimerContent.RenderTransform = null;
            TimerContent.Effect = null;
            timerBlur.Radius = 0;
        };

        TimerContent.BeginAnimation(OpacityProperty, fadeOut);
        timerTranslate.BeginAnimation(TranslateTransform.YProperty, slideUp);
        timerScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleDownX);
        timerScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleDownY);
        timerBlur.BeginAnimation(BlurEffect.RadiusProperty, blurOutAnim);

        double currentH2 = NotchBorder.ActualHeight > 0 ? NotchBorder.ActualHeight : _clockViewHeight;
        double currentWidthExit2 = NotchBorder.ActualWidth > 0 ? NotchBorder.ActualWidth : _clockViewWidth;
        AnimateClockViewNotchResize(currentWidthExit2, currentH2, _expandedWidth, _expandedHeight, durIn, inDelay, RestoreExpandedWindowSize);

        SecondaryContent.Visibility = Visibility.Visible;
        SecondaryContent.BeginAnimation(OpacityProperty, null);
        SecondaryContent.Opacity = 0;
        EnableKeyboardInput();

        var secondaryGroup = new TransformGroup();
        var secondaryScale = new ScaleTransform(0.96, 0.96);
        var secondaryTranslate = new TranslateTransform(0, 16);
        secondaryGroup.Children.Add(secondaryScale);
        secondaryGroup.Children.Add(secondaryTranslate);
        SecondaryContent.RenderTransform = secondaryGroup;
        SecondaryContent.RenderTransformOrigin = new Point(0.5, 0.5);
        SecondaryContent.UpdateLayout();

        var fadeIn = MakeAnim(0, 1, durIn, _easeAppleOut, inDelay);
        var springSlide = MakeAnim(16, 0, durIn, _easeAppleOut, inDelay);
        var springScaleX = MakeAnim(0.96, 1, durIn, _easeAppleOut, inDelay);
        var springScaleY = MakeAnim(0.96, 1, durIn, _easeAppleOut, inDelay);
        Timeline.SetDesiredFrameRate(fadeIn, fps);
        Timeline.SetDesiredFrameRate(springSlide, fps);
        Timeline.SetDesiredFrameRate(springScaleX, fps);
        Timeline.SetDesiredFrameRate(springScaleY, fps);

        fadeIn.Completed += (s, ev) =>
        {
            _isAnimating = false;
            _isScrollSessionLocked = false;
            NotchBorder.IsHitTestVisible = true;
            SecondaryContent.Opacity = 1;
            SecondaryContent.BeginAnimation(OpacityProperty, null);
            SecondaryContent.RenderTransform = null;

            if (IsCameraPreviewLifecycleActive)
            {
                StopCameraPreviewForViewExit();
            }
            ResetCameraSectionLayoutInstant();
        };

        SecondaryContent.BeginAnimation(OpacityProperty, fadeIn);
        secondaryTranslate.BeginAnimation(TranslateTransform.YProperty, springSlide);
        secondaryScale.BeginAnimation(ScaleTransform.ScaleXProperty, springScaleX);
        secondaryScale.BeginAnimation(ScaleTransform.ScaleYProperty, springScaleY);

        UpdateShelfCapacityIndicator();
    }

    #endregion

    #region Timer View Microinteractions

    private void TimerControlButton_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is Border button)
        {
            AnimateTimerButtonScale(button, 1.045);
        }
    }

    private void TimerControlButton_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Border button)
        {
            AnimateTimerButtonScale(button, 1.0);
        }
    }

    private void AnimateTimerButtonScale(Border button, double targetScale)
    {
        var scale = button.RenderTransform as ScaleTransform ?? new ScaleTransform(1, 1);
        button.RenderTransform = scale;
        button.RenderTransformOrigin = new Point(0.5, 0.5);

        var animX = MakeAnim(scale.ScaleX, targetScale, _dur150, _easeQuadOut);
        var animY = MakeAnim(scale.ScaleY, targetScale, _dur150, _easeQuadOut);
        Timeline.SetDesiredFrameRate(animX, VNotch.Services.AnimationConfig.TargetFps);
        Timeline.SetDesiredFrameRate(animY, VNotch.Services.AnimationConfig.TargetFps);

        scale.BeginAnimation(ScaleTransform.ScaleXProperty, animX);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, animY);
    }

    private void PlayTimerButtonPress(Border button)
    {
        PlayButtonPressAnimation(button);
    }

    // The digits themselves react: a quick scale bump that settles on a soft
    // spring, an amber flash rolling through the glyphs, and a brief orange
    // pulse on the capsule border.
    private void AnimateCountdownDigitBump(double magnitude = 1.0)
    {
        double peak = 1.0 + 0.05 * magnitude;

        CountdownDisplayScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        CountdownDisplayScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        CountdownDisplayScale.ScaleX = 1.0;
        CountdownDisplayScale.ScaleY = 1.0;

        var upX = MakeAnim(1.0, peak, _dur80, _easeQuadOut, null);
        var upY = MakeAnim(1.0, peak, _dur80, _easeQuadOut, null);
        upX.Completed += (_, _) =>
        {
            var settle = MakeAnim(peak, 1.0, _dur250, _easeSoftSpring, null);
            CountdownDisplayScale.BeginAnimation(ScaleTransform.ScaleXProperty, settle);
        };
        upY.Completed += (_, _) =>
        {
            var settle = MakeAnim(peak, 1.0, _dur250, _easeSoftSpring, null);
            CountdownDisplayScale.BeginAnimation(ScaleTransform.ScaleYProperty, settle);
        };
        CountdownDisplayScale.BeginAnimation(ScaleTransform.ScaleXProperty, upX);
        CountdownDisplayScale.BeginAnimation(ScaleTransform.ScaleYProperty, upY);

        AnimateCountdownDigitFlash();
        AnimateCountdownPanelBorderFlash();
    }

    private void AnimateCountdownDigitFlash()
    {
        var toAmber = new ColorAnimation(_countdownDigitsFlashColor, new Duration(TimeSpan.FromMilliseconds(90)))
        {
            EasingFunction = _easeQuadOut
        };
        Timeline.SetDesiredFrameRate(toAmber, VNotch.Services.AnimationConfig.TargetFps);
        toAmber.Completed += (_, _) =>
        {
            var toRest = new ColorAnimation(_countdownDigitsRestColor, new Duration(TimeSpan.FromMilliseconds(340)))
            {
                EasingFunction = _easeQuadOut
            };
            Timeline.SetDesiredFrameRate(toRest, VNotch.Services.AnimationConfig.TargetFps);
            CountdownDigitsBrush.BeginAnimation(SolidColorBrush.ColorProperty, toRest);
        };
        CountdownDigitsBrush.BeginAnimation(SolidColorBrush.ColorProperty, toAmber);
    }

    private void AnimateCountdownPanelBorder(Color target, int durationMs)
    {
        var anim = new ColorAnimation(target, new Duration(TimeSpan.FromMilliseconds(durationMs)))
        {
            EasingFunction = _easeQuadOut
        };
        Timeline.SetDesiredFrameRate(anim, VNotch.Services.AnimationConfig.TargetFps);
        CountdownPanelBorderBrush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
    }

    private void AnimateCountdownPanelBorderFlash()
    {
        var flash = new ColorAnimation(_countdownBorderFlashColor, new Duration(TimeSpan.FromMilliseconds(90)))
        {
            EasingFunction = _easeQuadOut
        };
        Timeline.SetDesiredFrameRate(flash, VNotch.Services.AnimationConfig.TargetFps);
        flash.Completed += (_, _) => AnimateCountdownPanelBorder(
            _isEditingTimer ? _countdownBorderEditingColor : _countdownBorderIdleColor, 380);
        CountdownPanelBorderBrush.BeginAnimation(SolidColorBrush.ColorProperty, flash);
    }

    private Border? GetStepHighlight(object sender) =>
        ReferenceEquals(sender, CountdownPlusBtn) ? CountdownPlusHighlight :
        ReferenceEquals(sender, CountdownMinusBtn) ? CountdownMinusHighlight : null;

    private void AnimateStepHighlightOpacity(Border highlight, double to, int durationMs)
    {
        var anim = MakeAnim(to, new Duration(TimeSpan.FromMilliseconds(durationMs)), _easeQuadOut);
        highlight.BeginAnimation(OpacityProperty, anim);
    }

    private void FlashStepHighlight(Border highlight)
    {
        var flash = MakeAnim(0.20, _dur80, _easeQuadOut);
        flash.Completed += (_, _) => AnimateStepHighlightOpacity(highlight, 0.08, 260);
        highlight.BeginAnimation(OpacityProperty, flash);
    }

    private void CountdownStepBtn_MouseEnter(object sender, MouseEventArgs e)
    {
        if (GetStepHighlight(sender) is { } highlight)
            AnimateStepHighlightOpacity(highlight, 0.08, 120);
    }

    #endregion

    #region Countdown Logic

    private void InitializeCountdownTimer()
    {
        _countdownTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _countdownTimer.Tick += CountdownTimer_Tick;
    }

    private void CountdownTimer_Tick(object? sender, EventArgs e)
    {
        if (_viewModel.Timer.Tick(TimeSpan.FromMilliseconds(100)))
        {
            _countdownTimer?.Stop();
            SetCountdownStartVisual(false);

            SystemSounds.Exclamation.Play();

            ShowCountdownCompletionOnPill();
            return;
        }

        UpdateTimerDisplay();
    }

    private bool _isCountdownCompleteVisible = false;

    private bool IsCountdownCompletionVisualActive =>
        _isCountdownCompleteVisible || CountdownCompleteOverlay.Visibility == Visibility.Visible;

    private void SuppressCompactMediaChromeForCountdownCompletion(bool animate = false)
    {
        _pendingFlipThumbnail = null;
        ResetAnimationThumbnailOverlay();
        CancelThumbnailSwitchAnimations(_currentMediaInfo?.Thumbnail);

        CompactThumbnailScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        CompactThumbnailScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        CompactThumbnailScale.ScaleX = 1.0;
        CompactThumbnailScale.ScaleY = 1.0;

        CompactThumbnailBorder.BeginAnimation(OpacityProperty, null);
        if (animate && CompactThumbnailBorder.Visibility == Visibility.Visible && CompactThumbnailBorder.Opacity > 0.01)
        {
            var thumbFade = MakeAnim(CompactThumbnailBorder.Opacity, 0.0,
                new Duration(TimeSpan.FromMilliseconds(180)), _easeQuadIn);
            Timeline.SetDesiredFrameRate(thumbFade, VNotch.Services.AnimationConfig.TargetFps);
            thumbFade.Completed += (_, _) =>
            {
                if (!IsCountdownCompletionVisualActive) return;
                CompactThumbnailBorder.BeginAnimation(OpacityProperty, null);
                CompactThumbnailBorder.Opacity = 0.0;
                CompactThumbnailBorder.Visibility = Visibility.Collapsed;
            };
            CompactThumbnailBorder.BeginAnimation(OpacityProperty, thumbFade);
        }
        else
        {
            CompactThumbnailBorder.Opacity = 0.0;
            CompactThumbnailBorder.Visibility = Visibility.Collapsed;
        }

        MusicViz.BeginAnimation(OpacityProperty, null);
        if (animate && MusicViz.Visibility == Visibility.Visible && MusicViz.Opacity > 0.01)
        {
            var vizFade = MakeAnim(MusicViz.Opacity, 0.0,
                new Duration(TimeSpan.FromMilliseconds(160)), _easeQuadIn);
            Timeline.SetDesiredFrameRate(vizFade, VNotch.Services.AnimationConfig.TargetFps);
            vizFade.Completed += (_, _) =>
            {
                if (!IsCountdownCompletionVisualActive) return;
                MusicViz.BeginAnimation(OpacityProperty, null);
                MusicViz.Opacity = 0.0;
                MusicViz.Visibility = Visibility.Collapsed;
            };
            MusicViz.BeginAnimation(OpacityProperty, vizFade);
        }
        else
        {
            MusicViz.Opacity = 0.0;
            MusicViz.Visibility = Visibility.Collapsed;
        }
    }

    private void EnsureExpandedStateForTimerSurface()
    {
        var state = _notchState.CurrentState;
        if (state == NotchState.Expanded)
            return;

        if (state == NotchState.Collapsed)
        {
            _notchState.TryTransitionTo(NotchState.Expanding);
            _notchState.TryTransitionTo(NotchState.Expanded);
            return;
        }

        if (state == NotchState.Expanding ||
            state == NotchState.SecondaryView ||
            state == NotchState.CameraExpanded)
        {
            if (_notchState.TryTransitionTo(NotchState.Expanded))
                return;
        }

        _notchState.ForceState(NotchState.Expanded);
    }

    private void BeginCountdownManualCollapseState()
    {
        var state = _notchState.CurrentState;
        if (state == NotchState.Collapsed || state == NotchState.Collapsing)
            return;

        if (state == NotchState.SecondaryView || state == NotchState.CameraExpanded)
        {
            _notchState.TryTransitionTo(NotchState.Expanded);
            state = _notchState.CurrentState;
        }

        if (state == NotchState.Expanded)
        {
            _notchState.TryTransitionTo(NotchState.Collapsing);
            return;
        }

        _notchState.ForceState(NotchState.Collapsing);
    }

    private void CompleteCountdownManualCollapseState()
    {
        var state = _notchState.CurrentState;
        if (state == NotchState.Collapsed)
            return;

        if (state == NotchState.Collapsing || state == NotchState.MusicCollapsing)
        {
            if (_notchState.TryTransitionTo(NotchState.Collapsed))
                return;
        }

        _notchState.ForceState(NotchState.Collapsed);
    }

    private void ShowCountdownCompletionOnPill()
    {
        _isCountdownCompleteVisible = true;
        SuppressCompactMediaChromeForCountdownCompletion(animate: true);
        AnimateCornerRadius(_cornerRadiusExpanded, TimeSpan.FromMilliseconds(360));

        AnimateCountdownCompletionToClockView();
    }

    private void AnimateCountdownCompletionToClockView()
    {
        EnsureExpandedStateForTimerSurface();
        // This animation can take ownership of Width/Height while the normal
        // expand animation is still running. Its old completion is then
        // removed, so finalize the logical expanded state here as well.
        _isExpanded = true;
        _isTimerView = true;
        _isSecondaryView = false;
        _isAnimating = true;
        _isScrollSessionLocked = true;
        NotchBorder.IsHitTestVisible = false;

        var exitDuration = new Duration(TimeSpan.FromMilliseconds(220));
        var resizeDuration = new Duration(TimeSpan.FromMilliseconds(420));

        AnimateCountdownCompletionContentOut(ExpandedContent, exitDuration);
        AnimateCountdownCompletionContentOut(SecondaryContent, exitDuration);
        AnimateCountdownCompletionContentOut(TimerContent, exitDuration);
        AnimateCountdownCompletionContentOut(CollapsedContent, exitDuration);
        AnimateCountdownCompletionContentOut(MusicCompactContent, exitDuration);
        AnimateCountdownCompletionNavOut(exitDuration);

        double currentWidth = NotchBorder.ActualWidth > 0 ? NotchBorder.ActualWidth : NotchBorder.Width;
        if (double.IsNaN(currentWidth) || currentWidth <= 0) currentWidth = _collapsedWidth;
        double currentHeight = NotchBorder.ActualHeight > 0 ? NotchBorder.ActualHeight : NotchBorder.Height;
        if (double.IsNaN(currentHeight) || currentHeight <= 0) currentHeight = _collapsedHeight;
        double targetWidth = CountdownCompleteViewWidth;

        NotchBorder.BeginAnimation(WidthProperty, null);
        NotchBorder.BeginAnimation(HeightProperty, null);
        NotchBorder.Width = currentWidth;
        NotchBorder.Height = currentHeight;

        var widthAnim = MakeAnim(currentWidth, targetWidth, resizeDuration, _easeExpOut6);
        var heightAnim = MakeAnim(currentHeight, _timerViewHeight, resizeDuration, _easeExpOut6);
        Timeline.SetDesiredFrameRate(widthAnim, VNotch.Services.AnimationConfig.TargetFps);
        Timeline.SetDesiredFrameRate(heightAnim, VNotch.Services.AnimationConfig.TargetFps);

        heightAnim.Completed += (_, _) =>
        {
            EnsureExpandedStateForTimerSurface();
            _isAnimating = false;
            _isScrollSessionLocked = false;
            NotchBorder.IsHitTestVisible = true;
            NotchBorder.BeginAnimation(WidthProperty, null);
            NotchBorder.BeginAnimation(HeightProperty, null);
            NotchBorder.Width = targetWidth;
            NotchBorder.Height = _timerViewHeight;
            RestoreExpandedWindowSize();
            ShowCompletionOverlayContent();
        };

        NotchBorder.BeginAnimation(WidthProperty, widthAnim, HandoffBehavior.SnapshotAndReplace);
        NotchBorder.BeginAnimation(HeightProperty, heightAnim, HandoffBehavior.SnapshotAndReplace);
    }

    private void AnimateCountdownCompletionContentOut(FrameworkElement element, Duration duration)
    {
        if (element.Visibility != Visibility.Visible || element.Opacity <= 0.01) return;

        element.BeginAnimation(OpacityProperty, null);
        element.Effect = null;

        var group = new TransformGroup();
        var scale = new ScaleTransform(1, 1);
        var translate = new TranslateTransform(0, 0);
        group.Children.Add(scale);
        group.Children.Add(translate);
        element.RenderTransform = group;
        element.RenderTransformOrigin = new Point(0.5, 0.5);

        var fade = MakeAnim(element.Opacity, 0.0, duration, _easeQuadIn);
        var slide = MakeAnim(0.0, -14.0, duration, _easeQuadIn);
        var scaleAnim = MakeAnim(1.0, 0.96, duration, _easeQuadIn);
        Timeline.SetDesiredFrameRate(fade, VNotch.Services.AnimationConfig.TargetFps);
        Timeline.SetDesiredFrameRate(slide, VNotch.Services.AnimationConfig.TargetFps);
        Timeline.SetDesiredFrameRate(scaleAnim, VNotch.Services.AnimationConfig.TargetFps);

        fade.Completed += (_, _) =>
        {
            element.BeginAnimation(OpacityProperty, null);
            element.Opacity = 0;
            element.Visibility = Visibility.Collapsed;
            element.RenderTransform = null;
        };

        element.BeginAnimation(OpacityProperty, fade);
        translate.BeginAnimation(TranslateTransform.YProperty, slide);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);
    }

    private void AnimateCountdownCompletionNavOut(Duration duration)
    {
        if (NavIconsPanel.Visibility == Visibility.Visible || NavIconsPanel.Opacity > 0.01)
        {
            NavIconsPanel.BeginAnimation(OpacityProperty, null);
            NavIconsTranslate.BeginAnimation(TranslateTransform.YProperty, null);

            var navFade = MakeAnim(NavIconsPanel.Opacity, 0.0, duration, _easeQuadIn);
            var navSlide = MakeAnim(NavIconsTranslate.Y, -8.0, duration, _easeQuadIn);
            Timeline.SetDesiredFrameRate(navFade, VNotch.Services.AnimationConfig.TargetFps);
            Timeline.SetDesiredFrameRate(navSlide, VNotch.Services.AnimationConfig.TargetFps);
            navFade.Completed += (_, _) =>
            {
                NavIconsPanel.BeginAnimation(OpacityProperty, null);
                NavIconsPanel.Opacity = 0;
                NavIconsPanel.Visibility = Visibility.Collapsed;
                NavIconsTranslate.BeginAnimation(TranslateTransform.YProperty, null);
                NavIconsTranslate.Y = 0;
            };
            NavIconsPanel.BeginAnimation(OpacityProperty, navFade);
            NavIconsTranslate.BeginAnimation(TranslateTransform.YProperty, navSlide);
        }

        if (NavIconsBackground.Visibility == Visibility.Visible || NavIconsBackground.Opacity > 0.01)
        {
            NavIconsBackground.BeginAnimation(OpacityProperty, null);
            var navBgFade = MakeAnim(NavIconsBackground.Opacity, 0.0, duration, _easeQuadIn);
            Timeline.SetDesiredFrameRate(navBgFade, VNotch.Services.AnimationConfig.TargetFps);
            navBgFade.Completed += (_, _) =>
            {
                NavIconsBackground.BeginAnimation(OpacityProperty, null);
                NavIconsBackground.Opacity = 0;
                NavIconsBackground.Visibility = Visibility.Collapsed;
            };
            NavIconsBackground.BeginAnimation(OpacityProperty, navBgFade);
        }
    }

    private void ShowCompletionOverlayContent()
    {
        ExpandedContent.Visibility = Visibility.Collapsed;
        TimerContent.Visibility = Visibility.Collapsed;
        SecondaryContent.Visibility = Visibility.Collapsed;
        SuppressCompactMediaChromeForCountdownCompletion();

        CountdownCompleteOverlay.BeginAnimation(OpacityProperty, null);
        CountdownCompleteOverlay.RenderTransform = new TranslateTransform(0, -10);
        CountdownCompleteOverlay.Visibility = Visibility.Visible;
        CountdownCompleteOverlay.Opacity = 0;

        var overlayFade = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(220)))
        {
            EasingFunction = _easeQuadOut
        };
        var overlaySlide = new DoubleAnimation(-10, 0, new Duration(TimeSpan.FromMilliseconds(300)))
        {
            EasingFunction = _easeExpOut6
        };
        Timeline.SetDesiredFrameRate(overlayFade, VNotch.Services.AnimationConfig.TargetFps);
        Timeline.SetDesiredFrameRate(overlaySlide, VNotch.Services.AnimationConfig.TargetFps);
        CountdownCompleteOverlay.BeginAnimation(OpacityProperty, overlayFade);
        ((TranslateTransform)CountdownCompleteOverlay.RenderTransform).BeginAnimation(TranslateTransform.YProperty, overlaySlide);

        CountdownCompleteSurface.BeginAnimation(OpacityProperty, null);
        CountdownCompleteSurface.Opacity = 0;

        PrepareCountdownCompleteElement(CountdownCompleteText, CountdownCompleteTextTranslate);
        PrepareCountdownCompleteElement(CountdownRestartHost, CountdownRestartTranslate);
        PrepareCountdownCompleteElement(CountdownDismissHost, CountdownDismissTranslate);

        var surfaceFade = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(180)))
        {
            EasingFunction = _easeQuadOut
        };
        Timeline.SetDesiredFrameRate(surfaceFade, VNotch.Services.AnimationConfig.TargetFps);
        CountdownCompleteSurface.BeginAnimation(OpacityProperty, surfaceFade);

        AnimateCountdownCompleteElement(CountdownCompleteText, CountdownCompleteTextTranslate, TimeSpan.Zero,
            (_, _) => StartCountdownCompleteTextFlash());
        AnimateCountdownCompleteElement(CountdownRestartHost, CountdownRestartTranslate, TimeSpan.FromMilliseconds(45));
        AnimateCountdownCompleteElement(CountdownDismissHost, CountdownDismissTranslate, TimeSpan.FromMilliseconds(80));
    }

    private void PrepareCountdownCompleteElement(FrameworkElement element, TranslateTransform translate)
    {
        element.BeginAnimation(OpacityProperty, null);
        translate.BeginAnimation(TranslateTransform.YProperty, null);
        element.Opacity = 0;
        translate.Y = -14;
    }

    private void AnimateCountdownCompleteElement(
        FrameworkElement element,
        TranslateTransform translate,
        TimeSpan beginTime,
        EventHandler? completed = null)
    {
        var duration = new Duration(TimeSpan.FromMilliseconds(340));
        var opacityAnim = new DoubleAnimation(0, 1, duration)
        {
            EasingFunction = _easeQuadOut,
            BeginTime = beginTime
        };
        var translateAnim = new DoubleAnimation(-14, 0, duration)
        {
            EasingFunction = _easeExpOut6,
            BeginTime = beginTime
        };

        if (completed != null)
        {
            opacityAnim.Completed += completed;
        }

        Timeline.SetDesiredFrameRate(opacityAnim, VNotch.Services.AnimationConfig.TargetFps);
        Timeline.SetDesiredFrameRate(translateAnim, VNotch.Services.AnimationConfig.TargetFps);
        element.BeginAnimation(OpacityProperty, opacityAnim);
        translate.BeginAnimation(TranslateTransform.YProperty, translateAnim);
    }

    private void StartCountdownCompleteTextFlash()
    {
        var flash = new DoubleAnimation(1, 0.2, new Duration(TimeSpan.FromMilliseconds(500)))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        };
        Timeline.SetDesiredFrameRate(flash, VNotch.Services.AnimationConfig.TargetFps);
        CountdownCompleteText.BeginAnimation(OpacityProperty, flash);
    }

    private void CountdownCompleteOverlay_MouseEnter(object sender, MouseEventArgs e)
    {
        AnimateCountdownCompleteHover(true);
    }

    private void CountdownCompleteOverlay_MouseLeave(object sender, MouseEventArgs e)
    {
        AnimateCountdownCompleteHover(false);
    }

    private void AnimateCountdownCompleteHover(bool isHovered)
    {
        if (isHovered && (!_isCountdownCompleteVisible || _isAnimating)) return;

        double targetScale = isHovered ? 1.004 : 1.0;
        double targetShadowScale = isHovered ? 1.0015 : 1.0;
        var duration = new Duration(TimeSpan.FromMilliseconds(isHovered ? 160 : 220));
        var easing = isHovered ? (IEasingFunction)_easeQuadOut : _easeExpOut6;

        var scaleX = MakeAnim(targetScale, duration, easing, VNotch.Services.AnimationConfig.TargetFps);
        var scaleY = MakeAnim(targetScale, duration, easing, VNotch.Services.AnimationConfig.TargetFps);
        var shadowScaleX = MakeAnim(targetShadowScale, duration, easing, VNotch.Services.AnimationConfig.TargetFps);
        var shadowScaleY = MakeAnim(targetShadowScale, duration, easing, VNotch.Services.AnimationConfig.TargetFps);
        NotchScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleX);
        NotchScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleY);
        NotchShadowScale.BeginAnimation(ScaleTransform.ScaleXProperty, shadowScaleX);
        NotchShadowScale.BeginAnimation(ScaleTransform.ScaleYProperty, shadowScaleY);
    }

    private void CountdownRestart_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_isAnimating) return;

        _countdownRemaining = _countdownDuration;
        _isCountdownRunning = true;
        if (_countdownTimer == null) InitializeCountdownTimer();
        _countdownTimer?.Start();

        AnimateCountdownRestartToTimerView();
    }

    private void AnimateCountdownRestartToTimerView()
    {
        _isAnimating = true;
        _isCountdownCompleteVisible = false;
        _isTimerView = true;
        _isSecondaryView = false;
        EnsureExpandedStateForTimerSurface();
        _lastViewSwitchUtc = DateTime.UtcNow;
        _isScrollSessionLocked = true;
        NotchBorder.IsHitTestVisible = false;

        AnimateCountdownCompleteHover(false);

        CountdownCompleteText.BeginAnimation(OpacityProperty, null);
        CountdownCompleteText.Opacity = 1;

        ExpandedContent.Visibility = Visibility.Collapsed;
        ExpandedContent.Opacity = 0;
        SecondaryContent.Visibility = Visibility.Collapsed;
        SecondaryContent.Opacity = 0;
        CollapsedContent.Visibility = Visibility.Collapsed;
        CollapsedContent.Opacity = 0;
        MusicCompactContent.Visibility = Visibility.Collapsed;
        MusicCompactContent.Opacity = 0;

        UpdateTimerDisplay();
        SetCountdownStartVisual(true);
        UpdateTimerNavIconsState();

        NavIconsPanel.BeginAnimation(OpacityProperty, null);
        NavIconsPanel.Visibility = Visibility.Visible;
        NavIconsPanel.Opacity = 0;
        NavIconsTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        NavIconsTranslate.Y = -6;

        NavIconsBackground.BeginAnimation(OpacityProperty, null);
        NavIconsBackground.Visibility = Visibility.Visible;
        NavIconsBackground.Opacity = 0;

        TimerContent.BeginAnimation(OpacityProperty, null);
        TimerContent.Visibility = Visibility.Visible;
        TimerContent.Opacity = 0;
        TimerContent.Effect = null;

        var timerGroup = new TransformGroup();
        var timerScale = new ScaleTransform(0.96, 0.96);
        var timerTranslate = new TranslateTransform(0, -22);
        timerGroup.Children.Add(timerScale);
        timerGroup.Children.Add(timerTranslate);
        TimerContent.RenderTransform = timerGroup;
        TimerContent.RenderTransformOrigin = new Point(0.5, 0.5);

        var overlayTranslate = new TranslateTransform(0, 0);
        CountdownCompleteOverlay.RenderTransform = overlayTranslate;

        var durOut = new Duration(TimeSpan.FromMilliseconds(220));
        var durIn = new Duration(TimeSpan.FromMilliseconds(430));
        var inDelay = TimeSpan.FromMilliseconds(70);
        int fps = VNotch.Services.AnimationConfig.TargetFps;

        var overlayFade = MakeAnim(CountdownCompleteOverlay.Opacity, 0, durOut, _easeQuadIn);
        var overlaySlide = MakeAnim(0, 18, durOut, _easeQuadIn);
        Timeline.SetDesiredFrameRate(overlayFade, fps);
        Timeline.SetDesiredFrameRate(overlaySlide, fps);

        overlayFade.Completed += (s, e) =>
        {
            DismissCountdownCompletion();
            CountdownCompleteOverlay.RenderTransform = null;
        };

        CountdownCompleteOverlay.BeginAnimation(OpacityProperty, overlayFade);
        overlayTranslate.BeginAnimation(TranslateTransform.YProperty, overlaySlide);

        var timerFadeIn = MakeAnim(0, 1, durIn, _easeExpOut6, inDelay);
        var timerSlideIn = MakeAnim(-22, 0, durIn, _easeExpOut7, inDelay);
        var timerScaleIn = MakeAnim(0.96, 1, durIn, _easeSoftSpring, inDelay);
        Timeline.SetDesiredFrameRate(timerFadeIn, fps);
        Timeline.SetDesiredFrameRate(timerSlideIn, fps);
        Timeline.SetDesiredFrameRate(timerScaleIn, fps);

        timerFadeIn.Completed += (s, e) =>
        {
            EnsureExpandedStateForTimerSurface();
            _isAnimating = false;
            _isScrollSessionLocked = false;
            NotchBorder.IsHitTestVisible = true;
            TimerContent.BeginAnimation(OpacityProperty, null);
            TimerContent.Opacity = 1;
            TimerContent.RenderTransform = null;

            NavIconsPanel.BeginAnimation(OpacityProperty, null);
            NavIconsPanel.Opacity = 1;
            NavIconsTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            NavIconsTranslate.Y = 0;
            NavIconsBackground.BeginAnimation(OpacityProperty, null);
            NavIconsBackground.Opacity = 1;

            UpdateTimerNavIconsState();
            UpdateTimerDisplay();

            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, () =>
            {
                RestoreTimerContentOpacity();
            });
        };

        TimerContent.BeginAnimation(OpacityProperty, timerFadeIn);
        timerTranslate.BeginAnimation(TranslateTransform.YProperty, timerSlideIn);
        timerScale.BeginAnimation(ScaleTransform.ScaleXProperty, timerScaleIn);
        timerScale.BeginAnimation(ScaleTransform.ScaleYProperty, timerScaleIn);

        var navFadeIn = MakeAnim(0, 1, new Duration(TimeSpan.FromMilliseconds(260)), _easeQuadOut, TimeSpan.FromMilliseconds(120));
        var navSlideIn = MakeAnim(-6, 0, new Duration(TimeSpan.FromMilliseconds(300)), _easeExpOut6, TimeSpan.FromMilliseconds(120));
        var navBgFadeIn = MakeAnim(0, 1, new Duration(TimeSpan.FromMilliseconds(300)), _easePowerOut3, TimeSpan.FromMilliseconds(140));
        Timeline.SetDesiredFrameRate(navFadeIn, fps);
        Timeline.SetDesiredFrameRate(navSlideIn, fps);
        Timeline.SetDesiredFrameRate(navBgFadeIn, fps);
        NavIconsPanel.BeginAnimation(OpacityProperty, navFadeIn);
        NavIconsTranslate.BeginAnimation(TranslateTransform.YProperty, navSlideIn);
        NavIconsBackground.BeginAnimation(OpacityProperty, navBgFadeIn);

        ApplyClockViewWindowSize();
        PrepareClockViewContentSize();
        RefreshClockView();

        double currentHeight = NotchBorder.ActualHeight > 0 ? NotchBorder.ActualHeight : _timerViewHeight;
        double currentWidth = NotchBorder.ActualWidth > 0 ? NotchBorder.ActualWidth : NotchBorder.Width;
        if (double.IsNaN(currentWidth) || currentWidth <= 0) currentWidth = _expandedWidth;
        AnimateClockViewNotchResize(currentWidth, currentHeight, _clockViewWidth, _clockViewHeight, durIn, TimeSpan.Zero);
    }

    private void CountdownDismiss_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_isAnimating) return;

        AnimateCountdownCompleteOverlayOut();
        _countdownRemaining = _countdownDuration;
        _isTimerView = false;
        _isSecondaryView = false;
        BeginCountdownManualCollapseState();

        _isAnimating = true;
        var durCollapse = new Duration(TimeSpan.FromMilliseconds(400));
        var widthAnim = MakeAnim(_collapsedWidth, durCollapse, _easeExpOut6, VNotch.Services.AnimationConfig.TargetFps);
        var heightAnim = MakeAnim(_collapsedHeight, durCollapse, _easeExpOut6, VNotch.Services.AnimationConfig.TargetFps);
        AnimateCornerRadius(_cornerRadiusCollapsed, TimeSpan.FromMilliseconds(360));

        heightAnim.Completed += (s, ev) =>
        {
            CompleteCountdownManualCollapseState();
            _isAnimating = false;
            _isScrollSessionLocked = false;
            NotchBorder.IsHitTestVisible = true;

            TimerContent.BeginAnimation(OpacityProperty, null);
            TimerContent.Visibility = Visibility.Collapsed;
            TimerContent.Opacity = 0;
            TimerContent.RenderTransform = null;
            ExpandedContent.BeginAnimation(OpacityProperty, null);
            ExpandedContent.Visibility = Visibility.Collapsed;
            ExpandedContent.Opacity = 0;
            ExpandedContent.RenderTransform = null;
            SecondaryContent.BeginAnimation(OpacityProperty, null);
            SecondaryContent.Visibility = Visibility.Collapsed;
            SecondaryContent.Opacity = 0;
            SecondaryContent.RenderTransform = null;
            NavIconsPanel.BeginAnimation(OpacityProperty, null);
            NavIconsPanel.Opacity = 0;
            NavIconsPanel.Visibility = Visibility.Collapsed;
            NavIconsBackground.BeginAnimation(OpacityProperty, null);
            NavIconsBackground.Opacity = 0;
            NavIconsBackground.Visibility = Visibility.Collapsed;
            ShelfCountBadge.Visibility = Visibility.Collapsed;
            DisableKeyboardInput();

            if (_isMusicCompactMode)
            {
                RestoreMusicCompactPillAfterCountdownDismiss();
            }
            else
            {
                CollapsedContent.BeginAnimation(OpacityProperty, null);
                CollapsedContent.Visibility = Visibility.Visible;
                CollapsedContent.Opacity = 0;
                AnimateCountdownCollapsedContentIn(CollapsedContent);
            }
        };

        NotchBorder.BeginAnimation(WidthProperty, widthAnim);
        NotchBorder.BeginAnimation(HeightProperty, heightAnim);

        SetCountdownStartVisual(false);
    }

    private void AnimateCountdownCompleteOverlayOut()
    {
        _isCountdownCompleteVisible = false;
        AnimateCountdownCompleteHover(false);

        AnimateCountdownCompleteElementsFadeOut();

        var overlayTranslate = CountdownCompleteOverlay.RenderTransform as TranslateTransform ?? new TranslateTransform(0, 0);
        CountdownCompleteOverlay.RenderTransform = overlayTranslate;

        var duration = new Duration(TimeSpan.FromMilliseconds(220));
        var overlayDelay = TimeSpan.FromMilliseconds(80);
        var fade = MakeAnim(CountdownCompleteOverlay.Opacity, 0.0, duration, _easeQuadIn, overlayDelay);
        var slide = MakeAnim(overlayTranslate.Y, 18.0, duration, _easeQuadIn, overlayDelay);
        Timeline.SetDesiredFrameRate(fade, VNotch.Services.AnimationConfig.TargetFps);
        Timeline.SetDesiredFrameRate(slide, VNotch.Services.AnimationConfig.TargetFps);

        fade.Completed += (_, _) =>
        {
            DismissCountdownCompletion();
            CountdownCompleteOverlay.RenderTransform = null;
        };

        CountdownCompleteOverlay.BeginAnimation(OpacityProperty, fade);
        overlayTranslate.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    private void AnimateCountdownCompleteElementsFadeOut()
    {
        int fps = VNotch.Services.AnimationConfig.TargetFps;
        var baseDuration = new Duration(TimeSpan.FromMilliseconds(160));
        var easing = _easeQuadIn;

        if (CountdownCompleteText != null && CountdownCompleteText.Visibility == Visibility.Visible)
        {
            double currentOpacity = CountdownCompleteText.Opacity;
            if (currentOpacity > 0.01)
            {
                CountdownCompleteText.BeginAnimation(OpacityProperty, null);
                var fadeAnim = MakeAnim(currentOpacity, 0, baseDuration, easing, TimeSpan.Zero);
                Timeline.SetDesiredFrameRate(fadeAnim, fps);
                CountdownCompleteText.BeginAnimation(OpacityProperty, fadeAnim);

                if (CountdownCompleteTextTranslate != null)
                {
                    CountdownCompleteTextTranslate.BeginAnimation(TranslateTransform.YProperty, null);
                    var slideAnim = MakeAnim(CountdownCompleteTextTranslate.Y, CountdownCompleteTextTranslate.Y - 8, baseDuration, easing, TimeSpan.Zero);
                    Timeline.SetDesiredFrameRate(slideAnim, fps);
                    CountdownCompleteTextTranslate.BeginAnimation(TranslateTransform.YProperty, slideAnim);
                }
            }
        }

        var wave2Delay = TimeSpan.FromMilliseconds(30);

        if (CountdownRestartHost != null && CountdownRestartHost.Visibility == Visibility.Visible)
        {
            double currentOpacity = CountdownRestartHost.Opacity;
            if (currentOpacity > 0.01)
            {
                CountdownRestartHost.BeginAnimation(OpacityProperty, null);
                var fadeAnim = MakeAnim(currentOpacity, 0, baseDuration, easing, wave2Delay);
                Timeline.SetDesiredFrameRate(fadeAnim, fps);
                CountdownRestartHost.BeginAnimation(OpacityProperty, fadeAnim);

                if (CountdownRestartTranslate != null)
                {
                    CountdownRestartTranslate.BeginAnimation(TranslateTransform.YProperty, null);
                    var slideAnim = MakeAnim(CountdownRestartTranslate.Y, CountdownRestartTranslate.Y - 6, baseDuration, easing, wave2Delay);
                    Timeline.SetDesiredFrameRate(slideAnim, fps);
                    CountdownRestartTranslate.BeginAnimation(TranslateTransform.YProperty, slideAnim);
                }
            }
        }

        if (CountdownDismissHost != null && CountdownDismissHost.Visibility == Visibility.Visible)
        {
            double currentOpacity = CountdownDismissHost.Opacity;
            if (currentOpacity > 0.01)
            {
                CountdownDismissHost.BeginAnimation(OpacityProperty, null);
                var fadeAnim = MakeAnim(currentOpacity, 0, baseDuration, easing, wave2Delay);
                Timeline.SetDesiredFrameRate(fadeAnim, fps);
                CountdownDismissHost.BeginAnimation(OpacityProperty, fadeAnim);

                if (CountdownDismissTranslate != null)
                {
                    CountdownDismissTranslate.BeginAnimation(TranslateTransform.YProperty, null);
                    var slideAnim = MakeAnim(CountdownDismissTranslate.Y, CountdownDismissTranslate.Y - 6, baseDuration, easing, wave2Delay);
                    Timeline.SetDesiredFrameRate(slideAnim, fps);
                    CountdownDismissTranslate.BeginAnimation(TranslateTransform.YProperty, slideAnim);
                }
            }
        }

        var wave3Delay = TimeSpan.FromMilliseconds(50);

        if (CountdownCompleteSurface != null && CountdownCompleteSurface.Visibility == Visibility.Visible)
        {
            double currentOpacity = CountdownCompleteSurface.Opacity;
            if (currentOpacity > 0.01)
            {
                CountdownCompleteSurface.BeginAnimation(OpacityProperty, null);
                var fadeAnim = MakeAnim(currentOpacity, 0, new Duration(TimeSpan.FromMilliseconds(180)), easing, wave3Delay);
                Timeline.SetDesiredFrameRate(fadeAnim, fps);
                CountdownCompleteSurface.BeginAnimation(OpacityProperty, fadeAnim);
            }
        }
    }

    private void AnimateCountdownCollapsedContentIn(FrameworkElement content)
    {
        var group = new TransformGroup();
        var scale = new ScaleTransform(0.88, 0.88);
        var translate = new TranslateTransform(0, -6);
        group.Children.Add(scale);
        group.Children.Add(translate);
        content.RenderTransform = group;
        content.RenderTransformOrigin = new Point(0.5, 0.5);

        var duration = new Duration(TimeSpan.FromMilliseconds(300));
        var fade = MakeAnim(0.0, 1.0, duration, _easePowerOut3);
        var slide = MakeAnim(-6.0, 0.0, duration, _easeExpOut6);
        var scaleAnim = MakeAnim(0.88, 1.0, duration, _easeSoftSpring);
        Timeline.SetDesiredFrameRate(fade, VNotch.Services.AnimationConfig.TargetFps);
        Timeline.SetDesiredFrameRate(slide, VNotch.Services.AnimationConfig.TargetFps);
        Timeline.SetDesiredFrameRate(scaleAnim, VNotch.Services.AnimationConfig.TargetFps);

        fade.Completed += (_, _) =>
        {
            content.BeginAnimation(OpacityProperty, null);
            content.Opacity = 1;
            content.RenderTransform = null;
        };

        content.BeginAnimation(OpacityProperty, fade);
        translate.BeginAnimation(TranslateTransform.YProperty, slide);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);
    }

    private void RestoreMusicCompactPillAfterCountdownDismiss()
    {
        MusicCompactContent.BeginAnimation(OpacityProperty, null);
        MusicCompactContent.Visibility = Visibility.Visible;
        MusicCompactContent.Opacity = 0;

        CollapsedContent.BeginAnimation(OpacityProperty, null);
        CollapsedContent.Visibility = Visibility.Collapsed;
        CollapsedContent.Opacity = 0;

        MusicCompactContentBlur.BeginAnimation(BlurEffect.RadiusProperty, null);
        MusicCompactContentBlur.Radius = 0;
        CompactHoverInfo.BeginAnimation(OpacityProperty, null);
        CompactHoverInfo.Opacity = 0;
        CompactHoverInfo.Visibility = Visibility.Collapsed;

        if (CompactThumbnailBorder != null && !_isClipboardPeekActive && !_isVolumeIndicatorActive)
        {
            CompactThumbnailBorder.BeginAnimation(OpacityProperty, null);
            CompactThumbnailScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            CompactThumbnailScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            CompactThumbnailBorder.Visibility = Visibility.Visible;

            if (_currentMediaInfo?.Thumbnail != null)
            {
                CompactThumbnail.Source = _currentMediaInfo.Thumbnail;
                ThumbnailImage.Source = _currentMediaInfo.Thumbnail;
            }

            PlayThumbnailRevealAnimation();
        }

        if (_currentMediaInfo != null && !_isClipboardPeekActive && !_isVolumeIndicatorActive)
        {
            MusicViz.IsPlaying = _currentMediaInfo.IsPlaying;
            MusicViz.TrackId = _currentMediaInfo.GetSignature();

            if (_currentMediaInfo.IsPlaying)
            {
                MusicViz.BeginAnimation(OpacityProperty, null);
                MusicViz.Opacity = 0;
                MusicViz.Visibility = Visibility.Visible;
                ShowMusicVisualizer(duration: _dur250);
            }
        }

        MusicCompactContent.InvalidateArrange();
        MusicCompactContent.UpdateLayout();
        AnimateCountdownCollapsedContentIn(MusicCompactContent);
    }

    private void DismissCountdownCompletion()
    {
        _isCountdownCompleteVisible = false;
        AnimateCountdownCompleteHover(false);

        CountdownCompleteText.BeginAnimation(OpacityProperty, null);
        CountdownCompleteText.Opacity = 1;
        CountdownCompleteTextTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        CountdownRestartHost.BeginAnimation(OpacityProperty, null);
        CountdownRestartTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        CountdownDismissHost.BeginAnimation(OpacityProperty, null);
        CountdownDismissTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        CountdownCompleteSurface.BeginAnimation(OpacityProperty, null);
        CountdownCompleteSurface.Opacity = 0;

        CountdownCompleteOverlay.Visibility = Visibility.Collapsed;
        CountdownCompleteOverlay.Opacity = 0;
    }

    private void CountdownMinus_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_isCountdownRunning) return;
        if (_isEditingTimer) CommitTimerEditing(allowRetry: false);

        FlashStepHighlight(CountdownMinusHighlight);
        ApplyCountdownStep(-1);
        StartCountdownRepeat(-1);
    }

    private void CountdownPlus_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_isCountdownRunning) return;
        if (_isEditingTimer) CommitTimerEditing(allowRetry: false);

        FlashStepHighlight(CountdownPlusHighlight);
        ApplyCountdownStep(+1);
        StartCountdownRepeat(+1);
    }

    private void ApplyCountdownStep(int direction)
    {
        if (_viewModel.Timer.Adjust(direction))
        {
            SetCountdownProgress(animate: true);
            AnimateCountdownDigitBump();
        }
    }

    private void StartCountdownRepeat(int direction)
    {
        StopCountdownRepeat();
        _countdownRepeatDirection = direction;
        _countdownRepeatCount = 0;
        _countdownRepeatTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(RepeatInitialDelayMs)
        };
        _countdownRepeatTimer.Tick += CountdownRepeat_Tick;
        _countdownRepeatTimer.Start();
    }

    private void CountdownRepeat_Tick(object? sender, EventArgs e)
    {
        if (_isCountdownRunning)
        {
            StopCountdownRepeat();
            return;
        }

        _countdownRepeatCount++;
        ApplyCountdownStep(_countdownRepeatDirection);

        if (_countdownRepeatCount == RepeatAccelerateAfter && _countdownRepeatTimer != null)
        {
            _countdownRepeatTimer.Interval = TimeSpan.FromMilliseconds(RepeatFastIntervalMs);
        }
    }

    private void StopCountdownRepeat()
    {
        if (_countdownRepeatTimer != null)
        {
            _countdownRepeatTimer.Stop();
            _countdownRepeatTimer.Tick -= CountdownRepeat_Tick;
            _countdownRepeatTimer = null;
        }
    }

    private void CountdownBtn_MouseLeaveOrUp(object sender, EventArgs e)
    {
        StopCountdownRepeat();
        if (GetStepHighlight(sender) is { } highlight)
        {
            // A mouse-up keeps the hover glow; a leave clears it.
            AnimateStepHighlightOpacity(highlight, e is MouseButtonEventArgs ? 0.08 : 0.0, 200);
        }
    }

    private void CountdownStart_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        PlayTimerButtonPress(CountdownStartBtn);

        if (_isEditingTimer)
            CommitTimerEditing(allowRetry: false);

        if (_countdownTimer == null)
            InitializeCountdownTimer();

        if (_isCountdownRunning)
        {
            _viewModel.Timer.Pause();
            _countdownTimer?.Stop();
            SetCountdownStartVisual(false);
        }
        else
        {
            _viewModel.Timer.Start();
            _countdownTimer?.Start();
            SetCountdownStartVisual(true);
        }
    }

    private void CountdownReset_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        PlayTimerButtonPress(CountdownResetBtn);
        if (_isEditingTimer) CancelTimerEditing();
        _viewModel.Timer.Reset();
        _countdownTimer?.Stop();
        SetCountdownStartVisual(false);
        SetCountdownProgress(animate: true);
        AnimateCountdownDigitBump(1.2);
    }

    private void UpdateTimerDisplay()
    {
        UpdateCountdownProgressFill();
    }

    private void CountdownDisplayPanel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateCountdownProgressFill();
    }

    private void UpdateCountdownProgressFill()
    {
        SetCountdownProgress(animate: false);
    }

    private void SetCountdownProgress(bool animate)
    {
        double progress = Math.Clamp(_viewModel.Timer.Progress, 0, 1);

        double trackWidth = CountdownProgressTrack.ActualWidth;
        if (trackWidth <= 0) return;

        double targetWidth = trackWidth * progress;
        double edgeOpacity = progress > 0.02 ? 1.0 : 0.0;

        if (animate)
        {
            var widthAnim = MakeAnim(targetWidth, new Duration(TimeSpan.FromMilliseconds(340)), _easeExpOut6);
            CountdownProgressFill.BeginAnimation(WidthProperty, widthAnim);
            var edgeAnim = MakeAnim(edgeOpacity, _dur200, _easeQuadOut);
            CountdownProgressEdge.BeginAnimation(OpacityProperty, edgeAnim);
        }
        else
        {
            CountdownProgressFill.BeginAnimation(WidthProperty, null);
            CountdownProgressFill.Width = targetWidth;
            CountdownProgressEdge.BeginAnimation(OpacityProperty, null);
            CountdownProgressEdge.Opacity = edgeOpacity;
        }
    }

    #region Custom Time Input Editing

    private bool _isEditingTimer;

    private void CountdownDisplayPanel_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_isEditingTimer) return;

        if (_isCountdownRunning)
        {
            _viewModel.Timer.Pause();
            _countdownTimer?.Stop();
            SetCountdownStartVisual(false);
        }

        StartTimerEditing();
    }

    // Edit mode swaps the digits for a text field with a vertical carousel:
    // digits lift out through the top while the input rises from below on a
    // spring, and the capsule border warms to orange while typing.
    private void StartTimerEditing()
    {
        if (_isEditingTimer) return;
        _isEditingTimer = true;

        // The notch is a WS_EX_NOACTIVATE overlay; without lifting that style
        // the window can never take keyboard focus and typing goes to the app
        // behind it.
        EnableKeyboardInput();

        CountdownInput.Text = _viewModel.Timer.DisplayText;

        var durOut = new Duration(TimeSpan.FromMilliseconds(110));
        var durIn = new Duration(TimeSpan.FromMilliseconds(360));
        var inDelay = TimeSpan.FromMilliseconds(50);

        var digitsFade = MakeAnim(CountdownDisplay.Opacity > 0 ? CountdownDisplay.Opacity : 1.0, 0.0, durOut, _easeQuadIn, null);
        digitsFade.Completed += (_, _) =>
        {
            if (_isEditingTimer) CountdownDisplay.Visibility = Visibility.Collapsed;
        };
        var digitsRise = MakeAnim(CountdownDisplayTranslate.Y, -11.0, durOut, _easeQuadIn, null);
        var digitsShrinkX = MakeAnim(1.0, 0.94, durOut, _easeQuadIn, null);
        var digitsShrinkY = MakeAnim(1.0, 0.94, durOut, _easeQuadIn, null);
        CountdownDisplay.BeginAnimation(OpacityProperty, digitsFade);
        CountdownDisplayTranslate.BeginAnimation(TranslateTransform.YProperty, digitsRise);
        CountdownDisplayScale.BeginAnimation(ScaleTransform.ScaleXProperty, digitsShrinkX);
        CountdownDisplayScale.BeginAnimation(ScaleTransform.ScaleYProperty, digitsShrinkY);

        CountdownInput.BeginAnimation(OpacityProperty, null);
        CountdownInputTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        CountdownInputTranslate.X = 0;
        CountdownInput.Visibility = Visibility.Visible;
        CountdownInput.Opacity = 0;

        var inputFade = MakeAnim(0.0, 1.0, _dur200, _easeQuadOut, inDelay);
        var inputRise = MakeAnim(12.0, 0.0, durIn, _easeExpOut6, inDelay);
        var inputGrowX = MakeAnim(0.97, 1.0, durIn, _easeSoftSpring, inDelay);
        var inputGrowY = MakeAnim(0.97, 1.0, durIn, _easeSoftSpring, inDelay);
        CountdownInput.BeginAnimation(OpacityProperty, inputFade);
        CountdownInputTranslate.BeginAnimation(TranslateTransform.YProperty, inputRise);
        CountdownInputScale.BeginAnimation(ScaleTransform.ScaleXProperty, inputGrowX);
        CountdownInputScale.BeginAnimation(ScaleTransform.ScaleYProperty, inputGrowY);

        AnimateCountdownPanelBorder(_countdownBorderEditingColor, 240);
        var trackDim = MakeAnim(0.3, _dur200, _easeQuadOut);
        CountdownProgressTrack.BeginAnimation(OpacityProperty, trackDim);

        CountdownInput.Focus();
        Keyboard.Focus(CountdownInput);
        CountdownInput.SelectAll();
    }

    private void ExitTimerEditing()
    {
        if (!_isEditingTimer) return;
        _isEditingTimer = false;

        DisableKeyboardInput();

        var durOut = new Duration(TimeSpan.FromMilliseconds(110));
        var durIn = new Duration(TimeSpan.FromMilliseconds(360));
        var inDelay = TimeSpan.FromMilliseconds(50);

        var inputFade = MakeAnim(CountdownInput.Opacity > 0 ? CountdownInput.Opacity : 1.0, 0.0, durOut, _easeQuadIn, null);
        inputFade.Completed += (_, _) =>
        {
            if (!_isEditingTimer) CountdownInput.Visibility = Visibility.Collapsed;
        };
        var inputDrop = MakeAnim(0.0, 12.0, durOut, _easeQuadIn, null);
        var inputShrinkX = MakeAnim(1.0, 0.97, durOut, _easeQuadIn, null);
        var inputShrinkY = MakeAnim(1.0, 0.97, durOut, _easeQuadIn, null);
        CountdownInput.BeginAnimation(OpacityProperty, inputFade);
        CountdownInputTranslate.BeginAnimation(TranslateTransform.YProperty, inputDrop);
        CountdownInputScale.BeginAnimation(ScaleTransform.ScaleXProperty, inputShrinkX);
        CountdownInputScale.BeginAnimation(ScaleTransform.ScaleYProperty, inputShrinkY);

        CountdownDisplay.BeginAnimation(OpacityProperty, null);
        CountdownDisplay.Visibility = Visibility.Visible;
        CountdownDisplay.Opacity = 0;

        var digitsFade = MakeAnim(0.0, 1.0, _dur200, _easeQuadOut, inDelay);
        var digitsDrop = MakeAnim(-11.0, 0.0, durIn, _easeExpOut6, inDelay);
        var digitsGrowX = MakeAnim(0.94, 1.0, durIn, _easeSoftSpring, inDelay);
        var digitsGrowY = MakeAnim(0.94, 1.0, durIn, _easeSoftSpring, inDelay);
        CountdownDisplay.BeginAnimation(OpacityProperty, digitsFade);
        CountdownDisplayTranslate.BeginAnimation(TranslateTransform.YProperty, digitsDrop);
        CountdownDisplayScale.BeginAnimation(ScaleTransform.ScaleXProperty, digitsGrowX);
        CountdownDisplayScale.BeginAnimation(ScaleTransform.ScaleYProperty, digitsGrowY);

        AnimateCountdownPanelBorder(_countdownBorderIdleColor, 260);
        var trackRestore = MakeAnim(1.0, _dur200, _easeQuadOut);
        CountdownProgressTrack.BeginAnimation(OpacityProperty, trackRestore);

        Keyboard.ClearFocus();
    }

    // Instant teardown for view switches: no animations, just a clean idle state.
    private void CancelTimerEditingInstant()
    {
        if (!_isEditingTimer) return;
        _isEditingTimer = false;

        DisableKeyboardInput();

        CountdownInput.BeginAnimation(OpacityProperty, null);
        CountdownInputTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        CountdownInputTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        CountdownInputScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        CountdownInputScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        CountdownInput.Opacity = 0;
        CountdownInput.Visibility = Visibility.Collapsed;

        CountdownDisplay.BeginAnimation(OpacityProperty, null);
        CountdownDisplayTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        CountdownDisplayScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        CountdownDisplayScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        CountdownDisplayTranslate.Y = 0;
        CountdownDisplayScale.ScaleX = 1.0;
        CountdownDisplayScale.ScaleY = 1.0;
        CountdownDisplay.Opacity = 1;
        CountdownDisplay.Visibility = Visibility.Visible;

        CountdownPanelBorderBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
        CountdownPanelBorderBrush.Color = _countdownBorderIdleColor;
        CountdownProgressTrack.BeginAnimation(OpacityProperty, null);
        CountdownProgressTrack.Opacity = 1;

        Keyboard.ClearFocus();
    }

    private void CommitTimerEditing(bool allowRetry)
    {
        if (!_isEditingTimer) return;
        string input = CountdownInput.Text;

        if (_viewModel.Timer.TryParseCustomTime(input, out TimeSpan customTime))
        {
            _viewModel.Timer.SetCustomDuration(customTime);
            ExitTimerEditing();
            SetCountdownProgress(animate: true);
            AnimateCountdownDigitFlash();
            AnimateCountdownPanelBorderFlash();
            return;
        }

        if (allowRetry && !string.IsNullOrWhiteSpace(input))
        {
            // Unparsable entry on Enter: shake it off and let the user retype.
            AnimateCountdownInputShake();
            return;
        }

        ExitTimerEditing();
    }

    private void CancelTimerEditing()
    {
        if (!_isEditingTimer) return;
        ExitTimerEditing();
    }

    private void AnimateCountdownInputShake()
    {
        var shake = new DoubleAnimationUsingKeyFrames
        {
            Duration = new Duration(TimeSpan.FromMilliseconds(380))
        };
        shake.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        shake.KeyFrames.Add(new EasingDoubleKeyFrame(-8, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(60)), _easeQuadOut));
        shake.KeyFrames.Add(new EasingDoubleKeyFrame(7, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(140)), _easeQuadOut));
        shake.KeyFrames.Add(new EasingDoubleKeyFrame(-4, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(220)), _easeQuadOut));
        shake.KeyFrames.Add(new EasingDoubleKeyFrame(2, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(300)), _easeQuadOut));
        shake.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(380)), _easeQuadOut));
        Timeline.SetDesiredFrameRate(shake, VNotch.Services.AnimationConfig.TargetFps);
        CountdownInputTranslate.BeginAnimation(TranslateTransform.XProperty, shake);

        var error = new ColorAnimation(_countdownBorderErrorColor, new Duration(TimeSpan.FromMilliseconds(90)))
        {
            EasingFunction = _easeQuadOut
        };
        Timeline.SetDesiredFrameRate(error, VNotch.Services.AnimationConfig.TargetFps);
        error.Completed += (_, _) =>
        {
            if (_isEditingTimer) AnimateCountdownPanelBorder(_countdownBorderEditingColor, 420);
        };
        CountdownPanelBorderBrush.BeginAnimation(SolidColorBrush.ColorProperty, error);

        CountdownInput.SelectAll();
    }

    private void CountdownInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CommitTimerEditing(allowRetry: true);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelTimerEditing();
        }
    }

    private void CountdownInput_LostFocus(object sender, RoutedEventArgs e)
    {
        CommitTimerEditing(allowRetry: false);
    }

    #endregion

    #endregion
}

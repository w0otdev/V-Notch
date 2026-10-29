using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using VNotch.Controls;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch;

public partial class MainWindow
{
    #region Hover Animations

    private const double NotchHoverScaleX = 1.08;

    private bool _compactMarqueeRefreshQueued;

    private void AnimateNotchHover(bool isHovered)
    {
        if (_isExpanded || _isAnimating || _isGreetingActive || _isVolumeIndicatorActive || _spotlightMorphSessionActive || _spotlightMorphOwnsNotchVisibility) return;

        if (!isHovered && _isGestureActive) return;

        var light = MakeAnim(isHovered ? 0.35 : 0, _dur200, _easeQuadOut);
        HoverGlow.BeginAnimation(OpacityProperty, light);
        if (VNotch.Services.AnimationConfig.ReduceMotion)
        {
            NotchScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            NotchShadowScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            HoverGlow.BeginAnimation(OpacityProperty, null);
            HoverGlow.Opacity = isHovered ? 0.35 : 0;
            NotchScale.ScaleX = NotchShadowScale.ScaleX = 1;
            return;
        }

        double targetScale = isHovered ? NotchHoverScaleX : 1.0;
        var duration = isHovered ? _dur500 : _dur350;
        var easing = isHovered ? (IEasingFunction)_easeSoftSpring : _easeQuadOut;

        var animX = MakeAnim(targetScale, duration, easing);
        NotchScale.BeginAnimation(ScaleTransform.ScaleXProperty, animX);
        NotchShadowScale.BeginAnimation(ScaleTransform.ScaleXProperty, animX);

        // Keep the liquid glass tracking the notch at full rate while the hover
        // scale plays, so the refracted backdrop doesn't jump in throttled steps.
        BeginGlassHoverMotion(animX);
    }

    private void AnimateThumbnailHover(bool isHovered)
    {
        if (_isExpanded || _isAnimating || _isGreetingActive || _isVolumeIndicatorActive) return;

        if (!isHovered && _isGestureActive) return;

        ResetAnimationThumbnailOverlay();

        if (!_isThumbnailSwitchActive)
        {
            ResetCompactThumbnailNextLayer();
        }

        bool islandMode = _settings.EnableDynamicIslandMode;
        double thumbScale;
        double notchWidth;
        double notchHeight;

        if (isHovered)
        {
            thumbScale = islandMode ? 1.28 : 1.5;
            notchWidth = _collapsedWidth + (islandMode ? 24 : 32);
            notchHeight = _collapsedHeight + (islandMode ? 22 : 36);
        }
        else
        {
            thumbScale = 1.0;
            notchWidth = _collapsedWidth;
            notchHeight = _collapsedHeight;
        }
        double infoOpacity = isHovered ? 1 : 0;

        var duration = isHovered ? _dur500 : _dur350;
        var easing = isHovered ? (IEasingFunction)_easeThumbSpring : _easeExpOut6;
        int animFps = VNotch.Services.AnimationConfig.TargetFps;

        var widthAnim = MakeAnim(notchWidth, duration, isHovered ? _easeExpOut6 : _easeQuadOut, animFps);
        var heightAnim = MakeAnim(notchHeight, duration, isHovered ? _easeExpOut6 : _easeQuadOut, animFps);
        VNotch.Services.RuntimeLog.Log("NOTCH-WIDTH",
            $"AnimateNotchHover -> {notchWidth} (hover={isHovered}, _isExpanded={_isExpanded}, _isAnimating={_isAnimating})");
        NotchBorder.BeginAnimation(WidthProperty, widthAnim);
        NotchBorder.BeginAnimation(HeightProperty, heightAnim);
        // Same as AnimateNotchHover: keep the glass at full rate for the resize.

        BeginGlassHoverMotion(widthAnim);

        if (isHovered)
        {
            ApplyCompactTitleContainerWidth(notchWidth);
        }

        CompactThumbnailBorder.RenderTransformOrigin = new Point(0, 0);

        var thumbScaleAnimX = MakeAnim(thumbScale, duration, easing, animFps);
        var thumbScaleAnimY = MakeAnim(thumbScale, duration, easing, animFps);
        CompactThumbnailScale.BeginAnimation(ScaleTransform.ScaleXProperty, thumbScaleAnimX);
        CompactThumbnailScale.BeginAnimation(ScaleTransform.ScaleYProperty, thumbScaleAnimY);

        if (isHovered)
        {
            CompactHoverInfo.Visibility = Visibility.Visible;

            CompactHoverInfo.UpdateLayout();
            UpdateCompactMarquee();

            QueueCompactMarqueeRefresh();
        }

        var fadeAnim = MakeAnim(
            infoOpacity,
            isHovered ? _dur200 : TimeSpan.FromMilliseconds(240),
            isHovered ? _easeQuadOut : _easePowerOut3);
        if (!isHovered)
        {
            fadeAnim.Completed += (s, e) =>
            {
                if (_isCompactThumbnailHovered || CompactHoverInfo.Opacity >= 0.1) return;

                CompactHoverInfo.Visibility = Visibility.Collapsed;
                CompactHoverInfo.OpacityMask = null;
            };
        }
        CompactHoverInfo.BeginAnimation(OpacityProperty, fadeAnim);

        double radius;
        if (isHovered)
        {
            radius = islandMode ? notchHeight / 2.0 : 24;
        }
        else
        {
            radius = _cornerRadiusCollapsed;
        }
        AnimateCornerRadius(radius, duration.TimeSpan);

        double thumbRadius = isHovered && islandMode ? 8 : 6;
        double startThumbRadius = CompactThumbnailBorder.CornerRadius.TopLeft;
        if (Math.Abs(thumbRadius - startThumbRadius) > 0.1)
        {
            CurrentCompactThumbnailRadius = startThumbRadius;
            var thumbRadiusAnim = MakeAnim(startThumbRadius, thumbRadius, duration, easing);
            this.BeginAnimation(CurrentCompactThumbnailRadiusProperty, thumbRadiusAnim);
        }
    }

    private void UpdateCompactMarquee()
    {
        if (_currentMediaInfo == null) return;

        CompactTitleMarquee.Text = _currentMediaInfo.CurrentTrack;
        CompactTitleMarqueeTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        CompactTitleMarquee.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        double textWidth = CompactTitleMarquee.DesiredSize.Width;

        double containerWidth = GetCompactTitleContainerWidth();
        if (containerWidth <= 0)
        {
            CompactHoverInfo.OpacityMask = null;
            CompactTitleMarqueeTranslate.X = 0;
            QueueCompactMarqueeRefresh();
            return;
        }

        const double marqueeTriggerOverflow = 10.0;

        if (textWidth > containerWidth + marqueeTriggerOverflow && containerWidth > 0)
        {

            CompactHoverInfo.OpacityMask = CompactMarqueeFadeBrush;
            MarqueeController.StartMarqueeAnimation(CompactTitleMarqueeTranslate, textWidth - containerWidth + 12);
        }
        else
        {

            CompactHoverInfo.OpacityMask = null;

            CompactTitleMarqueeTranslate.BeginAnimation(TranslateTransform.XProperty, null);
            CompactTitleMarqueeTranslate.X = Math.Max(0, (containerWidth - textWidth) / 2);
        }
    }

    private void QueueCompactMarqueeRefresh()
    {
        if (_compactMarqueeRefreshQueued) return;
        _compactMarqueeRefreshQueued = true;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            _compactMarqueeRefreshQueued = false;
            if (!_isExpanded && !_isAnimating && _isMusicCompactMode && CompactHoverInfo.Visibility == Visibility.Visible)
            {
                UpdateCompactMarquee();
            }
        }), DispatcherPriority.Loaded);
    }

    private double GetCompactTitleContainerWidth()
    {
        double explicitWidth = CompactTitleScrollContainer.Width;
        if (double.IsFinite(explicitWidth) && explicitWidth > 0)
        {
            return explicitWidth;
        }

        double width = CompactTitleScrollContainer.ActualWidth;
        if (double.IsFinite(width) && width > 0)
        {
            return width;
        }

        try
        {
            CompactHoverInfo.UpdateLayout();
            width = CompactTitleScrollContainer.ActualWidth;
            if (double.IsFinite(width) && width > 0)
            {
                return width;
            }
        }
        catch
        {
            return 0;
        }

        return 0;
    }

    private void ApplyCompactTitleContainerWidth(double notchWidth)
    {
        if (!double.IsFinite(notchWidth) || notchWidth <= 0)
        {
            notchWidth = NotchBorder.ActualWidth > 0 ? NotchBorder.ActualWidth : _collapsedWidth;
        }

        double horizontalInset = MusicCompactContent.Margin.Left + MusicCompactContent.Margin.Right;
        double titleWidth = Math.Max(0, notchWidth - horizontalInset);
        CompactTitleScrollContainer.Width = titleWidth;
    }

    #endregion

    #region Animation Helpers

    private static void FadeSwitch(FrameworkElement from, FrameworkElement to)
    {

        from.BeginAnimation(OpacityProperty, null);
        to.BeginAnimation(OpacityProperty, null);

        var fadeOut = MakeAnim(0, _dur100);
        fadeOut.Completed += (s, e) =>
        {
            if (from.Opacity < 0.05) from.Visibility = Visibility.Collapsed;
        };
        from.BeginAnimation(OpacityProperty, fadeOut);

        to.Visibility = Visibility.Visible;
        var fadeIn = MakeAnim(1, _dur200);
        to.BeginAnimation(OpacityProperty, fadeIn);
    }


    private static void AnimateIconSwitch(Canvas fromIcon, Canvas toIcon, TimeSpan duration, EasingFunctionBase easing) => Controls.ButtonFeedback.AnimateIconSwitch(fromIcon, toIcon, duration, easing);

    private static void PlayButtonPressAnimation(Border button) => Controls.ButtonFeedback.PlayButtonPressAnimation(button);

    private void PlayGentleButtonPressAnimation(Border button)
    {
        var transform = button.RenderTransform as ScaleTransform ?? new ScaleTransform(1, 1);
        button.RenderTransform = transform;
        button.RenderTransformOrigin = new Point(0.5, 0.5);

        transform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        transform.BeginAnimation(ScaleTransform.ScaleYProperty, null);

        var squish = MakeAnim(1d, 0.90d, _dur80, _easeQuadOut, null);
        var bounce = new DoubleAnimation(0.90, 1.0, _dur200)
        {
            EasingFunction = _easeSoftSpring,
            BeginTime = TimeSpan.Zero
        };
        Timeline.SetDesiredFrameRate(bounce, VNotch.Services.AnimationConfig.TargetFps);

        squish.Completed += (s, e) =>
        {
            transform.BeginAnimation(ScaleTransform.ScaleXProperty, bounce);
            transform.BeginAnimation(ScaleTransform.ScaleYProperty, bounce);
        };

        transform.BeginAnimation(ScaleTransform.ScaleXProperty, squish);
        transform.BeginAnimation(ScaleTransform.ScaleYProperty, squish);
    }

    private void PlayNextSkipAnimation()
    {
        PlayNextSkipAnimation(MediaPlayback.NextArrow0, MediaPlayback.NextArrow1);
    }

    private static void PlayNextSkipAnimation(System.Windows.Shapes.Path arrow0, System.Windows.Shapes.Path arrow1) => Controls.ButtonFeedback.PlayNextSkipAnimation(arrow0, arrow1);

    private void PlayPrevSkipAnimation()
    {
        PlayPrevSkipAnimation(MediaPlayback.PrevArrow0, MediaPlayback.PrevArrow2);
    }

    private static void PlayPrevSkipAnimation(System.Windows.Shapes.Path arrow0, System.Windows.Shapes.Path arrow2) => Controls.ButtonFeedback.PlayPrevSkipAnimation(arrow0, arrow2);

    private void PlayAppearAnimation()
    {
        bool showGreeting = _settings.EnableHelloGreeting;

        if (showGreeting)
        {
            _isGreetingActive = true;
            _isAnimating = true;
            NotchLiveContent.Visibility = Visibility.Hidden;
            PrepareGreetingPathsIfNeeded();
        }

        NotchBorder.Opacity = 0;

        var opacityAnim = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(400)),
            EasingFunction = _easeQuadOut
        };

        if (showGreeting)
        {
            opacityAnim.Completed += (s, e) =>
            {
                PlayGreetingAnimation();
            };
        }
        else
        {
            opacityAnim.Completed += (s, e) =>
            {
                StartStartupHold(TimeSpan.FromSeconds(3));
                SchedulePostAppearInitialization();
            };
        }

        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(opacityAnim, VNotch.Services.AnimationConfig.TargetFps);
        NotchBorder.BeginAnimation(OpacityProperty, opacityAnim);
    }

    public static readonly DependencyProperty CurrentCornerRadiusProperty =
        DependencyProperty.Register("CurrentCornerRadius", typeof(double), typeof(MainWindow),
            new PropertyMetadata(0.0, OnCurrentCornerRadiusChanged));

    public static readonly DependencyProperty CurrentThumbnailAnimationRadiusProperty =
        DependencyProperty.Register("CurrentThumbnailAnimationRadius", typeof(double), typeof(MainWindow),
            new PropertyMetadata(6.0, OnCurrentThumbnailAnimationRadiusChanged));

    public static readonly DependencyProperty CurrentCompactThumbnailRadiusProperty =
        DependencyProperty.Register("CurrentCompactThumbnailRadius", typeof(double), typeof(MainWindow),
            new PropertyMetadata(6.0, OnCurrentCompactThumbnailRadiusChanged));

    public double CurrentCornerRadius
    {
        get => (double)GetValue(CurrentCornerRadiusProperty);
        set => SetValue(CurrentCornerRadiusProperty, value);
    }

    public double CurrentThumbnailAnimationRadius
    {
        get => (double)GetValue(CurrentThumbnailAnimationRadiusProperty);
        set => SetValue(CurrentThumbnailAnimationRadiusProperty, value);
    }

    public double CurrentCompactThumbnailRadius
    {
        get => (double)GetValue(CurrentCompactThumbnailRadiusProperty);
        set => SetValue(CurrentCompactThumbnailRadiusProperty, value);
    }

    private static void OnCurrentCornerRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MainWindow window)
        {
            double radius = (double)e.NewValue;
            var cr = window.MakeNotchCornerRadius(radius);
            window.SyncShellCornerRadius(cr);
            window._glassMaterialPresenter.SyncCornerRadius(cr);
            window.UpdateNotchClip();
        }
    }

    private void InitializeShellCornerRadiusSync()
    {
        foreach (var layer in new[] { InnerClipBorder, NotchBackground, MediaBackground, MediaBackground2, NotchBorderShadow, HoverGlow })
            layer.IsVisibleChanged += (_, _) =>
            {
                if (layer.IsVisible) layer.CornerRadius = NotchBorder.CornerRadius;
            };
    }

    private void SyncShellCornerRadius(CornerRadius radius)
    {
        NotchBorder.CornerRadius = radius;
        SyncVisibleRadius(InnerClipBorder, radius);
        SyncVisibleRadius(NotchBackground, radius);
        SyncVisibleRadius(MediaBackground, radius);
        SyncVisibleRadius(MediaBackground2, radius);
        SyncVisibleRadius(NotchBorderShadow, radius);
        SyncVisibleRadius(HoverGlow, radius);
    }

    private static void SyncVisibleRadius(Border layer, CornerRadius radius)
    {
        if (layer.Visibility != Visibility.Collapsed && layer.CornerRadius != radius)
            layer.CornerRadius = radius;
    }

    private CornerRadius MakeNotchCornerRadius(double radius)
    {
        return _settings.EnableDynamicIslandMode
            ? new CornerRadius(radius, radius, radius, radius)
            : new CornerRadius(0, 0, radius, radius);
    }

    private bool _isModeTransitioning;
    private double _mtNotchW, _mtNotchH, _mtNotchBottomR;
    private double _mtIslandW, _mtIslandH, _mtIslandR;

    public static readonly DependencyProperty ModeTransitionTProperty =
        DependencyProperty.Register("ModeTransitionT", typeof(double), typeof(MainWindow),
            new PropertyMetadata(0.0, OnModeTransitionTChanged));

    public double ModeTransitionT
    {
        get => (double)GetValue(ModeTransitionTProperty);
        set => SetValue(ModeTransitionTProperty, value);
    }

    private static void OnModeTransitionTChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not MainWindow w) return;
        double t = (double)e.NewValue;

        double width = w._mtNotchW + (w._mtIslandW - w._mtNotchW) * t;
        double height = w._mtNotchH + (w._mtIslandH - w._mtNotchH) * t;
        double topRadius = w._mtIslandR * t;
        double bottomRadius = w._mtNotchBottomR + (w._mtIslandR - w._mtNotchBottomR) * t;
        double marginTop = DynamicIslandTopMargin * t;
        double earOpacity = 1.0 - t;

        w.ApplyModeTransitionFrame(width, height, topRadius, bottomRadius, marginTop, earOpacity);
    }

    private int _modeTransitionGeneration;

    private void AnimateModeTransition(int fps)
    {
        int gen = ++_modeTransitionGeneration;
        bool toIsland = _settings.EnableDynamicIslandMode;

        _mtNotchW = _settings.Width;
        _mtNotchH = _settings.Height;
        _mtNotchBottomR = _settings.CornerRadius;

        _mtIslandH = _settings.DynamicIslandHeight;
        _mtIslandW = _settings.DynamicIslandWidth;
        _mtIslandR = Math.Max(0, _mtIslandH / 2.0);

        _isModeTransitioning = true;
        UpdateGlassMotionState();

        bool showEars = !toIsland && !IsLiquidGlassEnabled;
        var earVisStart = showEars ? Visibility.Visible : Visibility.Collapsed;
        double earOpacityStart = showEars ? 1.0 : 0.0;
        if (LeftEar != null) { LeftEar.Visibility = earVisStart; LeftEar.Opacity = earOpacityStart; }
        if (RightEar != null) { RightEar.Visibility = earVisStart; RightEar.Opacity = earOpacityStart; }
        if (LeftShadowEar != null) { LeftShadowEar.Visibility = earVisStart; LeftShadowEar.Opacity = earOpacityStart; }
        if (RightShadowEar != null) { RightShadowEar.Visibility = earVisStart; RightShadowEar.Opacity = earOpacityStart; }

        NotchBorder.BeginAnimation(WidthProperty, null);
        NotchBorder.BeginAnimation(HeightProperty, null);
        this.BeginAnimation(CurrentCornerRadiusProperty, null);

        double from = toIsland ? 0.0 : 1.0;
        double to = toIsland ? 1.0 : 0.0;
        ModeTransitionT = to;

        var anim = new DoubleAnimation(from, to, _dur450)
        {
            EasingFunction = _easeExpOut6,
            FillBehavior = FillBehavior.Stop
        };
        Timeline.SetDesiredFrameRate(anim, fps);
        anim.Completed += (s, e) =>
        {
            if (gen != _modeTransitionGeneration) return;
            this.BeginAnimation(ModeTransitionTProperty, null);
            FinalizeModeTransition(toIsland);
        };
        this.BeginAnimation(ModeTransitionTProperty, anim);
    }

    private void ApplyModeTransitionFrame(double width, double height, double topRadius, double bottomRadius, double marginTop, double earOpacity)
    {
        NotchBorder.Width = width;
        NotchBorder.Height = height;

        var cr = new CornerRadius(topRadius, topRadius, bottomRadius, bottomRadius);
        SyncShellCornerRadius(cr);
        _glassMaterialPresenter.SyncCornerRadius(cr);

        if (NotchContainer != null)
        {
            var m = NotchContainer.Margin;
            if (Math.Abs(m.Top - marginTop) > 0.001)
            {
                NotchContainer.Margin = new Thickness(m.Left, marginTop, m.Right, m.Bottom);
            }
        }

        double effectiveEarOpacity = IsLiquidGlassEnabled ? 0.0 : earOpacity;
        if (LeftEar != null) LeftEar.Opacity = effectiveEarOpacity;
        if (RightEar != null) RightEar.Opacity = effectiveEarOpacity;
        if (LeftShadowEar != null) LeftShadowEar.Opacity = effectiveEarOpacity;
        if (RightShadowEar != null) RightShadowEar.Opacity = effectiveEarOpacity;

        UpdateNotchClip();
    }

    private void FinalizeModeTransition(bool toIsland)
    {
        _isModeTransitioning = false;
        UpdateGlassMotionState();

        NotchBorder.Width = _collapsedWidth;
        NotchBorder.Height = _collapsedHeight;

        var cr = MakeNotchCornerRadius(_cornerRadiusCollapsed);
        NotchBorder.CornerRadius = cr;
        InnerClipBorder.CornerRadius = cr;
        NotchBackground.CornerRadius = cr;
        MediaBackground.CornerRadius = cr;
        MediaBackground2.CornerRadius = cr;
        NotchBorderShadow.CornerRadius = cr;
        HoverGlow.CornerRadius = cr;
        _glassMaterialPresenter.SyncCornerRadius(cr);
        CurrentCornerRadius = _cornerRadiusCollapsed;

        if (NotchContainer != null)
        {
            var m = NotchContainer.Margin;
            NotchContainer.Margin = new Thickness(m.Left, toIsland ? DynamicIslandTopMargin : 0, m.Right, m.Bottom);
        }

        UpdateEarVisibility();

        UpdateNotchClip();
        UpdateMediaBackgroundFootprint();
    }

    private double GetCollapsedWidth()
    {
        double width = _settings.EnableDynamicIslandMode
            ? _settings.DynamicIslandWidth
            : _settings.Width;

        return width;
    }

    private double GetCollapsedHeight()
    {
        return _settings.EnableDynamicIslandMode
            ? _settings.DynamicIslandHeight
            : _settings.Height;
    }

    private double GetCollapsedCornerRadius()
    {
        return _settings.EnableDynamicIslandMode
            ? Math.Max(0, _collapsedHeight / 2.0)
            : _settings.CornerRadius;
    }

    private static void OnCurrentThumbnailAnimationRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not MainWindow window)
        {
            return;
        }

        double radius = (double)e.NewValue;
        var cornerRadius = new CornerRadius(radius);

        if (window.AnimationThumbnailBorder != null)
        {
            window.AnimationThumbnailBorder.CornerRadius = cornerRadius;
        }

        if (window.AnimationThumbnailRim != null)
        {
            window.AnimationThumbnailRim.CornerRadius = cornerRadius;
        }

        if (window.AnimationThumbnailClip != null)
        {
            window.AnimationThumbnailClip.RadiusX = radius;
            window.AnimationThumbnailClip.RadiusY = radius;
        }
    }

    private static void OnCurrentCompactThumbnailRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not MainWindow window) return;

        double radius = (double)e.NewValue;
        var cornerRadius = new CornerRadius(radius);
        window.CompactThumbnailBorder.CornerRadius = cornerRadius;
        if (window.CompactThumbnailRim != null)
        {
            window.CompactThumbnailRim.CornerRadius = cornerRadius;
        }
        if (window.CompactThumbnailClip != null)
        {
            window.CompactThumbnailClip.RadiusX = radius;
            window.CompactThumbnailClip.RadiusY = radius;
        }
    }

    private void AnimateCornerRadius(double targetRadius, TimeSpan duration)
    {
        double startRadius = NotchBorder.CornerRadius.BottomLeft;
        if (Math.Abs(targetRadius - startRadius) < 0.5)
        {
            CurrentCornerRadius = targetRadius;
            this.BeginAnimation(CurrentCornerRadiusProperty, null);
            return;
        }

        CurrentCornerRadius = startRadius;
        this.BeginAnimation(CurrentCornerRadiusProperty, null);

        var anim = MakeAnim(startRadius, targetRadius, new Duration(duration), _easeExpOut6, null);
        Timeline.SetDesiredFrameRate(anim, VNotch.Services.AnimationConfig.TargetFps);
        this.BeginAnimation(CurrentCornerRadiusProperty, anim);
    }

    private void AnimateThumbnailAnimationRadius(double fromRadius, double toRadius, Duration duration, IEasingFunction easing, TimeSpan? beginTime = null)
    {
        // Keep the endpoint underneath the explicit clock so the overlay's held
        // final frame and its live-thumbnail handoff use the same radius.
        CurrentThumbnailAnimationRadius = toRadius;
        this.BeginAnimation(CurrentThumbnailAnimationRadiusProperty, null);

        var anim = MakeAnim(fromRadius, toRadius, duration, easing, beginTime);
        this.BeginAnimation(CurrentThumbnailAnimationRadiusProperty, anim);
    }

    public void PlayTrackChangeBounce()
    {
        if (_isExpanded || _isAnimating) return;

        var durPeak = TimeSpan.FromMilliseconds(150);
        var durEnd = TimeSpan.FromMilliseconds(800);

        // While hovered the pill rests at the hover scale, not 1.0 — ending the
        // bounce there would leave it visibly shrunken until the next hover cycle.
        double restX = !_isGreetingActive && !_isVolumeIndicatorActive && NotchWrapper.IsMouseOver ? NotchHoverScaleX : 1.0;

        var bounceX = new DoubleAnimationUsingKeyFrames();
        bounceX.KeyFrames.Add(new EasingDoubleKeyFrame(restX + 0.12, KeyTime.FromTimeSpan(durPeak), _easeQuadOut));
        bounceX.KeyFrames.Add(new EasingDoubleKeyFrame(restX, KeyTime.FromTimeSpan(durEnd), _easeSoftSpring));
        Timeline.SetDesiredFrameRate(bounceX, VNotch.Services.AnimationConfig.TargetFps);

        var bounceY = new DoubleAnimationUsingKeyFrames();
        bounceY.KeyFrames.Add(new EasingDoubleKeyFrame(0.92, KeyTime.FromTimeSpan(durPeak), _easeQuadOut));
        bounceY.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(durEnd), _easeSoftSpring));
        Timeline.SetDesiredFrameRate(bounceY, VNotch.Services.AnimationConfig.TargetFps);

        NotchScale.BeginAnimation(ScaleTransform.ScaleXProperty, bounceX);
        NotchScale.BeginAnimation(ScaleTransform.ScaleYProperty, bounceY);
        NotchShadowScale.BeginAnimation(ScaleTransform.ScaleXProperty, bounceX);
        NotchShadowScale.BeginAnimation(ScaleTransform.ScaleYProperty, bounceY);
    }


    #endregion

    #region Status Bar Animation

    private void AnimateStatusBarReveal(bool show)
    {
        var dur = TimeSpan.FromMilliseconds(show ? 340 : 220);
        var easing = _easeExpOut5;
        var settingsEase = _easeBackOut022;
        int animFps = VNotch.Services.AnimationConfig.TargetFps;

        BatterySection.BeginAnimation(OpacityProperty, null);
        BatteryTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        NavIconsPanel.BeginAnimation(OpacityProperty, null);
        NavIconsBackground.BeginAnimation(OpacityProperty, null);
        NavIconsTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        SettingsButton.BeginAnimation(OpacityProperty, null);
        SettingsTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        SettingsScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        SettingsScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        SettingsRotate.BeginAnimation(RotateTransform.AngleProperty, null);

        bool showBatterySection = show && _settings.ShowBatteryIndicator;
        VNotch.Models.NotchView fallbackView = VNotch.Models.NotchView.Media;
        if (_isCameraView) fallbackView = VNotch.Models.NotchView.Camera;
        else if (_isAudioView) fallbackView = VNotch.Models.NotchView.AudioMixer;
        else if (_isTimerView) fallbackView = VNotch.Models.NotchView.Timer;
        else if (_isSecondaryView) fallbackView = VNotch.Models.NotchView.Secondary;
        var activeTargetView = _navigationVisualTarget ?? fallbackView;
        bool showNavIconsBg = activeTargetView is VNotch.Models.NotchView.Secondary
            or VNotch.Models.NotchView.Timer
            or VNotch.Models.NotchView.AudioMixer;
        double iconTargetY = _settings.EnableDynamicIslandMode ? 5 : 0;

        if (show)
        {
            BatterySection.Visibility = showBatterySection ? Visibility.Visible : Visibility.Collapsed;
            BatterySection.IsHitTestVisible = showBatterySection;
            NavIconsPanel.Visibility = Visibility.Visible;
            UpdateNavIconsActiveState();
            NavIconsBackground.Visibility = showNavIconsBg ? Visibility.Visible : Visibility.Collapsed;
            if (!showNavIconsBg)
                NavIconsBackground.Opacity = 0;
            SettingsButton.Visibility = Visibility.Visible;
            SettingsScale.ScaleX = 0.86;
            SettingsScale.ScaleY = 0.86;
            SettingsRotate.Angle = 20;
        }

        var batteryOpacityAnim = new DoubleAnimation
        {
            To = showBatterySection ? 1.0 : 0.0,
            Duration = dur,
            EasingFunction = easing
        };
        Timeline.SetDesiredFrameRate(batteryOpacityAnim, animFps);

        var batteryTranslateAnim = new DoubleAnimation
        {
            To = showBatterySection ? iconTargetY : -6,
            Duration = dur,
            EasingFunction = easing
        };
        Timeline.SetDesiredFrameRate(batteryTranslateAnim, animFps);

        var settingsDelay = show ? TimeSpan.FromMilliseconds(56) : TimeSpan.Zero;
        var settingsOpacityAnim = new DoubleAnimation
        {
            To = show ? 1.0 : 0.0,
            Duration = dur,
            EasingFunction = easing,
            BeginTime = settingsDelay
        };
        Timeline.SetDesiredFrameRate(settingsOpacityAnim, animFps);

        var settingsTranslateAnim = new DoubleAnimation
        {
            To = show ? iconTargetY : -6,
            Duration = dur,
            EasingFunction = settingsEase,
            BeginTime = settingsDelay
        };
        Timeline.SetDesiredFrameRate(settingsTranslateAnim, animFps);

        var settingsScaleAnim = new DoubleAnimation
        {
            To = show ? 1.0 : 0.86,
            Duration = new Duration(TimeSpan.FromMilliseconds(show ? 460 : 200)),
            EasingFunction = settingsEase,
            BeginTime = settingsDelay
        };
        Timeline.SetDesiredFrameRate(settingsScaleAnim, animFps);

        var settingsRotateAnim = new DoubleAnimation
        {
            To = show ? 45 : 20,
            Duration = new Duration(TimeSpan.FromMilliseconds(show ? 520 : 200)),
            EasingFunction = easing,
            BeginTime = settingsDelay
        };
        Timeline.SetDesiredFrameRate(settingsRotateAnim, animFps);

        if (_isUpdateAvailable && UpdateNotificationButton != null)
        {
            var updateOpacityAnim = new DoubleAnimation
            {
                To = show ? 1.0 : 0.0,
                Duration = dur,
                EasingFunction = easing,
                BeginTime = show ? TimeSpan.FromMilliseconds(30) : TimeSpan.Zero
            };
            Timeline.SetDesiredFrameRate(updateOpacityAnim, animFps);

            var updateTranslateAnim = new DoubleAnimation
            {
                To = show ? iconTargetY : -4,
                Duration = dur,
                EasingFunction = easing,
                BeginTime = show ? TimeSpan.FromMilliseconds(30) : TimeSpan.Zero
            };
            Timeline.SetDesiredFrameRate(updateTranslateAnim, animFps);

            if (show)
            {
                if (UpdateIconBrush != null)
                {
                    UpdateIconBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
                    UpdateIconBrush.Color = Color.FromRgb(48, 209, 88);
                }
                UpdateNotificationButton.Visibility = Visibility.Visible;
                UpdateNotificationButton.IsHitTestVisible = true;
                UpdateNotificationButton.Cursor = System.Windows.Input.Cursors.Hand;
                StartUpdatePulseAnimation();
            }
            else
            {
                UpdateNotificationButton.IsHitTestVisible = false;
                StopUpdatePulseAnimation();
            }

            UpdateNotificationButton.BeginAnimation(OpacityProperty, updateOpacityAnim);
            UpdateNotificationTranslate.BeginAnimation(TranslateTransform.YProperty, updateTranslateAnim);
        }
        else if (UpdateNotificationButton != null)
        {
            UpdateNotificationButton.Visibility = Visibility.Collapsed;
            UpdateNotificationButton.IsHitTestVisible = false;
            StopUpdatePulseAnimation();
        }

        BatterySection.BeginAnimation(OpacityProperty, batteryOpacityAnim);
        BatteryTranslate.BeginAnimation(TranslateTransform.YProperty, batteryTranslateAnim);

        var navOpacityAnim = new DoubleAnimation
        {
            To = show ? 1.0 : 0.0,
            Duration = dur,
            EasingFunction = easing
        };
        Timeline.SetDesiredFrameRate(navOpacityAnim, animFps);

        var navTranslateAnim = new DoubleAnimation
        {
            To = show ? iconTargetY : -6,
            Duration = dur,
            EasingFunction = easing
        };
        Timeline.SetDesiredFrameRate(navTranslateAnim, animFps);

        NavIconsPanel.BeginAnimation(OpacityProperty, navOpacityAnim);
        NavIconsTranslate.BeginAnimation(TranslateTransform.YProperty, navTranslateAnim);

        if (showNavIconsBg)
        {
            var navBgOpacityAnim = new DoubleAnimation
            {
                To = show ? 1.0 : 0.0,
                Duration = new Duration(TimeSpan.FromMilliseconds(show ? 340 : 120)),
                EasingFunction = easing
            };
            Timeline.SetDesiredFrameRate(navBgOpacityAnim, animFps);
            NavIconsBackground.BeginAnimation(OpacityProperty, navBgOpacityAnim);
        }

        SettingsButton.BeginAnimation(OpacityProperty, settingsOpacityAnim, HandoffBehavior.SnapshotAndReplace);
        SettingsTranslate.BeginAnimation(TranslateTransform.YProperty, settingsTranslateAnim, HandoffBehavior.SnapshotAndReplace);
        SettingsScale.BeginAnimation(ScaleTransform.ScaleXProperty, settingsScaleAnim, HandoffBehavior.SnapshotAndReplace);
        SettingsScale.BeginAnimation(ScaleTransform.ScaleYProperty, settingsScaleAnim, HandoffBehavior.SnapshotAndReplace);
        SettingsRotate.BeginAnimation(RotateTransform.AngleProperty, settingsRotateAnim, HandoffBehavior.SnapshotAndReplace);
    }

    #endregion

    #region Settings Absorb Animation
    public void PlaySettingsEjectAnimation()
    {
        if (_isExpanded || _isAnimating) return;

        int fps = VNotch.Services.AnimationConfig.TargetFps;

        NotchScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        NotchScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        NotchShadowScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        NotchShadowScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        NotchBorder.BeginAnimation(WidthProperty, null);
        NotchBorder.BeginAnimation(HeightProperty, null);

        var ejectDur = TimeSpan.FromMilliseconds(150);
        var springDur = TimeSpan.FromMilliseconds(600);

        double currentWidth = _collapsedWidth;
        double peakWidth = currentWidth + 40;

        var widthAnim = new DoubleAnimationUsingKeyFrames();
        widthAnim.KeyFrames.Add(new EasingDoubleKeyFrame(peakWidth,
            KeyTime.FromTimeSpan(ejectDur),
            _easeQuadOut));
        widthAnim.KeyFrames.Add(new EasingDoubleKeyFrame(currentWidth,
            KeyTime.FromTimeSpan(ejectDur + springDur),
            _easeSoftSpring));
        Timeline.SetDesiredFrameRate(widthAnim, fps);

        double currentHeight = _collapsedHeight;
        double peakHeight = currentHeight + 8;

        var heightAnim = new DoubleAnimationUsingKeyFrames();
        heightAnim.KeyFrames.Add(new EasingDoubleKeyFrame(peakHeight,
            KeyTime.FromTimeSpan(ejectDur),
            _easeQuadOut));
        heightAnim.KeyFrames.Add(new EasingDoubleKeyFrame(currentHeight,
            KeyTime.FromTimeSpan(ejectDur + springDur),
            _easeSoftSpring));
        Timeline.SetDesiredFrameRate(heightAnim, fps);

        var scaleY = new DoubleAnimationUsingKeyFrames();
        scaleY.KeyFrames.Add(new EasingDoubleKeyFrame(1.15,
            KeyTime.FromTimeSpan(ejectDur),
            _easeQuadOut));
        scaleY.KeyFrames.Add(new EasingDoubleKeyFrame(1.0,
            KeyTime.FromTimeSpan(ejectDur + springDur),
            _easeSoftSpring));
        Timeline.SetDesiredFrameRate(scaleY, fps);

        NotchBorder.BeginAnimation(WidthProperty, widthAnim);
        NotchBorder.BeginAnimation(HeightProperty, heightAnim);
        NotchScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleY);
        NotchShadowScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleY);
    }
    public void PlaySettingsAbsorbAnimation()
    {
        if (_isExpanded || _isAnimating) return;

        int fps = VNotch.Services.AnimationConfig.TargetFps;

        NotchScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        NotchScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        NotchShadowScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        NotchShadowScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        NotchBorder.BeginAnimation(WidthProperty, null);
        NotchBorder.BeginAnimation(HeightProperty, null);
        var absorbDelay = TimeSpan.FromMilliseconds(480);
        var openDur = TimeSpan.FromMilliseconds(200);
        var springDur = TimeSpan.FromMilliseconds(700);

        double currentWidth = _collapsedWidth;
        double peakWidth = currentWidth + 60;

        var widthAnim = new DoubleAnimationUsingKeyFrames { BeginTime = absorbDelay };
        widthAnim.KeyFrames.Add(new EasingDoubleKeyFrame(peakWidth,
            KeyTime.FromTimeSpan(openDur),
            _easeQuadOut));
        widthAnim.KeyFrames.Add(new EasingDoubleKeyFrame(currentWidth,
            KeyTime.FromTimeSpan(openDur + springDur),
            _easeSoftSpring));
        Timeline.SetDesiredFrameRate(widthAnim, fps);

        double currentHeight = _collapsedHeight;
        double peakHeight = currentHeight + 10;

        var heightAnim = new DoubleAnimationUsingKeyFrames { BeginTime = absorbDelay };
        heightAnim.KeyFrames.Add(new EasingDoubleKeyFrame(peakHeight,
            KeyTime.FromTimeSpan(openDur),
            _easeQuadOut));
        heightAnim.KeyFrames.Add(new EasingDoubleKeyFrame(currentHeight,
            KeyTime.FromTimeSpan(openDur + springDur),
            _easeSoftSpring));
        Timeline.SetDesiredFrameRate(heightAnim, fps);

        double currentRadius = _cornerRadiusCollapsed;
        double peakRadius = currentRadius + 6;

        var radiusAnim = new DoubleAnimationUsingKeyFrames { BeginTime = absorbDelay };
        radiusAnim.KeyFrames.Add(new EasingDoubleKeyFrame(peakRadius,
            KeyTime.FromTimeSpan(openDur),
            _easeQuadOut));
        radiusAnim.KeyFrames.Add(new EasingDoubleKeyFrame(currentRadius,
            KeyTime.FromTimeSpan(openDur + springDur),
            _easeSoftSpring));
        Timeline.SetDesiredFrameRate(radiusAnim, fps);

        var scaleY = new DoubleAnimationUsingKeyFrames { BeginTime = absorbDelay };
        scaleY.KeyFrames.Add(new EasingDoubleKeyFrame(1.12,
            KeyTime.FromTimeSpan(openDur),
            _easeQuadOut));
        scaleY.KeyFrames.Add(new EasingDoubleKeyFrame(1.0,
            KeyTime.FromTimeSpan(openDur + springDur),
            _easeSoftSpring));
        Timeline.SetDesiredFrameRate(scaleY, fps);

        NotchBorder.BeginAnimation(WidthProperty, widthAnim);
        NotchBorder.BeginAnimation(HeightProperty, heightAnim);
        NotchScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleY);
        NotchShadowScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleY);
        this.BeginAnimation(CurrentCornerRadiusProperty, radiusAnim);
    }

    #endregion

    #region Apple-Style Collapse Fade Out

    private void AnimateExpandedContentFadeOut()
    {
        if (!_isExpanded || ExpandedContent.Visibility != Visibility.Visible) return;

        int fps = VNotch.Services.AnimationConfig.TargetFps;

        var baseDuration = new Duration(TimeSpan.FromMilliseconds(180));
        var easing = _easeQuadIn;

        var wave1Delay = TimeSpan.Zero;

        if (MediaPlayback.PlayPauseButton != null && MediaPlayback.PlayPauseButton.Visibility == Visibility.Visible)
        {
            double currentOpacity = MediaPlayback.PlayPauseButton.Opacity;
            if (currentOpacity > 0.01)
            {
                MediaPlayback.PlayPauseButton.BeginAnimation(OpacityProperty, null);
                var fadeAnim = MakeAnim(currentOpacity, 0, baseDuration, easing, wave1Delay);
                Timeline.SetDesiredFrameRate(fadeAnim, fps);
                MediaPlayback.PlayPauseButton.BeginAnimation(OpacityProperty, fadeAnim);
            }
        }

        if (MediaPlayback.PrevButton != null && MediaPlayback.PrevButton.Visibility == Visibility.Visible)
        {
            double currentOpacity = MediaPlayback.PrevButton.Opacity;
            if (currentOpacity > 0.01)
            {
                MediaPlayback.PrevButton.BeginAnimation(OpacityProperty, null);
                var fadeAnim = MakeAnim(currentOpacity, 0, baseDuration, easing, wave1Delay);
                Timeline.SetDesiredFrameRate(fadeAnim, fps);
                MediaPlayback.PrevButton.BeginAnimation(OpacityProperty, fadeAnim);
            }
        }

        if (MediaPlayback.NextButton != null && MediaPlayback.NextButton.Visibility == Visibility.Visible)
        {
            double currentOpacity = MediaPlayback.NextButton.Opacity;
            if (currentOpacity > 0.01)
            {
                MediaPlayback.NextButton.BeginAnimation(OpacityProperty, null);
                var fadeAnim = MakeAnim(currentOpacity, 0, baseDuration, easing, wave1Delay);
                Timeline.SetDesiredFrameRate(fadeAnim, fps);
                MediaPlayback.NextButton.BeginAnimation(OpacityProperty, fadeAnim);
            }
        }

        var wave2Delay = TimeSpan.FromMilliseconds(20);

        if (MediaPlayback.ProgressBarContainer != null && MediaPlayback.ProgressBarContainer.Visibility == Visibility.Visible)
        {
            double currentOpacity = MediaPlayback.ProgressBarContainer.Opacity;
            if (currentOpacity > 0.01)
            {
                MediaPlayback.ProgressBarContainer.BeginAnimation(OpacityProperty, null);
                var fadeAnim = MakeAnim(currentOpacity, 0, baseDuration, easing, wave2Delay);
                Timeline.SetDesiredFrameRate(fadeAnim, fps);
                MediaPlayback.ProgressBarContainer.BeginAnimation(OpacityProperty, fadeAnim);
            }
        }

        if (MediaPlayback.RemainingTimeText != null && MediaPlayback.RemainingTimeText.Visibility == Visibility.Visible)
        {
            double currentOpacity = MediaPlayback.RemainingTimeText.Opacity;
            if (currentOpacity > 0.01)
            {
                MediaPlayback.RemainingTimeText.BeginAnimation(OpacityProperty, null);
                var fadeAnim = MakeAnim(currentOpacity, 0, baseDuration, easing, wave2Delay);
                Timeline.SetDesiredFrameRate(fadeAnim, fps);
                MediaPlayback.RemainingTimeText.BeginAnimation(OpacityProperty, fadeAnim);
            }
        }

        var wave3Delay = TimeSpan.FromMilliseconds(40);

        if (CalendarWidget != null && CalendarWidget.Visibility == Visibility.Visible)
        {
            double currentOpacity = CalendarWidget.Opacity;
            if (currentOpacity > 0.01)
            {
                CalendarWidget.BeginAnimation(OpacityProperty, null);
                var fadeAnim = MakeAnim(currentOpacity, 0, baseDuration, easing, wave3Delay);
                Timeline.SetDesiredFrameRate(fadeAnim, fps);
                CalendarWidget.BeginAnimation(OpacityProperty, fadeAnim);
            }
        }

        if (ShouldShowMediaBlurBackground && LyricsBlurBackground != null && LyricsBlurBackground.Visibility == Visibility.Visible)
        {
            double currentOpacity = LyricsBlurBackground.Opacity;
            if (currentOpacity > 0.01)
            {
                var wave4Delay = TimeSpan.FromMilliseconds(30);
                LyricsBlurBackground.BeginAnimation(OpacityProperty, null);
                var blurFadeAnim = MakeAnim(currentOpacity, 0, new Duration(TimeSpan.FromMilliseconds(150)), easing, wave4Delay);
                Timeline.SetDesiredFrameRate(blurFadeAnim, fps);
                LyricsBlurBackground.BeginAnimation(OpacityProperty, blurFadeAnim);
            }
        }

        if (MusicViz != null && MusicViz.Visibility == Visibility.Visible)
        {
            double currentOpacity = MusicViz.Opacity;
            if (currentOpacity > 0.01)
            {
                MusicViz.BeginAnimation(OpacityProperty, null);
                var fadeAnim = MakeAnim(currentOpacity, 0, new Duration(TimeSpan.FromMilliseconds(140)), easing, TimeSpan.FromMilliseconds(15));
                Timeline.SetDesiredFrameRate(fadeAnim, fps);
                MusicViz.BeginAnimation(OpacityProperty, fadeAnim);
            }
        }
    }

    private void RestoreExpandedContentOpacity()
    {
        if (MediaPlayback.PlayPauseButton != null)
        {
            MediaPlayback.PlayPauseButton.BeginAnimation(OpacityProperty, null);
            if (MediaPlayback.PlayPauseButton.Visibility == Visibility.Visible)
                MediaPlayback.PlayPauseButton.Opacity = 1.0;
        }

        if (MediaPlayback.PrevButton != null)
        {
            MediaPlayback.PrevButton.BeginAnimation(OpacityProperty, null);
            if (MediaPlayback.PrevButton.Visibility == Visibility.Visible)
                MediaPlayback.PrevButton.Opacity = 1.0;
        }

        if (MediaPlayback.NextButton != null)
        {
            MediaPlayback.NextButton.BeginAnimation(OpacityProperty, null);
            if (MediaPlayback.NextButton.Visibility == Visibility.Visible)
                MediaPlayback.NextButton.Opacity = 1.0;
        }

        if (MediaPlayback.ProgressBarContainer != null)
        {
            MediaPlayback.ProgressBarContainer.BeginAnimation(OpacityProperty, null);
            if (MediaPlayback.ProgressBarContainer.Visibility == Visibility.Visible)
                MediaPlayback.ProgressBarContainer.Opacity = 1.0;
        }

        if (MediaPlayback.RemainingTimeText != null)
        {
            MediaPlayback.RemainingTimeText.BeginAnimation(OpacityProperty, null);
            if (MediaPlayback.RemainingTimeText.Visibility == Visibility.Visible)
                MediaPlayback.RemainingTimeText.Opacity = 1.0;
        }

        if (CalendarWidget != null)
        {
            if (_isLyricsActive)
            {
                // Lyrics are active — ensure calendar is suppressed and lyrics widget is visible
                CalendarWidget.BeginAnimation(OpacityProperty, null);
                CalendarWidget.Visibility = Visibility.Collapsed;
                CalendarWidget.Opacity = 0;
                if (LyricsWidget != null)
                {
                    LyricsWidget.BeginAnimation(OpacityProperty, null);
                    LyricsWidget.Visibility = Visibility.Visible;
                    LyricsWidget.Opacity = 1.0;

                    if (_currentLyricIndex < 0 && !_isLyricsSearchVisible && LyricsPlaceholderPanel != null && LyricsPlaceholderPanel.Visibility != Visibility.Visible)
                    {
                        string placeholderTitle = _currentMediaInfo?.CurrentTrack ?? "";
                        string placeholderArtist = _currentMediaInfo?.CurrentArtist ?? "";
                        ShowLyricsPlaceholder(placeholderTitle, placeholderArtist, _lyricsProvider);
                    }
                }
            }
            else
            {
                CalendarWidget.BeginAnimation(OpacityProperty, null);
                if (IsNoWidgetMode) CalendarWidget.Visibility = Visibility.Collapsed;
                if (CalendarWidget.Visibility == Visibility.Visible)
                    CalendarWidget.Opacity = 1.0;
            }
        }

        if (MusicViz != null)
        {
            MusicViz.BeginAnimation(OpacityProperty, null);
            if (MusicViz.Visibility == Visibility.Visible)
                MusicViz.Opacity = 1.0;
        }

        if (GreetingSection != null)
        {
            GreetingSection.BeginAnimation(OpacityProperty, null);
            if (_isLyricsActive)
            {
                GreetingSection.Visibility = Visibility.Collapsed;
                GreetingSection.Opacity = 0;
            }
            else
            {
                bool showGreeting = ShouldShowGreetingSection;
                GreetingSection.Visibility = showGreeting ? Visibility.Visible : Visibility.Collapsed;
                if (showGreeting)
                    GreetingSection.Opacity = 1.0;
            }
        }
    }


    private void RestoreTimerContentOpacity()
    {
        if (CountdownWidget.CountdownDisplay != null)
        {
            CountdownWidget.CountdownDisplay.BeginAnimation(OpacityProperty, null);
            CountdownWidget.CountdownDisplay.Opacity = 1.0;
        }

        if (CountdownWidget.CountdownProgressFill != null)
        {
            CountdownWidget.CountdownProgressFill.BeginAnimation(OpacityProperty, null);
            CountdownWidget.CountdownProgressFill.Opacity = 1.0;
        }

        if (CountdownWidget.CountdownDisplayPanel != null)
        {
            CountdownWidget.CountdownDisplayPanel.BeginAnimation(OpacityProperty, null);
            CountdownWidget.CountdownDisplayPanel.Opacity = 1.0;
        }

        if (CountdownWidget.CountdownStartBtn != null)
        {
            CountdownWidget.CountdownStartBtn.BeginAnimation(OpacityProperty, null);
            CountdownWidget.CountdownStartBtn.Opacity = 1.0;
        }

        if (CountdownWidget.CountdownResetBtn != null)
        {
            CountdownWidget.CountdownResetBtn.BeginAnimation(OpacityProperty, null);
            CountdownWidget.CountdownResetBtn.Opacity = 1.0;
        }

        if (CountdownWidget.CountdownStepperCapsule != null)
        {
            CountdownWidget.CountdownStepperCapsule.BeginAnimation(OpacityProperty, null);
            // The stepper stays dimmed while a countdown is running.
            CountdownWidget.CountdownStepperCapsule.Opacity = _isCountdownRunning ? 0.4 : 1.0;
        }
    }

    #endregion
}

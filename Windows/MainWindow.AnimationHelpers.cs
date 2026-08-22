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
        if (_isExpanded || _isAnimating || _isGreetingActive) return;

        if (!isHovered && _isGestureActive) return;

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
        if (_isExpanded || _isAnimating || _isGreetingActive) return;

        if (!isHovered && _isGestureActive) return;

        ResetAnimationThumbnailOverlay();

        if (!_isThumbnailSwitchActive)
        {
            ResetCompactThumbnailNextLayer();
        }

        bool islandMode = _settings.EnableDynamicIslandMode;
        double thumbScale = isHovered
            ? (islandMode ? 1.28 : 1.5)
            : 1.0;
        double notchWidth = isHovered
            ? _collapsedWidth + (islandMode ? 24 : 32)
            : _collapsedWidth;
        double notchHeight = isHovered
            ? _collapsedHeight + (islandMode ? 22 : 36)
            : _collapsedHeight;
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

        double radius = isHovered
            ? (_settings.EnableDynamicIslandMode ? notchHeight / 2.0 : 24)
            : _cornerRadiusCollapsed;
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

    private void FadeSwitch(FrameworkElement from, FrameworkElement to)
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

    private void AnimateButtonScale(ScaleTransform scaleTransform, double targetScale)
    {
        var animX = new DoubleAnimation(scaleTransform.ScaleX, targetScale, _dur150) { EasingFunction = _easeQuadOut };
        var animY = new DoubleAnimation(scaleTransform.ScaleY, targetScale, _dur150) { EasingFunction = _easeQuadOut };

        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(animX, VNotch.Services.AnimationConfig.TargetFps);
        scaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, animX);
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(animY, VNotch.Services.AnimationConfig.TargetFps);
        scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, animY);
    }

    private void AnimateIconSwitch(Canvas fromIcon, Canvas toIcon, TimeSpan duration, EasingFunctionBase easing)
    {

        var fromTransform = fromIcon.RenderTransform as ScaleTransform ?? new ScaleTransform(1, 1);
        fromIcon.RenderTransform = fromTransform;
        fromTransform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        fromTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        fromIcon.BeginAnimation(OpacityProperty, null);

        var toTransform = toIcon.RenderTransform as ScaleTransform ?? new ScaleTransform(1, 1);
        toIcon.RenderTransform = toTransform;
        toTransform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        toTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        toIcon.BeginAnimation(OpacityProperty, null);

        fromIcon.Visibility = Visibility.Visible;
        fromTransform.ScaleX = 1;
        fromTransform.ScaleY = 1;
        fromIcon.Opacity = 1;

        toIcon.Visibility = Visibility.Visible;
        toTransform.ScaleX = 0.3;
        toTransform.ScaleY = 0.3;
        toIcon.Opacity = 0;

        var dur = new Duration(duration);
        var scaleDown = new DoubleAnimation(1, 0.3, dur) { EasingFunction = easing };
        var fadeOut = new DoubleAnimation(1, 0, dur) { EasingFunction = easing };
        var scaleUp = new DoubleAnimation(0.3, 1, dur) { EasingFunction = easing };
        var fadeIn = new DoubleAnimation(0, 1, dur) { EasingFunction = easing };

        var capturedFromIcon = fromIcon;
        var capturedFromTransform = fromTransform;

        fadeOut.Completed += (s, e) =>
        {

            capturedFromTransform.ScaleX = 1;
            capturedFromTransform.ScaleY = 1;
            capturedFromIcon.Opacity = 1;

            capturedFromIcon.Visibility = Visibility.Collapsed;
            capturedFromTransform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            capturedFromTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            capturedFromIcon.BeginAnimation(OpacityProperty, null);
        };

        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(scaleDown, VNotch.Services.AnimationConfig.TargetFps);
        fromTransform.BeginAnimation(ScaleTransform.ScaleXProperty, scaleDown);
        fromTransform.BeginAnimation(ScaleTransform.ScaleYProperty, scaleDown);
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(fadeOut, VNotch.Services.AnimationConfig.TargetFps);
        fromIcon.BeginAnimation(OpacityProperty, fadeOut);

        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(scaleUp, VNotch.Services.AnimationConfig.TargetFps);
        toTransform.BeginAnimation(ScaleTransform.ScaleXProperty, scaleUp);
        toTransform.BeginAnimation(ScaleTransform.ScaleYProperty, scaleUp);
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(fadeIn, VNotch.Services.AnimationConfig.TargetFps);
        toIcon.BeginAnimation(OpacityProperty, fadeIn);
    }

    private void PlayButtonPressAnimation(Border button)
    {
        var transform = button.RenderTransform as ScaleTransform ?? new ScaleTransform(1, 1);
        button.RenderTransform = transform;
        button.RenderTransformOrigin = new Point(0.5, 0.5);

        transform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        transform.BeginAnimation(ScaleTransform.ScaleYProperty, null);

        var squish = MakeAnim(1d, 0.82d, _dur80, _easeQuadIn, null);

        var bounce = new DoubleAnimation(0.82, 1.0, _dur250)
        {
            EasingFunction = _easeHapticBounce,
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
        PlayNextSkipAnimation(NextArrow0, NextArrow1, NextArrow2);
    }

    private void PlayNextSkipAnimation(System.Windows.Shapes.Path arrow0, System.Windows.Shapes.Path arrow1, System.Windows.Shapes.Path arrow2)
    {
        const double slideDistance = 220;

        var arrow1Transform = arrow1.RenderTransform as TranslateTransform ?? new TranslateTransform();
        arrow1.RenderTransform = arrow1Transform;

        var slideOut1 = new DoubleAnimation(0, slideDistance, _dur250) { EasingFunction = _easeQuadOut };
        var fadeOut1 = new DoubleAnimation(1, 0, _dur250) { EasingFunction = _easeQuadOut };

        var arrow0Transform = arrow0.RenderTransform as TranslateTransform ?? new TranslateTransform();
        arrow0.RenderTransform = arrow0Transform;

        var slideIn0 = new DoubleAnimation(-slideDistance, 0, _dur250) { EasingFunction = _easeQuadOut };
        var fadeIn0 = new DoubleAnimation(0, 1, _dur250) { EasingFunction = _easeQuadOut };

        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(slideOut1, VNotch.Services.AnimationConfig.TargetFps);
        arrow1Transform.BeginAnimation(TranslateTransform.XProperty, slideOut1);
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(fadeOut1, VNotch.Services.AnimationConfig.TargetFps);
        arrow1.BeginAnimation(OpacityProperty, fadeOut1);
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(slideIn0, VNotch.Services.AnimationConfig.TargetFps);
        arrow0Transform.BeginAnimation(TranslateTransform.XProperty, slideIn0);
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(fadeIn0, VNotch.Services.AnimationConfig.TargetFps);
        arrow0.BeginAnimation(OpacityProperty, fadeIn0);

        fadeOut1.Completed += (s, e) =>
        {
            arrow1Transform.X = 0;
            arrow1.Opacity = 1;
            arrow0Transform.X = 0;
            arrow0.Opacity = 0;

            arrow1Transform.BeginAnimation(TranslateTransform.XProperty, null);
            arrow1.BeginAnimation(OpacityProperty, null);
            arrow0Transform.BeginAnimation(TranslateTransform.XProperty, null);
            arrow0.BeginAnimation(OpacityProperty, null);
        };
    }

    private void PlayPrevSkipAnimation()
    {
        PlayPrevSkipAnimation(PrevArrow0, PrevArrow1, PrevArrow2);
    }

    private void PlayPrevSkipAnimation(System.Windows.Shapes.Path arrow0, System.Windows.Shapes.Path arrow1, System.Windows.Shapes.Path arrow2)
    {
        const double slideDistance = 220;

        var arrow2Transform = arrow2.RenderTransform as TranslateTransform ?? new TranslateTransform();
        arrow2.RenderTransform = arrow2Transform;

        var slideOut2 = new DoubleAnimation(0, -slideDistance, _dur250) { EasingFunction = _easeQuadOut };
        var fadeOut2 = new DoubleAnimation(1, 0, _dur250) { EasingFunction = _easeQuadOut };

        var arrow0Transform = arrow0.RenderTransform as TranslateTransform ?? new TranslateTransform();
        arrow0.RenderTransform = arrow0Transform;

        var slideIn0 = new DoubleAnimation(slideDistance, 0, _dur250) { EasingFunction = _easeQuadOut };
        var fadeIn0 = new DoubleAnimation(0, 1, _dur250) { EasingFunction = _easeQuadOut };

        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(slideOut2, VNotch.Services.AnimationConfig.TargetFps);
        arrow2Transform.BeginAnimation(TranslateTransform.XProperty, slideOut2);
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(fadeOut2, VNotch.Services.AnimationConfig.TargetFps);
        arrow2.BeginAnimation(OpacityProperty, fadeOut2);
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(slideIn0, VNotch.Services.AnimationConfig.TargetFps);
        arrow0Transform.BeginAnimation(TranslateTransform.XProperty, slideIn0);
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(fadeIn0, VNotch.Services.AnimationConfig.TargetFps);
        arrow0.BeginAnimation(OpacityProperty, fadeIn0);

        fadeOut2.Completed += (s, e) =>
        {
            arrow2Transform.X = 0;
            arrow2.Opacity = 1;
            arrow0Transform.X = 0;
            arrow0.Opacity = 0;

            arrow2Transform.BeginAnimation(TranslateTransform.XProperty, null);
            arrow2.BeginAnimation(OpacityProperty, null);
            arrow0Transform.BeginAnimation(TranslateTransform.XProperty, null);
            arrow0.BeginAnimation(OpacityProperty, null);
        };
    }

    private void PlayAppearAnimation()
    {
        bool showGreeting = _settings.EnableHelloGreeting;

        if (showGreeting)
        {
            _isGreetingActive = true;
            _isAnimating = true;
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
            window.NotchBorder.CornerRadius = cr;
            window.InnerClipBorder.CornerRadius = cr;
            window.NotchBackground.CornerRadius = cr;
            window.MediaBackground.CornerRadius = cr;
            window.MediaBackground2.CornerRadius = cr;
            window.NotchBorderShadow.CornerRadius = cr;
            window.HoverGlow.CornerRadius = cr;
            window.SyncGlassCornerRadius(cr);
            window.UpdateNotchClip();
        }
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

    private void AnimateModeTransition(int fps)
    {
        bool toIsland = _settings.EnableDynamicIslandMode;

        _mtNotchW = _settings.Width;
        _mtNotchH = _settings.Height;
        _mtNotchBottomR = _settings.CornerRadius;

        _mtIslandH = _settings.DynamicIslandHeight;
        _mtIslandW = _settings.DynamicIslandWidth;
        _mtIslandR = Math.Max(0, _mtIslandH / 2.0);

        _isModeTransitioning = true;

        if (LeftEar != null) LeftEar.Visibility = Visibility.Visible;
        if (RightEar != null) RightEar.Visibility = Visibility.Visible;
        if (LeftShadowEar != null) LeftShadowEar.Visibility = Visibility.Visible;
        if (RightShadowEar != null) RightShadowEar.Visibility = Visibility.Visible;

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
        NotchBorder.CornerRadius = cr;
        InnerClipBorder.CornerRadius = cr;
        NotchBackground.CornerRadius = cr;
        MediaBackground.CornerRadius = cr;
        MediaBackground2.CornerRadius = cr;
        NotchBorderShadow.CornerRadius = cr;

        if (NotchContainer != null)
        {
            var m = NotchContainer.Margin;
            if (Math.Abs(m.Top - marginTop) > 0.001)
            {
                NotchContainer.Margin = new Thickness(m.Left, marginTop, m.Right, m.Bottom);
            }
        }

        if (LeftEar != null) LeftEar.Opacity = earOpacity;
        if (RightEar != null) RightEar.Opacity = earOpacity;
        if (LeftShadowEar != null) LeftShadowEar.Opacity = earOpacity;
        if (RightShadowEar != null) RightShadowEar.Opacity = earOpacity;

        UpdateNotchClip();
    }

    private void FinalizeModeTransition(bool toIsland)
    {
        _isModeTransitioning = false;

        NotchBorder.Width = _collapsedWidth;
        NotchBorder.Height = _collapsedHeight;

        var cr = MakeNotchCornerRadius(_cornerRadiusCollapsed);
        NotchBorder.CornerRadius = cr;
        InnerClipBorder.CornerRadius = cr;
        NotchBackground.CornerRadius = cr;
        MediaBackground.CornerRadius = cr;
        MediaBackground2.CornerRadius = cr;
        NotchBorderShadow.CornerRadius = cr;
        CurrentCornerRadius = _cornerRadiusCollapsed;

        if (NotchContainer != null)
        {
            var m = NotchContainer.Margin;
            NotchContainer.Margin = new Thickness(m.Left, toIsland ? DynamicIslandTopMargin : 0, m.Right, m.Bottom);
        }

        var earVis = toIsland ? Visibility.Collapsed : Visibility.Visible;
        if (LeftEar != null) { LeftEar.Opacity = 1; LeftEar.Visibility = earVis; }
        if (RightEar != null) { RightEar.Opacity = 1; RightEar.Visibility = earVis; }
        if (LeftShadowEar != null) { LeftShadowEar.Opacity = 1; LeftShadowEar.Visibility = earVis; }
        if (RightShadowEar != null) { RightShadowEar.Opacity = 1; RightShadowEar.Visibility = earVis; }

        UpdateNotchClip();
        UpdateMediaBackgroundFootprint();
    }

    private double GetCollapsedWidth()
    {
        double width = _settings.EnableDynamicIslandMode
            ? _settings.DynamicIslandWidth
            : _settings.Width;

        if (_isDebugModeEnabled)
        {
            width += 220; // Extra width for debug text
        }

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
        double restX = !_isGreetingActive && NotchWrapper.IsMouseOver ? NotchHoverScaleX : 1.0;

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

    private BlurEffect? _mediaControlsHoverBlur;
    private TransformGroup? _currentTimeHoverTransform;
    private TransformGroup? _remainingTimeHoverTransform;
    private ScaleTransform? _currentTimeHoverScale;
    private ScaleTransform? _remainingTimeHoverScale;
    private TranslateTransform? _currentTimeHoverTranslate;
    private TranslateTransform? _remainingTimeHoverTranslate;

    private void AnimateProgressBarHover(bool isHovered)
    {
        double scaleX = isHovered ? 1.04 : 1.0;
        double blurRadius = _settings.EnableBlurEffects && isHovered ? 4.0 : 0.0;
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

        if (_settings.EnableBlurEffects)
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

    #endregion

    #region Status Bar Animation

    private void AnimateStatusBarReveal(bool show)
    {
        var dur = TimeSpan.FromMilliseconds(show ? 340 : 220);
        var easing = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 5 };
        var settingsEase = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.22 };
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
        double iconTargetY = _settings.EnableDynamicIslandMode ? 5 : 0;

        if (show)
        {
            BatterySection.Visibility = showBatterySection ? Visibility.Visible : Visibility.Collapsed;
            BatterySection.IsHitTestVisible = showBatterySection;
            NavIconsPanel.Visibility = Visibility.Visible;
            UpdateNavIconsActiveState();
            if (_isSecondaryView)
                NavIconsBackground.Visibility = Visibility.Visible;
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

        if (_isSecondaryView)
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
            new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        widthAnim.KeyFrames.Add(new EasingDoubleKeyFrame(currentWidth,
            KeyTime.FromTimeSpan(ejectDur + springDur),
            _easeSoftSpring));
        Timeline.SetDesiredFrameRate(widthAnim, fps);

        double currentHeight = _collapsedHeight;
        double peakHeight = currentHeight + 8;

        var heightAnim = new DoubleAnimationUsingKeyFrames();
        heightAnim.KeyFrames.Add(new EasingDoubleKeyFrame(peakHeight,
            KeyTime.FromTimeSpan(ejectDur),
            new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        heightAnim.KeyFrames.Add(new EasingDoubleKeyFrame(currentHeight,
            KeyTime.FromTimeSpan(ejectDur + springDur),
            _easeSoftSpring));
        Timeline.SetDesiredFrameRate(heightAnim, fps);

        var scaleY = new DoubleAnimationUsingKeyFrames();
        scaleY.KeyFrames.Add(new EasingDoubleKeyFrame(1.15,
            KeyTime.FromTimeSpan(ejectDur),
            new QuadraticEase { EasingMode = EasingMode.EaseOut }));
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
            new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        widthAnim.KeyFrames.Add(new EasingDoubleKeyFrame(currentWidth,
            KeyTime.FromTimeSpan(openDur + springDur),
            _easeSoftSpring));
        Timeline.SetDesiredFrameRate(widthAnim, fps);

        double currentHeight = _collapsedHeight;
        double peakHeight = currentHeight + 10;

        var heightAnim = new DoubleAnimationUsingKeyFrames { BeginTime = absorbDelay };
        heightAnim.KeyFrames.Add(new EasingDoubleKeyFrame(peakHeight,
            KeyTime.FromTimeSpan(openDur),
            new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        heightAnim.KeyFrames.Add(new EasingDoubleKeyFrame(currentHeight,
            KeyTime.FromTimeSpan(openDur + springDur),
            _easeSoftSpring));
        Timeline.SetDesiredFrameRate(heightAnim, fps);

        double currentRadius = _cornerRadiusCollapsed;
        double peakRadius = currentRadius + 6;

        var radiusAnim = new DoubleAnimationUsingKeyFrames { BeginTime = absorbDelay };
        radiusAnim.KeyFrames.Add(new EasingDoubleKeyFrame(peakRadius,
            KeyTime.FromTimeSpan(openDur),
            new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        radiusAnim.KeyFrames.Add(new EasingDoubleKeyFrame(currentRadius,
            KeyTime.FromTimeSpan(openDur + springDur),
            _easeSoftSpring));
        Timeline.SetDesiredFrameRate(radiusAnim, fps);

        var scaleY = new DoubleAnimationUsingKeyFrames { BeginTime = absorbDelay };
        scaleY.KeyFrames.Add(new EasingDoubleKeyFrame(1.12,
            KeyTime.FromTimeSpan(openDur),
            new QuadraticEase { EasingMode = EasingMode.EaseOut }));
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

        if (PlayPauseButton != null && PlayPauseButton.Visibility == Visibility.Visible)
        {
            double currentOpacity = PlayPauseButton.Opacity;
            if (currentOpacity > 0.01)
            {
                PlayPauseButton.BeginAnimation(OpacityProperty, null);
                var fadeAnim = MakeAnim(currentOpacity, 0, baseDuration, easing, wave1Delay);
                Timeline.SetDesiredFrameRate(fadeAnim, fps);
                PlayPauseButton.BeginAnimation(OpacityProperty, fadeAnim);
            }
        }

        if (PrevButton != null && PrevButton.Visibility == Visibility.Visible)
        {
            double currentOpacity = PrevButton.Opacity;
            if (currentOpacity > 0.01)
            {
                PrevButton.BeginAnimation(OpacityProperty, null);
                var fadeAnim = MakeAnim(currentOpacity, 0, baseDuration, easing, wave1Delay);
                Timeline.SetDesiredFrameRate(fadeAnim, fps);
                PrevButton.BeginAnimation(OpacityProperty, fadeAnim);
            }
        }

        if (NextButton != null && NextButton.Visibility == Visibility.Visible)
        {
            double currentOpacity = NextButton.Opacity;
            if (currentOpacity > 0.01)
            {
                NextButton.BeginAnimation(OpacityProperty, null);
                var fadeAnim = MakeAnim(currentOpacity, 0, baseDuration, easing, wave1Delay);
                Timeline.SetDesiredFrameRate(fadeAnim, fps);
                NextButton.BeginAnimation(OpacityProperty, fadeAnim);
            }
        }

        var wave2Delay = TimeSpan.FromMilliseconds(20);

        if (ProgressBarContainer != null && ProgressBarContainer.Visibility == Visibility.Visible)
        {
            double currentOpacity = ProgressBarContainer.Opacity;
            if (currentOpacity > 0.01)
            {
                ProgressBarContainer.BeginAnimation(OpacityProperty, null);
                var fadeAnim = MakeAnim(currentOpacity, 0, baseDuration, easing, wave2Delay);
                Timeline.SetDesiredFrameRate(fadeAnim, fps);
                ProgressBarContainer.BeginAnimation(OpacityProperty, fadeAnim);
            }
        }

        if (RemainingTimeText != null && RemainingTimeText.Visibility == Visibility.Visible)
        {
            double currentOpacity = RemainingTimeText.Opacity;
            if (currentOpacity > 0.01)
            {
                RemainingTimeText.BeginAnimation(OpacityProperty, null);
                var fadeAnim = MakeAnim(currentOpacity, 0, baseDuration, easing, wave2Delay);
                Timeline.SetDesiredFrameRate(fadeAnim, fps);
                RemainingTimeText.BeginAnimation(OpacityProperty, fadeAnim);
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

        if (_isLyricsActive && LyricsBlurBackground != null && LyricsBlurBackground.Visibility == Visibility.Visible)
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
        if (PlayPauseButton != null)
        {
            PlayPauseButton.BeginAnimation(OpacityProperty, null);
            if (PlayPauseButton.Visibility == Visibility.Visible)
                PlayPauseButton.Opacity = 1.0;
        }

        if (PrevButton != null)
        {
            PrevButton.BeginAnimation(OpacityProperty, null);
            if (PrevButton.Visibility == Visibility.Visible)
                PrevButton.Opacity = 1.0;
        }

        if (NextButton != null)
        {
            NextButton.BeginAnimation(OpacityProperty, null);
            if (NextButton.Visibility == Visibility.Visible)
                NextButton.Opacity = 1.0;
        }

        if (ProgressBarContainer != null)
        {
            ProgressBarContainer.BeginAnimation(OpacityProperty, null);
            if (ProgressBarContainer.Visibility == Visibility.Visible)
                ProgressBarContainer.Opacity = 1.0;
        }

        if (RemainingTimeText != null)
        {
            RemainingTimeText.BeginAnimation(OpacityProperty, null);
            if (RemainingTimeText.Visibility == Visibility.Visible)
                RemainingTimeText.Opacity = 1.0;
        }

        if (CalendarWidget != null)
        {
            CalendarWidget.BeginAnimation(OpacityProperty, null);
            if (CalendarWidget.Visibility == Visibility.Visible)
                CalendarWidget.Opacity = 1.0;
        }

        if (MusicViz != null)
        {
            MusicViz.BeginAnimation(OpacityProperty, null);
            if (MusicViz.Visibility == Visibility.Visible)
                MusicViz.Opacity = 1.0;
        }

    }

    private void AnimateTimerContentFadeOut()
    {
        if (TimerContent == null || TimerContent.Visibility != Visibility.Visible) return;

        int fps = VNotch.Services.AnimationConfig.TargetFps;
        var baseDuration = new Duration(TimeSpan.FromMilliseconds(160));
        var easing = _easeQuadIn;

        if (CountdownDisplay != null && CountdownDisplay.Visibility == Visibility.Visible)
        {
            double currentOpacity = CountdownDisplay.Opacity;
            if (currentOpacity > 0.01)
            {
                CountdownDisplay.BeginAnimation(OpacityProperty, null);
                var fadeAnim = MakeAnim(currentOpacity, 0, baseDuration, easing, TimeSpan.Zero);
                Timeline.SetDesiredFrameRate(fadeAnim, fps);
                CountdownDisplay.BeginAnimation(OpacityProperty, fadeAnim);
            }
        }

        var wave2Delay = TimeSpan.FromMilliseconds(25);

        if (CountdownProgressFill != null && CountdownProgressFill.Visibility == Visibility.Visible)
        {
            double currentOpacity = CountdownProgressFill.Opacity;
            if (currentOpacity > 0.01)
            {
                CountdownProgressFill.BeginAnimation(OpacityProperty, null);
                var fadeAnim = MakeAnim(currentOpacity, 0, baseDuration, easing, wave2Delay);
                Timeline.SetDesiredFrameRate(fadeAnim, fps);
                CountdownProgressFill.BeginAnimation(OpacityProperty, fadeAnim);
            }
        }

        if (CountdownDisplayPanel != null && CountdownDisplayPanel.Visibility == Visibility.Visible)
        {
            double currentOpacity = CountdownDisplayPanel.Opacity;
            if (currentOpacity > 0.01)
            {
                CountdownDisplayPanel.BeginAnimation(OpacityProperty, null);
                var fadeAnim = MakeAnim(currentOpacity, 0, new Duration(TimeSpan.FromMilliseconds(180)), easing, wave2Delay);
                Timeline.SetDesiredFrameRate(fadeAnim, fps);
                CountdownDisplayPanel.BeginAnimation(OpacityProperty, fadeAnim);
            }
        }

        var wave3Delay = TimeSpan.FromMilliseconds(40);

        if (CountdownStartBtn != null && CountdownStartBtn.Visibility == Visibility.Visible)
        {
            double currentOpacity = CountdownStartBtn.Opacity;
            if (currentOpacity > 0.01)
            {
                CountdownStartBtn.BeginAnimation(OpacityProperty, null);
                var fadeAnim = MakeAnim(currentOpacity, 0, baseDuration, easing, wave3Delay);
                Timeline.SetDesiredFrameRate(fadeAnim, fps);
                CountdownStartBtn.BeginAnimation(OpacityProperty, fadeAnim);
            }
        }

        if (CountdownResetBtn != null && CountdownResetBtn.Visibility == Visibility.Visible)
        {
            double currentOpacity = CountdownResetBtn.Opacity;
            if (currentOpacity > 0.01)
            {
                CountdownResetBtn.BeginAnimation(OpacityProperty, null);
                var fadeAnim = MakeAnim(currentOpacity, 0, baseDuration, easing, wave3Delay);
                Timeline.SetDesiredFrameRate(fadeAnim, fps);
                CountdownResetBtn.BeginAnimation(OpacityProperty, fadeAnim);
            }
        }

        if (CountdownStepperCapsule != null && CountdownStepperCapsule.Visibility == Visibility.Visible)
        {
            double currentOpacity = CountdownStepperCapsule.Opacity;
            if (currentOpacity > 0.01)
            {
                CountdownStepperCapsule.BeginAnimation(OpacityProperty, null);
                var fadeAnim = MakeAnim(currentOpacity, 0, baseDuration, easing, wave3Delay);
                Timeline.SetDesiredFrameRate(fadeAnim, fps);
                CountdownStepperCapsule.BeginAnimation(OpacityProperty, fadeAnim);
            }
        }
    }

    private void RestoreTimerContentOpacity()
    {
        if (CountdownDisplay != null)
        {
            CountdownDisplay.BeginAnimation(OpacityProperty, null);
            CountdownDisplay.Opacity = 1.0;
        }

        if (CountdownProgressFill != null)
        {
            CountdownProgressFill.BeginAnimation(OpacityProperty, null);
            CountdownProgressFill.Opacity = 1.0;
        }

        if (CountdownDisplayPanel != null)
        {
            CountdownDisplayPanel.BeginAnimation(OpacityProperty, null);
            CountdownDisplayPanel.Opacity = 1.0;
        }

        if (CountdownStartBtn != null)
        {
            CountdownStartBtn.BeginAnimation(OpacityProperty, null);
            CountdownStartBtn.Opacity = 1.0;
        }

        if (CountdownResetBtn != null)
        {
            CountdownResetBtn.BeginAnimation(OpacityProperty, null);
            CountdownResetBtn.Opacity = 1.0;
        }

        if (CountdownStepperCapsule != null)
        {
            CountdownStepperCapsule.BeginAnimation(OpacityProperty, null);
            // The stepper stays dimmed while a countdown is running.
            CountdownStepperCapsule.Opacity = _isCountdownRunning ? 0.4 : 1.0;
        }
    }

    #endregion
}

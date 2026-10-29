using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using VNotch.Controls;
using VNotch.Services;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch;

public partial class MainWindow
{

    #region Notch Expand/Collapse
    private enum LastExpandedView { Primary, Secondary, Timer, Audio, Camera }
    private LastExpandedView _lastExpandedViewBeforeCollapse = LastExpandedView.Primary;

    private const double ExpandedContentHorizontalInset = 16;
    private const double ExpandedContentVerticalInset = 10;

    private double ExpandedContentRestY => _settings.EnableDynamicIslandMode ? 8.5 : 4;


    private void RestoreExpandedContentRestLayout()
    {
        if (ExpandedContent == null) return;

        ExpandedContent.BeginAnimation(WidthProperty, null);
        ExpandedContent.BeginAnimation(HeightProperty, null);
        ExpandedContent.HorizontalAlignment = HorizontalAlignment.Center;
        ExpandedContent.VerticalAlignment = VerticalAlignment.Top;
        ExpandedContent.UseLayoutRounding = false;
        ExpandedContent.Width = _expandedWidth - ExpandedContentHorizontalInset;
        ExpandedContent.Height = _expandedHeight - ExpandedContentVerticalInset;

        double restY = ExpandedContentRestY;
        ExpandedContent.RenderTransform = restY != 0 ? new TranslateTransform(0, restY) : null;
    }

    private void PrepareExpandedContentLayoutForReveal()
    {
        if (ExpandedContent == null) return;

        UpdateProgressSectionLayout();
        MediaPlayback.RefreshMediaMarquee();
    }

    private static DoubleAnimationUsingKeyFrames MakeExpandGeometryAnimation(
        double from,
        double to,
        IEasingFunction easing,
        int fps)
    {
        var animation = new DoubleAnimationUsingKeyFrames
        {
            Duration = _dur600,
            FillBehavior = FillBehavior.HoldEnd
        };
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(_dur500.TimeSpan), easing));
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(to, KeyTime.FromTimeSpan(_dur600.TimeSpan)));
        Timeline.SetDesiredFrameRate(animation, fps);
        return animation;
    }

    private double GetCurrentExpandedContentTranslationY()
    {
        if (ExpandedContent == null) return 0;
        var transform = ExpandedContent.RenderTransform;
        if (transform == null || transform == Transform.Identity) return 0;

        if (transform is TranslateTransform tt)
        {
            return tt.Y;
        }

        if (transform is TransformGroup tg)
        {
            foreach (var child in tg.Children)
            {
                if (child is TranslateTransform ctt)
                {
                    return ctt.Y;
                }
            }
        }

        return 0;
    }

    private bool TryComputeThumbnailExpandTarget(out (double X, double Y) target)
    {
        target = default;
        try
        {
            if (ThumbnailBorder == null || InnerClipBorder == null) return false;
            if (!ThumbnailBorder.IsLoaded || !InnerClipBorder.IsLoaded) return false;
            if (ThumbnailBorder.ActualWidth <= 0 || ThumbnailBorder.ActualHeight <= 0) return false;
            if (InnerClipBorder.ActualWidth <= 0 || InnerClipBorder.ActualHeight <= 0) return false;

            var thumbPos = ThumbnailBorder.TransformToAncestor(InnerClipBorder).Transform(new Point(0, 0));

            ConfigureAnimationThumbnailRestSlot();

            var overlayMargin = AnimationThumbnailBorder.Margin;
            double targetX = thumbPos.X - overlayMargin.Left;

            double currentTranslationY = GetCurrentExpandedContentTranslationY();
            double contentTargetY = ExpandedContentRestY;
            double targetY = (thumbPos.Y - currentTranslationY) + contentTargetY - overlayMargin.Top;

            if (double.IsNaN(targetX) || double.IsInfinity(targetX) ||
                double.IsNaN(targetY) || double.IsInfinity(targetY))
            {
                return false;
            }

            if (Math.Abs(targetX) > 2000 || Math.Abs(targetY) > 2000) return false;

            target = (targetX, targetY);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void ConfigureAnimationThumbnailRestSlot()
    {
        double left = _settings.EnableDynamicIslandMode ? 12 : 8;
        double top = 4;

        if (_settings.EnableDynamicIslandMode)
        {
            const double compactThumbSize = 22;
            top = Math.Max(0, (GetCollapsedHeight() - compactThumbSize) / 2.0);
        }

        AnimationThumbnailBorder.Margin = new Thickness(left, top, 0, 0);
    }

    private Size GetExpandedThumbnailAnimationSize()
    {
        double width = ThumbnailBorder?.ActualWidth > 0 ? ThumbnailBorder.ActualWidth : 102;
        double height = ThumbnailBorder?.ActualHeight > 0 ? ThumbnailBorder.ActualHeight : 102;
        DpiScale dpi = VisualTreeHelper.GetDpi(ThumbnailBorder ?? AnimationThumbnailBorder);

        // The live thumbnail is layout-rounded, while an animated Width/Height
        width = Math.Round(width * dpi.DpiScaleX) / dpi.DpiScaleX;
        height = Math.Round(height * dpi.DpiScaleY) / dpi.DpiScaleY;
        return new Size(width, height);
    }

    private bool TryComputeCompactThumbnailRestOffset(out (double X, double Y) offset)
    {
        offset = default;
        try
        {
            ConfigureAnimationThumbnailRestSlot();

            if (CompactThumbnailBorder == null || InnerClipBorder == null) return false;
            if (!CompactThumbnailBorder.IsLoaded || !InnerClipBorder.IsLoaded) return false;
            if (CompactThumbnailBorder.ActualWidth <= 0 || CompactThumbnailBorder.ActualHeight <= 0) return false;
            if (InnerClipBorder.ActualWidth <= 0 || InnerClipBorder.ActualHeight <= 0) return false;

            Point compactPosition = CompactThumbnailBorder
                .TransformToAncestor(InnerClipBorder)
                .Transform(new Point(0, 0));
            Thickness overlayMargin = AnimationThumbnailBorder.Margin;
            double offsetX = compactPosition.X - overlayMargin.Left;
            double offsetY = compactPosition.Y - overlayMargin.Top;

            if (!double.IsFinite(offsetX) || !double.IsFinite(offsetY)) return false;
            if (Math.Abs(offsetX) > 2000 || Math.Abs(offsetY) > 2000) return false;

            offset = (offsetX, offsetY);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private (double X, double Y)? _cachedCompactThumbnailOffset;
    private bool _cachedCompactIslandMode;
    private double _cachedCompactHeight;
    private double _cachedCompactDpi;

    private void CacheCompactThumbnailRestOffset(object? sender, EventArgs e)
    {
        if (_isAnimating || _isExpanded || _isMusicExpanded ||
            MusicCompactContent.Visibility == Visibility.Collapsed ||
            !MusicCompactContent.IsArrangeValid || !CompactThumbnailBorder.IsArrangeValid) return;
        if (TryComputeCompactThumbnailRestOffset(out var offset))
        {
            _cachedCompactThumbnailOffset = offset;
            _cachedCompactIslandMode = _settings.EnableDynamicIslandMode;
            _cachedCompactHeight = GetCollapsedHeight();
            _cachedCompactDpi = GetGlassDpiScale();
        }
    }

    private (double X, double Y) MeasureCompactThumbnailRestOffset()
    {
        if (_cachedCompactThumbnailOffset.HasValue &&
            _cachedCompactIslandMode == _settings.EnableDynamicIslandMode &&
            Math.Abs(_cachedCompactHeight - GetCollapsedHeight()) < 1e-6 &&
            Math.Abs(_cachedCompactDpi - GetGlassDpiScale()) < 1e-6)
            return _cachedCompactThumbnailOffset.Value;
        return MusicCompactContent.Visibility != Visibility.Collapsed && MusicCompactContent.IsArrangeValid &&
            TryComputeCompactThumbnailRestOffset(out var measuredOffset) ? measuredOffset : (0, 0);
    }

    private bool _isThumbnailExpandAnimating;
    private Action? _pendingThumbnailHandoff;
    private long _thumbnailOverlayVersion;

    private void CancelMediaThumbnailTransition()
    {


        ResetAnimationThumbnailOverlay();
        ThumbnailBorder.BeginAnimation(OpacityProperty, null);
        ThumbnailBorder.Opacity = 1;
        CompactThumbnailBorder.BeginAnimation(OpacityProperty, null);
        CompactThumbnailBorder.Opacity = 1;
    }

    private void ResetAnimationThumbnailOverlay(bool clearSource = true)
    {
        ++_thumbnailOverlayVersion;
        _isThumbnailExpandAnimating = false;
        _pendingThumbnailHandoff = null;
        AnimationThumbnailBorder.BeginAnimation(OpacityProperty, null);
        AnimationThumbnailBorder.BeginAnimation(WidthProperty, null);
        AnimationThumbnailBorder.BeginAnimation(HeightProperty, null);
        AnimationThumbnailBorder.BeginAnimation(Border.BorderThicknessProperty, null);
        AnimationThumbnailTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        AnimationThumbnailTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        AnimationThumbnailClip.BeginAnimation(RectangleGeometry.RectProperty, null);
        this.BeginAnimation(CurrentThumbnailAnimationRadiusProperty, null);
        CurrentThumbnailAnimationRadius = 6;

        AnimationThumbnailBorder.Visibility = Visibility.Collapsed;
        AnimationThumbnailBorder.Opacity = 0;
        AnimationThumbnailBorder.Width = 22;
        AnimationThumbnailBorder.Height = 22;
        AnimationThumbnailBorder.BorderThickness = new Thickness(0);
        AnimationThumbnailBorder.CornerRadius = new CornerRadius(6);
        if (AnimationThumbnailRim != null)
        {
            AnimationThumbnailRim.CornerRadius = new CornerRadius(6);
            AnimationThumbnailRim.BorderThickness = IsLiquidGlassEnabled ? new Thickness(0.5) : new Thickness(0);
        }
        AnimationThumbnailClip.Rect = new Rect(0, 0, 22, 22);
        AnimationThumbnailClip.RadiusX = 6;
        AnimationThumbnailClip.RadiusY = 6;
        AnimationThumbnailTranslate.X = 0;
        AnimationThumbnailTranslate.Y = 0;

        if (AnimationThumbnailBorder.Effect is DropShadowEffect animShadow)
        {
            animShadow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, null);
            animShadow.BeginAnimation(DropShadowEffect.OpacityProperty, null);
            animShadow.BeginAnimation(DropShadowEffect.ShadowDepthProperty, null);
            animShadow.BlurRadius = 4;
            animShadow.Opacity = 0.35;
            animShadow.ShadowDepth = 0.8;
        }

        if (clearSource)
        {
            AnimationThumbnailImage.Source = null;
        }
    }

    private void HandoffAnimationThumbnailToExpanded()
    {
        SynchronizeMediaPinForTransition();
        if (ThumbnailBorder != null)
        {
            if (!_showingEmptyThumbnail && CompactThumbnail.Source != null && ThumbnailImage != null)
            {
                ThumbnailImage.Source = CompactThumbnail.Source;
                ThumbnailImage.Visibility = Visibility.Visible;
                ThumbnailFallback.Visibility = Visibility.Collapsed;
            }
            else if (ThumbnailImage != null && ThumbnailImage.Source == null)
            {
                ThumbnailImage.Visibility = Visibility.Collapsed;
                ThumbnailFallback.Visibility = Visibility.Visible;
            }
            ThumbnailBorder.BeginAnimation(OpacityProperty, null);
            ThumbnailBorder.Opacity = 1;
            if (ThumbnailScale != null)
            {
                ThumbnailScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                ThumbnailScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                ThumbnailScale.ScaleX = 1;
                ThumbnailScale.ScaleY = 1;
            }
        }

        ResetAnimationThumbnailOverlay(clearSource: false);
    }

    private void HandoffAnimationThumbnailToCompact()
    {
        SynchronizeMediaPinForTransition();
        if (CompactThumbnailBorder != null)
        {
            if (ThumbnailImage.Source != null && CompactThumbnail != null)
            {
                CompactThumbnail.Source = ThumbnailImage.Source;
                CompactThumbnail.Visibility = Visibility.Visible;
            }
            CompactThumbnailBorder.BeginAnimation(OpacityProperty, null);
            CompactThumbnailBorder.Visibility = Visibility.Visible;
            CompactThumbnailBorder.Opacity = 1;
        }

        ResetAnimationThumbnailOverlay(clearSource: false);
    }

    private (double X, double Y)? _cachedThumbnailExpandTarget;
    private double _lastMeasuredTargetExpandedWidth;
    private double _lastMeasuredTargetExpandedHeight;
    private double _lastMeasuredTargetDpi;

    private DoubleAnimation? _cachedThumbWidthExpand;
    private DoubleAnimation? _cachedThumbHeightExpand;
    private RectAnimation? _cachedThumbRectExpand;

    private DoubleAnimation? _cachedThumbWidthCollapse;
    private DoubleAnimation? _cachedThumbHeightCollapse;
    private RectAnimation? _cachedThumbRectCollapse;

    private void DismissStateBeforeExpand()
    {
        _hoverCollapseTimer.Stop();
        _suppressHoverCollapseUntilUtc = DateTime.UtcNow.AddMilliseconds(800);

        MediaPlayback.CancelRewindForNavigation();

        if (IsCountdownCompletionVisualActive)
        {
            DismissCountdownCompletion();
        }

        if (_isVolumeIndicatorActive)
        {
            DismissVolumeIndicatorImmediate(animateExit: true);
        }

        if (_isChargingNotificationVisible)
        {
            StopChargingNotificationDismissTimer();
            ChargingNotification.BeginAnimation(OpacityProperty, null);
            ChargingNotification.Opacity = 0;
            ChargingNotification.Visibility = Visibility.Collapsed;
            _isChargingNotificationVisible = false;
        }
        if (_isBluetoothNotificationVisible)
        {
            _bluetoothController.MarkDismissed();
            BluetoothNotification.BeginAnimation(OpacityProperty, null);
            BluetoothNotification.Opacity = 0;
            BluetoothNotification.Visibility = Visibility.Collapsed;
            BluetoothDisconnectNotification.BeginAnimation(OpacityProperty, null);
            BluetoothDisconnectNotification.Opacity = 0;
            BluetoothDisconnectNotification.Visibility = Visibility.Collapsed;
            _isBluetoothNotificationVisible = false;
        }
        _compactPillArbiter.ForceClear();

        if (_isCompactThumbnailHovered)
        {
            _isCompactThumbnailHovered = false;
            _compactThumbnailHoverLeaveTimer.Stop();
        }
        CompactHoverInfo.BeginAnimation(OpacityProperty, null);
        CompactHoverInfo.Opacity = 0;
        CompactHoverInfo.Visibility = Visibility.Collapsed;
        ResetAnimationThumbnailOverlay();
        this.BeginAnimation(CurrentCompactThumbnailRadiusProperty, null);
        CurrentCompactThumbnailRadius = 6;
        ResetCompactThumbnailRestingState();
    }

    private void ResetNotchScaleBounce()
    {
        double liveScaleX = NotchScale.ScaleX;
        double liveScaleY = NotchScale.ScaleY;

        NotchScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        NotchScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        NotchShadowScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        NotchShadowScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);

        bool wasBouncing = Math.Abs(liveScaleX - 1.0) > 0.005 || Math.Abs(liveScaleY - 1.0) > 0.005;
        if (wasBouncing)
        {
            NotchScale.ScaleX = liveScaleX;
            NotchScale.ScaleY = liveScaleY;
            NotchShadowScale.ScaleX = liveScaleX;
            NotchShadowScale.ScaleY = liveScaleY;

            var settleX = MakeAnim(liveScaleX, 1.0, _dur200, _easeQuadOut);
            var settleY = MakeAnim(liveScaleY, 1.0, _dur200, _easeQuadOut);
            settleX.Completed += (s, e) =>
            {
                NotchScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                NotchScale.ScaleX = 1.0;
                NotchShadowScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                NotchShadowScale.ScaleX = 1.0;
            };
            settleY.Completed += (s, e) =>
            {
                NotchScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                NotchScale.ScaleY = 1.0;
                NotchShadowScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                NotchShadowScale.ScaleY = 1.0;
            };
            NotchScale.BeginAnimation(ScaleTransform.ScaleXProperty, settleX);
            NotchScale.BeginAnimation(ScaleTransform.ScaleYProperty, settleY);
            NotchShadowScale.BeginAnimation(ScaleTransform.ScaleXProperty, settleX);
            NotchShadowScale.BeginAnimation(ScaleTransform.ScaleYProperty, settleY);
        }
        else
        {
            NotchScale.ScaleX = 1.0;
            NotchScale.ScaleY = 1.0;
            NotchShadowScale.ScaleX = 1.0;
            NotchShadowScale.ScaleY = 1.0;
        }
    }

    private void SetupExpandedContentBeforeAnimation()
    {
        ExpandedContent.BeginAnimation(OpacityProperty, null);
        CollapsedContent.BeginAnimation(OpacityProperty, null);
        MusicCompactContent.BeginAnimation(OpacityProperty, null);
        SecondaryContent.BeginAnimation(OpacityProperty, null);
        ResetAnimationThumbnailOverlay();
        MediaBackground.BeginAnimation(OpacityProperty, null);
        MediaBackground2.BeginAnimation(OpacityProperty, null);

        ExpandedContentBlur.BeginAnimation(BlurEffect.RadiusProperty, null);
        CollapsedContentBlur.BeginAnimation(BlurEffect.RadiusProperty, null);
        MusicCompactContentBlur.BeginAnimation(BlurEffect.RadiusProperty, null);

        MediaBackground.Opacity = 0;
        MediaBackground2.Opacity = 0;

        SecondaryContent.Visibility = Visibility.Collapsed;
        TimerContent.Visibility = Visibility.Collapsed;

        ExpandedContent.Opacity = 0;
        ExpandedContent.Visibility = Visibility.Visible;

        if (LyricsBlurBackground != null)
        {
            _isLyricsBlurFadeInProgress = false;
            LyricsBlurBackground.BeginAnimation(OpacityProperty, null);
            LyricsBlurBackground.Opacity = 0;
            LyricsBlurBackground.Visibility = Visibility.Collapsed;
        }
        if (_isLyricsActive && LyricsCanvasBackground != null)
        {
            LyricsCanvasBackground.BeginAnimation(OpacityProperty, null);
            LyricsCanvasBackground.Opacity = 0;
        }
        ExpandedContent.Width = _expandedWidth - ExpandedContentHorizontalInset;
        ExpandedContent.Height = _expandedHeight - ExpandedContentVerticalInset;
        PrepareExpandedContentLayoutForReveal();
    }

    private (double X, double Y)? EnsureCachedThumbnailExpandTarget()
    {
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (_cachedThumbnailExpandTarget.HasValue &&
            Math.Abs(_lastMeasuredTargetExpandedWidth - _expandedWidth) < 0.1 &&
            Math.Abs(_lastMeasuredTargetExpandedHeight - _expandedHeight) < 0.1 &&
            Math.Abs(_lastMeasuredTargetDpi - dpi) < 0.001)
        {
            return _cachedThumbnailExpandTarget;
        }

        // A cold cache needs only the media subtree's destination layout. Never
        // resize the shell or flush the window's pending Measure/Arrange queue.
        var destination = new Size(_expandedWidth, _expandedHeight);
        ExpandedContent.Measure(destination);
        ExpandedContent.Arrange(new Rect(destination));
        if (ThumbnailBorder.ActualWidth > 0 && ThumbnailBorder.ActualHeight > 0)
        {
            ConfigureAnimationThumbnailRestSlot();
            var local = ThumbnailBorder.TransformToAncestor(ExpandedContent).Transform(new Point());
            _cachedThumbnailExpandTarget = (
                local.X + (_expandedWidth - ExpandedContent.ActualWidth) / 2 - AnimationThumbnailBorder.Margin.Left,
                local.Y + ExpandedContent.Margin.Top + ExpandedContentRestY - AnimationThumbnailBorder.Margin.Top);
            _lastMeasuredTargetExpandedWidth = _expandedWidth;
            _lastMeasuredTargetExpandedHeight = _expandedHeight;
            _lastMeasuredTargetDpi = dpi;
        }

        return _cachedThumbnailExpandTarget;
    }

    private void EnsureExpandThumbnailAnimations(double expandedThumbWidth, double expandedThumbHeight, Duration thumbDur, IEasingFunction thumbEase, int thumbFps)
    {
        if (_cachedThumbWidthExpand != null &&
            _cachedThumbWidthExpand.Duration == thumbDur &&
            Math.Abs((_cachedThumbWidthExpand.To ?? 0) - expandedThumbWidth) <= 0.001 &&
            Math.Abs((_cachedThumbHeightExpand?.To ?? 0) - expandedThumbHeight) <= 0.001)
        {
            return;
        }

        _cachedThumbWidthExpand = MakeAnim(22, expandedThumbWidth, thumbDur, thumbEase, null);
        _cachedThumbHeightExpand = MakeAnim(22, expandedThumbHeight, thumbDur, thumbEase, null);
        Timeline.SetDesiredFrameRate(_cachedThumbWidthExpand, thumbFps);
        Timeline.SetDesiredFrameRate(_cachedThumbHeightExpand, thumbFps);

        _cachedThumbRectExpand = new RectAnimation(
            new Rect(0, 0, 22, 22),
            new Rect(0, 0, expandedThumbWidth, expandedThumbHeight),
            thumbDur)
        {
            EasingFunction = thumbEase
        };
        Timeline.SetDesiredFrameRate(_cachedThumbRectExpand, thumbFps);

        _cachedThumbWidthExpand.Freeze();
        _cachedThumbHeightExpand.Freeze();
        _cachedThumbRectExpand.Freeze();
    }

    private void AnimateThumbnailExpandOverlay((double X, double Y) compactThumbnailRestOffset)
    {
        SynchronizeMediaPinForTransition();
        var cachedExpandTarget = EnsureCachedThumbnailExpandTarget();
        if (!cachedExpandTarget.HasValue)
        {
            ResetAnimationThumbnailOverlay();
            if (CompactThumbnailBorder != null) CompactThumbnailBorder.Opacity = 1;
            if (ThumbnailBorder != null) ThumbnailBorder.Opacity = 1;
            return;
        }

        if (ThumbnailBorder != null)
        {
            var source = _showingEmptyThumbnail ? null : CompactThumbnail.Source;
            ThumbnailImage.Source = source;
            ThumbnailImage.Visibility = source == null ? Visibility.Collapsed : Visibility.Visible;
            ThumbnailFallback.Visibility = source == null ? Visibility.Visible : Visibility.Collapsed;
            ThumbnailImage.Opacity = 1;
        }
        AnimationThumbnailImage.Source = _showingEmptyThumbnail ? null : CompactThumbnail.Source;
        AnimationThumbnailBorder.Visibility = Visibility.Visible;
        AnimationThumbnailBorder.Opacity = 1;
        AnimationThumbnailBorder.CornerRadius = new CornerRadius(6);
        AnimationThumbnailClip.RadiusX = 6;
        AnimationThumbnailClip.RadiusY = 6;
        AnimationThumbnailBorder.Width = 22;
        AnimationThumbnailBorder.Height = 22;
        AnimationThumbnailClip.Rect = new Rect(0, 0, 22, 22);
        var (compactRestX, compactRestY) = compactThumbnailRestOffset;
        AnimationThumbnailTranslate.X = compactRestX;
        AnimationThumbnailTranslate.Y = compactRestY;

        var (targetX, targetY) = cachedExpandTarget.Value;

        var thumbDur = _dur500;
        var thumbEase = _easeThumbSpring;
        int thumbFps = VNotch.Services.AnimationConfig.TargetFps;
        Size expandedThumbSize = GetExpandedThumbnailAnimationSize();
        double expandedThumbWidth = expandedThumbSize.Width;
        double expandedThumbHeight = expandedThumbSize.Height;

        EnsureExpandThumbnailAnimations(expandedThumbWidth, expandedThumbHeight, thumbDur, thumbEase, thumbFps);

        var thumbTranslateXAnim = MakeAnim(compactRestX, targetX, thumbDur, thumbEase, null);
        var thumbTranslateYAnim = MakeAnim(compactRestY, targetY, thumbDur, thumbEase, null);
        Timeline.SetDesiredFrameRate(thumbTranslateXAnim, thumbFps);
        Timeline.SetDesiredFrameRate(thumbTranslateYAnim, thumbFps);

        _isThumbnailExpandAnimating = true;
        _pendingThumbnailHandoff = null;
        long overlayVersion = ++_thumbnailOverlayVersion;
        thumbTranslateYAnim.Completed += (s, e) =>
        {
            if (overlayVersion != _thumbnailOverlayVersion) return;
            _isThumbnailExpandAnimating = false;
            var handoff = _pendingThumbnailHandoff;
            _pendingThumbnailHandoff = null;
            handoff?.Invoke();
        };

        AnimationThumbnailBorder.Width = expandedThumbWidth;
        AnimationThumbnailBorder.Height = expandedThumbHeight;
        AnimationThumbnailBorder.BorderThickness = new Thickness(0);
        AnimationThumbnailTranslate.X = targetX;
        AnimationThumbnailTranslate.Y = targetY;
        AnimationThumbnailClip.Rect = new Rect(0, 0, expandedThumbWidth, expandedThumbHeight);
        AnimationThumbnailBorder.BeginAnimation(WidthProperty, _cachedThumbWidthExpand);
        AnimationThumbnailBorder.BeginAnimation(HeightProperty, _cachedThumbHeightExpand);
        AnimationThumbnailTranslate.BeginAnimation(TranslateTransform.XProperty, thumbTranslateXAnim);
        AnimationThumbnailTranslate.BeginAnimation(TranslateTransform.YProperty, thumbTranslateYAnim);
        AnimateThumbnailAnimationRadius(6, 14, thumbDur, _easeExpOut6);

        AnimationThumbnailClip.BeginAnimation(RectangleGeometry.RectProperty, _cachedThumbRectExpand);

        if (IsLiquidGlassEnabled && AnimationThumbnailBorder.Effect is DropShadowEffect animShadow)
        {
            animShadow.BeginAnimation(DropShadowEffect.OpacityProperty, MakeAnim(0.35, 0.55, thumbDur, _easeExpOut6));
        }

        if (CompactThumbnailBorder != null)
        {
            CompactThumbnailBorder.Opacity = 0;
            CompactThumbnailBorder.Visibility = Visibility.Collapsed;
        }
        if (ThumbnailBorder != null) ThumbnailBorder.Opacity = 0;
    }

    private void AnimateLyricsBlurImageSwitch(ImageSource? newThumbnail)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => AnimateLyricsBlurImageSwitch(newThumbnail));
            return;
        }

        if (LyricsBlurImage == null || newThumbnail == null) return;
        newThumbnail = LyricsBackgroundBrightness.Apply(newThumbnail, _settings.BrightenDarkLyricsBackground);

        if (LyricsBlurImage.Source == null || !_isExpanded)
        {
            LyricsBlurImage.BeginAnimation(OpacityProperty, null);
            LyricsBlurImage.Source = newThumbnail;
            LyricsBlurImage.Opacity = 1.0;
            if (LyricsBlurImageNext != null)
            {
                LyricsBlurImageNext.BeginAnimation(OpacityProperty, null);
                LyricsBlurImageNext.Opacity = 0.0;
                LyricsBlurImageNext.Source = null;
            }
            return;
        }

        if (ReferenceEquals(LyricsBlurImage.Source, newThumbnail))
        {
            // Reversing a toggle mid-transition must also cancel the other layer.
            if (LyricsBlurImageNext != null)
            {
                LyricsBlurImageNext.BeginAnimation(OpacityProperty, null);
                LyricsBlurImageNext.Opacity = 0;
                LyricsBlurImageNext.Source = null;
            }
            return;
        }

        if (ReferenceEquals(LyricsBlurImageNext?.Source, newThumbnail))
        {
            return;
        }

        if (LyricsBlurImageNext != null)
        {
            LyricsBlurImageNext.BeginAnimation(OpacityProperty, null);
            if (LyricsBlurImageNext.Opacity > 0.5 && LyricsBlurImageNext.Source != null)
            {
                LyricsBlurImage.Source = LyricsBlurImageNext.Source;
            }
            LyricsBlurImageNext.Opacity = 0;
            LyricsBlurImageNext.Source = newThumbnail;
            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(400))
            {
                EasingFunction = new ExponentialEase { Exponent = 4, EasingMode = EasingMode.EaseOut }
            };
            fadeIn.Completed += (s, e) =>
            {
                if (ReferenceEquals(LyricsBlurImageNext.Source, newThumbnail))
                {
                    LyricsBlurImage.Source = newThumbnail;
                    LyricsBlurImageNext.BeginAnimation(OpacityProperty, null);
                    LyricsBlurImageNext.Opacity = 0;
                    LyricsBlurImageNext.Source = null;
                }
            };
            Timeline.SetDesiredFrameRate(fadeIn, VNotch.Services.AnimationConfig.TargetFps);
            LyricsBlurImageNext.BeginAnimation(OpacityProperty, fadeIn);
        }
        else
        {
            LyricsBlurImage.Source = newThumbnail;
            LyricsBlurImage.Opacity = 1.0;
        }
    }

    private bool _isLyricsBlurFadeInProgress = false;

    private void HideLyricsBlurBackgroundImmediately()
    {
        _isLyricsBlurFadeInProgress = false;
        if (LyricsBlurBackground == null) return;
        LyricsBlurBackground.BeginAnimation(OpacityProperty, null);
        LyricsBlurBackground.Opacity = 0;
        LyricsBlurBackground.Visibility = Visibility.Collapsed;
    }

    private bool CanShowLyricsBlurBackground => _settings.EnableBlurEffects && !IsLiquidGlassEnabled &&
        ShouldShowMediaBlurBackground && !_isSpotifyCanvasMediaOpen &&
        _isExpanded && !_isTimerView && !_isAudioView && !_isSecondaryView;

    private void FadeInLyricsBlurBackgroundIfActive(bool force = false)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => FadeInLyricsBlurBackgroundIfActive(force));
            return;
        }

        if (!CanShowLyricsBlurBackground)
        {
            HideLyricsBlurBackgroundImmediately();
            return;
        }
        if (LyricsBlurBackground == null) return;

        bool wasCollapsed = LyricsBlurBackground.Visibility != Visibility.Visible;

        if (LyricsBlurImage != null)
        {
            if ((wasCollapsed || LyricsBlurImage.Source == null) && _currentMediaInfo?.Thumbnail != null)
            {
                LyricsBlurImage.Source = LyricsBackgroundBrightness.Apply(_currentMediaInfo.Thumbnail, _settings.BrightenDarkLyricsBackground);
                if (wasCollapsed && LyricsBlurImageNext != null)
                {
                    LyricsBlurImageNext.BeginAnimation(OpacityProperty, null);
                    LyricsBlurImageNext.Opacity = 0.0;
                    LyricsBlurImageNext.Source = null;
                }
            }
            if (LyricsBlurImageNext == null || LyricsBlurImageNext.Opacity <= 0.01)
            {
                LyricsBlurImage.BeginAnimation(OpacityProperty, null);
                LyricsBlurImage.Opacity = 1.0;
            }
        }
        double currentOpacity = wasCollapsed ? 0 : LyricsBlurBackground.Opacity;

        if (!force && !wasCollapsed && (currentOpacity >= 0.52 || _isLyricsBlurFadeInProgress))
        {
            return;
        }

        _isLyricsBlurFadeInProgress = true;
        LyricsBlurBackground.BeginAnimation(OpacityProperty, null);
        LyricsBlurBackground.Opacity = currentOpacity;
        LyricsBlurBackground.Visibility = Visibility.Visible;

        var fadeIn = new DoubleAnimation(currentOpacity, 0.55, new Duration(TimeSpan.FromMilliseconds(400)))
        {
            EasingFunction = new ExponentialEase { Exponent = 4, EasingMode = EasingMode.EaseOut }
        };
        fadeIn.Completed += (s, e) =>
        {
            if (!CanShowLyricsBlurBackground)
            {
                HideLyricsBlurBackgroundImmediately();
                return;
            }
            _isLyricsBlurFadeInProgress = false;
            LyricsBlurBackground.BeginAnimation(OpacityProperty, null);
            LyricsBlurBackground.Opacity = 0.55;
        };
        Timeline.SetDesiredFrameRate(fadeIn, VNotch.Services.AnimationConfig.TargetFps);
        LyricsBlurBackground.BeginAnimation(OpacityProperty, fadeIn);
    }

    private void FinalizeCompactThumbnailStateAfterExpand(bool suppressCompactThumbnailMotion)
    {
        if (CompactThumbnailBorder != null && !_isClipboardPeekActive && !suppressCompactThumbnailMotion)
        {
            CompactThumbnailBorder.BeginAnimation(OpacityProperty, null);
            CompactThumbnailBorder.Visibility = Visibility.Visible;
            CompactThumbnailBorder.Opacity = 1;
            CompactThumbnailScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            CompactThumbnailScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            CompactThumbnailScale.ScaleX = 1.0;
            CompactThumbnailScale.ScaleY = 1.0;

            CompactThumbnailOutScale.ScaleX = 1.0;
            CompactThumbnailOutScale.ScaleY = 1.0;
            CompactThumbnailOutBlur.Radius = 0.0;
            CompactThumbnail.Opacity = 1.0;
        }
        else if (suppressCompactThumbnailMotion)
        {
            SuppressCompactMediaChromeForCountdownCompletion();
        }
    }

    private void OnExpandCompleted(int generation, bool suppressCompactThumbnailMotion, VNotch.Models.NotchView effectiveTarget = VNotch.Models.NotchView.Media)
    {
        if (generation != _viewTransitionGeneration || generation != _transitionCoordinator.ActiveTransitionId) return;
        _isAnimating = false;
        NotchBorder.IsHitTestVisible = true;

        _isCameraView = effectiveTarget == VNotch.Models.NotchView.Camera;

        if (effectiveTarget == VNotch.Models.NotchView.Timer)
        {
            _isTimerView = true;
            _isSecondaryView = false;
            _isAudioView = false;
            VNotch.Presenters.NotchContentTransitionPresenter.ClearTransformAndEffects(TimerContent);
            ApplyClockViewWindowSize();
        }
        else if (effectiveTarget == VNotch.Models.NotchView.Secondary)
        {
            _isSecondaryView = true;
            _isTimerView = false;
            _isAudioView = false;
            VNotch.Presenters.NotchContentTransitionPresenter.ClearTransformAndEffects(SecondaryContent);
            ResizeHostWindowHeight(ClipboardTrayHeight);
        }
        else if (effectiveTarget == VNotch.Models.NotchView.AudioMixer)
        {
            _isAudioView = true;
            _isTimerView = false;
            _isSecondaryView = false;
            VNotch.Presenters.NotchContentTransitionPresenter.ClearTransformAndEffects(AudioContent);
            if (AudioScrollViewer != null)
            {
                VNotch.Presenters.NotchContentTransitionPresenter.ClearTransformAndEffects(AudioScrollViewer);
                AudioScrollViewer.BeginAnimation(OpacityProperty, null);
                AudioScrollViewer.Visibility = Visibility.Visible;
                AudioScrollViewer.Opacity = 1;
            }
            if (!AudioContent.ApplyPendingSnapshot())
                SettleAudioNotchToFit();
            StartAudioPoll();
            AudioContent.Refresh(SettleAudioNotchToFit);
            ResizeHostWindowHeight(_audioViewHeight > 0 ? _audioViewHeight : _audioViewMaxHeight);
        }
        else if (effectiveTarget == VNotch.Models.NotchView.Camera)
        {
            ResizeHostWindowHeight(CameraViewHeight);
        }
        else
        {
            _isTimerView = false;
            _isSecondaryView = false;
            _isAudioView = false;
            RestoreExpandedWindowSize();
        }

        if (_pendingCameraPreviewVisualTeardown)
        {
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, FinalizePendingCameraPreviewTeardown);
        }

        _transitionCoordinator.CompleteTransition(generation);
        UpdateSpotifyCanvasPresentationContext();
        UpdateNavIconsActiveState();

        if (_focusNotchAfterExpand) FocusNotchForKeyboard();

        RestoreExpandedContentOpacity();

        if (effectiveTarget == VNotch.Models.NotchView.Media)
        {
            UpdateProgressTimerState();
            UpdateCalendarInfo();
            ShowMediaBackground();
            FadeInLyricsBlurBackgroundIfActive();
            FadeInSpotifyCanvasBackgroundIfReady();
            ResumeSpotifyCanvasLifecycle();

            if (_isLyricsActive)
            {
                UpdateLyricsDisplay();
            }
            else
            {
                CheckAndRetryYouTubeSubtitlesOnExpand();
            }

            StartProgressCatchUpAnimation();
            RenderProgressBar();
        }

        if (!_showingEmptyThumbnail && _pendingFlipThumbnail != null)
        {
            var thumb = _pendingFlipThumbnail;
            _pendingFlipThumbnail = null;
            ThumbnailImage.Source = thumb;
            CompactThumbnail.Source = thumb;
            ThumbnailImage.Visibility = Visibility.Visible;
            ThumbnailFallback.Visibility = Visibility.Collapsed;
        }

        CollapsedContentBlur.BeginAnimation(BlurEffect.RadiusProperty, null);
        CollapsedContentBlur.Radius = 0;
        MusicCompactContentBlur.BeginAnimation(BlurEffect.RadiusProperty, null);
        MusicCompactContentBlur.Radius = 0;

        if (effectiveTarget == VNotch.Models.NotchView.Media && _isMusicCompactMode && TryComputeThumbnailExpandTarget(out var updatedTarget))
        {
            _cachedThumbnailExpandTarget = updatedTarget;
        }

        if (effectiveTarget == VNotch.Models.NotchView.Media && _isThumbnailExpandAnimating)
        {
            _pendingThumbnailHandoff = () =>
            {
                if (generation != _viewTransitionGeneration) return;
                HandoffAnimationThumbnailToExpanded();
                FinalizeCompactThumbnailStateAfterExpand(suppressCompactThumbnailMotion);
            };
        }
        else
        {
            HandoffAnimationThumbnailToExpanded();
            FinalizeCompactThumbnailStateAfterExpand(suppressCompactThumbnailMotion);
        }

        CollapsedContent.Visibility = Visibility.Collapsed;
        MusicCompactContent.Visibility = Visibility.Collapsed;
    }

    private void ExpandNotch(long? transitionId = null, VNotch.Models.NotchView? targetView = null,
        VNotch.Models.NotchView fromView = VNotch.Models.NotchView.Compact)
    {
        if (transitionId == null)
        {
            var target = targetView ?? DetermineTargetExpandedView();
            _transitionCoordinator.RequestView(target, "ExpandNotch");
            return;
        }

        if (_isGreetingActive)
        {
            _transitionCoordinator.CancelTransition(transitionId.Value, "GreetingActive");
            return;
        }
        int generation = (int)transitionId;
        _viewTransitionGeneration = generation;
        _isAnimating = true;

        var effectiveTarget = targetView ?? VNotch.Models.NotchView.Media;
        if (_isAudioView && effectiveTarget != VNotch.Models.NotchView.AudioMixer) StopAudioPoll();
        if (effectiveTarget != VNotch.Models.NotchView.Media)
        {
            HideMediaBackground();
            SuspendSpotifyCanvasLifecycle();
            if (_isThumbnailExpandAnimating && fromView == VNotch.Models.NotchView.Media)
            {
                HandoffAnimationThumbnailToExpanded();
            }
            else
            {
                ResetAnimationThumbnailOverlay();
            }

            if (fromView == VNotch.Models.NotchView.Media && !AnimationConfig.ReduceMotion)
            {
                PlayThumbnailViewHideAnimation();
            }
            else
            {
                if (ThumbnailScale != null)
                {
                    ThumbnailScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                    ThumbnailScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                    ThumbnailScale.ScaleX = 1;
                    ThumbnailScale.ScaleY = 1;
                }
                ThumbnailBorder.BeginAnimation(OpacityProperty, null);
                ThumbnailBorder.Opacity = 0;
            }
        }


        bool suppressCompactThumbnailMotion = IsCountdownCompletionVisualActive;
        if (suppressCompactThumbnailMotion)
        {
            SuppressCompactMediaChromeForCountdownCompletion();
        }
        CancelThumbnailSwitchForExpand();

        // Read effective animated values before detaching clocks. ActualWidth may
        // still describe the previous layout pass when an animation reverses.
        double currentWidth = _collapsedWidth;
        if (double.IsFinite(NotchBorder.Width) && NotchBorder.Width > 0)
        {
            currentWidth = NotchBorder.Width;
        }
        else if (NotchBorder.ActualWidth > 0)
        {
            currentWidth = NotchBorder.ActualWidth;
        }

        double currentHeight = _collapsedHeight;
        if (double.IsFinite(NotchBorder.Height) && NotchBorder.Height > 0)
        {
            currentHeight = NotchBorder.Height;
        }
        else if (NotchBorder.ActualHeight > 0)
        {
            currentHeight = NotchBorder.ActualHeight;
        }
        double currentCornerRadius = CurrentCornerRadius;
        NotchBorder.BeginAnimation(WidthProperty, null);
        NotchBorder.BeginAnimation(HeightProperty, null);
        this.BeginAnimation(CurrentCornerRadiusProperty, null);
        NotchBorder.Width = currentWidth;
        NotchBorder.Height = currentHeight;
        CurrentCornerRadius = currentCornerRadius;

        DismissStateBeforeExpand();

        (double X, double Y) compactThumbnailRestOffset = (0, 0);
        bool morphMediaArtwork = effectiveTarget == VNotch.Models.NotchView.Media &&
            fromView == VNotch.Models.NotchView.Compact && !AnimationConfig.ReduceMotion;
        if (morphMediaArtwork && _isMusicCompactMode && CompactThumbnail.Source != null && !suppressCompactThumbnailMotion)
        {
            compactThumbnailRestOffset = MeasureCompactThumbnailRestOffset();
        }

        EnsureTopmost();

        ResetNotchScaleBounce();

        double targetWidth = _expandedWidth;
        double targetHeight = _expandedHeight;

        if (effectiveTarget == VNotch.Models.NotchView.Timer)
        {
            VNotch.Presenters.NotchContentTransitionPresenter.ClearTransformAndEffects(TimerContent);
            targetWidth = _clockViewWidth;
            targetHeight = _clockViewHeight;
            if (targetHeight > currentHeight)
                ApplyClockViewWindowSize();
            PrepareClockViewContentSize();
            RefreshClockView();
            UpdateTimerNavIconsState();
        }
        else if (effectiveTarget == VNotch.Models.NotchView.Secondary)
        {
            VNotch.Presenters.NotchContentTransitionPresenter.ClearTransformAndEffects(SecondaryContent);
            targetWidth = ClipboardTrayWidth;
            targetHeight = ClipboardTrayHeight;
            SecondaryContent.Width = targetWidth - SecondaryContent.Margin.Left - SecondaryContent.Margin.Right;
            // Lay out the tray at its destination size. The shell's rounded clip
            // reveals it while expanding, without remeasuring every card each frame.
            SecondaryContent.Height = targetHeight - SecondaryContent.Margin.Top - SecondaryContent.Margin.Bottom;
            SecondaryContent.VerticalAlignment = VerticalAlignment.Top;
            if (targetHeight > currentHeight)
                ResizeHostWindowHeight(targetHeight);
            UpdateNavIconsActiveState();
            EnableKeyboardInput();
        }
        else if (effectiveTarget == VNotch.Models.NotchView.Camera)
        {
            targetWidth = CameraViewWidth;
            targetHeight = CameraViewHeight;
            CameraContent.Width = targetWidth - CameraContent.Margin.Left - CameraContent.Margin.Right;
            if (targetHeight > currentHeight)
                ResizeHostWindowHeight(targetHeight);
            UpdateNavIconsActiveState();
        }
        else if (effectiveTarget == VNotch.Models.NotchView.AudioMixer)
        {
            VNotch.Presenters.NotchContentTransitionPresenter.ClearTransformAndEffects(AudioContent);
            if (AudioScrollViewer != null)
            {
                VNotch.Presenters.NotchContentTransitionPresenter.ClearTransformAndEffects(AudioScrollViewer);
                AudioScrollViewer.BeginAnimation(OpacityProperty, null);
                AudioScrollViewer.Visibility = Visibility.Visible;
                AudioScrollViewer.Opacity = 1;
            }
            AudioContent.PrepareForEntrance();

            targetWidth = _audioViewWidth;
            targetHeight = _audioViewHeight > 0 ? _audioViewHeight : _audioViewMaxHeight;
            if (targetHeight > currentHeight)
                ResizeHostWindowHeight(targetHeight);
            UpdateNavIconsActiveState();
        }
        else
        {
            if (targetHeight > currentHeight)
                RestoreExpandedWindowSize();
            SetupExpandedContentBeforeAnimation();
        }

        AnimateStatusBarReveal(true);

        NotchBorder.IsHitTestVisible = false;
        int animFps = VNotch.Services.AnimationConfig.TargetFps;

        var fadeOutAnim = MakeAnim(0, _dur200, _easeQuadOut);
        var fadeInAnim = MakeAnim(0d, 1d, _dur400, _easePowerOut3);
        var glowAnim = MakeAnim(0.15, _dur200);

        bool useThumbnailOverlayMorph = morphMediaArtwork && _isMusicCompactMode && CompactThumbnail.Source != null && !suppressCompactThumbnailMotion;
        if (useThumbnailOverlayMorph)
        {
            AnimateThumbnailExpandOverlay(compactThumbnailRestOffset);
        }

        var motion = new VNotch.Models.TransitionMotionConfig(
            Duration: _dur500,
            Easing: _easeAppleOut,
            TargetFps: animFps,
            ReduceMotion: AnimationConfig.ReduceMotion
        );

        var plan = new VNotch.Models.TransitionPlan(
            SessionId: generation,
            FromView: fromView,
            TargetView: effectiveTarget,
            TargetShape: _isMusicCompactMode ? VNotch.Controllers.NotchShapeState.MusicExpanding : VNotch.Controllers.NotchShapeState.Expanding,
            TargetWidth: targetWidth,
            TargetHeight: targetHeight,
            TargetCornerRadius: _cornerRadiusExpanded,
            Motion: motion
        );

        if (_notchShellPresenter != null)
        {
            _notchShellPresenter.AnimateShell(plan, result =>
            {
                if (result.Status == VNotch.Models.TransitionExecutionStatus.Completed)
                {
                    OnExpandCompleted(generation, suppressCompactThumbnailMotion, effectiveTarget);
                }
            });
        }
        else
        {
            var widthAnim = MakeExpandGeometryAnimation(currentWidth, targetWidth, _easeAppleOut, animFps);
            var heightAnim = MakeExpandGeometryAnimation(currentHeight, targetHeight, _easeAppleOut, animFps);
            heightAnim.Completed += (s, e) => OnExpandCompleted(generation, suppressCompactThumbnailMotion, effectiveTarget);
            NotchBorder.BeginAnimation(WidthProperty, widthAnim);
            NotchBorder.BeginAnimation(HeightProperty, heightAnim);
            NotchBorder.Width = targetWidth;
            NotchBorder.Height = targetHeight;
            AnimateCornerRadius(_cornerRadiusExpanded, TimeSpan.FromMilliseconds(400));
        }

        CollapsedContent.BeginAnimation(OpacityProperty, fadeOutAnim);
        MusicCompactContent.BeginAnimation(OpacityProperty, fadeOutAnim);

        // Opacity/transform clocks keep the transition on the compositor without
        // changing the blur kernel and intermediate surface on every tick.
        CollapsedContentBlur.Radius = 0;
        MusicCompactContentBlur.Radius = 0;

        if (_notchContentPresenter != null)
        {
            var contentPlan = plan;
            _notchContentPresenter.TransitionContent(contentPlan, _ => { });
        }
        else
        {
            if (effectiveTarget == VNotch.Models.NotchView.Timer && TimerContent != null)
            {
                VNotch.Presenters.NotchContentTransitionPresenter.ClearTransformAndEffects(TimerContent);
                ExpandedContent.Visibility = Visibility.Collapsed;
                TimerContent.Visibility = Visibility.Visible;
                TimerContent.BeginAnimation(OpacityProperty, fadeInAnim);
                TimerContent.Opacity = 1;
            }
            else if (effectiveTarget == VNotch.Models.NotchView.Secondary && SecondaryContent != null)
            {
                VNotch.Presenters.NotchContentTransitionPresenter.ClearTransformAndEffects(SecondaryContent);
                ExpandedContent.Visibility = Visibility.Collapsed;
                SecondaryContent.Visibility = Visibility.Visible;
                SecondaryContent.BeginAnimation(OpacityProperty, fadeInAnim);
                SecondaryContent.Opacity = 1;
            }
            else if (effectiveTarget == VNotch.Models.NotchView.AudioMixer && AudioContent != null)
            {
                VNotch.Presenters.NotchContentTransitionPresenter.ClearTransformAndEffects(AudioContent);
                if (AudioScrollViewer != null)
                {
                    VNotch.Presenters.NotchContentTransitionPresenter.ClearTransformAndEffects(AudioScrollViewer);
                    AudioScrollViewer.BeginAnimation(OpacityProperty, null);
                    AudioScrollViewer.Visibility = Visibility.Visible;
                    AudioScrollViewer.Opacity = 1;
                }
                ExpandedContent.Visibility = Visibility.Collapsed;
                AudioContent.Visibility = Visibility.Visible;
                AudioContent.BeginAnimation(OpacityProperty, fadeInAnim);
                AudioContent.Opacity = 1;
            }
            else
            {
                VNotch.Presenters.NotchContentTransitionPresenter.ClearTransformAndEffects(ExpandedContent);
                ExpandedContent.BeginAnimation(OpacityProperty, fadeInAnim);
                ExpandedContent.Opacity = 1;
            }
        }

        if (effectiveTarget == VNotch.Models.NotchView.Secondary)
            ClipboardTrayView.BeginEntrance();
        else if (effectiveTarget == VNotch.Models.NotchView.Timer)
            PlayClockViewUnfoldIn(TimeSpan.FromMilliseconds(35));
        else if (effectiveTarget == VNotch.Models.NotchView.AudioMixer)
            AudioContent?.RevealRows(TimeSpan.FromMilliseconds(45));
        else if (effectiveTarget == VNotch.Models.NotchView.Camera)
            PlayCameraContentReveal(TimeSpan.FromMilliseconds(35));

        if (effectiveTarget == VNotch.Models.NotchView.Media)
        {
            double contentTargetY = ExpandedContentRestY;
            var expandedGroup = new TransformGroup();
            var expandedTranslate = new TranslateTransform(0, contentTargetY);
            expandedGroup.Children.Add(expandedTranslate);
            ExpandedContent.RenderTransform = expandedGroup;
            ExpandedContent.RenderTransformOrigin = new Point(0.5, 0.4);
            var springSlide = MakeAnim(10, contentTargetY, _dur400, _easeExpOut6);

            ExpandedContentBlur.Radius = 0;
            expandedTranslate.BeginAnimation(TranslateTransform.YProperty, springSlide);
            PlayMediaContentReveal(TimeSpan.FromMilliseconds(25), animateThumbnail: !useThumbnailOverlayMorph);
        }

        HoverGlow.BeginAnimation(OpacityProperty, glowAnim);
    }

    private int _mediaContentRevealGeneration;
    private int _cameraContentRevealGeneration;

    private void PlayThumbnailViewHideAnimation()
    {
        if (ThumbnailBorder == null) return;

        int gen = ++_mediaContentRevealGeneration;
        int fps = AnimationConfig.TargetFps;

        double currentOpacity = ThumbnailBorder.Opacity > 0.01 ? ThumbnailBorder.Opacity : 1.0;
        ThumbnailBorder.BeginAnimation(OpacityProperty, null);
        ThumbnailBorder.Opacity = currentOpacity;

        double currentScaleX = ThumbnailScale?.ScaleX ?? 1.0;
        double currentScaleY = ThumbnailScale?.ScaleY ?? 1.0;
        if (ThumbnailScale != null)
        {
            ThumbnailScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            ThumbnailScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            ThumbnailScale.ScaleX = currentScaleX;
            ThumbnailScale.ScaleY = currentScaleY;
        }

        var dur = new Duration(TimeSpan.FromMilliseconds(160));
        var fadeOut = MakeAnim(currentOpacity, 0.0, dur, _easeQuadOut);
        Timeline.SetDesiredFrameRate(fadeOut, fps);

        fadeOut.Completed += (_, _) =>
        {
            if (gen != _mediaContentRevealGeneration) return;
            ThumbnailBorder.BeginAnimation(OpacityProperty, null);
            ThumbnailBorder.Opacity = 0;
            if (ThumbnailScale != null)
            {
                ThumbnailScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                ThumbnailScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                ThumbnailScale.ScaleX = 1.0;
                ThumbnailScale.ScaleY = 1.0;
            }
        };

        ThumbnailBorder.BeginAnimation(OpacityProperty, fadeOut);
        if (ThumbnailScale != null)
        {
            var scaleXOut = MakeAnim(currentScaleX, 0.82, dur, _easePowerOut3);
            var scaleYOut = MakeAnim(currentScaleY, 0.82, dur, _easePowerOut3);
            Timeline.SetDesiredFrameRate(scaleXOut, fps);
            Timeline.SetDesiredFrameRate(scaleYOut, fps);
            ThumbnailScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleXOut);
            ThumbnailScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleYOut);
        }
    }

    private void ResetMediaContentReveal()
    {
        _mediaContentRevealGeneration++;

        if (ThumbnailBorder != null && !_isThumbnailExpandAnimating)
        {
            ThumbnailBorder.BeginAnimation(OpacityProperty, null);
            ThumbnailBorder.Opacity = 1;
        }
        if (ThumbnailScale != null)
        {
            ThumbnailScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            ThumbnailScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            ThumbnailScale.ScaleX = 1;
            ThumbnailScale.ScaleY = 1;
        }

        if (MediaPlayback != null)
        {
            MediaPlayback.BeginAnimation(OpacityProperty, null);
            MediaPlayback.Opacity = 1;
            if (MediaPlayback.RenderTransform is TranslateTransform playbackTranslate)
            {
                playbackTranslate.BeginAnimation(TranslateTransform.YProperty, null);
                playbackTranslate.X = 0;
                playbackTranslate.Y = 0;
            }
            else
            {
                MediaPlayback.RenderTransform = null;
            }
        }

        if (MediaRightColumn != null)
        {
            MediaRightColumn.BeginAnimation(OpacityProperty, null);
            MediaRightColumn.Opacity = 1;
            if (MediaRightColumn.RenderTransform is TransformGroup rightGroup &&
                rightGroup.Children.Count == 2 &&
                rightGroup.Children[0] is ScaleTransform rightScale &&
                rightGroup.Children[1] is TranslateTransform rightTranslate)
            {
                rightScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                rightScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                rightTranslate.BeginAnimation(TranslateTransform.YProperty, null);
                rightScale.ScaleX = 1;
                rightScale.ScaleY = 1;
                rightTranslate.X = 0;
                rightTranslate.Y = 0;
            }
            else
            {
                MediaRightColumn.RenderTransform = null;
            }
        }
    }

    private void PlayMediaContentReveal(TimeSpan baseDelay, bool animateThumbnail = true)
    {
        if (AnimationConfig.ReduceMotion)
        {
            ResetMediaContentReveal();
            return;
        }

        int gen = ++_mediaContentRevealGeneration;
        int fps = AnimationConfig.TargetFps;

        if (animateThumbnail && ThumbnailBorder != null)
        {
            SynchronizeMediaPinForTransition();
            if (!_showingEmptyThumbnail && CompactThumbnail?.Source != null && ThumbnailImage != null)
            {
                ThumbnailImage.Source = CompactThumbnail.Source;
                ThumbnailImage.Visibility = Visibility.Visible;
                ThumbnailFallback.Visibility = Visibility.Collapsed;
            }
            else if (ThumbnailImage != null && ThumbnailImage.Source == null)
            {
                ThumbnailImage.Visibility = Visibility.Collapsed;
                ThumbnailFallback.Visibility = Visibility.Visible;
            }

            ThumbnailBorder.BeginAnimation(OpacityProperty, null);
            ThumbnailBorder.Opacity = 0;
            if (ThumbnailScale != null)
            {
                ThumbnailScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                ThumbnailScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                ThumbnailScale.ScaleX = 0.84;
                ThumbnailScale.ScaleY = 0.84;
            }

            var thumbFadeDur = new Duration(TimeSpan.FromMilliseconds(280));
            var thumbScaleDur = new Duration(TimeSpan.FromMilliseconds(360));
            var thumbFade = MakeAnim(0, 1, thumbFadeDur, _easeAppleOut, baseDelay);
            Timeline.SetDesiredFrameRate(thumbFade, fps);

            thumbFade.Completed += (_, _) =>
            {
                if (gen != _mediaContentRevealGeneration) return;
                ThumbnailBorder.BeginAnimation(OpacityProperty, null);
                ThumbnailBorder.Opacity = 1;
            };
            ThumbnailBorder.BeginAnimation(OpacityProperty, thumbFade);

            if (ThumbnailScale != null)
            {
                var thumbScaleX = MakeAnim(0.84, 1.0, thumbScaleDur, _easeExpOut6, baseDelay);
                var thumbScaleY = MakeAnim(0.84, 1.0, thumbScaleDur, _easeExpOut6, baseDelay);
                Timeline.SetDesiredFrameRate(thumbScaleX, fps);
                Timeline.SetDesiredFrameRate(thumbScaleY, fps);
                thumbScaleX.Completed += (_, _) =>
                {
                    if (gen != _mediaContentRevealGeneration) return;
                    ThumbnailScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                    ThumbnailScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                    ThumbnailScale.ScaleX = 1.0;
                    ThumbnailScale.ScaleY = 1.0;
                };
                ThumbnailScale.BeginAnimation(ScaleTransform.ScaleXProperty, thumbScaleX);
                ThumbnailScale.BeginAnimation(ScaleTransform.ScaleYProperty, thumbScaleY);
            }
        }

        if (MediaPlayback != null)
        {
            if (MediaPlayback.RenderTransform is not TranslateTransform playbackTranslate || playbackTranslate.IsFrozen)
            {
                playbackTranslate = new TranslateTransform(0, 10);
                MediaPlayback.RenderTransform = playbackTranslate;
            }
            else
            {
                playbackTranslate.BeginAnimation(TranslateTransform.YProperty, null);
                playbackTranslate.X = 0;
                playbackTranslate.Y = 10;
            }

            MediaPlayback.BeginAnimation(OpacityProperty, null);
            MediaPlayback.Opacity = 0;

            var delay1 = baseDelay + TimeSpan.FromMilliseconds(20);
            var dur1 = new Duration(TimeSpan.FromMilliseconds(340));
            var fade1 = MakeAnim(0, 1, dur1, _easeAppleOut, delay1);
            var slide1 = MakeAnim(10, 0, dur1, _easeExpOut6, delay1);
            Timeline.SetDesiredFrameRate(fade1, fps);
            Timeline.SetDesiredFrameRate(slide1, fps);

            fade1.Completed += (_, _) =>
            {
                if (gen != _mediaContentRevealGeneration) return;
                MediaPlayback.BeginAnimation(OpacityProperty, null);
                MediaPlayback.Opacity = 1;
                playbackTranslate.BeginAnimation(TranslateTransform.YProperty, null);
                playbackTranslate.Y = 0;
            };

            MediaPlayback.BeginAnimation(OpacityProperty, fade1);
            playbackTranslate.BeginAnimation(TranslateTransform.YProperty, slide1);
        }

        if (MediaRightColumn != null)
        {
            ScaleTransform rightScale;
            TranslateTransform rightTranslate;
            if (MediaRightColumn.RenderTransform is TransformGroup rightGroup &&
                !rightGroup.IsFrozen &&
                rightGroup.Children.Count == 2 &&
                rightGroup.Children[0] is ScaleTransform existingScale && !existingScale.IsFrozen &&
                rightGroup.Children[1] is TranslateTransform existingTranslate && !existingTranslate.IsFrozen)
            {
                rightScale = existingScale;
                rightTranslate = existingTranslate;
                rightScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                rightScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                rightTranslate.BeginAnimation(TranslateTransform.YProperty, null);
                rightScale.ScaleX = 0.96;
                rightScale.ScaleY = 0.96;
                rightTranslate.X = 0;
                rightTranslate.Y = 12;
            }
            else
            {
                rightScale = new ScaleTransform(0.96, 0.96);
                rightTranslate = new TranslateTransform(0, 12);
                var group = new TransformGroup();
                group.Children.Add(rightScale);
                group.Children.Add(rightTranslate);
                MediaRightColumn.RenderTransform = group;
            }
            MediaRightColumn.RenderTransformOrigin = new Point(0.5, 0.5);
            MediaRightColumn.BeginAnimation(OpacityProperty, null);
            MediaRightColumn.Opacity = 0;

            var delay2 = baseDelay + TimeSpan.FromMilliseconds(60);
            var dur2 = new Duration(TimeSpan.FromMilliseconds(380));
            var fade2 = MakeAnim(0, 1, dur2, _easeAppleOut, delay2);
            var slide2 = MakeAnim(12, 0, dur2, _easeExpOut6, delay2);
            var scaleX2 = MakeAnim(0.96, 1.0, dur2, _easeExpOut6, delay2);
            var scaleY2 = MakeAnim(0.96, 1.0, dur2, _easeExpOut6, delay2);
            Timeline.SetDesiredFrameRate(fade2, fps);
            Timeline.SetDesiredFrameRate(slide2, fps);
            Timeline.SetDesiredFrameRate(scaleX2, fps);
            Timeline.SetDesiredFrameRate(scaleY2, fps);

            fade2.Completed += (_, _) =>
            {
                if (gen != _mediaContentRevealGeneration) return;
                MediaRightColumn.BeginAnimation(OpacityProperty, null);
                MediaRightColumn.Opacity = 1;
                rightScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                rightScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                rightTranslate.BeginAnimation(TranslateTransform.YProperty, null);
                rightScale.ScaleX = 1;
                rightScale.ScaleY = 1;
                rightTranslate.Y = 0;
            };

            MediaRightColumn.BeginAnimation(OpacityProperty, fade2);
            rightTranslate.BeginAnimation(TranslateTransform.YProperty, slide2);
            rightScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleX2);
            rightScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleY2);
        }
    }

    private void ResetCameraContentReveal()
    {
        _cameraContentRevealGeneration++;
        if (CameraSection != null)
        {
            CameraSection.BeginAnimation(OpacityProperty, null);
            CameraSection.Opacity = 1;
        }
        if (CameraSectionScale != null)
        {
            CameraSectionScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            CameraSectionScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            CameraSectionScale.ScaleX = 1;
            CameraSectionScale.ScaleY = 1;
        }
    }

    private void PlayCameraContentReveal(TimeSpan baseDelay)
    {
        if (AnimationConfig.ReduceMotion || CameraSection == null || CameraSectionScale == null)
        {
            ResetCameraContentReveal();
            return;
        }

        int gen = ++_cameraContentRevealGeneration;
        int fps = AnimationConfig.TargetFps;

        CameraSection.BeginAnimation(OpacityProperty, null);
        CameraSection.Opacity = 0;
        CameraSectionScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        CameraSectionScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        CameraSectionScale.ScaleX = 0.94;
        CameraSectionScale.ScaleY = 0.94;

        var durFade = new Duration(TimeSpan.FromMilliseconds(280));
        var durScale = new Duration(TimeSpan.FromMilliseconds(400));
        var fadeAnim = MakeAnim(0, 1, durFade, _easeAppleOut, baseDelay);
        var scaleXAnim = MakeAnim(0.94, 1.0, durScale, _easeExpOut6, baseDelay);
        var scaleYAnim = MakeAnim(0.94, 1.0, durScale, _easeExpOut6, baseDelay);
        Timeline.SetDesiredFrameRate(fadeAnim, fps);
        Timeline.SetDesiredFrameRate(scaleXAnim, fps);
        Timeline.SetDesiredFrameRate(scaleYAnim, fps);

        scaleXAnim.Completed += (_, _) =>
        {
            if (gen != _cameraContentRevealGeneration) return;
            CameraSection.BeginAnimation(OpacityProperty, null);
            CameraSection.Opacity = 1;
            CameraSectionScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            CameraSectionScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            CameraSectionScale.ScaleX = 1;
            CameraSectionScale.ScaleY = 1;
        };

        CameraSection.BeginAnimation(OpacityProperty, fadeAnim);
        CameraSectionScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleXAnim);
        CameraSectionScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleYAnim);
    }

    private LastExpandedView DetermineCurrentExpandedView()
    {
        if (_isCameraView) return LastExpandedView.Camera;
        if (_isAudioView) return LastExpandedView.Audio;
        if (_isTimerView) return LastExpandedView.Timer;
        if (_isSecondaryView) return LastExpandedView.Secondary;
        return LastExpandedView.Primary;
    }

    private VNotch.Models.NotchView DetermineTargetExpandedView()
    {

        if (_settings.ReopenLastViewOnExpand)
        {
            return _lastExpandedViewBeforeCollapse switch
            {
                LastExpandedView.Timer => VNotch.Models.NotchView.Timer,
                LastExpandedView.Secondary => VNotch.Models.NotchView.Secondary,
                LastExpandedView.Audio => VNotch.Models.NotchView.AudioMixer,
                LastExpandedView.Camera when !_settings.HideCamera => VNotch.Models.NotchView.Camera,
                _ => VNotch.Models.NotchView.Media
            };
        }
        return VNotch.Models.NotchView.Media;
    }

    private void AnimateAuxiliaryViewCollapse(FrameworkElement content, int generation)
    {
        if (content.Visibility != Visibility.Visible) return;

        // Capture the rendered state so closing during a view transition does not jump.
        double opacity = content.Opacity;
        var transform = content.RenderTransform?.Value ?? Matrix.Identity;
        double blurRadius = (content.Effect as BlurEffect)?.Radius ?? 0;
        content.BeginAnimation(OpacityProperty, null);
        content.Opacity = opacity;

        var scale = new ScaleTransform(transform.M11, transform.M22);
        var translate = new TranslateTransform(transform.OffsetX, transform.OffsetY);
        var group = new TransformGroup();
        group.Children.Add(scale);
        group.Children.Add(translate);
        content.RenderTransform = group;
        content.RenderTransformOrigin = new Point(0.5, 0);

        int fps = AnimationConfig.TargetFps;
        var fade = MakeAnim(opacity, 0, _dur200, _easeQuadOut);
        var shrinkX = MakeAnim(scale.ScaleX, 0.88, _dur250, _easePowerOut3);
        var shrinkY = MakeAnim(scale.ScaleY, 0.88, _dur250, _easePowerOut3);
        var slide = MakeAnim(translate.Y, -16, _dur250, _easePowerOut3);
        Timeline.SetDesiredFrameRate(fade, fps);
        Timeline.SetDesiredFrameRate(shrinkX, fps);
        Timeline.SetDesiredFrameRate(shrinkY, fps);
        Timeline.SetDesiredFrameRate(slide, fps);

        fade.Completed += (_, _) =>
        {
            if (generation != _viewTransitionGeneration) return;
            VNotch.Presenters.NotchContentTransitionPresenter.ResetElementVisualState(content);
        };

        scale.BeginAnimation(ScaleTransform.ScaleXProperty, shrinkX);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, shrinkY);
        translate.BeginAnimation(TranslateTransform.YProperty, slide);
        if (_settings.EnableBlurEffects)
        {
            var blur = new BlurEffect { Radius = blurRadius, RenderingBias = RenderingBias.Performance };
            content.Effect = blur;
            var blurOut = MakeAnim(blurRadius, 10, _dur200, _easeQuadOut);
            Timeline.SetDesiredFrameRate(blurOut, fps);
            blur.BeginAnimation(BlurEffect.RadiusProperty, blurOut);
        }
        else
        {
            content.Effect = null;
        }
        content.BeginAnimation(OpacityProperty, fade);
    }
    private void EnsureCollapseThumbnailAnimations(double expandedThumbWidth, double expandedThumbHeight, Duration thumbDur, IEasingFunction thumbEase, TimeSpan thumbDelay, int thumbFps)
    {
        if (_cachedThumbWidthCollapse != null &&
            _cachedThumbWidthCollapse.Duration == thumbDur &&
            Math.Abs((_cachedThumbWidthCollapse.From ?? 0) - expandedThumbWidth) <= 0.001 &&
            Math.Abs((_cachedThumbHeightCollapse?.From ?? 0) - expandedThumbHeight) <= 0.001)
        {
            return;
        }

        _cachedThumbWidthCollapse = MakeAnim(expandedThumbWidth, 22, thumbDur, thumbEase, thumbDelay);
        _cachedThumbHeightCollapse = MakeAnim(expandedThumbHeight, 22, thumbDur, thumbEase, thumbDelay);
        Timeline.SetDesiredFrameRate(_cachedThumbWidthCollapse, thumbFps);
        Timeline.SetDesiredFrameRate(_cachedThumbHeightCollapse, thumbFps);

        _cachedThumbRectCollapse = new RectAnimation(
            new Rect(0, 0, expandedThumbWidth, expandedThumbHeight),
            new Rect(0, 0, 22, 22),
            thumbDur)
        {
            EasingFunction = thumbEase,
            BeginTime = thumbDelay
        };
        Timeline.SetDesiredFrameRate(_cachedThumbRectCollapse, thumbFps);

        _cachedThumbWidthCollapse.Freeze();
        _cachedThumbHeightCollapse.Freeze();
        _cachedThumbRectCollapse.Freeze();
    }

    private void AnimateThumbnailCollapseOverlay()
    {
        _mediaContentRevealGeneration++;
        SynchronizeMediaPinForTransition();
        if (CompactThumbnailBorder != null)
        {
            CompactThumbnail.Source = ThumbnailImage.Source;
            CompactThumbnail.Visibility = Visibility.Visible;
            CompactThumbnail.Opacity = 1;
            CompactThumbnailBorder.Opacity = 0;
            CompactThumbnailBorder.Visibility = Visibility.Hidden;
        }
        if (ThumbnailBorder != null)
        {
            ThumbnailBorder.BeginAnimation(OpacityProperty, null);
            ThumbnailBorder.Opacity = 0;
        }

        if (!_cachedThumbnailExpandTarget.HasValue &&
            TryComputeThumbnailExpandTarget(out var measuredTarget))
        {
            _cachedThumbnailExpandTarget = measuredTarget;
        }

        var cachedExpandTarget = _cachedThumbnailExpandTarget;
        if (!cachedExpandTarget.HasValue) return;

        var (startX, startY) = cachedExpandTarget.Value;
        Size expandedThumbSize = GetExpandedThumbnailAnimationSize();
        double expandedThumbWidth = expandedThumbSize.Width;
        double expandedThumbHeight = expandedThumbSize.Height;

        AnimationThumbnailImage.Source = ThumbnailImage.Source;
        AnimationThumbnailBorder.Visibility = Visibility.Visible;
        AnimationThumbnailBorder.Opacity = 1;
        AnimationThumbnailBorder.CornerRadius = new CornerRadius(14);
        if (AnimationThumbnailRim != null) AnimationThumbnailRim.CornerRadius = new CornerRadius(14);
        AnimationThumbnailClip.RadiusX = 14;
        AnimationThumbnailClip.RadiusY = 14;
        AnimationThumbnailBorder.Width = expandedThumbWidth;
        AnimationThumbnailBorder.Height = expandedThumbHeight;
        AnimationThumbnailBorder.BorderThickness = new Thickness(0);
        AnimationThumbnailClip.Rect = new Rect(0, 0, expandedThumbWidth, expandedThumbHeight);
        AnimationThumbnailTranslate.X = startX;
        AnimationThumbnailTranslate.Y = startY;

        var thumbDelay = TimeSpan.FromMilliseconds(30);
        var thumbDur = _dur500;
        var thumbEase = _easeThumbSpring;
        int thumbFps = VNotch.Services.AnimationConfig.TargetFps;

        EnsureCollapseThumbnailAnimations(expandedThumbWidth, expandedThumbHeight, thumbDur, thumbEase, thumbDelay, thumbFps);

        var (compactRestX, compactRestY) = MeasureCompactThumbnailRestOffset();
        var thumbTranslateXAnim = MakeAnim(startX, compactRestX, thumbDur, thumbEase, thumbDelay);
        var thumbTranslateYAnim = MakeAnim(startY, compactRestY, thumbDur, thumbEase, thumbDelay);
        Timeline.SetDesiredFrameRate(thumbTranslateXAnim, thumbFps);
        Timeline.SetDesiredFrameRate(thumbTranslateYAnim, thumbFps);

        AnimationThumbnailBorder.BeginAnimation(WidthProperty, _cachedThumbWidthCollapse);
        AnimationThumbnailBorder.BeginAnimation(HeightProperty, _cachedThumbHeightCollapse);
        AnimationThumbnailTranslate.BeginAnimation(TranslateTransform.XProperty, thumbTranslateXAnim);
        AnimationThumbnailTranslate.BeginAnimation(TranslateTransform.YProperty, thumbTranslateYAnim);
        AnimateThumbnailAnimationRadius(14, 6, thumbDur, _easeExpOut6, thumbDelay);

        AnimationThumbnailClip.BeginAnimation(RectangleGeometry.RectProperty, _cachedThumbRectCollapse);

        if (IsLiquidGlassEnabled && AnimationThumbnailBorder.Effect is DropShadowEffect animShadow)
        {
            animShadow.BeginAnimation(DropShadowEffect.OpacityProperty, MakeAnim(0.55, 0.35, thumbDur, thumbEase, thumbDelay));
        }
    }

    private void DismissCollapseNotificationsAndOverlays()
    {
        if (_isChargingNotificationVisible)
        {
            StopChargingNotificationDismissTimer();
            ChargingNotification.BeginAnimation(OpacityProperty, null);
            ChargingNotification.Opacity = 0;
            ChargingNotification.Visibility = Visibility.Collapsed;
            _isChargingNotificationVisible = false;
        }
        if (_isBluetoothNotificationVisible)
        {
            _bluetoothController.MarkDismissed();
            BluetoothNotification.BeginAnimation(OpacityProperty, null);
            BluetoothNotification.Opacity = 0;
            BluetoothNotification.Visibility = Visibility.Collapsed;
            BluetoothDisconnectNotification.BeginAnimation(OpacityProperty, null);
            BluetoothDisconnectNotification.Opacity = 0;
            BluetoothDisconnectNotification.Visibility = Visibility.Collapsed;
            _isBluetoothNotificationVisible = false;
        }
        _compactPillArbiter.ForceClear();

        NavIconsPanel.BeginAnimation(OpacityProperty, null);
        NavIconsPanel.Opacity = 0;
        NavIconsPanel.Visibility = Visibility.Collapsed;
        NavIconsBackground.BeginAnimation(OpacityProperty, null);
        NavIconsBackground.Opacity = 0;
        NavIconsBackground.Visibility = Visibility.Collapsed;
    }

    private void FinalizeCompactModeAfterCollapse(FrameworkElement contentToShow, bool suppressCompactThumbnailMotion, bool deferCompactThumbnailReveal = false)
    {
        if (!_isMusicCompactMode)
        {
            ResetAnimationThumbnailOverlay();
            return;
        }

        contentToShow.Opacity = 1;
        contentToShow.BeginAnimation(OpacityProperty, null);
        contentToShow.RenderTransform = null;

        if (!deferCompactThumbnailReveal)
        {
            ResetCompactThumbnailRestingState();
        }
        CompactHoverInfo.BeginAnimation(OpacityProperty, null);
        CompactHoverInfo.Opacity = 0;
        CompactHoverInfo.Visibility = Visibility.Collapsed;

        bool allowThumbnailHandoff = !_isClipboardPeekActive && !_isVolumeIndicatorActive && !suppressCompactThumbnailMotion && !deferCompactThumbnailReveal;
        if (allowThumbnailHandoff)
        {
            HandoffAnimationThumbnailToCompact();
        }
        else
        {
            ResetAnimationThumbnailOverlay();
        }
    }

    private void FinalizeCompactThumbnailAndVisualizerAfterCollapse(bool suppressCompactThumbnailMotion, bool deferCompactThumbnailReveal = false)
    {
        bool allowCompactChrome = !_isClipboardPeekActive && !_isVolumeIndicatorActive && !suppressCompactThumbnailMotion;

        if (CompactThumbnailBorder != null)
        {
            if (allowCompactChrome && !deferCompactThumbnailReveal)
            {
                CompactThumbnailBorder.BeginAnimation(OpacityProperty, null);
                CompactThumbnailBorder.Visibility = Visibility.Visible;
                CompactThumbnailBorder.Opacity = 1;
            }
            else if (suppressCompactThumbnailMotion)
            {
                SuppressCompactMediaChromeForCountdownCompletion();
            }
        }

        if (ThumbnailBorder != null) ThumbnailBorder.Opacity = 1;

        if (_isMusicCompactMode && _currentMediaInfo?.IsPlaying == true && allowCompactChrome)
        {
            ShowMusicVisualizer(animate: false);
        }

        MusicCompactContent.InvalidateArrange();
        MusicCompactContent.UpdateLayout();

        if (_pendingFlipThumbnail != null && !suppressCompactThumbnailMotion)
        {
            var thumb = _pendingFlipThumbnail;
            _pendingFlipThumbnail = null;
            AnimateThumbnailSwitchOnly(thumb, force: true);
        }
        else if (suppressCompactThumbnailMotion)
        {
            _pendingFlipThumbnail = null;
        }
    }

    private void OnCollapseCompleted(
        int generation,
        bool wasAudio,
        FrameworkElement contentToShow,
        bool suppressCompactThumbnailMotion,
        bool deferCompactThumbnailReveal = false)
    {
        if (generation != _viewTransitionGeneration || generation != _transitionCoordinator.ActiveTransitionId) return;
        _isAnimating = false;
        NotchBorder.IsHitTestVisible = true;
        _transitionCoordinator.CompleteTransition(generation);
        UpdateSpotifyCanvasPresentationContext();
        NotchBorder.BeginAnimation(WidthProperty, null);
        NotchBorder.BeginAnimation(HeightProperty, null);
        NotchBorder.Width = _collapsedWidth;
        NotchBorder.Height = _collapsedHeight;

        RestoreExpandedWindowSize();
        FinalizePendingCameraPreviewTeardown();
        if (wasAudio)
        {
            RestorePrivacyDotVisibility();
        }

        contentToShow.RenderTransform = null;

        DismissCollapseNotificationsAndOverlays();

        ResetContentTransformAndEffectsAfterCollapse();



        FinalizeCompactModeAfterCollapse(contentToShow, suppressCompactThumbnailMotion, deferCompactThumbnailReveal);
        FinalizeCompactThumbnailAndVisualizerAfterCollapse(suppressCompactThumbnailMotion, deferCompactThumbnailReveal);

        if (_settings.StayBehindWindows && _isDesktopEdgePromoted && !IsDesktopNotchInteractionActive())
        {
            ScheduleDesktopLayerDemotion();
        }
    }

    private void ResetContentTransformAndEffectsAfterCollapse()
    {
        VNotch.Presenters.NotchContentTransitionPresenter.ResetElementVisualState(ExpandedContent);
        VNotch.Presenters.NotchContentTransitionPresenter.ResetElementVisualState(TimerContent);
        VNotch.Presenters.NotchContentTransitionPresenter.ResetElementVisualState(AudioContent);
        if (AudioScrollViewer != null)
        {
            VNotch.Presenters.NotchContentTransitionPresenter.ResetElementVisualState(AudioScrollViewer);
        }
        VNotch.Presenters.NotchContentTransitionPresenter.ResetElementVisualState(SecondaryContent);
        VNotch.Presenters.NotchContentTransitionPresenter.ResetElementVisualState(CameraContent);

        ResetMediaContentReveal();
        ResetClockViewUnfoldIn();
        ResetCameraContentReveal();
        AudioContent.ResetRowAnimations();

        ExpandedContentBlur.BeginAnimation(BlurEffect.RadiusProperty, null);
        ExpandedContentBlur.Radius = 0;
        CollapsedContentBlur.BeginAnimation(BlurEffect.RadiusProperty, null);
        CollapsedContentBlur.Radius = 0;
        MusicCompactContentBlur.BeginAnimation(BlurEffect.RadiusProperty, null);
        MusicCompactContentBlur.Radius = 0;

        _isTimerView = false;
        _isSecondaryView = false;
        _isAudioView = false;
        _isCameraView = false;
    }

    private void PrepareStateBeforeCollapse()
    {

        System.Windows.Input.Keyboard.ClearFocus();


        _lastExpandedViewBeforeCollapse = DetermineCurrentExpandedView();

        if (IsCameraPreviewLifecycleActive)
        {
            StopCameraPreviewForViewTransition();
        }
        _isAnimating = true;
        SuspendSpotifyCanvasLifecycle();
        if (IsCountdownCompletionVisualActive)
        {
            SuppressCompactMediaChromeForCountdownCompletion();
        }
        CancelThumbnailSwitchAnimations();

        MediaPlayback.CancelRewindForNavigation();

        EnsureTopmost();

        AnimateExpandedContentFadeOut();
        AnimateStatusBarReveal(false);

        NavIconsBackground.BeginAnimation(OpacityProperty, null);
        NavIconsBackground.Opacity = 0;
        NavIconsBackground.Visibility = Visibility.Collapsed;
        NavIconsPanel.BeginAnimation(OpacityProperty, null);
        NavIconsPanel.Opacity = 0;
        NavIconsPanel.Visibility = Visibility.Collapsed;
    }

    private void OnExpandedContentFadeOutCompleted(int generation, bool wasSecondary, bool wasTimer)
    {
        if (generation != _viewTransitionGeneration) return;
        ExpandedContent.BeginAnimation(OpacityProperty, null);
        ExpandedContent.Opacity = 0;
        ExpandedContent.Visibility = Visibility.Collapsed;
        ExpandedContent.RenderTransform = null;

        if (wasSecondary) return;

        SecondaryContent.BeginAnimation(OpacityProperty, null);
        SecondaryContent.Opacity = 0;
        SecondaryContent.Visibility = Visibility.Collapsed;
        SecondaryContent.RenderTransform = null;
        if (!wasTimer)
        {
            TimerContent.Visibility = Visibility.Collapsed;
            TimerContent.Opacity = 0;
        }
    }

    private void ResetContentBlurAndOverlaysBeforeCollapse()
    {
        ExpandedContent.BeginAnimation(OpacityProperty, null);
        MusicCompactContent.BeginAnimation(OpacityProperty, null);
        ResetCalendarScroll();
        ResetCalendarHoverFocusVisualState();
        CollapsedContent.BeginAnimation(OpacityProperty, null);
        ResetAnimationThumbnailOverlay();

        this.BeginAnimation(CurrentCornerRadiusProperty, null);

        ExpandedContentBlur.BeginAnimation(BlurEffect.RadiusProperty, null);
        CollapsedContentBlur.BeginAnimation(BlurEffect.RadiusProperty, null);
        MusicCompactContentBlur.BeginAnimation(BlurEffect.RadiusProperty, null);
    }

    private void CollapseNotch(long? transitionId = null, VNotch.Models.NotchView? fromView = null)
    {
        if (transitionId == null)
        {
            _transitionCoordinator.RequestCollapse("CollapseNotch");
            return;
        }

        // Debug-lock permission was checked when the coordinator accepted this
        // request. Debug view selection temporarily unlocks only that request.
        if (_isGreetingActive)
        {
            _transitionCoordinator.CancelTransition(transitionId.Value, "GreetingActive");
            return;
        }
        int generation = (int)transitionId;
        _viewTransitionGeneration = generation;
        _isAnimating = true;

        PrepareStateBeforeCollapse();
        if (fromView.HasValue && fromView.Value != VNotch.Models.NotchView.Compact)
        {
            _lastExpandedViewBeforeCollapse = fromView.Value switch
            {
                VNotch.Models.NotchView.Timer => LastExpandedView.Timer,
                VNotch.Models.NotchView.Secondary => LastExpandedView.Secondary,
                VNotch.Models.NotchView.AudioMixer => LastExpandedView.Audio,
                VNotch.Models.NotchView.Camera => LastExpandedView.Camera,
                _ => LastExpandedView.Primary
            };
        }

        bool wasSecondary = _isSecondaryView;
        bool wasTimer = _isTimerView;
        bool wasAudio = _isAudioView;
        _isAudioView = false;
        if (wasAudio)
        {
            StopAudioPoll();
            AudioContent.ReleaseSessionCache();
        }

        _isSecondaryView = false;
        _isTimerView = false;

        ResetContentBlurAndOverlaysBeforeCollapse();

        NotchBorder.IsHitTestVisible = false;
        int animFps = VNotch.Services.AnimationConfig.TargetFps;

        double currentWidth = NotchBorder.ActualWidth > 0 ? NotchBorder.ActualWidth : _expandedWidth;
        double currentHeight = NotchBorder.ActualHeight > 0 ? NotchBorder.ActualHeight : _expandedHeight;

        var expandedGroup = new TransformGroup();
        var expandedTranslate = new TranslateTransform(0, ExpandedContentRestY);
        expandedGroup.Children.Add(expandedTranslate);
        ExpandedContent.RenderTransform = expandedGroup;
        ExpandedContent.RenderTransformOrigin = new Point(0.5, 0.4);

        var fadeOutAnim = MakeAnim(0, _dur200, _easeQuadOut);
        double currentY = ExpandedContentRestY;
        var slideOutAnim = MakeAnim(currentY, -10, _dur400, _easeExpOut6);

        fadeOutAnim.Completed += (s, e) => OnExpandedContentFadeOutCompleted(generation, wasSecondary, wasTimer);

        var fadeOutBlurAnim = MakeAnim(0, TimeSpan.FromMilliseconds(150), _easeQuadOut);
        MediaBackground.BeginAnimation(OpacityProperty, fadeOutBlurAnim);
        MediaBackground2.BeginAnimation(OpacityProperty, fadeOutBlurAnim);

        FrameworkElement contentToShow = _isMusicCompactMode ? MusicCompactContent : CollapsedContent;
        FrameworkElement contentToHide = _isMusicCompactMode ? CollapsedContent : MusicCompactContent;

        contentToHide.BeginAnimation(OpacityProperty, null);
        contentToHide.Visibility = Visibility.Collapsed;
        contentToHide.Opacity = 0;

        bool skipContentZoom = _isMusicCompactMode;
        var showGroup = new TransformGroup();
        var showScale = new ScaleTransform(skipContentZoom ? 1.0 : 0.8, skipContentZoom ? 1.0 : 0.8);
        showGroup.Children.Add(showScale);
        contentToShow.RenderTransform = showGroup;
        contentToShow.RenderTransformOrigin = new Point(0.5, 0.5);

        var fadeInAnim = MakeAnim(1, _dur400, _easePowerOut3);
        var springShow = MakeAnim(skipContentZoom ? 1.0 : 0.8, 1, _dur400, _easeMenuSpring);

        var glowAnim = MakeAnim(0, _dur150);

        // Opacity/transform clocks keep the transition on the compositor without
        // changing the blur kernel and intermediate surface on every tick.

        CollapsedContentBlur.Radius = 0;
        MusicCompactContentBlur.Radius = 0;

        bool suppressCompactThumbnailMotion = IsCountdownCompletionVisualActive;
        bool animateCompactThumbnailRevealOnCollapse = false;
        if (!AnimationConfig.ReduceMotion && _isMusicCompactMode && !suppressCompactThumbnailMotion)
        {
            if (_lastExpandedViewBeforeCollapse == LastExpandedView.Primary && ThumbnailImage.Source != null)
            {
                AnimateThumbnailCollapseOverlay();
            }
            else if (CompactThumbnailBorder != null && (ThumbnailImage.Source != null || CompactThumbnail.Source != null))
            {
                SynchronizeMediaPinForTransition();
                if (!_showingEmptyThumbnail && ThumbnailImage.Source != null)
                {
                    CompactThumbnail.Source = ThumbnailImage.Source;
                    CompactThumbnail.Visibility = Visibility.Visible;
                    CompactThumbnail.Opacity = 1.0;
                }
                CompactThumbnailBorder.Visibility = Visibility.Visible;
                PlayThumbnailRevealAnimation(delay: TimeSpan.FromMilliseconds(160));
                animateCompactThumbnailRevealOnCollapse = true;
            }
        }

        var motion = new VNotch.Models.TransitionMotionConfig(
            Duration: _dur500,
            Easing: _easeExpOut6,
            TargetFps: animFps,
            ReduceMotion: AnimationConfig.ReduceMotion
        );

        var plan = new VNotch.Models.TransitionPlan(
            SessionId: generation,
            FromView: _lastExpandedViewBeforeCollapse switch
            {
                LastExpandedView.Timer => VNotch.Models.NotchView.Timer,
                LastExpandedView.Secondary => VNotch.Models.NotchView.Secondary,
                LastExpandedView.Audio => VNotch.Models.NotchView.AudioMixer,
                LastExpandedView.Camera => VNotch.Models.NotchView.Camera,
                _ => VNotch.Models.NotchView.Media
            },
            TargetView: VNotch.Models.NotchView.Compact,
            TargetShape: _isMusicCompactMode ? VNotch.Controllers.NotchShapeState.MusicCollapsing : VNotch.Controllers.NotchShapeState.Collapsing,
            TargetWidth: _collapsedWidth,
            TargetHeight: _collapsedHeight,
            TargetCornerRadius: _cornerRadiusCollapsed,
            Motion: motion
        );

        if (_notchShellPresenter != null)
        {
            _notchShellPresenter.AnimateShell(plan, result =>
            {
                if (result.Status == VNotch.Models.TransitionExecutionStatus.Completed)
                {
                    OnCollapseCompleted(generation, wasAudio, contentToShow, suppressCompactThumbnailMotion, animateCompactThumbnailRevealOnCollapse);
                }
            });
        }
        else
        {
            NotchBorder.BeginAnimation(WidthProperty, null);
            NotchBorder.BeginAnimation(HeightProperty, null);
            NotchBorder.Width = currentWidth;
            NotchBorder.Height = currentHeight;

            var widthAnim = MakeAnim(currentWidth, _collapsedWidth, _dur500, _easeExpOut6, animFps);
            var heightAnim = MakeAnim(currentHeight, _collapsedHeight, _dur500, _easeExpOut6, animFps);
            heightAnim.Completed += (s, e) => OnCollapseCompleted(generation, wasAudio, contentToShow, suppressCompactThumbnailMotion, animateCompactThumbnailRevealOnCollapse);

            NotchBorder.BeginAnimation(WidthProperty, widthAnim);
            NotchBorder.BeginAnimation(HeightProperty, heightAnim);
            AnimateCornerRadius(_cornerRadiusCollapsed, TimeSpan.FromMilliseconds(400));
        }

        if (_notchContentPresenter != null)
        {
            var contentPlan = plan;
            _notchContentPresenter.TransitionContent(contentPlan, _ => { });
        }
        else
        {
            ExpandedContent.BeginAnimation(OpacityProperty, fadeOutAnim);
            if (SecondaryContent.Visibility == Visibility.Visible)
            {
                SecondaryContent.BeginAnimation(OpacityProperty, fadeOutAnim);
            }
        }

        // Apply after the presenter so its general fade cannot replace the exit motion.
        AnimateAuxiliaryViewCollapse(SecondaryContent, generation);
        AnimateAuxiliaryViewCollapse(CameraContent, generation);
        AnimateAuxiliaryViewCollapse(TimerContent, generation);
        AnimateAuxiliaryViewCollapse(AudioContent, generation);

        expandedTranslate.BeginAnimation(TranslateTransform.YProperty, slideOutAnim);
        ExpandedContentBlur.Radius = 0;

        contentToShow.Visibility = Visibility.Visible;
        contentToShow.BeginAnimation(OpacityProperty, fadeInAnim);
        showScale.BeginAnimation(ScaleTransform.ScaleXProperty, springShow);
        showScale.BeginAnimation(ScaleTransform.ScaleYProperty, springShow);

        var compactBlurTarget = _isMusicCompactMode ? MusicCompactContentBlur : CollapsedContentBlur;
        compactBlurTarget.Radius = 0;

        HoverGlow.BeginAnimation(OpacityProperty, glowAnim);
    }

    #endregion

}

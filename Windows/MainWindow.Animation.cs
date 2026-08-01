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
    private enum LastExpandedView { Primary, Secondary, Timer, Audio }
    private LastExpandedView _lastExpandedViewBeforeCollapse = LastExpandedView.Primary;

    private double ExpandedContentRestY => _settings.EnableDynamicIslandMode ? 8.5 : 4;

    private EventHandler? _mainViewHorizontalStabilizer;

    private void StartMainViewHorizontalStabilizer(TranslateTransform contentTranslate)
    {
        StopMainViewHorizontalStabilizer();

        void Stabilize()
        {
            if (ExpandedContent == null || NotchContainer == null || !ExpandedContent.IsLoaded)
            {
                return;
            }

            try
            {
                Point renderedOrigin = ExpandedContent
                    .TransformToAncestor(NotchContainer)
                    .Transform(new Point(0, 0));
                double layoutOriginX = renderedOrigin.X - contentTranslate.X;
                double targetOriginX = (NotchContainer.ActualWidth - ExpandedContent.ActualWidth) / 2.0;
                DpiScale dpi = VisualTreeHelper.GetDpi(ExpandedContent);
                double correction = targetOriginX - layoutOriginX;
                correction = Math.Round(correction * dpi.DpiScaleX) / dpi.DpiScaleX;

                if (Math.Abs(contentTranslate.X - correction) > 0.001)
                {
                    contentTranslate.X = correction;
                }
            }
            catch (InvalidOperationException)
            {
                // The transition replaced the visual tree between layout ticks.
            }
        }

        _mainViewHorizontalStabilizer = (_, _) => Stabilize();
        ExpandedContent.LayoutUpdated += _mainViewHorizontalStabilizer;
        Stabilize();
    }

    private void StopMainViewHorizontalStabilizer()
    {
        if (_mainViewHorizontalStabilizer == null || ExpandedContent == null)
        {
            return;
        }

        ExpandedContent.LayoutUpdated -= _mainViewHorizontalStabilizer;
        _mainViewHorizontalStabilizer = null;
    }

    private void RestoreExpandedContentRestLayout()
    {
        if (ExpandedContent == null) return;

        ExpandedContent.BeginAnimation(WidthProperty, null);
        ExpandedContent.BeginAnimation(HeightProperty, null);
        ExpandedContent.HorizontalAlignment = HorizontalAlignment.Center;
        ExpandedContent.VerticalAlignment = VerticalAlignment.Top;
        ExpandedContent.UseLayoutRounding = true;
        ExpandedContent.Width = _expandedWidth - 16;
        ExpandedContent.Height = _expandedHeight - 10;

        double restY = ExpandedContentRestY;
        ExpandedContent.RenderTransform = restY != 0 ? new TranslateTransform(0, restY) : null;
        ExpandedContent.UpdateLayout();
    }

    private void PrepareExpandedContentLayoutForReveal()
    {
        if (ExpandedContent == null) return;

        ExpandedContent.UpdateLayout();
        UpdateProgressSectionLayout();
        RefreshMediaMarquee();
        ExpandedContent.UpdateLayout();
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
        // endpoint is rendered at its fractional device-pixel size. Snap the
        // overlay endpoint to the exact physical size used by the live visual.
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

    private (double X, double Y) MeasureCompactThumbnailRestOffset()
    {
        ApplyDynamicIslandContentAlignment(_settings.EnableDynamicIslandMode);
        MusicCompactContent.InvalidateMeasure();
        MusicCompactContent.InvalidateArrange();
        MusicCompactContent.UpdateLayout();

        return TryComputeCompactThumbnailRestOffset(out var measuredOffset)
            ? measuredOffset
            : (0, 0);
    }

    private double GetCompactThumbnailCenteredTop()
    {
        if (!_settings.EnableDynamicIslandMode) return 4;
        const double compactThumbSize = 22;
        double collapsedHeight = GetCollapsedHeight();
        return Math.Max(0, (collapsedHeight - compactThumbSize) / 2.0);
    }

    private void ResetAnimationThumbnailOverlay(bool clearSource = true)
    {
        _animationThumbnailHandoffGeneration++;
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
        AnimationThumbnailClip.Rect = new Rect(0, 0, 22, 22);
        AnimationThumbnailClip.RadiusX = 6;
        AnimationThumbnailClip.RadiusY = 6;
        AnimationThumbnailTranslate.X = 0;
        AnimationThumbnailTranslate.Y = 0;

        if (clearSource)
        {
            AnimationThumbnailImage.Source = null;
        }
    }

    private int _animationThumbnailHandoffGeneration;

    private void CrossfadeAnimationThumbnailToExpanded((double X, double Y)? liveTarget = null)
    {
        if (ThumbnailBorder == null ||
            AnimationThumbnailBorder.Visibility != Visibility.Visible ||
            AnimationThumbnailBorder.Opacity <= 0)
        {
            if (ThumbnailBorder != null) ThumbnailBorder.Opacity = 1;
            ResetAnimationThumbnailOverlay();
            return;
        }

        // Put the live thumbnail underneath the overlay first, then dissolve the
        // overlay. A hard Visibility swap makes WPF resample the same bitmap in a
        // different visual tree and reads as a tiny zoom at the final frame.
        ThumbnailBorder.BeginAnimation(OpacityProperty, null);
        ThumbnailBorder.Opacity = 1;

        int generation = ++_animationThumbnailHandoffGeneration;
        var handoff = MakeAnim(1, 0, _dur100, _easeQuadOut);

        if (liveTarget.HasValue)
        {
            var (liveX, liveY) = liveTarget.Value;
            double overlayX = AnimationThumbnailTranslate.X;
            double overlayY = AnimationThumbnailTranslate.Y;

            if (Math.Abs(overlayX - liveX) > 0.001)
            {
                AnimationThumbnailTranslate.BeginAnimation(
                    TranslateTransform.XProperty,
                    MakeAnim(overlayX, liveX, _dur100, _easeQuadOut));
            }
            if (Math.Abs(overlayY - liveY) > 0.001)
            {
                AnimationThumbnailTranslate.BeginAnimation(
                    TranslateTransform.YProperty,
                    MakeAnim(overlayY, liveY, _dur100, _easeQuadOut));
            }
        }

        handoff.Completed += (_, _) =>
        {
            if (generation != _animationThumbnailHandoffGeneration) return;
            ResetAnimationThumbnailOverlay();
        };

        // Attach the clock while the visible base value is still 1. Setting the
        // base to 0 first exposes a one-frame overlay=0/live=1 swap before the
        // clock's From value becomes active on the next render pass.
        AnimationThumbnailBorder.BeginAnimation(OpacityProperty, handoff);
    }

    private (double X, double Y)? _cachedThumbnailExpandTarget;

    private DoubleAnimation? _cachedThumbWidthExpand;
    private DoubleAnimation? _cachedThumbHeightExpand;
    private RectAnimation? _cachedThumbRectExpand;

    private DoubleAnimation? _cachedThumbWidthCollapse;
    private DoubleAnimation? _cachedThumbHeightCollapse;
    private RectAnimation? _cachedThumbRectCollapse;

    private void ExpandNotch()
    {
        if (_isAnimating || _isExpanded || _isGreetingActive) return;
        _isAnimating = true;
        _notchState.TryTransitionTo(NotchState.Expanding);

        bool suppressCompactThumbnailMotion = IsCountdownCompletionVisualActive;
        if (suppressCompactThumbnailMotion)
        {
            SuppressCompactMediaChromeForCountdownCompletion();
        }
        CancelThumbnailSwitchForExpand();

        NotchBorder.BeginAnimation(WidthProperty, null);
        NotchBorder.BeginAnimation(HeightProperty, null);

        _hoverCollapseTimer.Stop();
        _suppressHoverCollapseUntilUtc = DateTime.UtcNow.AddMilliseconds(800);

        if (_isRewindAnimating)
        {
            _isRewindAnimating = false;
            StopRewindTextAnimation();
            ProgressBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            ProgressBarScale.ScaleX = 0;
            _progressDisplayRatio = 0;
        }

        if (_isVolumeIndicatorActive)
        {
            DismissVolumeIndicatorImmediate();
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

        bool wasHovered = _isCompactThumbnailHovered;
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

        // Capture the compact thumbnail in the overlay's own coordinate space
        // while the notch is still in its real collapsed layout. On the first
        // expansion after startup, the compact grid can settle at a different
        // device-pixel origin than its nominal margin; a hard-coded (0, 0)
        // start then makes the overlay appear offset before snapping into place.
        (double X, double Y) compactThumbnailRestOffset = (0, 0);
        if (_isMusicCompactMode && CompactThumbnail.Source != null && !suppressCompactThumbnailMotion)
        {
            compactThumbnailRestOffset = MeasureCompactThumbnailRestOffset();
        }

        EnsureTopmost();

        double currentWidth = NotchBorder.ActualWidth > 0 ? NotchBorder.ActualWidth : _collapsedWidth;
        double currentHeight = NotchBorder.ActualHeight > 0 ? NotchBorder.ActualHeight : _collapsedHeight;

        NotchBorder.BeginAnimation(WidthProperty, null);
        NotchBorder.BeginAnimation(HeightProperty, null);
        this.BeginAnimation(CurrentCornerRadiusProperty, null);

        NotchBorder.Width = currentWidth;
        NotchBorder.Height = currentHeight;

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

        if (_isLyricsActive && LyricsBlurBackground != null)
        {
            LyricsBlurBackground.BeginAnimation(OpacityProperty, null);
            LyricsBlurBackground.Opacity = 0;
            LyricsBlurBackground.Visibility = Visibility.Collapsed;
        }
        if (_isLyricsActive && LyricsCanvasBackground != null)
        {
            LyricsCanvasBackground.BeginAnimation(OpacityProperty, null);
            LyricsCanvasBackground.Opacity = 0;
        }
        ExpandedContent.Width = _expandedWidth - 16;
        ExpandedContent.Height = _expandedHeight - 10;
        // The cold-open layout has not yet run the media sizing callbacks that
        // establish the final progress/text geometry. Measure that state before
        // deriving the thumbnail endpoint so the first expansion uses the same
        // coordinates as every later expansion.
        PrepareExpandedContentLayoutForReveal();

        AnimateStatusBarReveal(true);

        NotchBorder.IsHitTestVisible = false;
        int animFps = VNotch.Services.AnimationConfig.TargetFps;

        // Land layout geometry at 500 ms, then hold the exact device-pixel frame
        // through the 600 ms lifecycle completion. MainView is centered inside
        // this animated parent; letting both finish on the same tick exposes a
        // final 1 px parity change on fractional-DPI displays.
        var widthAnim = MakeExpandGeometryAnimation(currentWidth, _expandedWidth, _easeExpOut6, animFps);
        var heightAnim = MakeExpandGeometryAnimation(currentHeight, _expandedHeight, _easeExpOut6, animFps);
        var fadeOutAnim = MakeAnim(0, _dur200, _easeQuadOut);

        double contentTargetY = ExpandedContentRestY;
        var expandedGroup = new TransformGroup();
        // Keep the resting value below the animation clock. The held clock stays
        // attached after opening so WPF keeps the exact same pixel-snap mode as
        // the final animated frame; the next transition replaces this group.
        var expandedTranslate = new TranslateTransform(0, contentTargetY);
        expandedGroup.Children.Add(expandedTranslate);
        ExpandedContent.RenderTransform = expandedGroup;
        ExpandedContent.RenderTransformOrigin = new Point(0.5, 0.4);

        var fadeInAnim = MakeAnim(0d, 1d, _dur400, _easePowerOut3);
        var springSlide = MakeAnim(10, contentTargetY, _dur400, _easeExpOut6);

        var glowAnim = MakeAnim(0.15, _dur200);

        double contentBlurRadius = _settings.EnableBlurEffects ? 24 : 0;
        var blurOutAnim = MakeAnim(0, contentBlurRadius, _dur350, _easeQuadIn);
        var blurInAnim = MakeAnim(contentBlurRadius, 0, _dur500, _easePowerOut3);
        ExpandedContentBlur.Radius = contentBlurRadius;

        if (_isMusicCompactMode && CompactThumbnail.Source != null && !suppressCompactThumbnailMotion)
        {
            var cachedExpandTarget = _cachedThumbnailExpandTarget;
            if (!cachedExpandTarget.HasValue)
            {
                double prevNotchWidth = NotchBorder.Width;
                double prevNotchHeight = NotchBorder.Height;
                double prevExpandedWidth = ExpandedContent.Width;
                double prevExpandedHeight = ExpandedContent.Height;

                NotchBorder.Width = _expandedWidth;
                NotchBorder.Height = _expandedHeight;
                ExpandedContent.Width = _expandedWidth - 16;
                ExpandedContent.Height = _expandedHeight - 10;

                UpdateLayout();
                PrepareExpandedContentLayoutForReveal();
                UpdateLayout();

                if (TryComputeThumbnailExpandTarget(out var computedTarget))
                {
                    _cachedThumbnailExpandTarget = computedTarget;
                    cachedExpandTarget = computedTarget;
                }

                NotchBorder.Width = prevNotchWidth;
                NotchBorder.Height = prevNotchHeight;
                ExpandedContent.Width = prevExpandedWidth;
                ExpandedContent.Height = prevExpandedHeight;

                UpdateLayout();
            }

            if (!cachedExpandTarget.HasValue)
            {
                ResetAnimationThumbnailOverlay();
                if (CompactThumbnailBorder != null) CompactThumbnailBorder.Opacity = 1;
                if (ThumbnailBorder != null) ThumbnailBorder.Opacity = 1;
            }
            else
            {
                AnimationThumbnailImage.Source = CompactThumbnail.Source;
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

                // Start owning the overlay on the first rendered frame. A delayed
                // clock exposes the committed final base value before its From
                // value becomes active, producing a final -> start -> final jump
                // on the first expansion after process startup.
                var thumbDur = _dur500;
                // Thumbnail geometry must approach the live border monotonically.
                // The elastic easing overshoots the 102.4 px endpoint to about
                // 110.4 px, covers the title gap, then shrinks back at handoff.
                // That reads as a wrong-position/zoom snap on the cold opening.
                var thumbEase = _easeExpOut6;
                int thumbFps = VNotch.Services.AnimationConfig.TargetFps;
                Size expandedThumbSize = GetExpandedThumbnailAnimationSize();
                double expandedThumbWidth = expandedThumbSize.Width;
                double expandedThumbHeight = expandedThumbSize.Height;
                Thickness compactBorderThickness = CompactThumbnailBorder?.BorderThickness ?? new Thickness(0);
                Thickness expandedBorderThickness = ThumbnailBorder?.BorderThickness ?? new Thickness(0);

                if (_cachedThumbWidthExpand == null ||
                    _cachedThumbWidthExpand.Duration != thumbDur ||
                    Math.Abs((_cachedThumbWidthExpand.To ?? 0) - expandedThumbWidth) > 0.001 ||
                    Math.Abs((_cachedThumbHeightExpand?.To ?? 0) - expandedThumbHeight) > 0.001)
                {
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

                var thumbTranslateXAnim = MakeAnim(compactRestX, targetX, thumbDur, thumbEase, null);
                var thumbTranslateYAnim = MakeAnim(compactRestY, targetY, thumbDur, thumbEase, null);
                var thumbBorderThicknessAnim = new ThicknessAnimation(
                    compactBorderThickness,
                    expandedBorderThickness,
                    thumbDur)
                {
                    // BorderThickness cannot accept the negative overshoot of
                    // the elastic geometry easing.
                    EasingFunction = _easeExpOut6
                };
                Timeline.SetDesiredFrameRate(thumbTranslateXAnim, thumbFps);
                Timeline.SetDesiredFrameRate(thumbTranslateYAnim, thumbFps);
                Timeline.SetDesiredFrameRate(thumbBorderThicknessAnim, thumbFps);

                // Commit the exact live-thumbnail geometry below the explicit
                // clocks. HoldEnd and the later crossfade now share one endpoint.
                AnimationThumbnailBorder.Width = expandedThumbWidth;
                AnimationThumbnailBorder.Height = expandedThumbHeight;
                AnimationThumbnailBorder.BorderThickness = expandedBorderThickness;
                AnimationThumbnailTranslate.X = targetX;
                AnimationThumbnailTranslate.Y = targetY;
                AnimationThumbnailClip.Rect = new Rect(0, 0, expandedThumbWidth, expandedThumbHeight);
                AnimationThumbnailBorder.BeginAnimation(WidthProperty, _cachedThumbWidthExpand);
                AnimationThumbnailBorder.BeginAnimation(HeightProperty, _cachedThumbHeightExpand);
                AnimationThumbnailBorder.BeginAnimation(Border.BorderThicknessProperty, thumbBorderThicknessAnim);
                AnimationThumbnailTranslate.BeginAnimation(TranslateTransform.XProperty, thumbTranslateXAnim);
                AnimationThumbnailTranslate.BeginAnimation(TranslateTransform.YProperty, thumbTranslateYAnim);
                AnimateThumbnailAnimationRadius(6, 14, thumbDur, _easeExpOut6);

                AnimationThumbnailClip.BeginAnimation(RectangleGeometry.RectProperty, _cachedThumbRectExpand);

                if (CompactThumbnailBorder != null)
                {
                    CompactThumbnailBorder.Opacity = 0;
                    CompactThumbnailBorder.Visibility = Visibility.Collapsed;
                }
                if (ThumbnailBorder != null) ThumbnailBorder.Opacity = 0;
            }
        }

        heightAnim.Completed += (s, e) =>
        {
            StopMainViewHorizontalStabilizer();
            _isAnimating = false;
            _isExpanded = true;
            _notchState.TryTransitionTo(NotchState.Expanded);
            NotchBorder.IsHitTestVisible = true;

            RestoreExpandedContentOpacity();

            UpdateProgressTimerState();
            UpdateCalendarInfo();
            ShowMediaBackground();

            if (_settings.EnableBlurEffects && _isLyricsActive && LyricsBlurBackground != null)
            {
                LyricsBlurImage.BeginAnimation(OpacityProperty, null);
                LyricsBlurImage.Opacity = 1;
                LyricsBlurBackground.Visibility = Visibility.Visible;
                LyricsBlurBackground.BeginAnimation(OpacityProperty, null);
                var fadeIn = new DoubleAnimation(0, 0.55, new Duration(TimeSpan.FromMilliseconds(250)))
                {
                    EasingFunction = new ExponentialEase { Exponent = 4, EasingMode = EasingMode.EaseOut }
                };
                System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(fadeIn, VNotch.Services.AnimationConfig.TargetFps);
                LyricsBlurBackground.BeginAnimation(OpacityProperty, fadeIn);
            }

            FadeInSpotifyCanvasBackgroundIfReady();
            ResumeSpotifyCanvasLifecycle();

            if (_isLyricsActive)
            {
                UpdateLyricsDisplay();
            }

            StartProgressCatchUpAnimation();
            RenderProgressBar();

            if (_pendingFlipThumbnail != null)
            {
                // A refined/cropped artwork update often arrives during the
                // first expansion after startup. Starting its blur/scale morph
                // on this exact handoff frame makes the live image move under
                // the overlay. Fold the newest bitmap into the live layer and
                // let the existing overlay dissolve reveal it at fixed geometry.
                var thumb = _pendingFlipThumbnail;
                _pendingFlipThumbnail = null;
                ThumbnailImage.Source = thumb;
                CompactThumbnail.Source = thumb;
            }

            // Do not detach the opacity, size, translation, or expanded blur
            // clocks here. Removing the whole-panel opacity clock makes WPF tear
            // down its compositing layer at the handoff; at fractional DPI that
            // re-snaps every child by a physical pixel.
            // Their HoldEnd presentation is the frame the user just saw. The
            // destination values are already stored underneath them and the next
            // transition safely replaces/clears the clocks while content moves.
            CollapsedContentBlur.BeginAnimation(BlurEffect.RadiusProperty, null);
            CollapsedContentBlur.Radius = 0;
            MusicCompactContentBlur.BeginAnimation(BlurEffect.RadiusProperty, null);
            MusicCompactContentBlur.Radius = 0;

            (double X, double Y)? thumbnailHandoffTarget = null;
            if (_isMusicCompactMode)
            {
                if (TryComputeThumbnailExpandTarget(out var updatedTarget))
                {
                    _cachedThumbnailExpandTarget = updatedTarget;
                    thumbnailHandoffTarget = updatedTarget;
                }
            }

            CrossfadeAnimationThumbnailToExpanded(thumbnailHandoffTarget);
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

            CollapsedContent.Visibility = Visibility.Collapsed;
            MusicCompactContent.Visibility = Visibility.Collapsed;

            if (_settings.ReopenLastViewOnExpand && !_isSecondaryView && !_isTimerView && !_isAudioView)
            {
                if (_lastExpandedViewBeforeCollapse == LastExpandedView.Secondary)
                {
                    SwitchToSecondaryView();
                }
                else if (_lastExpandedViewBeforeCollapse == LastExpandedView.Timer)
                {
                    SwitchToTimerView();
                }
                else if (_lastExpandedViewBeforeCollapse == LastExpandedView.Audio)
                {
                    SwitchToAudioView();
                }
            }
        };

        // Begin stabilization only after the hidden final-layout measurement.
        // Starting it before the cold target probe can bake its temporary
        // centering correction into the cached thumbnail endpoint.
        StartMainViewHorizontalStabilizer(expandedTranslate);

        NotchBorder.BeginAnimation(WidthProperty, widthAnim);
        NotchBorder.BeginAnimation(HeightProperty, heightAnim);
        // Set the destinations as animation bases while the explicit From/To
        // clocks own presentation. HoldEnd can then remain visually stable.
        NotchBorder.Width = _expandedWidth;
        NotchBorder.Height = _expandedHeight;
        CollapsedContent.BeginAnimation(OpacityProperty, fadeOutAnim);
        MusicCompactContent.BeginAnimation(OpacityProperty, fadeOutAnim);

        CollapsedContentBlur.BeginAnimation(BlurEffect.RadiusProperty, blurOutAnim);
        MusicCompactContentBlur.BeginAnimation(BlurEffect.RadiusProperty, blurOutAnim);

        ExpandedContent.BeginAnimation(OpacityProperty, fadeInAnim);
        // Preserve the fully-visible value underneath the HoldEnd clock. The
        // collapse path replaces this clock before it starts moving the panel.
        ExpandedContent.Opacity = 1;
        expandedTranslate.BeginAnimation(TranslateTransform.YProperty, springSlide);

        ExpandedContentBlur.BeginAnimation(BlurEffect.RadiusProperty, blurInAnim);

        HoverGlow.BeginAnimation(OpacityProperty, glowAnim);
        AnimateCornerRadius(_cornerRadiusExpanded, TimeSpan.FromMilliseconds(400));
    }

    private void CollapseNotch()
    {
        if (_isAnimating || !_isExpanded || _isGreetingActive) return;

        StopMainViewHorizontalStabilizer();

        _lastExpandedViewBeforeCollapse = _isAudioView
            ? LastExpandedView.Audio
            : _isTimerView
                ? LastExpandedView.Timer
                : _isSecondaryView
                    ? LastExpandedView.Secondary
                    : LastExpandedView.Primary;

        if (_isSecondaryView)
        {
            StopCameraPreviewForViewExit();
        }
        _isAnimating = true;
        _notchState.TryTransitionTo(NotchState.Collapsing);
        SuspendSpotifyCanvasLifecycle();
        bool suppressCompactThumbnailMotion = IsCountdownCompletionVisualActive;
        if (suppressCompactThumbnailMotion)
        {
            SuppressCompactMediaChromeForCountdownCompletion();
        }
        CancelThumbnailSwitchAnimations();

        if (_isRewindAnimating)
        {
            _isRewindAnimating = false;
            StopRewindTextAnimation();
            ProgressBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            ProgressBarScale.ScaleX = 0;
            _progressDisplayRatio = 0;
        }

        EnsureTopmost();

        AnimateExpandedContentFadeOut();

        AnimateStatusBarReveal(false);

        NavIconsBackground.BeginAnimation(OpacityProperty, null);
        NavIconsBackground.Opacity = 0;
        NavIconsBackground.Visibility = Visibility.Collapsed;
        NavIconsPanel.BeginAnimation(OpacityProperty, null);
        NavIconsPanel.Opacity = 0;
        NavIconsPanel.Visibility = Visibility.Collapsed;

        bool wasSecondary = _isSecondaryView;
        bool wasTimer = _isTimerView;
        bool wasAudio = _isAudioView;
        _isAudioView = false;
        if (wasAudio) { StopAudioPoll(); _audioMixerServiceCached?.ReleaseSessionCache(); }
        if (wasSecondary)
        {
            if (IsCameraPreviewLifecycleActive)
            {
                StopCameraPreviewForViewExit();
            }

            SecondaryContent.BeginAnimation(OpacityProperty, null);
            var secondaryGroup = new TransformGroup();
            var secondaryScale = new ScaleTransform(1, 1);
            var secondaryTranslate = new TranslateTransform(0, 0);
            secondaryGroup.Children.Add(secondaryScale);
            secondaryGroup.Children.Add(secondaryTranslate);
            SecondaryContent.RenderTransform = secondaryGroup;
            SecondaryContent.RenderTransformOrigin = new Point(0.5, 0.5);

            var secFadeOut = MakeAnim(1, 0, _dur200, _easeQuadIn);
            var secSlideDown = MakeAnim(0, 16, _dur250, _easeQuadIn);
            var secScaleDown = MakeAnim(1, 0.93, _dur250, _easeQuadIn);
            Timeline.SetDesiredFrameRate(secSlideDown, VNotch.Services.AnimationConfig.TargetFps);
            Timeline.SetDesiredFrameRate(secScaleDown, VNotch.Services.AnimationConfig.TargetFps);

            secFadeOut.Completed += (s, e) =>
            {
                SecondaryContent.BeginAnimation(OpacityProperty, null);
                SecondaryContent.Opacity = 0;
                SecondaryContent.Visibility = Visibility.Collapsed;
                SecondaryContent.RenderTransform = null;
                TimerContent.Visibility = Visibility.Collapsed;
                TimerContent.Opacity = 0;
            };

            SecondaryContent.BeginAnimation(OpacityProperty, secFadeOut);
            secondaryTranslate.BeginAnimation(TranslateTransform.YProperty, secSlideDown);
            secondaryScale.BeginAnimation(ScaleTransform.ScaleXProperty, secScaleDown);
            secondaryScale.BeginAnimation(ScaleTransform.ScaleYProperty, secScaleDown);

            _isSecondaryView = false;
            _isTimerView = false;
        }
        else
        {
            SecondaryContent.BeginAnimation(OpacityProperty, null);
            _isTimerView = false;
        }

        if (wasTimer)
        {
            if (IsCameraPreviewLifecycleActive)
            {
                StopCameraPreviewForViewExit();
            }

            AnimateTimerContentFadeOut();

            TimerContent.BeginAnimation(OpacityProperty, null);

            var timerGroup = new TransformGroup();
            var timerScale = new ScaleTransform(1, 1);
            var timerTranslate = new TranslateTransform(0, 0);
            timerGroup.Children.Add(timerScale);
            timerGroup.Children.Add(timerTranslate);
            TimerContent.RenderTransform = timerGroup;
            TimerContent.RenderTransformOrigin = new Point(0.5, 0.5);

            var timerFadeOut = MakeAnim(TimerContent.Opacity, 0, _dur200, _easeQuadIn, TimeSpan.FromMilliseconds(60));
            var timerSlideDown = MakeAnim(0, 12, _dur250, _easeQuadIn, TimeSpan.FromMilliseconds(60));
            var timerScaleDown = MakeAnim(1, 0.95, _dur250, _easeQuadIn, TimeSpan.FromMilliseconds(60));
            var timerBlur = TimerContent.Effect as BlurEffect ?? new BlurEffect { Radius = 0, RenderingBias = RenderingBias.Performance };
            TimerContent.Effect = timerBlur;
            var timerBlurOut = MakeAnim(timerBlur.Radius, 10, _dur200, _easeQuadIn, TimeSpan.FromMilliseconds(60));
            Timeline.SetDesiredFrameRate(timerFadeOut, VNotch.Services.AnimationConfig.TargetFps);
            Timeline.SetDesiredFrameRate(timerSlideDown, VNotch.Services.AnimationConfig.TargetFps);
            Timeline.SetDesiredFrameRate(timerScaleDown, VNotch.Services.AnimationConfig.TargetFps);

            timerFadeOut.Completed += (s, e) =>
            {
                TimerContent.BeginAnimation(OpacityProperty, null);
                TimerContent.Opacity = 0;
                TimerContent.Visibility = Visibility.Collapsed;
                TimerContent.RenderTransform = null;
                TimerContent.Effect = null;
                timerBlur.Radius = 0;
            };

            TimerContent.BeginAnimation(OpacityProperty, timerFadeOut);
            timerTranslate.BeginAnimation(TranslateTransform.YProperty, timerSlideDown);
            timerScale.BeginAnimation(ScaleTransform.ScaleXProperty, timerScaleDown);
            timerScale.BeginAnimation(ScaleTransform.ScaleYProperty, timerScaleDown);
            timerBlur.BeginAnimation(BlurEffect.RadiusProperty, timerBlurOut);
        }

        if (wasAudio)
        {
            AudioContent.BeginAnimation(OpacityProperty, null);
            var audioFadeOut = MakeAnim(AudioContent.Opacity, 0, _dur200, _easeQuadIn);
            var audioBlur = AudioContent.Effect as BlurEffect ?? new BlurEffect { Radius = 0, RenderingBias = RenderingBias.Performance };
            AudioContent.Effect = audioBlur;
            var audioBlurOut = MakeAnim(audioBlur.Radius, 10, _dur200, _easeQuadIn);
            Timeline.SetDesiredFrameRate(audioFadeOut, VNotch.Services.AnimationConfig.TargetFps);

            audioFadeOut.Completed += (s, e) =>
            {
                AudioContent.BeginAnimation(OpacityProperty, null);
                AudioContent.Opacity = 0;
                AudioContent.Visibility = Visibility.Collapsed;
                AudioContent.RenderTransform = null;
                AudioContent.Effect = null;
                audioBlur.Radius = 0;
            };

            AudioContent.BeginAnimation(OpacityProperty, audioFadeOut);
            audioBlur.BeginAnimation(BlurEffect.RadiusProperty, audioBlurOut);
        }

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

        NotchBorder.IsHitTestVisible = false;
        int animFps = VNotch.Services.AnimationConfig.TargetFps;

        var widthAnim = MakeAnim(_collapsedWidth, _dur500, _easeExpOut6, animFps);
        var heightAnim = MakeAnim(_collapsedHeight, _dur500, _easeExpOut6, animFps);

        var expandedGroup = new TransformGroup();
        var expandedTranslate = new TranslateTransform(0, ExpandedContentRestY);
        expandedGroup.Children.Add(expandedTranslate);
        ExpandedContent.RenderTransform = expandedGroup;
        ExpandedContent.RenderTransformOrigin = new Point(0.5, 0.4);

        var fadeOutAnim = MakeAnim(0, _dur200, _easeQuadOut);
        double currentY = ExpandedContentRestY;
        var slideOutAnim = MakeAnim(currentY, -10, _dur400, _easeExpOut6);

        fadeOutAnim.Completed += (s, e) =>
        {
            ExpandedContent.BeginAnimation(OpacityProperty, null);
            ExpandedContent.Opacity = 0;
            ExpandedContent.Visibility = Visibility.Collapsed;
            ExpandedContent.RenderTransform = null;

            if (!wasSecondary)
            {
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
        };

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

        double contentBlurRadius = _settings.EnableBlurEffects ? 24 : 0;
        var blurOutAnim = MakeAnim(0, contentBlurRadius, _dur350, _easeQuadIn);
        var blurInAnim = MakeAnim(contentBlurRadius, 0, _dur500, _easePowerOut3);

        CollapsedContentBlur.Radius = contentBlurRadius;
        MusicCompactContentBlur.Radius = contentBlurRadius;

        if (_isMusicCompactMode && ThumbnailImage.Source != null && !suppressCompactThumbnailMotion)
        {
            if (CompactThumbnailBorder != null)
            {
                CompactThumbnailBorder.Opacity = 0;
                CompactThumbnailBorder.Visibility = Visibility.Hidden;
            }
            if (ThumbnailBorder != null) ThumbnailBorder.Opacity = 0;

            if (!_cachedThumbnailExpandTarget.HasValue &&
                TryComputeThumbnailExpandTarget(out var measuredTarget))
            {
                _cachedThumbnailExpandTarget = measuredTarget;
            }

            var cachedExpandTarget = _cachedThumbnailExpandTarget;
            if (cachedExpandTarget.HasValue)
            {
                var (startX, startY) = cachedExpandTarget.Value;
                Size expandedThumbSize = GetExpandedThumbnailAnimationSize();
                double expandedThumbWidth = expandedThumbSize.Width;
                double expandedThumbHeight = expandedThumbSize.Height;

                AnimationThumbnailImage.Source = ThumbnailImage.Source;
                AnimationThumbnailBorder.Visibility = Visibility.Visible;
                AnimationThumbnailBorder.Opacity = 1;
                AnimationThumbnailBorder.CornerRadius = new CornerRadius(14);
                AnimationThumbnailClip.RadiusX = 14;
                AnimationThumbnailClip.RadiusY = 14;
                AnimationThumbnailBorder.Width = expandedThumbWidth;
                AnimationThumbnailBorder.Height = expandedThumbHeight;
                AnimationThumbnailClip.Rect = new Rect(0, 0, expandedThumbWidth, expandedThumbHeight);
                AnimationThumbnailTranslate.X = startX;
                AnimationThumbnailTranslate.Y = startY;

                var thumbDelay = TimeSpan.FromMilliseconds(30);
                var thumbDur = _dur500;
                var thumbEase = _easeThumbSpring;
                int thumbFps = VNotch.Services.AnimationConfig.TargetFps;

                if (_cachedThumbWidthCollapse == null ||
                    _cachedThumbWidthCollapse.Duration != thumbDur ||
                    Math.Abs((_cachedThumbWidthCollapse.From ?? 0) - expandedThumbWidth) > 0.001 ||
                    Math.Abs((_cachedThumbHeightCollapse?.From ?? 0) - expandedThumbHeight) > 0.001)
                {
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
            }
        }

        heightAnim.Completed += (s, e) =>
        {
            _isAnimating = false;
            _isExpanded = false;
            _notchState.TryTransitionTo(NotchState.Collapsed);
            NotchBorder.IsHitTestVisible = true;
            NotchBorder.BeginAnimation(WidthProperty, null);
            NotchBorder.BeginAnimation(HeightProperty, null);
            NotchBorder.Width = _collapsedWidth;
            NotchBorder.Height = _collapsedHeight;

            if (wasTimer || wasAudio)
            {
                RestoreExpandedWindowSize();
            }
            if (wasAudio)
            {
                RestorePrivacyDotVisibility();
            }

            contentToShow.RenderTransform = null;

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

            ExpandedContentBlur.BeginAnimation(BlurEffect.RadiusProperty, null);
            ExpandedContentBlur.Radius = 0;
            CollapsedContentBlur.BeginAnimation(BlurEffect.RadiusProperty, null);
            CollapsedContentBlur.Radius = 0;
            MusicCompactContentBlur.BeginAnimation(BlurEffect.RadiusProperty, null);
            MusicCompactContentBlur.Radius = 0;

            if (_isMusicCompactMode)
            {
                contentToShow.Opacity = 1;
                contentToShow.BeginAnimation(OpacityProperty, null);
                contentToShow.RenderTransform = null;

                ResetAnimationThumbnailOverlay();

                ResetCompactThumbnailRestingState();
                CompactHoverInfo.BeginAnimation(OpacityProperty, null);
                CompactHoverInfo.Opacity = 0;
                CompactHoverInfo.Visibility = Visibility.Collapsed;
            }

            if (CompactThumbnailBorder != null && !_isClipboardPeekActive && !_isVolumeIndicatorActive && !suppressCompactThumbnailMotion)
            {
                CompactThumbnailBorder.BeginAnimation(OpacityProperty, null);
                CompactThumbnailBorder.Visibility = Visibility.Visible;
                CompactThumbnailBorder.Opacity = 1;
            }
            else if (suppressCompactThumbnailMotion)
            {
                SuppressCompactMediaChromeForCountdownCompletion();
            }
            if (ThumbnailBorder != null) ThumbnailBorder.Opacity = 1;

            if (_isMusicCompactMode && _currentMediaInfo?.IsPlaying == true && !_isClipboardPeekActive && !_isVolumeIndicatorActive && !suppressCompactThumbnailMotion)
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

        };

        NotchBorder.BeginAnimation(WidthProperty, widthAnim);
        NotchBorder.BeginAnimation(HeightProperty, heightAnim);

        ExpandedContent.BeginAnimation(OpacityProperty, fadeOutAnim);
        expandedTranslate.BeginAnimation(TranslateTransform.YProperty, slideOutAnim);

        ExpandedContentBlur.BeginAnimation(BlurEffect.RadiusProperty, blurOutAnim);

        if (SecondaryContent.Visibility == Visibility.Visible)
        {
            SecondaryContent.BeginAnimation(OpacityProperty, fadeOutAnim);

        }

        contentToShow.Visibility = Visibility.Visible;
        contentToShow.BeginAnimation(OpacityProperty, fadeInAnim);
        showScale.BeginAnimation(ScaleTransform.ScaleXProperty, springShow);
        showScale.BeginAnimation(ScaleTransform.ScaleYProperty, springShow);

        var compactBlurTarget = _isMusicCompactMode ? MusicCompactContentBlur : CollapsedContentBlur;
        compactBlurTarget.BeginAnimation(BlurEffect.RadiusProperty, blurInAnim);

        HoverGlow.BeginAnimation(OpacityProperty, glowAnim);
        AnimateCornerRadius(_cornerRadiusCollapsed, TimeSpan.FromMilliseconds(400));
    }

    #endregion

}

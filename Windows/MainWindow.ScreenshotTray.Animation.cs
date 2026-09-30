using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch;

public partial class MainWindow
{
    private static readonly Duration ScreenshotThumbnailDuration = new(TimeSpan.FromMilliseconds(650));
    private Rectangle? _screenshotMorph;
    private bool _screenshotMorphReturning;
    private bool _screenshotMorphRunning;
    private bool _screenshotCompactHandoffPending;
    private int _screenshotMorphVersion;

    private Rect GetScreenshotCompactThumbnailBounds()
    {
        // Use the same rest slot as the media thumbnail, in both notch modes.
        ConfigureAnimationThumbnailRestSlot();
        var inset = AnimationThumbnailBorder.Margin;
        return new Rect(inset.Left, inset.Top, 22, 22);
    }

    private void AlignScreenshotCompactThumbnail()
    {
        if (_screenshotCompact == null || _screenshotThumbnail == null) return;
        var bounds = GetScreenshotCompactThumbnailBounds();
        _screenshotCompact.Margin = new Thickness(bounds.Left, 0, bounds.Left, 0);
        _screenshotThumbnail.VerticalAlignment = VerticalAlignment.Top;
        _screenshotThumbnail.Margin = new Thickness(0, bounds.Top, 0, 0);
    }

    private void BeginScreenshotThumbnailExpand()
    {
        Rect? liveBounds = _screenshotMorph?.Visibility == Visibility.Visible &&
            _screenshotMorph.RenderTransform is TranslateTransform liveOffset
            ? new Rect(liveOffset.X, liveOffset.Y, _screenshotMorph.Width, _screenshotMorph.Height) : null;
        double radius = liveBounds.HasValue ? _screenshotMorph!.RadiusX : 6;
        EndScreenshotThumbnailMorph();
        if (Services.AnimationConfig.ReduceMotion || _screenshotHost == null ||
            _screenshotThumbnail == null || _screenshotTray == null || _activeScreenshot == null ||
            _screenshotThumbnail.ActualWidth <= 0) return;

        var start = liveBounds ?? _screenshotThumbnail.TransformToAncestor(_screenshotHost)
            .TransformBounds(new Rect(_screenshotThumbnail.RenderSize));
        var target = _screenshotTray.PreviewBounds;
        if (target.IsEmpty || target.Width <= 0 || target.Height <= 0) return;
        AnimateScreenshotThumbnail(start, target, radius, 12, ScreenshotThumbnailDuration);
    }

    private void BeginScreenshotThumbnailCollapse()
    {
        if (!IsScreenshotPillActive || Services.AnimationConfig.ReduceMotion ||
            _screenshotTray == null || _activeScreenshot == null)
        {
            EndScreenshotThumbnailMorph();
            return;
        }
        // Read the live overlay before cancelling its clocks so reversal during
        // expansion starts at the current position and scale, without a jump.
        var start = _screenshotTray.PreviewBounds;
        double radius = 12;
        if (_screenshotMorph?.Visibility == Visibility.Visible &&
            _screenshotMorph.RenderTransform is TranslateTransform offset)
        {
            start = new Rect(offset.X, offset.Y, _screenshotMorph.Width, _screenshotMorph.Height);
            radius = _screenshotMorph.RadiusX;
        }
        EndScreenshotThumbnailMorph();
        if (start.IsEmpty || start.Width <= 0 || start.Height <= 0) return;
        _screenshotMorphReturning = true;
        var target = GetScreenshotCompactThumbnailBounds();
        AnimateScreenshotThumbnail(start, target, radius, 6, ScreenshotThumbnailDuration);
    }

    private void AnimateScreenshotThumbnail(Rect start, Rect target, double fromRadius, double toRadius, Duration duration)
    {

        // Keep one image above the fading content throughout the shell expansion.
        // UniformToFill gradually reveals the capture as its square crop expands.
        if (_screenshotMorph == null)
        {
            _screenshotMorph = new Rectangle
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                IsHitTestVisible = false
            };
            Panel.SetZIndex(_screenshotMorph, 10);
            RenderOptions.SetBitmapScalingMode(_screenshotMorph, BitmapScalingMode.HighQuality);
            _screenshotHost!.Children.Add(_screenshotMorph);
        }
        var offset = new TranslateTransform();
        _screenshotMorph.RenderTransform = offset;
        _screenshotMorph.Fill = new ImageBrush(_activeScreenshot) { Stretch = Stretch.UniformToFill };
        _screenshotMorph.Visibility = Visibility.Visible;
        _screenshotTray!.SetPreviewMorphing(true);
        // Match AnimateThumbnailExpandOverlay: spring for geometry, exponential
        // easing for corner rounding, using the media thumbnail animation factory.
        int fps = Services.AnimationConfig.TargetFps;
        var geometryEase = _easeThumbSpring;
        var delay = _screenshotMorphReturning ? TimeSpan.FromMilliseconds(36) : TimeSpan.Zero;
        // Reuse the same cached width/height timelines as the media thumbnail.
        System.Windows.Media.Animation.DoubleAnimation width;
        System.Windows.Media.Animation.DoubleAnimation height;
        if (_screenshotMorphReturning)
        {
            EnsureCollapseThumbnailAnimations(start.Width, start.Height, duration, geometryEase, delay, fps);
            width = _cachedThumbWidthCollapse!.Clone();
            height = _cachedThumbHeightCollapse!.Clone();
        }
        else
        {
            EnsureExpandThumbnailAnimations(target.Width, target.Height, duration, geometryEase, fps);
            width = _cachedThumbWidthExpand!.Clone();
            height = _cachedThumbHeightExpand!.Clone();
            width.From = start.Width;
            height.From = start.Height;
        }
        // A delayed animation displays base values before its clock starts.
        // Keep the live starting frame here, never the compact destination.
        _screenshotMorph.Width = start.Width;
        _screenshotMorph.Height = start.Height;
        _screenshotMorph.RadiusX = _screenshotMorph.RadiusY = fromRadius;
        offset.X = start.X;
        offset.Y = start.Y;
        _screenshotMorph.BeginAnimation(WidthProperty, width);
        _screenshotMorph.BeginAnimation(HeightProperty, height);
        _screenshotMorph.BeginAnimation(Rectangle.RadiusXProperty, WithFps(MakeAnim(fromRadius, toRadius, duration, _easeExpOut6, delay)));
        _screenshotMorph.BeginAnimation(Rectangle.RadiusYProperty, WithFps(MakeAnim(fromRadius, toRadius, duration, _easeExpOut6, delay)));
        offset.BeginAnimation(TranslateTransform.XProperty, WithFps(MakeAnim(start.X, target.X, duration, geometryEase, delay)));
        var moveY = WithFps(MakeAnim(start.Y, target.Y, duration, geometryEase, delay));
        int version = ++_screenshotMorphVersion;
        _screenshotMorphRunning = true;
        moveY.Completed += (_, _) =>
        {
            if (version != _screenshotMorphVersion) return;
            _screenshotMorphRunning = false;
            if (_screenshotCompactHandoffPending) ShowScreenshotCompactContent();
        };
        offset.BeginAnimation(TranslateTransform.YProperty, moveY);
    }

    private void EndScreenshotThumbnailMorph()
    {
        ++_screenshotMorphVersion;
        _screenshotMorphRunning = false;
        _screenshotCompactHandoffPending = false;
        _screenshotMorphReturning = false;
        _screenshotTray?.SetPreviewMorphing(false);
        if (_screenshotMorph == null) return;
        _screenshotMorph.Visibility = Visibility.Collapsed;
        _screenshotMorph.Fill = null;
        _screenshotMorph.BeginAnimation(WidthProperty, null);
        _screenshotMorph.BeginAnimation(HeightProperty, null);
        _screenshotMorph.BeginAnimation(Rectangle.RadiusXProperty, null);
        _screenshotMorph.BeginAnimation(Rectangle.RadiusYProperty, null);
        if (_screenshotMorph.RenderTransform is TranslateTransform offset)
        {
            offset.BeginAnimation(TranslateTransform.XProperty, null);
            offset.BeginAnimation(TranslateTransform.YProperty, null);
        }
        _screenshotMorph.RenderTransform = null;
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using VNotch.Services;

namespace VNotch.Controls;

public partial class ClipboardTray
{
    private readonly Dictionary<FrameworkElement, object> _pendingMenuEntrances = [];
    private readonly Dictionary<ContextMenu, FrameworkElement> _menuAnimationTargets = [];
    private static bool MenuKeyboardInput => InputManager.Current.MostRecentInputDevice is KeyboardDevice;

    private static readonly BackEase AppleSpring = CreateAppleSpring();

    private static BackEase CreateAppleSpring()
    {
        var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.32 };
        ease.Freeze();
        return ease;
    }

    private void QueueMenuEntrance(FrameworkElement surface, FrameworkElement? target, Func<bool> isOpen, bool fresh, bool submenu = false)
    {
        if (target == null && MenuKeyboardInput)
        {
            _pendingMenuEntrances.Remove(surface);
            ResetMenuSurface(surface);
            return;
        }
        var request = new object();
        _pendingMenuEntrances[surface] = request;
        if (fresh)
        {
            surface.BeginAnimation(OpacityProperty, null);
            surface.Opacity = 0;
        }
        // Wait for popup placement and its first layout. Starting earlier changes
        // the scale origin after the first visible frame and makes the panel jump.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (!_pendingMenuEntrances.TryGetValue(surface, out var current) || !ReferenceEquals(current, request)) return;
            _pendingMenuEntrances.Remove(surface);
            if (!_disposed && isOpen()) AnimateMenuSurface(surface, target, fresh, submenu);
        }));
    }

    private void CancelMenuEntrance(FrameworkElement surface)
    {
        _pendingMenuEntrances.Remove(surface);
        ResetMenuSurface(surface);
    }

    private static void ConfigureMenuDismissal(Popup popup)
    {
        // Native fade releases capture and disables hit testing immediately, then
        // retains the popup window just long enough to finish its dismissal.
        popup.PopupAnimation = !AnimationConfig.ReduceMotion ? PopupAnimation.Fade : PopupAnimation.None;
    }

    private static void AnimateMenuSurface(FrameworkElement surface, FrameworkElement? target, bool fresh, bool submenu = false)
    {
        double opacity = fresh ? 0 : surface.Opacity;
        var rendered = surface.RenderTransform.Value;
        bool isAnchored = target != null;
        double scaleFrom = fresh ? (isAnchored ? (submenu ? 0.88 : 0.80) : (submenu ? 0.98 : 0.96)) : rendered.M11;
        surface.BeginAnimation(OpacityProperty, null);
        surface.Opacity = 1;
        surface.RenderTransform = Transform.Identity;

        if (target == null && MenuKeyboardInput) return;

        bool reduced = AnimationConfig.ReduceMotion;
        int duration = reduced ? 80 : isAnchored ? 260 : (submenu ? MotionStandard : 200);
        if (!reduced)
        {
            Point anchor = fresh ? MenuAnchor(surface, target) : surface.RenderTransformOrigin;
            surface.RenderTransformOrigin = anchor;
            // Completed or replaced clocks restore the settled dimensions.
            var scale = new ScaleTransform(1, 1);
            var slide = new TranslateTransform();
            surface.RenderTransform = new TransformGroup { Children = { scale, slide } };

            // Travel towards the settled position from the trigger-facing edge with authentic Apple spring physics
            double fromX = fresh ? (isAnchored ? (anchor.X <= 0.5 ? -14 : 14) : (anchor.X - 0.5) * 8) : rendered.OffsetX;
            double fromY = fresh ? (isAnchored ? (anchor.Y <= 0.5 ? -10 : 10) : (anchor.Y - 0.5) * 8) : rendered.OffsetY;

            var ease = isAnchored ? (IEasingFunction)AppleSpring : TrayMotion.EaseOut;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, MenuAnimation(scaleFrom, 1, duration, ease));
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, MenuAnimation(scaleFrom, 1, duration, ease));
            slide.BeginAnimation(TranslateTransform.XProperty, MenuAnimation(fromX, 0, duration, ease));
            slide.BeginAnimation(TranslateTransform.YProperty, MenuAnimation(fromY, 0, duration, ease));
        }

        var opacityEase = isAnchored ? (IEasingFunction)new CubicEase { EasingMode = EasingMode.EaseOut } : TrayMotion.EaseOut;
        surface.BeginAnimation(OpacityProperty, MenuAnimation(opacity, 1, reduced ? 80 : isAnchored ? 150 : Math.Min(duration, MotionFast), opacityEase), HandoffBehavior.SnapshotAndReplace);
    }

    private static Point MenuAnchor(FrameworkElement surface, FrameworkElement? target)
    {
        // Resolve after placement so menus flipped above/left of the trigger still
        // grow from the correct edge, including across displays with different DPI.
        if (target == null || surface.ActualWidth <= 0 || surface.ActualHeight <= 0 ||
            PresentationSource.FromVisual(surface) == null || PresentationSource.FromVisual(target) == null)
            return new Point(0, 0.05);
        try
        {
            Point trigger = target.PointToScreen(new Point(target.ActualWidth / 2, target.ActualHeight / 2));
            Point anchor = surface.PointFromScreen(trigger);
            return new Point(Math.Clamp(anchor.X / surface.ActualWidth, 0, 1), Math.Clamp(anchor.Y / surface.ActualHeight, 0, 1));
        }
        catch (InvalidOperationException)
        {
            return new Point(0, 0.05);
        }
    }

    private static DoubleAnimation MenuAnimation(double from, double to, int duration, IEasingFunction? easing = null)
    {
        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(duration))
        {
            EasingFunction = easing ?? TrayMotion.EaseOut,
            FillBehavior = FillBehavior.Stop
        };
        Timeline.SetDesiredFrameRate(animation, AnimationConfig.TargetFps);
        return animation;
    }

    internal static void AnimateTriggerHaptic(Button? trigger)
    {
        if (trigger == null || AnimationConfig.ReduceMotion) return;
        if (trigger.Template?.FindName("HoverScale", trigger) is ScaleTransform st)
        {
            var keyframes = new DoubleAnimationUsingKeyFrames();
            keyframes.KeyFrames.Add(new SplineDoubleKeyFrame(0.86, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(60)), new KeySpline(0.1, 0.9, 0.2, 1.0)));
            keyframes.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(240)), new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.45 }));
            Timeline.SetDesiredFrameRate(keyframes, AnimationConfig.TargetFps);
            st.BeginAnimation(ScaleTransform.ScaleXProperty, keyframes);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, keyframes);
        }
    }

    private static void ResetMenuSurface(FrameworkElement surface)
    {
        surface.BeginAnimation(OpacityProperty, null);
        surface.Opacity = 1;
        surface.RenderTransform = Transform.Identity;
    }

    private void Submenu_Opened(object sender, EventArgs e)
    {
        if (sender is not Popup { Child: FrameworkElement child } popup) return;
        if (popup.TemplatedParent is MenuItem item && item.Template.FindName("SubmenuSurface", item) is FrameworkElement surface)
            QueueMenuEntrance(surface, popup.PlacementTarget as FrameworkElement, () => popup.IsOpen,
                surface.RenderTransform is not TransformGroup, submenu: true);
        ConfigureMenuDismissal(popup);
        EnsureMenuTopmost(child);
    }

    private void Submenu_Closed(object sender, EventArgs e)
    {
        if (sender is not Popup popup) return;
        if (popup.TemplatedParent is MenuItem item && item.Template.FindName("SubmenuSurface", item) is FrameworkElement surface)
            CancelMenuEntrance(surface);
        popup.PopupAnimation = PopupAnimation.None;
    }

    private void MenuItem_Pressed(object sender, RoutedEventArgs e) => SetMenuRowOpacity(sender, 0.7);
    private void MenuItem_Released(object sender, RoutedEventArgs e) => SetMenuRowOpacity(sender, 1);

    private static void SetMenuRowOpacity(object sender, double opacity)
    {
        if (sender is MenuItem item && item.Template?.FindName("MenuRow", item) is Border row)
            TrayMotion.SetOpacity(row, opacity);
    }
}

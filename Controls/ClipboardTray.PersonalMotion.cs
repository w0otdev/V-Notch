using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VNotch.Services;

namespace VNotch.Controls;

public sealed partial class ClipboardTray
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct VnCategoryPillMotionNative
    {
        public double StartScaleX;
        public double PeakScaleX;
        public double SettleOvershootScaleX;
        public double DipScaleY;
        public double ReboundScaleY;
        public double ContentPopScale;
        public double GlowPeakOpacity;
        public int PeakTimeMs;
        public int SettleOvershootTimeMs;
        public int DurationMs;
    }

    [LibraryImport("VNotch.Native")]
    internal static partial int vn_animation_compute_category_pill_motion(
        double currentX,
        double currentWidth,
        double targetX,
        double targetWidth,
        byte reducedMotion,
        out VnCategoryPillMotionNative outMotion);

    private Rect _categorySelectionBounds = Rect.Empty;
    private string? _animatedCategoryName;

    private void CategoryPanel_LayoutUpdated(object? sender, EventArgs e)
    {
        if (_categoryReordering) { CategorySelection.Opacity = 0; return; }
        if (_disposed || !IsVisible || !CategoryPanel.IsArrangeValid) return;
        var selected = _categoryItems.FirstOrDefault(item => item.IsSelected);
        if (selected == null || CategoryPanel.ItemContainerGenerator.ContainerFromItem(selected) is not ContentPresenter presenter ||
            VisualTreeHelper.GetChildrenCount(presenter) == 0 || VisualTreeHelper.GetChild(presenter, 0) is not Button button ||
            button.ActualWidth <= 0) return;

        if (CategorySelection.RenderTransform is TransformGroup transformGroup &&
            !transformGroup.IsFrozen &&
            !transformGroup.Children.Contains(_categoryElasticTransform))
        {
            transformGroup.Children.Add(_categoryElasticTransform);
        }

        var pos = button.TranslatePoint(new Point(), CategoryPanel);
        var bounds = new Rect(pos, button.RenderSize);
        TrayMotion.SetBackground(CategorySelection, button.Background);
        CategorySelection.Opacity = 1;
        if (bounds == _categorySelectionBounds && _animatedCategoryName == selected.Name) return;

        bool categoryChanged = _animatedCategoryName != null && _animatedCategoryName != selected.Name;
        bool animate = !_categorySelectionBounds.IsEmpty && !AnimationConfig.ReduceMotion && !_trayLeaving;
        _animatedCategoryName = selected.Name;
        _categorySelectionBounds = bounds;

        double currentX = CategorySelectionOffset.X;
        double renderedWidth = CategorySelection.Width * CategorySelectionScale.ScaleX;
        CategorySelection.Width = bounds.Width;
        CategorySelectionScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        CategorySelectionScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);

        if (vn_animation_compute_category_pill_motion(
                currentX,
                renderedWidth,
                bounds.X,
                bounds.Width,
                animate ? (byte)0 : (byte)1,
                out var motion) < 0)
        {
            CategorySelectionScale.ScaleX = 1;
            CategorySelectionScale.ScaleY = 1;
            MoveCategoryIndicator(CategorySelectionOffset, TranslateTransform.XProperty, bounds.X, false);
            return;
        }

        CategorySelectionScale.ScaleX = animate ? motion.StartScaleX : 1;
        CategorySelectionScale.ScaleY = 1;
        MoveCategoryIndicator(CategorySelectionOffset, TranslateTransform.XProperty, bounds.X, animate);
        AnimateCategoryPillMorph(motion, animate);

        if (categoryChanged)
        {
            AnimateSelectedCategoryContent(button, motion.ContentPopScale, animate);
            EnsureCategoryButtonVisible(bounds, animate);
        }
    }

    private void AnimateCategoryPillMorph(in VnCategoryPillMotionNative motion, bool animate)
    {
        CategorySelectionPulse?.BeginAnimation(OpacityProperty, null);
        if (!animate || motion.DurationMs <= 0)
        {
            CategorySelectionScale.ScaleX = 1;
            CategorySelectionScale.ScaleY = 1;
            if (CategorySelectionPulse != null) CategorySelectionPulse.Opacity = 0;
            return;
        }

        var scaleXFrames = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.HoldEnd };
        scaleXFrames.KeyFrames.Add(new DiscreteDoubleKeyFrame(motion.StartScaleX, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        scaleXFrames.KeyFrames.Add(new EasingDoubleKeyFrame(motion.PeakScaleX, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(motion.PeakTimeMs)), TrayMotion.EaseOut));
        scaleXFrames.KeyFrames.Add(new EasingDoubleKeyFrame(motion.SettleOvershootScaleX, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(motion.SettleOvershootTimeMs)), TrayMotion.EaseOut));
        scaleXFrames.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(motion.DurationMs)), TrayMotion.EaseOut));
        Timeline.SetDesiredFrameRate(scaleXFrames, AnimationConfig.TargetFps);
        CategorySelectionScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleXFrames, HandoffBehavior.SnapshotAndReplace);

        var scaleYFrames = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.HoldEnd };
        scaleYFrames.KeyFrames.Add(new DiscreteDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        scaleYFrames.KeyFrames.Add(new EasingDoubleKeyFrame(motion.DipScaleY, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(motion.PeakTimeMs)), TrayMotion.EaseOut));
        scaleYFrames.KeyFrames.Add(new EasingDoubleKeyFrame(motion.ReboundScaleY, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(motion.SettleOvershootTimeMs)), TrayMotion.EaseOut));
        scaleYFrames.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(motion.DurationMs)), TrayMotion.EaseOut));
        Timeline.SetDesiredFrameRate(scaleYFrames, AnimationConfig.TargetFps);
        CategorySelectionScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleYFrames, HandoffBehavior.SnapshotAndReplace);

        if (CategorySelectionPulse != null)
        {
            var pulseFrames = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
            pulseFrames.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            pulseFrames.KeyFrames.Add(new EasingDoubleKeyFrame(motion.GlowPeakOpacity, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(70)), TrayMotion.EaseOut));
            pulseFrames.KeyFrames.Add(new EasingDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(motion.DurationMs)), TrayMotion.EaseOut));
            Timeline.SetDesiredFrameRate(pulseFrames, AnimationConfig.TargetFps);
            CategorySelectionPulse.Opacity = 0;
            CategorySelectionPulse.BeginAnimation(OpacityProperty, pulseFrames, HandoffBehavior.SnapshotAndReplace);
        }
    }

    private static void AnimateSelectedCategoryContent(Button button, double peakScale, bool animate)
    {
        if (button.Template?.FindName("ContentHost", button) is not FrameworkElement contentHost) return;
        if (contentHost.RenderTransform is not TransformGroup group || group.IsFrozen ||
            group.Children.OfType<ScaleTransform>().FirstOrDefault() is not { } scale)
        {
            return;
        }

        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        scale.ScaleX = 1;
        scale.ScaleY = 1;
        if (!animate || peakScale <= 1.0) return;

        var pop = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        pop.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.92, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        pop.KeyFrames.Add(new EasingDoubleKeyFrame(peakScale, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(115)), TrayMotion.EaseOut));
        pop.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(245)), TrayMotion.EaseOut));
        Timeline.SetDesiredFrameRate(pop, AnimationConfig.TargetFps);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, pop, HandoffBehavior.SnapshotAndReplace);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, pop, HandoffBehavior.SnapshotAndReplace);
    }

    private void EnsureCategoryButtonVisible(Rect bounds, bool animate)
    {
        if (CategoryScroll == null || CategoryScroll.ViewportWidth <= 0 || CategoryScroll.ScrollableWidth <= 0) return;
        const double edgePadding = 24.0;
        double viewLeft = CategoryScroll.HorizontalOffset;
        double viewRight = viewLeft + CategoryScroll.ViewportWidth;
        double targetOffset = viewLeft;

        if (bounds.Left - edgePadding < viewLeft)
            targetOffset = Math.Max(0, bounds.Left - edgePadding);
        else if (bounds.Right + edgePadding > viewRight)
            targetOffset = Math.Min(CategoryScroll.ScrollableWidth, bounds.Right + edgePadding - CategoryScroll.ViewportWidth);

        if (Math.Abs(targetOffset - viewLeft) < 0.5) return;
        _categoryScrollTarget = targetOffset;
        if (!animate || AnimationConfig.ReduceMotion)
        {
            CategoryScroll.ScrollToHorizontalOffset(targetOffset);
            StopCategoryScrollFrames();
            return;
        }
        StartCategoryScrollFrames();
    }

    private static void MoveCategoryIndicator(Animatable target, DependencyProperty property, double value, bool animate)
    {
        double current = (double)target.GetValue(property);
        target.BeginAnimation(property, null);
        // Keep the rendered value until WPF ticks the replacement clock.
        target.SetValue(property, animate ? current : value);
        if (!animate) return;
        var motion = new DoubleAnimation(current, value, TimeSpan.FromMilliseconds(280))
        { EasingFunction = TrayMotion.EaseOut, FillBehavior = FillBehavior.HoldEnd };
        Timeline.SetDesiredFrameRate(motion, AnimationConfig.TargetFps);
        target.BeginAnimation(property, motion, HandoffBehavior.SnapshotAndReplace);
    }

    private void Passcode_FocusChanged(object sender, KeyboardFocusChangedEventArgs e) => UpdatePinCaret();
    private void Passcode_VisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdatePinCaret();

    private void UpdatePinCaret()
    {
        if (PinCaret == null || PasscodeBox == null) return;
        PinCaret.BeginAnimation(OpacityProperty, null);
        bool visible = !_disposed && PasscodeBox.IsVisible && PasscodeBox.IsKeyboardFocused && _previousPinLength < 6;
        PinCaret.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible) return;
        // The six dot slots are 19 DIPs wide. Start centered, then follow the filled slots.
        double offset = _previousPinLength == 0 ? 0 : (_previousPinLength - 3) * 19;
        AnimateValue(PinCaretOffset, TranslateTransform.XProperty, offset, MotionFast);
        PinCaret.Opacity = 1;
        if (AnimationConfig.ReduceMotion) return;
        var blink = new DoubleAnimation(1, 0.15, TimeSpan.FromMilliseconds(520))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        Timeline.SetDesiredFrameRate(blink, AnimationConfig.TargetFps);
        PinCaret.BeginAnimation(OpacityProperty, blink);
    }

    internal void ShakeLockContent()
    {
        if (_disposed || LockContent == null) return;
        if (LockContent.RenderTransform is not TranslateTransform translate || translate.IsFrozen)
        {
            translate = new TranslateTransform();
            LockContent.RenderTransform = translate;
        }
        translate.BeginAnimation(TranslateTransform.XProperty, null);
        translate.X = 0;
        if (AnimationConfig.ReduceMotion) return;

        var shake = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        shake.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        shake.KeyFrames.Add(new EasingDoubleKeyFrame(-7, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(55)), TrayEase));
        shake.KeyFrames.Add(new EasingDoubleKeyFrame(6, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(115)), TrayEase));
        shake.KeyFrames.Add(new EasingDoubleKeyFrame(-4, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(175)), TrayEase));
        shake.KeyFrames.Add(new EasingDoubleKeyFrame(2.5, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(230)), TrayEase));
        shake.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(290)), TrayEase));
        Timeline.SetDesiredFrameRate(shake, AnimationConfig.TargetFps);
        translate.BeginAnimation(TranslateTransform.XProperty, shake, HandoffBehavior.SnapshotAndReplace);
    }
}

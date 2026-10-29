using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VNotch.Services;

namespace VNotch.Controls;

public sealed partial class ClipboardTray
{
    private Rect _categorySelectionBounds = Rect.Empty;

    private void CategoryPanel_LayoutUpdated(object? sender, EventArgs e)
    {
        if (_categoryReordering) { CategorySelection.Opacity = 0; return; }
        if (_disposed || !IsVisible || !CategoryPanel.IsArrangeValid) return;
        var selected = _categoryItems.FirstOrDefault(item => item.IsSelected);
        if (selected == null || CategoryPanel.ItemContainerGenerator.ContainerFromItem(selected) is not ContentPresenter presenter ||
            VisualTreeHelper.GetChildrenCount(presenter) == 0 || VisualTreeHelper.GetChild(presenter, 0) is not Button button ||
            button.ActualWidth <= 0) return;
        var pos = button.TranslatePoint(new Point(), CategoryPanel);
        var bounds = new Rect(pos, button.RenderSize);
        TrayMotion.SetBackground(CategorySelection, button.Background);
        CategorySelection.Opacity = 1;
        if (bounds == _categorySelectionBounds) return;
        bool animate = !_categorySelectionBounds.IsEmpty && !AnimationConfig.ReduceMotion && !_trayLeaving;
        _categorySelectionBounds = bounds;
        double renderedWidth = CategorySelection.Width * CategorySelectionScale.ScaleX;
        CategorySelection.Width = bounds.Width;
        CategorySelectionScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        CategorySelectionScale.ScaleX = renderedWidth / bounds.Width;
        MoveCategoryIndicator(CategorySelectionOffset, TranslateTransform.XProperty, bounds.X, animate);
        MoveCategoryIndicator(CategorySelectionScale, ScaleTransform.ScaleXProperty, 1, animate);
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

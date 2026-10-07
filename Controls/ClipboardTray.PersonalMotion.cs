using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VNotch.Services;

namespace VNotch.Controls;

public partial class ClipboardTray
{
    private Rect _categorySelectionBounds = Rect.Empty;

    private void CategoryPanel_LayoutUpdated(object? sender, EventArgs e)
    {
        if (_disposed || !IsVisible || !CategoryPanel.IsArrangeValid) return;
        var selected = _categoryItems.FirstOrDefault(item => item.IsSelected);
        if (selected == null || CategoryPanel.ItemContainerGenerator.ContainerFromItem(selected) is not ContentPresenter presenter ||
            VisualTreeHelper.GetChildrenCount(presenter) == 0 || VisualTreeHelper.GetChild(presenter, 0) is not Button button ||
            button.ActualWidth <= 0) return;
        var bounds = new Rect(button.TranslatePoint(new Point(), CategoryPanel), button.RenderSize);
        if (bounds == _categorySelectionBounds) return;
        bool animate = !_categorySelectionBounds.IsEmpty && !AnimationConfig.ReduceMotion && !_trayLeaving;
        _categorySelectionBounds = bounds;
        TrayMotion.SetBackground(CategorySelection, button.Background);
        CategorySelection.Opacity = 1;
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
}

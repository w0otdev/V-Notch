using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VNotch.Services;

namespace VNotch;

public partial class SettingsWindow
{
    private void SettingsSwitch_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox check) UpdateSettingsSwitch(check, animate: false);
    }

    private void SettingsSwitch_StateChanged(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { IsLoaded: true } check)
            UpdateSettingsSwitch(check, animate: check.IsVisible);
    }

    private static void UpdateSettingsSwitch(CheckBox check, bool animate)
    {
        check.ApplyTemplate();
        if (check.Template is not { } template ||
            template.FindName("SwitchVisual", check) is not FrameworkElement track ||
            template.FindName("SwitchThumb", check) is not Border { RenderTransform: TranslateTransform thumb } thumbBorder ||
            template.FindName("SwitchOnTrack", check) is not Border onTrack) return;

        // WPF freezes transforms defined in a shared template until an instance needs motion.
        if (thumb.IsFrozen)
        {
            thumb = thumb.CloneCurrentValue();
            thumbBorder.RenderTransform = thumb;
        }

        animate &= !AnimationConfig.ReduceMotion && SystemParameters.ClientAreaAnimation;
        bool on = check.IsChecked == true;
        double trackWidth = track.ActualWidth > 0 ? track.ActualWidth : track.Width;
        double thumbWidth = thumbBorder.ActualWidth > 0 ? thumbBorder.ActualWidth : thumbBorder.Width;
        double travel = Math.Max(0, trackWidth - thumbWidth - thumbBorder.Margin.Left - thumbBorder.Margin.Right);
        AnimateSettingsSwitchValue(thumb, TranslateTransform.XProperty, on ? travel : 0, animate);
        AnimateSettingsSwitchValue(onTrack, OpacityProperty, on ? 1 : 0, animate);
    }

    private static void AnimateSettingsSwitchValue(Animatable target, DependencyProperty property, double value, bool animate)
    {
        // Capture the presented value before replacing a clock so rapid toggles reverse smoothly.
        double current = (double)target.GetValue(property);
        if (!animate || Math.Abs(current - value) < 0.001)
        {
            target.BeginAnimation(property, null);
            target.SetValue(property, value);
            return;
        }

        var animation = AnimationPrimitives.WithFps(new DoubleAnimation(current, value, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = AnimationPrimitives._easeMoveSpring,
            FillBehavior = FillBehavior.Stop
        });
        target.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
        target.SetValue(property, value);
    }

    private static void AnimateSettingsSwitchValue(UIElement target, DependencyProperty property, double value, bool animate)
    {
        double current = (double)target.GetValue(property);
        if (!animate || Math.Abs(current - value) < 0.001)
        {
            target.BeginAnimation(property, null);
            target.SetValue(property, value);
            return;
        }

        var animation = AnimationPrimitives.WithFps(new DoubleAnimation(current, value, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = AnimationPrimitives._easeAppleOut,
            FillBehavior = FillBehavior.Stop
        });
        target.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
        target.SetValue(property, value);
    }
}

using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VNotch.Services;

namespace VNotch;

public partial class SettingsWindow
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VnSettingsSwitchMotionOut
    {
        public double TargetX;
        public double PeakScaleX;
        public double DipScaleY;
        public double ReboundScaleX;
        public double ReboundScaleY;
        public double PressScaleX;
        public double PressScaleY;
        public double PressShiftX;
        public int PeakTimeMs;
        public int ReboundTimeMs;
        public int DurationMs;
    }

    [LibraryImport("VNotch.Native", EntryPoint = "vn_animation_compute_settings_switch_motion")]
    private static unsafe partial int NativeComputeSettingsSwitchMotion(
        double trackWidth,
        double thumbWidth,
        double marginX,
        byte isChecked,
        byte isPressed,
        byte reducedMotion,
        VnSettingsSwitchMotionOut* outMotion);

    private void SettingsSwitch_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox check) UpdateSettingsSwitch(check, animate: false, isPressed: false);
    }

    private void SettingsSwitch_StateChanged(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { IsLoaded: true } check)
            UpdateSettingsSwitch(check, animate: check.IsVisible, isPressed: false);
    }

    private void SettingsSwitch_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is CheckBox { IsLoaded: true, IsEnabled: true } check)
            UpdateSettingsSwitchPress(check, isPressed: true);
    }

    private void SettingsSwitch_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is CheckBox { IsLoaded: true } check)
            UpdateSettingsSwitchPress(check, isPressed: false);
    }

    private void SettingsSwitch_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is CheckBox { IsLoaded: true } check && e.LeftButton == MouseButtonState.Pressed)
            UpdateSettingsSwitchPress(check, isPressed: false);
    }

    private static unsafe VnSettingsSwitchMotionOut ComputeSwitchMotion(
        double trackWidth,
        double thumbWidth,
        double marginX,
        bool isChecked,
        bool isPressed,
        bool animate)
    {
        VnSettingsSwitchMotionOut motion = default;
        try
        {
            if (NativeComputeSettingsSwitchMotion(
                    trackWidth,
                    thumbWidth,
                    marginX,
                    isChecked ? (byte)1 : (byte)0,
                    isPressed ? (byte)1 : (byte)0,
                    animate ? (byte)0 : (byte)1,
                    &motion) == 0)
            {
                return motion;
            }
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }

        double travel = Math.Max(0, trackWidth - thumbWidth - marginX * 2.0);
        return new VnSettingsSwitchMotionOut
        {
            TargetX = isChecked ? travel : 0.0,
            PeakScaleX = animate ? 1.22 : 1.0,
            DipScaleY = animate ? 0.90 : 1.0,
            ReboundScaleX = animate ? 0.96 : 1.0,
            ReboundScaleY = animate ? 1.035 : 1.0,
            PressScaleX = animate && isPressed ? 1.15 : 1.0,
            PressScaleY = animate && isPressed ? 0.94 : 1.0,
            PressShiftX = animate && isPressed ? (isChecked ? -1.8 : 1.8) : 0.0,
            PeakTimeMs = animate ? 75 : 0,
            ReboundTimeMs = animate ? 175 : 0,
            DurationMs = animate ? 250 : 0
        };
    }

    private static void EnsureMutableThumbTransforms(
        CheckBox check,
        Border thumbBorder,
        ref TranslateTransform thumbTranslate,
        out ScaleTransform? thumbCapScale)
    {
        if (thumbTranslate.IsFrozen)
        {
            thumbTranslate = thumbTranslate.CloneCurrentValue();
            thumbBorder.RenderTransform = thumbTranslate;
        }

        thumbCapScale = null;
        if (check.Template?.FindName("SwitchThumbCap", check) is Border { RenderTransform: ScaleTransform capScale } capBorder)
        {
            if (capScale.IsFrozen)
            {
                capScale = capScale.CloneCurrentValue();
                capBorder.RenderTransform = capScale;
            }
            thumbCapScale = capScale;
        }
    }

    private static void UpdateSettingsSwitchPress(CheckBox check, bool isPressed)
    {
        check.ApplyTemplate();
        if (check.Template is not { } template ||
            template.FindName("SwitchVisual", check) is not FrameworkElement track ||
            template.FindName("SwitchThumb", check) is not Border { RenderTransform: TranslateTransform thumb } thumbBorder) return;

        EnsureMutableThumbTransforms(check, thumbBorder, ref thumb, out var capScale);
        bool animate = check.IsVisible && !AnimationConfig.ReduceMotion && SystemParameters.ClientAreaAnimation;
        bool on = check.IsChecked == true;
        double trackWidth = track.ActualWidth > 0 ? track.ActualWidth : track.Width;
        double thumbWidth = thumbBorder.ActualWidth > 0 ? thumbBorder.ActualWidth : thumbBorder.Width;
        var motion = ComputeSwitchMotion(trackWidth, thumbWidth, thumbBorder.Margin.Left, on, isPressed, animate);

        AnimateSettingsSwitchValue(thumb, TranslateTransform.XProperty, motion.TargetX + motion.PressShiftX, animate, 130);
        if (capScale != null)
        {
            AnimateSettingsSwitchValue(capScale, ScaleTransform.ScaleXProperty, motion.PressScaleX, animate, 130);
            AnimateSettingsSwitchValue(capScale, ScaleTransform.ScaleYProperty, motion.PressScaleY, animate, 130);
        }
    }

    private static void UpdateSettingsSwitch(CheckBox check, bool animate, bool isPressed = false)
    {
        check.ApplyTemplate();
        if (check.Template is not { } template ||
            template.FindName("SwitchVisual", check) is not FrameworkElement track ||
            template.FindName("SwitchThumb", check) is not Border { RenderTransform: TranslateTransform thumb } thumbBorder ||
            template.FindName("SwitchOnTrack", check) is not Border onTrack) return;

        // WPF freezes transforms defined in a shared template until an instance needs motion.
        EnsureMutableThumbTransforms(check, thumbBorder, ref thumb, out var capScale);

        animate &= !AnimationConfig.ReduceMotion && SystemParameters.ClientAreaAnimation;
        bool on = check.IsChecked == true;
        double trackWidth = track.ActualWidth > 0 ? track.ActualWidth : track.Width;
        double thumbWidth = thumbBorder.ActualWidth > 0 ? thumbBorder.ActualWidth : thumbBorder.Width;
        var motion = ComputeSwitchMotion(trackWidth, thumbWidth, thumbBorder.Margin.Left, on, isPressed, animate);

        AnimateSettingsSwitchValue(thumb, TranslateTransform.XProperty, motion.TargetX, animate, Math.Max(180, motion.DurationMs));
        AnimateSettingsSwitchValue(onTrack, OpacityProperty, on ? 1 : 0, animate);

        if (capScale != null)
        {
            AnimateSettingsSwitchThumbLiquidMorph(capScale, motion, animate);
        }
    }

    private static readonly CubicEase _switchEaseOut = CreateFrozenCubicEase(EasingMode.EaseOut);
    private static readonly CubicEase _switchEaseInOut = CreateFrozenCubicEase(EasingMode.EaseInOut);

    private static CubicEase CreateFrozenCubicEase(EasingMode mode)
    {
        var ease = new CubicEase { EasingMode = mode };
        ease.Freeze();
        return ease;
    }

    private static void AnimateSettingsSwitchThumbLiquidMorph(ScaleTransform capScale, VnSettingsSwitchMotionOut motion, bool animate)
    {
        if (!animate || motion.DurationMs <= 0)
        {
            capScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            capScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            capScale.ScaleX = 1.0;
            capScale.ScaleY = 1.0;
            return;
        }

        double curX = capScale.ScaleX;
        double curY = capScale.ScaleY;
        var scaleXAnim = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        scaleXAnim.KeyFrames.Add(new EasingDoubleKeyFrame(curX, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        scaleXAnim.KeyFrames.Add(new EasingDoubleKeyFrame(motion.PeakScaleX, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(motion.PeakTimeMs)), _switchEaseOut));
        scaleXAnim.KeyFrames.Add(new EasingDoubleKeyFrame(motion.ReboundScaleX, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(motion.ReboundTimeMs)), _switchEaseInOut));
        scaleXAnim.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(motion.DurationMs)), AnimationPrimitives._easeAppleOut));
        Timeline.SetDesiredFrameRate(scaleXAnim, AnimationConfig.TargetFps);

        var scaleYAnim = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        scaleYAnim.KeyFrames.Add(new EasingDoubleKeyFrame(curY, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        scaleYAnim.KeyFrames.Add(new EasingDoubleKeyFrame(motion.DipScaleY, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(motion.PeakTimeMs)), _switchEaseOut));
        scaleYAnim.KeyFrames.Add(new EasingDoubleKeyFrame(motion.ReboundScaleY, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(motion.ReboundTimeMs)), _switchEaseInOut));
        scaleYAnim.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(motion.DurationMs)), AnimationPrimitives._easeAppleOut));
        Timeline.SetDesiredFrameRate(scaleYAnim, AnimationConfig.TargetFps);

        capScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleXAnim, HandoffBehavior.SnapshotAndReplace);
        capScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleYAnim, HandoffBehavior.SnapshotAndReplace);
        capScale.ScaleX = 1.0;
        capScale.ScaleY = 1.0;
    }

    private static void AnimateSettingsSwitchValue(Animatable target, DependencyProperty property, double value, bool animate, int durationMs = 220)
    {
        // Capture the presented value before replacing a clock so rapid toggles reverse smoothly.
        double current = (double)target.GetValue(property);
        if (!animate || Math.Abs(current - value) < 0.001)
        {
            target.BeginAnimation(property, null);
            target.SetValue(property, value);
            return;
        }

        var animation = AnimationPrimitives.WithFps(new DoubleAnimation(current, value, TimeSpan.FromMilliseconds(durationMs))
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

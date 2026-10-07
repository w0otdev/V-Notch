using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VNotch.Services;

namespace VNotch.Controls;

public partial class ClipboardTray
{
    private void CategoryCount_TargetUpdated(object sender, DataTransferEventArgs e)
    {
        if (sender is not TextBlock count || e.Property != TextBlock.TextProperty) return;
        string? previous = count.Tag as string;
        count.Tag = count.Text;
        if (previous == null || previous == count.Text || !count.IsLoaded || !count.IsVisible || _disposed || _trayLeaving) return;

        double settledOpacity = (double)count.GetAnimationBaseValue(OpacityProperty);
        double fromOpacity = Math.Abs(count.Opacity - settledOpacity) > 0.001 ? count.Opacity : settledOpacity * 0.35;
        var scale = count.RenderTransform as ScaleTransform;
        double fromScale = scale != null && Math.Abs(scale.ScaleX - 1) > 0.001 ? scale.ScaleX : 0.95;
        count.BeginAnimation(OpacityProperty, null);
        count.RenderTransform = scale = new ScaleTransform(1, 1);
        var fade = new DoubleAnimation(fromOpacity, settledOpacity,
            TimeSpan.FromMilliseconds(AnimationConfig.ReduceMotion ? 80 : 180))
        { EasingFunction = TrayMotion.EaseOut, FillBehavior = FillBehavior.Stop };
        Timeline.SetDesiredFrameRate(fade, AnimationConfig.TargetFps);
        count.BeginAnimation(OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
        if (AnimationConfig.ReduceMotion) return;
        var grow = new DoubleAnimation(fromScale, 1, TimeSpan.FromMilliseconds(180))
        { EasingFunction = TrayMotion.EaseOut, FillBehavior = FillBehavior.Stop };
        Timeline.SetDesiredFrameRate(grow, AnimationConfig.TargetFps);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using VNotch.Services;

namespace VNotch;

public partial class TranslationWindow
{
    private bool _topFadeVisible, _bottomFadeVisible;

    private void UpdateResultScrollFades(bool animate = true)
    {
        bool overflow = ResultText.Text.Length > 0 && ResultScroll.ScrollableHeight > .5;
        SetResultScrollFade(ResultTopFade, overflow && ResultScroll.VerticalOffset > .5, ref _topFadeVisible, animate);
        SetResultScrollFade(ResultBottomFade, overflow && ResultScroll.ScrollableHeight - ResultScroll.VerticalOffset > .5, ref _bottomFadeVisible, animate);
    }

    private void SetResultScrollFade(Border edge, bool visible, ref bool previous, bool animate)
    {
        bool reduced = AnimationConfig.ReduceMotion || !SystemParameters.ClientAreaAnimation;
        if (visible == previous && animate && !reduced) return;
        previous = visible;
        double from = edge.Opacity, target = visible ? 1 : 0;
        edge.BeginAnimation(OpacityProperty, null);
        edge.Opacity = target;
        if (!animate || reduced || !IsVisible || _popupAnimating || Math.Abs(from - target) < .001) return;
        var fade = Motion(from, target, 180, AnimationPrimitives._easeSineInOut);
        Timeline.SetDesiredFrameRate(fade, Math.Min(60, AnimationConfig.TargetFps));
        fade.FillBehavior = FillBehavior.Stop;
        edge.BeginAnimation(OpacityProperty, fade);
    }

    private void ResultScrollMotionChanged() => UpdateResultScrollFades(animate: false);
}

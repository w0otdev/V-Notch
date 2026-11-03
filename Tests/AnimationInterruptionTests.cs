using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VNotch.Controls;
using VNotch.Presenters;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class AnimationInterruptionTests
{
    [Fact]
    public void CancelingContentMotionHoldsAnimatedChildTransformsUntilTheNextTransition() => SharedStaTestRunner.Run(() =>
    {
        var translate = new TranslateTransform(0, 4);
        var scale = new ScaleTransform(1, 1);
        var group = new TransformGroup();
        group.Children.Add(scale);
        group.Children.Add(translate);
        var content = new Canvas { Name = "ExpandedContent", RenderTransform = group };
        HoldAt(translate, TranslateTransform.YProperty, 4, 18);
        HoldAt(scale, ScaleTransform.ScaleXProperty, 1, .8);
        Matrix current = content.RenderTransform.Value;
        using var presenter = new NotchContentTransitionPresenter(new() { ExpandedContent = content });

        presenter.CancelActiveTransition();

        Assert.Same(group, content.RenderTransform);
        Assert.Same(scale, group.Children[0]);
        Assert.Same(translate, group.Children[1]);
        Assert.Equal(current, content.RenderTransform.Value);
        Assert.All(Assert.IsType<TransformGroup>(content.RenderTransform).Children,
            transform => Assert.False(transform.HasAnimatedProperties));
    });

    [Fact]
    public void ReversingAnIconSwitchPreservesBothRenderedIcons() => SharedStaTestRunner.Run(() =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        try
        {
            AnimationConfig.SetReduceMotion(false);
            var outgoing = new Canvas { RenderTransform = new ScaleTransform(1, 1) };
            var incoming = new Canvas { RenderTransform = new ScaleTransform(1, 1) };
            var outScale = (ScaleTransform)outgoing.RenderTransform;
            var inScale = (ScaleTransform)incoming.RenderTransform;
            HoldAt(outgoing, UIElement.OpacityProperty, 1, 0);
            HoldAt(incoming, UIElement.OpacityProperty, 0, 1);
            HoldAt(outScale, ScaleTransform.ScaleXProperty, 1, .3);
            HoldAt(outScale, ScaleTransform.ScaleYProperty, 1, .3);
            HoldAt(inScale, ScaleTransform.ScaleXProperty, .3, 1);
            HoldAt(inScale, ScaleTransform.ScaleYProperty, .3, 1);
            double outOpacity = outgoing.Opacity, inOpacity = incoming.Opacity;
            double outSize = outScale.ScaleX, inSize = inScale.ScaleX;

            ButtonFeedback.AnimateIconSwitch(incoming, outgoing, TimeSpan.FromMilliseconds(160), AnimationPrimitives._easeQuadOut);

            Assert.Equal(outOpacity, outgoing.Opacity, 8);
            Assert.Equal(inOpacity, incoming.Opacity, 8);
            Assert.Equal(outSize, outScale.ScaleX, 8);
            Assert.Equal(inSize, inScale.ScaleX, 8);
        }
        finally { AnimationConfig.SetReduceMotion(previous); }
    });

    [Fact]
    public void RepeatedIconReversalsFinishWithoutStaleClocksOrHidingTheLatestGlyph() => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        try
        {
            AnimationConfig.SetReduceMotion(false);
            var from = new Canvas();
            var to = new Canvas { Visibility = Visibility.Collapsed };
            for (int i = 0; i < 5; i++)
            {
                ButtonFeedback.AnimateIconSwitch(from, to, TimeSpan.FromMilliseconds(100), AnimationPrimitives._easeQuadOut);
                await WpfFrameWaiter.NextAsync(ct);
                (from, to) = (to, from);
            }
            await WpfFrameWaiter.UntilAsync(() => !from.HasAnimatedProperties && !to.HasAnimatedProperties
                && !from.RenderTransform.HasAnimatedProperties && !to.RenderTransform.HasAnimatedProperties,
                "icon switch releases all clocks", ct);
            Assert.Equal(Visibility.Collapsed, to.Visibility);
            Assert.Equal(Visibility.Visible, from.Visibility);
            Assert.Equal(1, from.Opacity);
            Assert.Equal(1, ((ScaleTransform)from.RenderTransform).ScaleX);
        }
        finally { AnimationConfig.SetReduceMotion(previous); }
    });

    [Fact]
    public void ReducedMotionIconSwitchFinishesImmediatelyAndClearsOldClocks() => SharedStaTestRunner.Run(() =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        var from = new Canvas();
        var to = new Canvas { Visibility = Visibility.Collapsed };
        try
        {
            AnimationConfig.SetReduceMotion(false);
            ButtonFeedback.AnimateIconSwitch(from, to, TimeSpan.FromMilliseconds(100), AnimationPrimitives._easeQuadOut);
            AnimationConfig.SetReduceMotion(true);
            ButtonFeedback.AnimateIconSwitch(to, from, TimeSpan.FromMilliseconds(100), AnimationPrimitives._easeQuadOut);
            Assert.Equal(Visibility.Visible, from.Visibility);
            Assert.Equal(Visibility.Collapsed, to.Visibility);
            Assert.False(from.HasAnimatedProperties);
            Assert.False(to.HasAnimatedProperties);
            Assert.False(from.RenderTransform.HasAnimatedProperties);
            Assert.False(to.RenderTransform.HasAnimatedProperties);
        }
        finally { AnimationConfig.SetReduceMotion(previous); }
    });

    [Theory]
    [InlineData(60, 144)]
    [InlineData(120, 144)]
    [InlineData(45, 60)]
    public void SeekSpringUsesCompositionTimeWithoutDroppingTheFrameRemainder(int target, int displayHz) => SharedStaTestRunner.Run(() =>
    {
        var configFlags = BindingFlags.Static | BindingFlags.NonPublic;
        int previousFps = (int)typeof(AnimationConfig).GetField("_configuredFps", configFlags)!.GetValue(null)!;
        bool previousAutoFps = (bool)typeof(AnimationConfig).GetField("_autoFps", configFlags)!.GetValue(null)!;
        int updates = 0;
        var spring = new ProgressSpringRenderer(_ => updates++, () => true, () => 1)
        {
            DisplayRatio = .1, TargetRatio = .9, SpringTargetRatio = .9
        };
        var render = typeof(ProgressSpringRenderer).GetMethod("OnRendering", BindingFlags.Instance | BindingFlags.NonPublic)!;
        try
        {
            AnimationConfig.Configure(target, autoFps: false);
            spring.Start();
            for (int frame = 0; frame < displayHz; frame++)
            {
                var args = (RenderingEventArgs)Activator.CreateInstance(typeof(RenderingEventArgs),
                    BindingFlags.Instance | BindingFlags.NonPublic, null,
                    [TimeSpan.FromSeconds(30 + frame / (double)displayHz)], null)!;
                render.Invoke(spring, [null, args]);
                render.Invoke(spring, [null, args]); // WPF can raise Rendering twice for one frame.
            }
            Assert.InRange(updates, target - 1, target + 1);
            Assert.InRange(spring.DisplayRatio, .85, .9);
        }
        finally
        {
            spring.Stop();
            AnimationConfig.Configure(previousFps, previousAutoFps);
        }
    });

    private static void HoldAt(IAnimatable target, DependencyProperty property, double from, double to)
    {
        var clock = (AnimationClock)new DoubleAnimation(from, to, TimeSpan.FromSeconds(1)).CreateClock(true);
        target.ApplyAnimationClock(property, clock);
        clock.Controller!.Begin();
        clock.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(350), TimeSeekOrigin.BeginTime);
    }
}

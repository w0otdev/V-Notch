using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VNotch.Controls;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class AnimationInterruptionTests
{
    [Fact]
    public void ReversingAnIconSwitchPreservesBothRenderedIcons() => SharedStaTestRunner.Run(() =>
    {
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
    });

    [Theory]
    [InlineData(60, 144)]
    [InlineData(120, 144)]
    [InlineData(45, 60)]
    public void SeekSpringUsesCompositionTimeWithoutDroppingTheFrameRemainder(int target, int displayHz) => SharedStaTestRunner.Run(() =>
    {
        int previousFps = AnimationConfig.TargetFps;
        int updates = 0;
        var spring = new ProgressSpringRenderer(_ => updates++, () => true, () => 1)
        {
            DisplayRatio = .1, TargetRatio = .9, SpringTargetRatio = .9
        };
        var render = typeof(ProgressSpringRenderer).GetMethod("OnRendering", BindingFlags.Instance | BindingFlags.NonPublic)!;
        try
        {
            AnimationConfig.Configure(target);
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
            AnimationConfig.Configure(previousFps);
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

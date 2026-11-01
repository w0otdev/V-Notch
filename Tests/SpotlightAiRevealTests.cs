using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

[Collection(SpotlightWindowAnimationCollection.Name)]
public sealed class SpotlightAiRevealTests
{
    [Theory]
    [InlineData("AnimateAiPageReveal")]
    [InlineData("AnimateAiArrival")]
    public void CompletedRevealCanReplayRepeatedly(string methodName)
    {
        SharedStaTestRunner.RunAsync(async ct =>
        {
            bool previous = AnimationConfig.ReduceMotion;
            var element = new Border();
            using var source = new System.Windows.Interop.HwndSource(
                new System.Windows.Interop.HwndSourceParameters("ai-reveal-test")
                {
                    Width = 100,
                    Height = 100,
                    PositionX = -32000,
                    PositionY = -32000,
                    WindowStyle = unchecked((int)0x80000000)
                });
            source.RootVisual = element;
            try
            {
                AnimationConfig.SetReduceMotion(false);
                var method = typeof(SpotlightWindow).GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic)!;
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    // Reproduce a completed clock that remains attached to the element.
                    var completed = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(1))
                    { FillBehavior = FillBehavior.Stop };
                    var clock = completed.CreateClock();
                    element.ApplyAnimationClock(UIElement.OpacityProperty, clock);
                    clock.Controller!.SkipToFill();
                    Assert.True(element.HasAnimatedProperties);
                    Assert.Equal(1d, element.Opacity);

                    // Observe the fade whenever WPF publishes it. A fixed 80ms
                    // sample can arrive after completion on an instrumented runner.
                    double minimumOpacity = 1;
                    var descriptor = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(UIElement.OpacityProperty, typeof(Border));
                    EventHandler observe = (_, _) => minimumOpacity = Math.Min(minimumOpacity, element.Opacity);
                    descriptor.AddValueChanged(element, observe);
                    try
                    {
                        method.Invoke(null, methodName == "AnimateAiPageReveal"
                            ? new object[] { element } : new object[] { element, 6d, 240 });
                        observe(null, EventArgs.Empty);
                        element.UpdateLayout();
                        await WpfFrameWaiter.UntilAsync(
                            () => minimumOpacity < 1 && element.Opacity == 1d,
                            $"AI reveal {attempt + 1} completion", ct);
                    }
                    finally { descriptor.RemoveValueChanged(element, observe); }
                    Assert.True(minimumOpacity < 1, $"Reveal {attempt + 1} never published a fade-in value.");
                    Assert.Equal(1d, element.Opacity);
                }
            }
            finally
            {
                element.BeginAnimation(UIElement.OpacityProperty, null);
                AnimationConfig.SetReduceMotion(previous);
            }
        });
    }
}

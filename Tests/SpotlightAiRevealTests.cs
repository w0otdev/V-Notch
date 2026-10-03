using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Threading;
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
        SharedStaTestRunner.Run(() =>
        {
            bool previous = AnimationConfig.ReduceMotion;
            var element = new Border();
            using var source = new System.Windows.Interop.HwndSource(
                new System.Windows.Interop.HwndSourceParameters("ai-reveal-test")
                { Width = 100, Height = 100, PositionX = -32000, PositionY = -32000,
                  WindowStyle = unchecked((int)0x80000000) });
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

                    method.Invoke(null, methodName == "AnimateAiPageReveal"
                        ? new object[] { element } : new object[] { element, 6d, 240 });
                    element.UpdateLayout();
                    Pump(TimeSpan.FromMilliseconds(80));
                    Assert.True(element.Opacity < 1, $"Reveal {attempt + 1} did not fade in.");
                    Pump(TimeSpan.FromMilliseconds(350));
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

    private static void Pump(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = duration };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}

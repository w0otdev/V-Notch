using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class AiStopButtonTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepeatedRevealAndHideClearMotionClocks(bool reducedMotion) => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        try
        {
            AnimationConfig.SetReduceMotion(reducedMotion);
            using var request = new CancellationTokenSource();
            using var fixture = new SpotlightWindowFixture(new());
            var window = fixture.Window;
            window.ShowSpotlight();
            typeof(SpotlightWindow).GetMethod("ToggleAiMode", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
            var button = window.AiStopButton;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                SetRequestActive(window, request);
                window.UpdateLayout();
                await WpfFrameWaiter.UntilAsync(() => button.IsLoaded && button.IsVisible, "Stop button is presented", ct);
                await WpfFrameWaiter.NextAsync(ct);
                var surface = (Grid)button.Template.FindName("RevealSurface", button);
                var body = (Grid)button.Template.FindName("ButtonContent", button);
                var scale = Assert.IsType<ScaleTransform>(surface.RenderTransform);
                Assert.False(scale.IsFrozen);
                await WpfFrameWaiter.UntilAsync(() => surface.Opacity == 1 && scale.ScaleX == 1,
                    "Stop button reveal completes", ct);
                SetRequestActive(window, null);
                Assert.False(button.IsEnabled);
                await WpfFrameWaiter.UntilAsync(() => button.Visibility == Visibility.Collapsed,
                    "Stop button exit completes", ct);
                Assert.False(surface.HasAnimatedProperties);
                Assert.False(scale.HasAnimatedProperties);
                Assert.False(Assert.IsType<ScaleTransform>(body.RenderTransform).HasAnimatedProperties);
                Assert.Equal(1, scale.ScaleX);
                var glow = (FrameworkElement)button.Template.FindName("BreathingGlow", button);
                Assert.False(glow.HasAnimatedProperties);
            }
        }
        finally { AnimationConfig.SetReduceMotion(previous); }
    });

    [Fact]
    public void EnablingReducedMotionDuringRevealResetsTransforms() => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        try
        {
            AnimationConfig.SetReduceMotion(false);
            using var request = new CancellationTokenSource();
            using var fixture = new SpotlightWindowFixture(new());
            var window = fixture.Window;
            window.ShowSpotlight();
            typeof(SpotlightWindow).GetMethod("ToggleAiMode", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
            SetRequestActive(window, request);
            window.UpdateLayout();
            await WpfFrameWaiter.UntilAsync(() => window.AiStopButton.IsLoaded && window.AiStopButton.IsVisible,
                "Stop button is presented", ct);
            await WpfFrameWaiter.NextAsync(ct);
            AnimationConfig.SetReduceMotion(true);
            var surface = (Grid)window.AiStopButton.Template.FindName("RevealSurface", window.AiStopButton);
            var scale = Assert.IsType<ScaleTransform>(surface.RenderTransform);
            Assert.Equal(1, surface.Opacity);
            Assert.Equal(1, scale.ScaleX);
            Assert.Equal(1, scale.ScaleY);
            Assert.False(scale.HasAnimatedProperties);
            var glow = (FrameworkElement)window.AiStopButton.Template.FindName("BreathingGlow", window.AiStopButton);
            Assert.False(glow.HasAnimatedProperties);
        }
        finally { AnimationConfig.SetReduceMotion(previous); }
    });

    [Fact]
    public void NewRequestCanReverseExitWithoutBeingCollapsedByOldCompletion() => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        try
        {
            AnimationConfig.SetReduceMotion(false);
            using var request = new CancellationTokenSource();
            using var fixture = new SpotlightWindowFixture(new());
            var window = fixture.Window;
            window.ShowSpotlight();
            typeof(SpotlightWindow).GetMethod("ToggleAiMode", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
            var button = window.AiStopButton;
            SetRequestActive(window, request);
            window.UpdateLayout();
            await WpfFrameWaiter.UntilAsync(() => button.IsLoaded && button.IsVisible, "Stop button is presented", ct);
            await WpfFrameWaiter.NextAsync(ct);
            var surface = (Grid)button.Template.FindName("RevealSurface", button);
            await WpfFrameWaiter.UntilAsync(() => surface.Opacity == 1, "Stop button shown", ct);

            SetRequestActive(window, null);
            Assert.Equal(Visibility.Visible, button.Visibility);
            Assert.False(button.IsEnabled);
            Assert.False(button.IsHitTestVisible);
            SetRequestActive(window, request);
            await WpfFrameWaiter.NextAsync(ct);
            await WpfFrameWaiter.UntilAsync(() => surface.Opacity == 1, "Stop button exit reversed", ct);
            Assert.Equal(Visibility.Visible, button.Visibility);
            Assert.True(button.IsEnabled);
            var glow = (FrameworkElement)button.Template.FindName("BreathingGlow", button);
            Assert.True(glow.HasAnimatedProperties);
            SetRequestActive(window, null);
            await WpfFrameWaiter.UntilAsync(() => button.Visibility == Visibility.Collapsed, "Stop button hidden", ct);
            Assert.False(glow.HasAnimatedProperties);
        }
        finally { AnimationConfig.SetReduceMotion(previous); }
    });

    private static void SetRequestActive(SpotlightWindow window, CancellationTokenSource? request)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        // Window refreshes derive the button state from the active request, including during entrance.
        typeof(SpotlightWindow).GetField("_aiRequest", flags)!.SetValue(window, request);
        typeof(SpotlightWindow).GetMethod("UpdateAiActionState", flags)!.Invoke(window, null);
    }
}

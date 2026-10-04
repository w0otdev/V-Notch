using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using VNotch.Controllers;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class LiquidGlassInteractionTests
{
    [Fact]
    public void VisibleControllerUnhooksAfterSettlingAndWhenHidden() => SharedStaTestRunner.Run(() =>
    {
        bool previousMotion = AnimationConfig.ReduceMotion;
        var source = new Border();
        var effect = new LiquidGlassRefractionEffect();
        var window = new BackgroundWindow
        {
            Width = 100, Height = 100, Left = -10000, Top = -10000,
            ShowActivated = false, ShowInTaskbar = false, Content = source
        };
        using var controller = new LiquidGlassInteractionController(source, source, effect);
        try
        {
            AnimationConfig.SetReduceMotion(false);
            window.Show();
            window.UpdateLayout();
            Assert.False(controller.IsRenderingSubscribed);
            source.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
                { RoutedEvent = Mouse.MouseMoveEvent });
            Assert.True(controller.IsRenderingSubscribed);
            PumpUntil(() => !controller.IsRenderingSubscribed);
            Assert.Equal(1, effect.PointerActive);
            source.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = Mouse.PreviewMouseDownEvent });
            Assert.True(controller.IsRenderingSubscribed);
            window.Hide();
            Assert.False(controller.IsRenderingSubscribed);
            Assert.Equal(0, effect.PointerActive);
            Assert.Equal(0, effect.PressAmount);
            window.Show();
            AnimationConfig.SetReduceMotion(true);
            source.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
                { RoutedEvent = Mouse.MouseMoveEvent });
            Assert.False(controller.IsRenderingSubscribed);
            Assert.Equal(1, effect.PointerActive);
        }
        finally
        {
            window.Close();
            AnimationConfig.SetReduceMotion(previousMotion);
        }
    });

    [Fact]
    public void IdleStateDoesNotMoveLightOrRequireRendering()
    {
        var state = new LiquidGlassInteractionState();
        var original = state.Frame;
        for (int i = 0; i < 1000; i++) state.Advance(1.0 / 144);
        Assert.True(state.IsSettled);
        Assert.Equal(original, state.Frame);
        SharedStaTestRunner.Run(() =>
        {
            var source = new Border();
            using var controller = new LiquidGlassInteractionController(source, source, new LiquidGlassRefractionEffect());
            Assert.False(controller.IsRenderingSubscribed);
        });
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(144)]
    [InlineData(240)]
    public void HoverPressAndReleaseSettleAtEveryRefreshRate(int fps)
    {
        var state = new LiquidGlassInteractionState();
        state.MovePointer(0.9, 0.2);
        Assert.False(state.IsSettled);
        Settle(state, fps);
        Assert.Equal(1, state.Frame.Active);
        Assert.Equal(0.9, state.Frame.PointerX);
        state.SetPressed(true);
        Settle(state, fps);
        Assert.Equal(1, state.Frame.Press);
        Assert.Equal(state.Frame.PointerX, state.Frame.LightX);
        state.SetPressed(false);
        state.SetActive(false);
        Settle(state, fps);
        Assert.Equal(0, state.Frame.Active);
        Assert.Equal(0, state.Frame.Press);
        Assert.Equal(0.5, state.Frame.LightX);
        Assert.Equal(-0.08, state.Frame.LightY);
    }

    [Fact]
    public void ReducedMotionSnapsWithoutRunningAnAnimation()
    {
        var state = new LiquidGlassInteractionState();
        state.MovePointer(2, -1);
        state.SetPressed(true);
        state.SnapToTargets();
        Assert.True(state.IsSettled);
        Assert.Equal(new GlassInteractionFrame(1, 0, 1, 1, 1, 0), state.Frame);
    }

    private static void Settle(LiquidGlassInteractionState state, int fps)
    {
        for (int i = 0; i < fps * 5 && !state.IsSettled; i++) state.Advance(1.0 / fps);
        Assert.True(state.IsSettled);
    }

    private static void PumpUntil(Func<bool> condition)
    {
        var frame = new DispatcherFrame();
        long deadline = Environment.TickCount64 + 5000;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) =>
        {
            if (condition() || Environment.TickCount64 >= deadline) frame.Continue = false;
        };
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        Assert.True(condition(), "Interaction rendering remained subscribed after settling.");
    }
}

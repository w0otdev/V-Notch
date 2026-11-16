using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Models;
using VNotch.Controllers;
using VNotch.Presenters;
using VNotch.Services;
using VNotch.Tests.Fakes;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class NotchTabMotionTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void RetargetingVisibleChromePreservesTheRenderedFrame() => SharedStaTestRunner.Run(() =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        using var fixture = new GreetingAcceptanceTests.MainWindowFixture("en", false,
            settings => settings.EnableLocalOnlyMode = true);
        var window = fixture.Window;
        try
        {
            AnimationConfig.SetReduceMotion(false);
            AnimationConfig.ClientAreaAnimationOverride = true;
            typeof(MainWindow).GetField("_navigationVisualTarget", Private)!.SetValue(window, NotchView.Media);
            window.NavIconsPanel.Visibility = Visibility.Visible;
            window.SettingsButton.Visibility = Visibility.Visible;
            HoldAt(window.NavIconsPanel, UIElement.OpacityProperty, 0, 1);
            HoldAt(window.NavIconsTranslate, TranslateTransform.YProperty, -6, 0);
            HoldAt(window.SettingsScale, ScaleTransform.ScaleXProperty, .96, 1);
            HoldAt(window.SettingsScale, ScaleTransform.ScaleYProperty, .96, 1);
            HoldAt(window.SettingsRotate, RotateTransform.AngleProperty, 32, 45);
            double opacity = window.NavIconsPanel.Opacity, y = window.NavIconsTranslate.Y;
            double scale = window.SettingsScale.ScaleX, angle = window.SettingsRotate.Angle;

            Invoke(window, "AnimateStatusBarReveal", true);

            Assert.Equal(opacity, window.NavIconsPanel.Opacity, 8);
            Assert.Equal(y, window.NavIconsTranslate.Y, 8);
            Assert.Equal(scale, window.SettingsScale.ScaleX, 8);
            Assert.Equal(angle, window.SettingsRotate.Angle, 8);
        }
        finally
        {
            AnimationConfig.ClientAreaAnimationOverride = null;
            AnimationConfig.SetReduceMotion(previous);
        }
    });

    [Fact]
    public void ClickingATabDoesNotPlayADragDropSettle() => SharedStaTestRunner.Run(() =>
    {
        using var fixture = new GreetingAcceptanceTests.MainWindowFixture("en", false,
            settings => settings.EnableLocalOnlyMode = true);
        var window = fixture.Window;
        typeof(MainWindow).GetField("_navDragItem", Private)!.SetValue(window, window.HomeIconButton);
        Invoke(window, "EndNavDrag", false);
        var transforms = Assert.IsType<TransformGroup>(window.HomeIconButton.RenderTransform);
        Assert.All(transforms.Children, transform => Assert.False(transform.HasAnimatedProperties));
    });

    private static void Invoke(MainWindow window, string method, params object[] args) =>
        typeof(MainWindow).GetMethod(method, Private)!.Invoke(window, args);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LatestTabClickWinsWithoutRestartingTheNavigationIcons(bool island) => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        await using var fixture = new GreetingAcceptanceTests.MainWindowFixture("en", false,
            settings =>
            {
                settings.EnableLocalOnlyMode = true; settings.DisableMouseLeaveAutoClose = true;
                settings.EnableDynamicIslandMode = island;
            },
            services => services.AddSingleton<IMediaDetectionService>(new FakeMediaDetectionService()));
        var window = fixture.Window;
        var coordinator = (NotchTransitionCoordinator)typeof(MainWindow).GetField("_transitionCoordinator", Private)!.GetValue(window)!;
        try
        {
            AnimationConfig.SetReduceMotion(true);
            window.Show();
            window.NavigateToNotchView(NotchView.Media);
            await WpfFrameWaiter.UntilAsync(() => coordinator.CurrentView == NotchView.Media && !coordinator.IsTransitionActive,
                "initial media view", ct);
            AnimationConfig.SetReduceMotion(false);
            double navigationY = window.NavIconsTranslate.Y;
            foreach (var target in new[] { NotchView.Timer, NotchView.AudioMixer, NotchView.Secondary,
                NotchView.Media, NotchView.AiUsage, NotchView.Media, NotchView.AiUsage, NotchView.Timer })
            {
                window.NavigateToNotchView(target);
                Assert.Equal(target, coordinator.TargetView);
                await WpfFrameWaiter.NextAsync(ct);
                Assert.True(window.NotchBorder.IsHitTestVisible);
                Assert.Equal(navigationY, window.NavIconsTranslate.Y, 8);
                Assert.Equal(1, window.NavIconsPanel.Opacity, 8);
                Assert.Equal(1, window.SettingsScale.ScaleX, 8);
                Assert.Equal(45, window.SettingsRotate.Angle, 8);
            }
            await WpfFrameWaiter.UntilAsync(() => !coordinator.IsTransitionActive, "latest tab settled", ct);
            Assert.Equal(NotchView.Timer, coordinator.CurrentView);
            Assert.Equal(Visibility.Visible, window.TimerContent.Visibility);
            Assert.Equal(1, window.TimerContent.Opacity);
            Assert.True(window.TimerContent.IsHitTestVisible);
        }
        finally { AnimationConfig.SetReduceMotion(previous); }
    });

    [Theory]
    [InlineData(1, NotchView.Timer)]
    [InlineData(-1, NotchView.Timer)]
    [InlineData(1, NotchView.AiUsage)]
    [InlineData(-1, NotchView.AiUsage)]
    public void ReversingAPageTransitionPreservesTheRenderedFrameAndRestoresLayout(int direction, NotchView detailView) => SharedStaTestRunner.RunAsync(async ct =>
    {
        var mediaTransform = new TranslateTransform(0, 6);
        var media = new Canvas { Name = "ExpandedContent", RenderTransform = mediaTransform };
        var timer = new Canvas { Visibility = Visibility.Collapsed, Opacity = 0 };
        using var presenter = new NotchContentTransitionPresenter(new()
        {
            ExpandedContent = media,
            TimerContent = detailView == NotchView.Timer ? timer : null,
            AiUsageContent = detailView == NotchView.AiUsage ? timer : null
        });
        var motion = new TransitionMotionConfig(TimeSpan.FromMilliseconds(250), AnimationPrimitives._easeUiOut, 60, false);
        var plan = new TransitionPlan(1, NotchView.Media, detailView, NotchShapeState.Expanding, 300, 200, 20, motion, direction);
        presenter.TransitionContent(plan, _ => { });
        double enterDistance = detailView == NotchView.AiUsage ? 28 : 22;
        Assert.Equal(enterDistance * direction, timer.RenderTransform.Value.OffsetX, 8);
        Assert.False(timer.IsHitTestVisible);
        var mediaOffset = Assert.IsType<TranslateTransform>(Assert.IsType<TransformGroup>(media.RenderTransform).Children[^1]);
        var timerOffset = Assert.IsType<TranslateTransform>(Assert.IsType<TransformGroup>(timer.RenderTransform).Children[^1]);
        HoldAt(mediaOffset, TranslateTransform.XProperty, 0, -14 * direction);
        HoldAt(timerOffset, TranslateTransform.XProperty, enterDistance * direction, 0);
        if (detailView == NotchView.AiUsage)
        {
            var mediaScale = Assert.IsType<ScaleTransform>(Assert.IsType<TransformGroup>(media.RenderTransform).Children[^2]);
            var detailScale = Assert.IsType<ScaleTransform>(Assert.IsType<TransformGroup>(timer.RenderTransform).Children[^2]);
            Assert.Equal(0.97, detailScale.ScaleX, 8);
            HoldAt(mediaScale, ScaleTransform.ScaleXProperty, 1, 0.97);
            HoldAt(mediaScale, ScaleTransform.ScaleYProperty, 1, 0.97);
            HoldAt(detailScale, ScaleTransform.ScaleXProperty, 0.97, 1);
            HoldAt(detailScale, ScaleTransform.ScaleYProperty, 0.97, 1);
        }
        HoldAt(media, UIElement.OpacityProperty, 1, 0);
        HoldAt(timer, UIElement.OpacityProperty, 0, 1);
        presenter.CancelActiveTransition();
        Matrix mediaFrame = media.RenderTransform.Value, timerFrame = timer.RenderTransform.Value;
        double mediaOpacity = media.Opacity, timerOpacity = timer.Opacity;
        bool completed = false;

        presenter.TransitionContent(plan with
        {
            SessionId = 2,
            FromView = detailView,
            TargetView = NotchView.Media,
            NavigationDirection = -direction
        }, _ => completed = true);

        Assert.Equal(mediaFrame, media.RenderTransform.Value);
        Assert.Equal(timerFrame, timer.RenderTransform.Value);
        Assert.Equal(mediaOpacity, media.Opacity, 8);
        Assert.Equal(timerOpacity, timer.Opacity, 8);
        await WpfFrameWaiter.UntilAsync(() => completed, "reversed page transition settled", ct);
        Assert.Same(mediaTransform, media.RenderTransform);
        Assert.Equal(6, media.RenderTransform.Value.OffsetY);
        Assert.Equal(0, media.RenderTransform.Value.OffsetX);
        Assert.True(media.IsHitTestVisible);
        Assert.Equal(Visibility.Collapsed, timer.Visibility);
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AiDetailTransitionWithoutMotionSettlesImmediately(bool reduceMotion) => SharedStaTestRunner.Run(() =>
    {
        var media = new Canvas { Name = "ExpandedContent" };
        var detail = new Canvas { Visibility = Visibility.Collapsed, Opacity = 0 };
        using var presenter = new NotchContentTransitionPresenter(new() { ExpandedContent = media, AiUsageContent = detail });
        var motion = new TransitionMotionConfig(TimeSpan.FromMilliseconds(reduceMotion ? 500 : 0),
            AnimationPrimitives._easeUiOut, 60, reduceMotion);
        var plan = new TransitionPlan(1, NotchView.Media, NotchView.AiUsage, NotchShapeState.Expanding, 500, 240, 20, motion);
        bool completed = false;
        presenter.TransitionContent(plan, _ => completed = true);
        Assert.True(completed);
        Assert.Equal(Visibility.Collapsed, media.Visibility);
        Assert.Equal(Visibility.Visible, detail.Visibility);
        Assert.Equal(1, detail.Opacity);
        Assert.True(detail.RenderTransform == null || detail.RenderTransform.Value.IsIdentity);
        Assert.True(detail.IsHitTestVisible);
    });

    private static void HoldAt(IAnimatable target, DependencyProperty property, double from, double to)
    {
        var clock = (AnimationClock)new DoubleAnimation(from, to, TimeSpan.FromSeconds(1)).CreateClock(true);
        target.ApplyAnimationClock(property, clock);
        clock.Controller!.Begin();
        clock.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(350), TimeSeekOrigin.BeginTime);
    }
}

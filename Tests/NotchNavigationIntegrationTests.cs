using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Contracts;
using VNotch.Controllers;
using VNotch.Models;
using VNotch.Services;
using VNotch.ViewModels;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class NotchNavigationIntegrationTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryExpandedViewCanNavigateToEveryOtherViewAndCollapse(bool blurEnabled) => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = new GreetingAcceptanceTests.MainWindowFixture("en", greeting: false,
            settings =>
            {
                settings.EnableLocalOnlyMode = true;
                settings.DisableMouseLeaveAutoClose = true;
                settings.EnableBlurEffects = blurEnabled;
                settings.ShelfWidget = "none";
            },
            services => services.AddSingleton<IMediaDetectionService>(new Fakes.FakeMediaDetectionService()));
        var window = fixture.Window;
        using var mixer = new AudioMixerService();
        typeof(MainWindow).GetField("_audioMixerServiceCached", Private)!.SetValue(window, mixer);
        typeof(MainWindow).GetField("_masterVolumeCached", Private)!.SetValue(window, new Fakes.FakeVolumeService());
        await StartAsync(window, ct);
        var coordinator = Field<NotchTransitionCoordinator>(window, "_transitionCoordinator");
        var views = new[] { NotchView.Media, NotchView.Secondary, NotchView.Timer, NotchView.AudioMixer };
        async Task Navigate(NotchView view)
        {
            if (coordinator.CurrentView != view)
            {
                if (view == NotchView.Compact) window.SetDebugViewLock(false);
                Assert.True(coordinator.RequestView(view, "NavigationMatrix"), $"Navigation from {coordinator.CurrentView} to {view} was rejected.");
                window.SetDebugViewLock(true);
                await WpfFrameWaiter.UntilAsync(() => !coordinator.IsTransitionActive && !IsAnimating(window),
                    $"navigation to {view}", ct);
            }
            Assert.Equal(view, coordinator.CurrentView);
            Assert.Equal(view, ((ShellViewModel)window.DataContext).CurrentView);
            Assert.Equal(view != NotchView.Compact, Property<bool>(window, "_isExpanded"));
            Assert.True(window.NotchBorder.IsHitTestVisible);
        }
        foreach (var from in views)
        {
            foreach (var to in views.Where(view => view != from))
            {
                await Navigate(from);
                await Navigate(to);
                var content = to switch
                {
                    NotchView.Media => window.ExpandedContent,
                    NotchView.Secondary => window.SecondaryContent,
                    NotchView.Timer => window.TimerContent,
                    _ => window.AudioContent
                };
                Assert.Equal(Visibility.Visible, content.Visibility);
            }
            await Navigate(NotchView.Compact);
        }
        Assert.Equal(NotchShapeState.Collapsed, coordinator.ShapeState);
    }, timeoutSeconds: 90);

    [Fact]
    public void InterruptedAnimationsSettleInTheLatestViewAcrossAllConsumers() => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = CreateFixture();
        var window = fixture.Window;
        await StartAsync(window, ct);
        var coordinator = Field<NotchTransitionCoordinator>(window, "_transitionCoordinator");
        Assert.Same(coordinator, Field<INotchManager>(window, "_notchManager").TransitionCoordinator);
        for (int i = 0; i < 12; i++)
        {
            window.SetDebugViewState("MediaExpanded");
            await WpfFrameWaiter.NextAsync(ct);
            window.SetDebugViewState("CollapsedNotch");
            await WpfFrameWaiter.NextAsync(ct);
            window.SetDebugViewState(i % 2 == 0 ? "SecondaryShelf" : "TimerStopwatch");
            await WpfFrameWaiter.NextAsync(ct);
        }
        window.SetDebugViewState("SecondaryShelf");
        await WpfFrameWaiter.UntilAsync(() => !IsAnimating(window), "the final shelf transition", ct);
        Assert.Equal(NotchView.Secondary, coordinator.CurrentView);
        Assert.Equal(NotchShapeState.Expanded, coordinator.ShapeState);
        Assert.Equal(NotchView.Secondary, ((ShellViewModel)window.DataContext).CurrentView);
        Assert.True(Property<bool>(window, "_isExpanded"));
        Assert.True(Property<bool>(window, "_isSecondaryView"));
        Assert.Equal(Visibility.Visible, window.SecondaryContent.Visibility);

        window.SetDebugViewState("CollapsedNotch");
        await WpfFrameWaiter.UntilAsync(() => !IsAnimating(window), "collapse after reversal", ct);
        Assert.Equal(NotchView.Compact, coordinator.CurrentView);
        Assert.Equal(NotchView.Compact, ((ShellViewModel)window.DataContext).CurrentView);
        Assert.False(Property<bool>(window, "_isExpanded"));
        Assert.False(Property<bool>(window, "_isSecondaryView"));
    });

    [Fact]
    public void ReopeningMediaDuringCollapseRestoresTheExpandedShell() => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = CreateFixture();
        var window = fixture.Window;
        await StartAsync(window, ct);
        window.SetDebugViewState("MediaExpanded");
        await WpfFrameWaiter.UntilAsync(() => !IsAnimating(window), "initial media expansion", ct);
        double expandedWidth = window.NotchBorder.Width;
        window.SetDebugViewState("CollapsedNotch");
        await WpfFrameWaiter.UntilAsync(() => window.NotchBorder.Width < expandedWidth - 10, "partway through collapse", ct);
        var coordinator = Field<NotchTransitionCoordinator>(window, "_transitionCoordinator");
        Assert.True(coordinator.RequestView(NotchView.Media, "ReverseCollapse"));
        await WpfFrameWaiter.UntilAsync(() => !IsAnimating(window), "reversed media expansion", ct);
        Assert.Equal(NotchView.Media, coordinator.CurrentView);
        Assert.Equal(expandedWidth, window.NotchBorder.Width, 2);
        Assert.Equal(Visibility.Visible, window.ExpandedContent.Visibility);
        Assert.Equal(1, window.ExpandedContent.Opacity, 2);
    });

    [Fact]
    public void CountdownDismissCompletesTheCoordinatorAndAllowsReopening() => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = CreateFixture();
        var window = fixture.Window;
        await StartAsync(window, ct);
        var coordinator = Field<NotchTransitionCoordinator>(window, "_transitionCoordinator");
        coordinator.NotifyCountdownCompleted();
        await WpfFrameWaiter.UntilAsync(() => !IsAnimating(window), "countdown completion", ct);
        Assert.Equal(NotchView.Timer, coordinator.CurrentView);

        window.SetDebugViewLock(false);
        var click = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        {
            RoutedEvent = UIElement.MouseLeftButtonDownEvent
        };
        typeof(MainWindow).GetMethod("CountdownDismiss_Click", Private)!.Invoke(window, [window, click]);
        window.SetDebugViewLock(true);
        Assert.Equal(NotchView.Compact, coordinator.TargetView);
        await WpfFrameWaiter.UntilAsync(() => !IsAnimating(window), "countdown dismissal", ct);
        Assert.Equal(NotchView.Compact, coordinator.CurrentView);
        Assert.Equal(NotchView.Compact, ((ShellViewModel)window.DataContext).CurrentView);
        window.SetDebugViewState("TimerStopwatch");
        await WpfFrameWaiter.UntilAsync(() => !IsAnimating(window), "reopening timer", ct);
        Assert.Equal(NotchView.Timer, coordinator.CurrentView);
        Assert.Equal(Visibility.Visible, window.TimerContent.Visibility);
    });

    [Fact]
    public void ExpansionKeepsContentCenteredAndDetachesSizeHandlersOnCompletion() => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = CreateFixture();
        var window = fixture.Window;
        await StartAsync(window, ct);
        window.SetDebugViewState("MediaExpanded");
        int samples = 0;
        double maxError = 0;
        void Sample(object? _, EventArgs args)
        {
            if (Field<SizeChangedEventHandler?>(window, "_mainViewHorizontalStabilizer") == null) return;
            var origin = window.ExpandedContent.TransformToAncestor(window.NotchContainer).Transform(new Point());
            double expected = (window.NotchContainer.ActualWidth - window.ExpandedContent.ActualWidth) / 2;
            maxError = Math.Max(maxError, Math.Abs(origin.X - expected));
            samples++;
        }
        CompositionTarget.Rendering += Sample;
        try { await WpfFrameWaiter.UntilAsync(() => !IsAnimating(window), "centered expansion", ct); }
        finally { CompositionTarget.Rendering -= Sample; }
        Assert.True(samples > 0);
        Assert.InRange(maxError, 0, 1.5);
        Assert.Null(Field<SizeChangedEventHandler?>(window, "_mainViewHorizontalStabilizer"));
        Assert.Null(Field<System.Windows.Threading.DispatcherOperation?>(window, "_mainViewHorizontalStabilizerOperation"));
    });

    private static GreetingAcceptanceTests.MainWindowFixture CreateFixture() => new("en", greeting: false,
        settings => { settings.EnableLocalOnlyMode = true; settings.DisableMouseLeaveAutoClose = true; },
        services => services.AddSingleton<IMediaDetectionService>(new Fakes.FakeMediaDetectionService()));

    private static async Task StartAsync(MainWindow window, CancellationToken ct)
    {
        window.SetDebugViewLock(true);
        window.ShowActivated = false;
        window.Show();
        await WpfFrameWaiter.UntilAsync(() => Field<bool>(window, "_isStartupLayoutReady") && !IsAnimating(window), "startup", ct);
        Field<IMediaDetectionService>(window, "_mediaService").Stop();
    }

    private static T Field<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, Private)!.GetValue(window)!;
    private static T Property<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetProperty(name, Private)!.GetValue(window)!;
    private static bool IsAnimating(MainWindow window) => Property<bool>(window, "_isAnimating");
}

using System.Reflection;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Controllers;
using VNotch.Models;
using VNotch.Services;
using VNotch.Tests.Fakes;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class MainWindowNavigationSequenceTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void RepeatedNavigationSettlesOnTheRequestedViewAndCollapses(bool island, bool reducedMotion) => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        await using var fixture = new GreetingAcceptanceTests.MainWindowFixture("en", false,
            s => { s.EnableLocalOnlyMode = true; s.DisableMouseLeaveAutoClose = true; s.EnableDynamicIslandMode = island; },
            services => services.AddSingleton<IMediaDetectionService>(new FakeMediaDetectionService()));
        var window = fixture.Window;
        var coordinator = (NotchTransitionCoordinator)typeof(MainWindow).GetField("_transitionCoordinator", Private)!.GetValue(window)!;
        try
        {
            AnimationConfig.SetReduceMotion(reducedMotion);
            window.Show();
            var targets = new[] { NotchView.Media, NotchView.Timer, NotchView.AudioMixer,
                NotchView.Media, NotchView.Secondary, NotchView.Timer, NotchView.Secondary,
                NotchView.AudioMixer, NotchView.Timer, NotchView.Media, NotchView.Compact };
            for (int i = 0; i < targets.Length; i++)
            {
                var target = targets[i];
                AnimationConfig.SetReduceMotion(reducedMotion || (i > 2 && i < targets.Length - 1));
                Assert.True(coordinator.RequestView(target, "NavigationRegression"));
                await WpfFrameWaiter.UntilAsync(() => !coordinator.IsTransitionActive,
                    "navigation to " + target, ct);
                Assert.Equal(target, coordinator.CurrentView);
                Assert.Equal(target == NotchView.Compact ? NotchShapeState.Collapsed : NotchShapeState.Expanded,
                    coordinator.ShapeState);
                Assert.True(double.IsFinite(window.NotchBorder.Width));
                Assert.True(window.NotchBorder.Width > 0);
                Assert.Equal(target == NotchView.Secondary ? Visibility.Visible : Visibility.Collapsed,
                    window.SecondaryContent.Visibility);
            }
        }
        finally { AnimationConfig.SetReduceMotion(previous); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CollapsingExpandedMusicRestoresTheNormalPlayerAndBatteryPreference(bool battery) => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = new GreetingAcceptanceTests.MainWindowFixture("en", false,
            s => { s.EnableLocalOnlyMode = true; s.DisableMouseLeaveAutoClose = true; s.ShowBatteryIndicator = battery; },
            services => services.AddSingleton<IMediaDetectionService>(new FakeMediaDetectionService()));
        var window = fixture.Window;
        var coordinator = (NotchTransitionCoordinator)typeof(MainWindow).GetField("_transitionCoordinator", Private)!.GetValue(window)!;
        window.Show();
        Assert.True(coordinator.RequestView(NotchView.Media, "MusicExpansion", isMusic: true));
        await WpfFrameWaiter.UntilAsync(() => !coordinator.IsTransitionActive, "music expansion", ct);
        Assert.Equal(NotchShapeState.MusicExpanded, coordinator.ShapeState);
        Assert.True(coordinator.RequestView(NotchView.Media, "CollapseMusicWidget"));
        await WpfFrameWaiter.UntilAsync(() => !coordinator.IsTransitionActive, "music collapse", ct);
        Assert.Equal(NotchShapeState.Expanded, coordinator.ShapeState);
        Assert.Equal(Visibility.Visible, window.MediaPlayback.MediaControls.Visibility);
        Assert.Equal(Visibility.Collapsed, window.InlineControls.Visibility);
        Assert.Equal(battery ? Visibility.Visible : Visibility.Collapsed, window.BatterySection.Visibility);
        Assert.True(double.IsNaN(window.MediaWidgetContainer.Width));
    });
}

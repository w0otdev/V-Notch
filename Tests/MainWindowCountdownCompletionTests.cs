using System.Reflection;
using System.Windows;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Controllers;
using VNotch.Models;
using VNotch.Services;
using VNotch.Tests.Fakes;
using VNotch.ViewModels;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class MainWindowCountdownCompletionTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task RestartingACompletedCountdownRestoresTheClockAndStartsTheSameTimer(bool music) => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = Create();
        var window = fixture.Window;
        await ShowCompletion(window, music, ct);
        var timer = ((ShellViewModel)window.DataContext).Timer;
        var args = Click();
        Invoke(window, "CountdownRestart_Click", window, args);
        Assert.True(args.Handled);
        Assert.True(timer.IsRunning);
        Assert.True(Get<bool>(window, "_isAnimating"));
        await WpfFrameWaiter.UntilAsync(() => !Get<bool>(window, "_isAnimating") && window.CountdownCompleteOverlay.Visibility == Visibility.Collapsed, "countdown restart transition", ct);
        Assert.Equal(Visibility.Visible, window.TimerContent.Visibility);
        Assert.Equal(1, window.TimerContent.Opacity);
        Assert.Equal(Visibility.Visible, window.NavIconsPanel.Visibility);
        Assert.Equal(1, window.NavIconsPanel.Opacity);
        Assert.Null(window.TimerContent.RenderTransform);
        Assert.True(window.NotchBorder.IsHitTestVisible);
        Assert.False(Get<bool>(window, "_isScrollSessionLocked"));
        Assert.True(timer.Remaining > TimeSpan.Zero);
        Assert.False(Get<bool>(window, "_isCountdownCompleteVisible"));
        timer.Pause();
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task DismissingACompletedCountdownRestoresTheAppropriateCompactContent(bool music) => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = Create();
        var window = fixture.Window;
        await ShowCompletion(window, music, ct);
        var mouse = new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount);
        Invoke(window, "CountdownCompleteOverlay_MouseEnter", window, mouse);
        await WpfFrameWaiter.UntilAsync(() => Math.Abs(window.NotchScale.ScaleX - 1.004) < .00001, "countdown completion hover", ct);
        Invoke(window, "CountdownCompleteOverlay_MouseLeave", window, mouse);
        var args = Click();
        Invoke(window, "CountdownDismiss_Click", window, args);
        Assert.True(args.Handled);
        await WpfFrameWaiter.UntilAsync(() => !Get<bool>(window, "_isAnimating") && window.CountdownCompleteOverlay.Visibility == Visibility.Collapsed, "countdown dismiss transition", ct);
        var content = music ? window.MusicCompactContent : window.CollapsedContent;
        await WpfFrameWaiter.UntilAsync(() => content.Opacity == 1 && content.RenderTransform == null, "restored compact content", ct);
        Assert.Equal(Visibility.Visible, content.Visibility);
        Assert.Equal(Visibility.Collapsed, window.TimerContent.Visibility);
        Assert.False(Get<bool>(window, "_isCountdownCompleteVisible"));
        Assert.Equal(NotchView.Compact, Get<NotchTransitionCoordinator>(window, "_transitionCoordinator").CurrentView);
        Assert.True(window.NotchBorder.IsHitTestVisible);
    });

    private static async Task ShowCompletion(MainWindow window, bool music, CancellationToken ct)
    {
        window.NotchBorder.Width = 300;
        window.NotchBorder.Height = 40;
        if (music) window.SetDebugViewState("CompactMusicPill");
        Get<NotchTransitionCoordinator>(window, "_transitionCoordinator").NotifyCountdownCompleted();
        await WpfFrameWaiter.UntilAsync(() => Get<bool>(window, "_isCountdownCompleteVisible") && !Get<bool>(window, "_isAnimating") && window.CountdownCompleteOverlay.Visibility == Visibility.Visible, "countdown completion presentation", ct);
        await WpfFrameWaiter.UntilAsync(() => window.CountdownRestartHost.Opacity == 1 && window.CountdownDismissHost.Opacity == 1, "countdown completion controls", ct);
    }
    private static GreetingAcceptanceTests.MainWindowFixture Create() => new("en", false, settings => { settings.EnableLocalOnlyMode = true; settings.DisableMouseLeaveAutoClose = true; }, services => services.AddSingleton<IMediaDetectionService>(new FakeMediaDetectionService()));
    private static MouseButtonEventArgs Click() => new(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent };
    private static object? Invoke(MainWindow window, string name, params object?[] arguments) => typeof(MainWindow).GetMethod(name, Private)!.Invoke(window, arguments);
    private static T Get<T>(MainWindow window, string name)
    {
        var field = typeof(MainWindow).GetField(name, Private);
        return (T)(field != null ? field.GetValue(window) : typeof(MainWindow).GetProperty(name, Private)!.GetValue(window))!;
    }
}

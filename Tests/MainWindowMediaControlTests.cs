using System.Reflection;
using System.Windows;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Models;
using VNotch.Services;
using VNotch.Tests.Fakes;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class MainWindowMediaControlTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MainAndInlineButtonsDispatchPlaybackCommandsExactlyOnce(bool inline) => SharedStaTestRunner.RunAsync(async ct =>
    {
        var media = new FakeMediaDetectionService();
        await using var fixture = CreateFixture(media);
        var window = fixture.Window;
        var prefix = inline ? "Inline" : "";
        Invoke(window, prefix + "PlayPauseButton_Click", window, ClickArgs());
        await WpfFrameWaiter.UntilAsync(() => media.PlayPauseCount == 1, "play/pause command", ct);
        ExpireThrottles(window);
        Invoke(window, prefix + "NextButton_Click", window, ClickArgs());
        await WpfFrameWaiter.UntilAsync(() => media.NextTrackCount == 1, "next track command", ct);
        ExpireThrottles(window);
        Invoke(window, prefix + "PrevButton_Click", window, ClickArgs());
        await WpfFrameWaiter.UntilAsync(() => media.PreviousTrackCount == 1, "previous track command", ct);
        ExpireThrottles(window);
        Invoke(window, prefix + "PlayPauseButton_Click", window, ClickArgs());
        await WpfFrameWaiter.UntilAsync(() => media.PlayPauseCount == 2, "second play/pause command", ct);
        Assert.Equal(2, media.PlayPauseCount);
        Assert.Equal(1, media.NextTrackCount);
        Assert.Equal(1, media.PreviousTrackCount);
    });

    [Fact]
    public void InstantPlayPauseSuppressesRepeatedClicksDuringItsDebounceWindow() => SharedStaTestRunner.RunAsync(async ct =>
    {
        var media = new FakeMediaDetectionService();
        await using var fixture = CreateFixture(media);
        var window = fixture.Window;
        await (Task)Invoke(window, "TogglePlayPauseInstantAsync")!;
        await (Task)Invoke(window, "TogglePlayPauseInstantAsync")!;
        Assert.Equal(1, media.PlayPauseCount);
        ExpireThrottles(window);
        await (Task)Invoke(window, "TogglePlayPauseInstantAsync")!;
        Assert.Equal(2, media.PlayPauseCount);
        ct.ThrowIfCancellationRequested();
    });

    [Theory]
    [InlineData(0.5f, 120, 0.55f)]
    [InlineData(0.5f, -120, 0.45f)]
    [InlineData(0.98f, 120, 1f)]
    [InlineData(0.02f, -120, 0f)]
    public void CompactVolumeChangesClampAndRestoreTheMediaPill(float initial, int delta, float expected) => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = CreateFixture(new FakeMediaDetectionService());
        var window = fixture.Window;
        window.SetDebugViewState("CompactMusicPill");
        typeof(MainWindow).GetField("_currentVolume", Private)!.SetValue(window, initial);
        Invoke(window, "ApplyVolumeStep", delta);
        Assert.Equal(expected, Field<float>(window, "_currentVolume"), 4);
        Assert.True(Field<bool>(window, "_isVolumeIndicatorActive"));
        Assert.Equal(Visibility.Visible, window.VolumeIndicatorContainer.Visibility);
        await WpfFrameWaiter.UntilAsync(() => Math.Abs(window.VolumeIndicatorContainer.Opacity - 1) < 1e-6,
            "compact volume indicator", ct);
        Invoke(window, "HideVolumeIndicator");
        await WpfFrameWaiter.UntilAsync(() => window.VolumeIndicatorContainer.Visibility == Visibility.Collapsed,
            "compact volume indicator dismissal", ct);
        Assert.False(Field<bool>(window, "_isVolumeIndicatorActive"));
        Assert.Equal(Visibility.Visible, window.CompactThumbnailBorder.Visibility);
        Invoke(window, "ShowVolumeIndicator", expected);
        Invoke(window, "DismissVolumeIndicatorImmediate", true, false);
        Assert.False(Field<bool>(window, "_isVolumeIndicatorActive"));
        Assert.Equal(Visibility.Collapsed, window.VolumeIndicatorContainer.Visibility);
    });

    private static GreetingAcceptanceTests.MainWindowFixture CreateFixture(FakeMediaDetectionService media) => new("en", greeting: false,
        settings => { settings.EnableLocalOnlyMode = true; settings.DisableMouseLeaveAutoClose = true; },
        services => services.AddSingleton<IMediaDetectionService>(media));
    private static MouseButtonEventArgs ClickArgs() => new(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
    { RoutedEvent = UIElement.MouseLeftButtonDownEvent };
    private static object? Invoke(MainWindow window, string method, params object?[] arguments) =>
        typeof(MainWindow).GetMethod(method, Private)!.Invoke(window, arguments);
    private static T Field<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, Private)!.GetValue(window)!;
    // Both MainWindow (250 ms) and MediaViewModel (200 ms) throttle transport commands by
    // wall clock. Backdating them is deterministic; sleeping past the window was flaky on slow agents.
    private static void ExpireThrottles(MainWindow window)
    {
        typeof(MainWindow).GetField("_lastMediaActionTime", Private)!.SetValue(window, DateTime.UtcNow.AddSeconds(-5));
        TestWait.ExpireMediaThrottle(((VNotch.ViewModels.ShellViewModel)window.DataContext).Media);
    }
}

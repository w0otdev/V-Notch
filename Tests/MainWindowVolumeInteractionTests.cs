using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Services;
using VNotch.Tests.Fakes;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class MainWindowVolumeInteractionTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData(120)]
    [InlineData(-120)]
    public Task WheelDetentsActivateImmediatelyAndSmallDeltasAccumulateUntilIntentIsClear(int direction) => SharedStaTestRunner.RunAsync(ct =>
    {
        using var fixture = Create(new FakeMediaDetectionService());
        var window = fixture.Window;
        Assert.False(Wheel(window, 0, out _));
        Assert.True(Wheel(window, direction, out int delta));
        Assert.Equal(direction, delta);
        Assert.True(Wheel(window, Math.Sign(direction) * 10, out delta));
        Assert.Equal(Math.Sign(direction) * 10, delta);
        Invoke(window, "ResetCompactVolumeWheelIntent");
        for (int i = 0; i < 4; i++) Assert.False(Wheel(window, Math.Sign(direction) * 50, out _));
        Assert.True(Wheel(window, Math.Sign(direction) * 50, out delta));
        Assert.Equal(direction, delta);
        Invoke(window, "SuppressCompactVolumeWheelForClick");
        Assert.False(Wheel(window, direction, out _));
        Set(window, "_suppressCompactVolumeWheelUntilUtc", DateTime.MinValue);
        Assert.False(Wheel(window, 100, out _));
        Assert.False(Wheel(window, -100, out _));
        Assert.Equal(-100d, Get<double>(window, "_compactVolumeWheelAccumulator"));
        Set(window, "_lastCompactVolumeWheelUtc", DateTime.UtcNow.AddSeconds(-2));
        Assert.False(Wheel(window, -100, out _));
        Assert.Equal(-100d, Get<double>(window, "_compactVolumeWheelAccumulator"));
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task FirstWheelStepReadsTheSessionBaselineAndDismissalCancelsItsPresentation(bool available) => SharedStaTestRunner.RunAsync(async ct =>
    {
        var media = new FakeMediaDetectionService { ReadSessionVolume = () => (available, .7f, false) };
        using var fixture = Create(media);
        var window = fixture.Window;
        window.SetDebugViewState("CompactMusicPill");
        Set(window, "_currentVolume", .3f);
        Invoke(window, "AdjustVolumeByScroll", 120);
        await WpfFrameWaiter.UntilAsync(() => media.LastSessionVolume.HasValue, "session volume wheel write", ct);
        Assert.Equal(available ? .75f : .35f, media.LastSessionVolume!.Value, 4);
        Assert.True(Get<bool>(window, "_isVolumeIndicatorActive"));
        await WpfFrameWaiter.UntilAsync(() => window.VolumeIndicatorContainer.Opacity == 1, "volume indicator entrance", ct);
        Invoke(window, "DismissVolumeIndicatorImmediate", true, true);
        Assert.True(Get<bool>(window, "_isVolumeIndicatorExiting"));
        Assert.False(window.VolumeIndicatorContainer.IsHitTestVisible);
        await WpfFrameWaiter.UntilAsync(() => !Get<bool>(window, "_isVolumeIndicatorExiting"), "volume indicator exit", ct);
        Assert.Equal(Visibility.Collapsed, window.VolumeIndicatorContainer.Visibility);
        Assert.True(window.VolumeIndicatorContainer.IsHitTestVisible);
    });

    [Fact]
    public Task VolumeIndicatorTimerReturnsTheCompactPillToMedia() => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = Create(new FakeMediaDetectionService());
        var window = fixture.Window;
        window.SetDebugViewState("CompactMusicPill");
        Invoke(window, "ShowVolumeIndicator", .6f);
        var timer = Get<System.Windows.Threading.DispatcherTimer>(window, "_volumeIndicatorHideTimer");
        timer.Interval = TimeSpan.FromMilliseconds(1);
        await WpfFrameWaiter.UntilAsync(() => !Get<bool>(window, "_isVolumeIndicatorActive"), "automatic volume indicator dismissal", ct);
        await WpfFrameWaiter.UntilAsync(() => window.VolumeIndicatorContainer.Visibility == Visibility.Collapsed, "volume fade completion", ct);
        Assert.False(timer.IsEnabled);
        Assert.False(Get<bool>(window, "_volumeSynced"));
    });

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public Task SessionVolumeRefreshUpdatesOnlyAnAvailableSession(bool available, bool muted) => SharedStaTestRunner.RunAsync(async ct =>
    {
        var media = new FakeMediaDetectionService { ReadSessionVolume = () => (available, .72f, muted) };
        using var fixture = Create(media);
        var window = fixture.Window;
        Set(window, "_currentVolume", .25f);
        Invoke(window, "SyncVolumeFromActiveSession");
        await WpfFrameWaiter.UntilAsync(() => !Get<bool>(window, "_volumeReadInFlight"), "session volume refresh", ct);
        Assert.Equal(available ? .72f : .25f, Get<float>(window, "_currentVolume"), 4);
        if (available) Assert.Equal(.72, window.VolumeBarScale.ScaleX, 5);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ADelayedReadCannotOverwriteADragOrANewerVolumeInteraction(bool dragging) => SharedStaTestRunner.RunAsync(async ct =>
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var media = new FakeMediaDetectionService { ReadSessionVolume = () => { started.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException(); return (true, .9f, false); } };
        using var fixture = Create(media);
        var window = fixture.Window;
        try
        {
            Set(window, "_currentVolume", .25f);
            Invoke(window, "SyncVolumeFromActiveSession");
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            Invoke(window, "SyncVolumeFromActiveSession");
            if (dragging) Set(window, "_isDraggingVolume", true);
            else Set(window, "_volumeInteractionVersion", Get<int>(window, "_volumeInteractionVersion") + 1);
            release.Set();
            await WpfFrameWaiter.UntilAsync(() => !Get<bool>(window, "_volumeReadInFlight"), "discarding stale volume read", ct);
            Assert.Equal(.25f, Get<float>(window, "_currentVolume"));
        }
        finally { release.Set(); Set(window, "_isDraggingVolume", false); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task MuteClicksAreSerializedAndRefreshTheIconWhenTheDriverAcceptsTheToggle(bool accepted) => SharedStaTestRunner.RunAsync(async ct =>
    {
        var media = new FakeMediaDetectionService { ReadSessionVolume = () => (true, .4f, true), SessionMuteToggleSucceeded = accepted };
        using var fixture = Create(media);
        var window = fixture.Window;
        int version = Get<int>(window, "_volumeInteractionVersion");
        for (int i = 0; i < 3; i++)
        {
            var args = Click();
            Invoke(window, "VolumeIcon_MouseDown", window, args);
            Assert.True(args.Handled);
        }
        await WpfFrameWaiter.UntilAsync(() => media.SessionMuteToggleCount == 3 && (!accepted || Get<float>(window, "_currentVolume") == .4f), "serialized session mute clicks", ct);
        Assert.True(Get<int>(window, "_volumeInteractionVersion") >= version + 3);
        await WpfFrameWaiter.UntilAsync(() => !Get<bool>(window, "_volumeReadInFlight"), "mute refresh completion", ct);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task VolumeDragKeepsThePresentationConsistentAndReleasesItsDragState(bool compact) => SharedStaTestRunner.RunAsync(async ct =>
    {
        var media = new FakeMediaDetectionService();
        using var fixture = Create(media);
        var window = fixture.Window;
        if (compact)
        {
            window.SetDebugViewState("CompactMusicPill");
            Invoke(window, "ShowVolumeIndicator", .5f);
        }
        string prefix = compact ? "VolumeIndicator" : "VolumeBar";
        var down = Click();
        Invoke(window, prefix + "_MouseDown", window, down);
        Assert.True(down.Handled);
        Assert.True(Get<bool>(window, compact ? "_isDraggingVolumeIndicator" : "_isDraggingVolume"));
        await WpfFrameWaiter.UntilAsync(() => media.LastSessionVolume.HasValue, "volume drag write", ct);
        Assert.InRange(media.LastSessionVolume!.Value, 0, 1);
        var up = Click();
        Invoke(window, prefix + "_MouseUp", window, up);
        Assert.True(up.Handled);
        Assert.False(Get<bool>(window, compact ? "_isDraggingVolumeIndicator" : "_isDraggingVolume"));
        var ignored = Click();
        Invoke(window, prefix + "_MouseUp", window, ignored);
        Assert.False(ignored.Handled);
        if (compact) Invoke(window, "DismissVolumeIndicatorImmediate", true, false);
    });

    [Fact]
    public Task MediaButtonsAndVolumeIconCompleteTheirHoverAnimations() => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = Create(new FakeMediaDetectionService());
        var window = fixture.Window;
        var button = new Border { RenderTransform = new ScaleTransform(1, 1) };
        var args = new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount);
        Invoke(window, "MediaButton_MouseEnter", button, args);
        await WpfFrameWaiter.UntilAsync(() => Math.Abs(((ScaleTransform)button.RenderTransform).ScaleX - 1.18) < .0001, "media hover scale", ct);
        Assert.NotNull(button.CacheMode);
        Invoke(window, "MediaButton_MouseLeave", button, args);
        await WpfFrameWaiter.UntilAsync(() => button.CacheMode == null, "media hover cleanup", ct);
        Assert.Equal(1, ((ScaleTransform)button.RenderTransform).ScaleX, 5);
        Invoke(window, "VolumeIcon_MouseEnter", window, args);
        await WpfFrameWaiter.UntilAsync(() => Math.Abs(window.VolumeIconScale.ScaleX - 1.2) < .0001, "volume icon hover", ct);
        Invoke(window, "VolumeIcon_MouseLeave", window, args);
        await WpfFrameWaiter.UntilAsync(() => Math.Abs(window.VolumeIconScale.ScaleX - 1) < .0001, "volume icon resting scale", ct);
    });

    private static GreetingAcceptanceTests.MainWindowFixture Create(FakeMediaDetectionService media) => new("en", false, settings => { settings.EnableLocalOnlyMode = true; settings.DisableMouseLeaveAutoClose = true; }, services => services.AddSingleton<IMediaDetectionService>(media));
    private static bool Wheel(MainWindow window, int raw, out int result)
    {
        object?[] arguments = [raw, 0];
        bool accepted = (bool)Invoke(window, "TryGetCompactVolumeWheelDelta", arguments)!;
        result = (int)arguments[1]!;
        return accepted;
    }
    private static MouseButtonEventArgs Click() => new(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent };
    private static object? Invoke(MainWindow window, string name, params object?[] arguments) => typeof(MainWindow).GetMethod(name, Private)!.Invoke(window, arguments);
    private static T Get<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, Private)!.GetValue(window)!;
    private static void Set(MainWindow window, string name, object value) => typeof(MainWindow).GetField(name, Private)!.SetValue(window, value);
}

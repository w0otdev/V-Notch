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

    [Fact]
    public void VolumeFillChangesWidthWithoutSquashingRoundedEnds() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = Create(new FakeMediaDetectionService());
        var window = fixture.Window;
        foreach (var (container, fill, level, radius) in new[]
        {
            ((FrameworkElement)window.VolumeBarContainer, window.VolumeBarFront, window.VolumeBarScale, 2d),
            ((FrameworkElement)window.VolumeIndicatorContainer, window.VolumeIndicatorFill, window.VolumeIndicatorScale, 3d)
        })
        {
            container.Visibility = Visibility.Visible;
            container.Measure(new Size(300, 20));
            container.Arrange(new Rect(0, 0, 300, 20));
            foreach (double volume in new[] { 0d, .01, .1, .5, 1 })
            {
                level.ScaleX = volume;
                System.Windows.Data.BindingOperations.GetMultiBindingExpression(fill, FrameworkElement.WidthProperty)!.UpdateTarget();
                Assert.Equal(container.ActualWidth * volume, fill.Width, 5);
                Assert.True(fill.RenderTransform.Value.IsIdentity);
                Assert.Equal(new CornerRadius(radius), fill.CornerRadius);
            }
        }
        ct.ThrowIfCancellationRequested();
    });

    [Theory]
    [InlineData(120)]
    [InlineData(-120)]
    public void WheelDetentsActivateImmediatelyAndSmallDeltasAccumulateUntilIntentIsClear(int direction) => SharedStaTestRunner.RunAsync(ct =>
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
    public void FirstWheelStepReadsTheSessionBaselineAndDismissalCancelsItsPresentation(bool available) => SharedStaTestRunner.RunAsync(async ct =>
    {
        var media = new FakeMediaDetectionService { ReadSessionVolume = () => (available, .7f, false) };
        await using var fixture = Create(media);
        var window = fixture.Window;
        window.SetDebugViewState("CompactMusicPill");
        Set(window, "_currentVolume", .3f);
        Invoke(window, "AdjustVolumeByScroll", 120);
        await WpfFrameWaiter.UntilAsync(() => media.LastSessionVolume.HasValue, "session volume wheel write", ct);
        Assert.Equal(available ? .75f : .35f, media.LastSessionVolume!.Value, 4);
        Assert.True(Get<bool>(window, "_isVolumeIndicatorActive"));
        await WpfFrameWaiter.UntilAsync(() => Math.Abs(window.VolumeIndicatorContainer.Opacity - 1) < 1e-6, "volume indicator entrance", ct);
        Invoke(window, "DismissVolumeIndicatorImmediate", true, true);
        Assert.True(Get<bool>(window, "_isVolumeIndicatorExiting"));
        Assert.False(window.VolumeIndicatorContainer.IsHitTestVisible);
        await WpfFrameWaiter.UntilAsync(() => !Get<bool>(window, "_isVolumeIndicatorExiting"), "volume indicator exit", ct);
        Assert.Equal(Visibility.Collapsed, window.VolumeIndicatorContainer.Visibility);
        Assert.True(window.VolumeIndicatorContainer.IsHitTestVisible);
    });

    [Fact]
    public void VolumeIndicatorTimerReturnsTheCompactPillToMedia() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = Create(new FakeMediaDetectionService());
        var window = fixture.Window;
        window.SetDebugViewState("CompactMusicPill");
        Invoke(window, "ShowVolumeIndicator", .6f);
        var timer = Get<UiScheduledTimer>(window, "_volumeIndicatorHideTimer");
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
    public void SessionVolumeRefreshUpdatesOnlyAnAvailableSession(bool available, bool muted) => SharedStaTestRunner.RunAsync(async ct =>
    {
        var media = new FakeMediaDetectionService { ReadSessionVolume = () => (available, .72f, muted) };
        await using var fixture = Create(media);
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
    public void ADelayedReadCannotOverwriteADragOrANewerVolumeInteraction(bool dragging) => SharedStaTestRunner.RunAsync(async ct =>
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var media = new FakeMediaDetectionService { ReadSessionVolume = () => { started.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(5))) { throw new TimeoutException(); } return (true, .9f, false); } };
        await using var fixture = Create(media);
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
    public void MuteClicksAreSerializedAndRefreshTheIconWhenTheDriverAcceptsTheToggle(bool accepted) => SharedStaTestRunner.RunAsync(async ct =>
    {
        var media = new FakeMediaDetectionService { ReadSessionVolume = () => (true, .4f, true), SessionMuteToggleSucceeded = accepted };
        await using var fixture = Create(media);
        var window = fixture.Window;
        int version = Get<int>(window, "_volumeInteractionVersion");
        for (int i = 0; i < 3; i++)
        {
            var args = Click();
            Invoke(window, "VolumeIcon_MouseDown", window, args);
            Assert.True(args.Handled);
        }
        await WpfFrameWaiter.UntilAsync(() => media.SessionMuteToggleCount == 3 && (!accepted || Math.Abs(Get<float>(window, "_currentVolume") - .4f) < 1e-4f), "serialized session mute clicks", ct);
        Assert.True(Get<int>(window, "_volumeInteractionVersion") >= version + 3);
        await WpfFrameWaiter.UntilAsync(() => !Get<bool>(window, "_volumeReadInFlight"), "mute refresh completion", ct);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void VolumeDragKeepsThePresentationConsistentAndReleasesItsDragState(bool compact) => SharedStaTestRunner.RunAsync(async ct =>
    {
        var media = new FakeMediaDetectionService();
        await using var fixture = Create(media);
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
    public void MediaButtonsAndVolumeIconCompleteTheirHoverAnimations() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = Create(new FakeMediaDetectionService());
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

    [Fact]
    public void VolumeIndicatorHudUpdatesSegmentedTicksIconAndPercentageLabel() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = Create(new FakeMediaDetectionService());
        var window = fixture.Window;
        window.SetDebugViewState("CompactMusicPill");
        double collapsedHeight = Get<double>(window, "_collapsedHeight");

        Invoke(window, "SetVolumeHudMediaTint", Color.FromRgb(0x3A, 0x96, 0xFF), true);
        Invoke(window, "ShowVolumeIndicator", .81f);
        Assert.Equal("81%", window.VolumeIndicatorPercentText.Text);
        Assert.Equal("", window.VolumeIndicatorIcon.Text);
        Assert.Equal(Visibility.Collapsed, window.VolumeIndicatorMuteSlash.Visibility);
        Assert.Equal(40, window.VolumeIndicatorTicksHost.Children.Count);

        var brushes = Get<SolidColorBrush[]>(window, "_volumeHudTickBrushes");
        int litCount = brushes.Count(b => b.Color.A >= 0xF0);
        Assert.Equal(32, litCount);
        Assert.NotEqual(brushes[0].Color, brushes[31].Color);

        // Hover while volume HUD is active must not stretch NotchScale
        Invoke(window, "AnimateNotchHover", true);
        Assert.Equal(1.0, window.NotchScale.ScaleX, 4);

        // Overscroll above 100% and below 0% triggers spring transform without throwing
        Set(window, "_currentVolume", 1.0f);
        Invoke(window, "ApplyVolumeStep", 120);
        Assert.Equal("100%", window.VolumeIndicatorPercentText.Text);

        Invoke(window, "ShowVolumeIndicator", 0f);
        Assert.Equal("0%", window.VolumeIndicatorPercentText.Text);
        Assert.Equal("", window.VolumeIndicatorIcon.Text);
        Assert.Equal(Visibility.Visible, window.VolumeIndicatorMuteSlash.Visibility);
        Assert.Equal(0.0, window.VolumeIndicatorWave1.Opacity, 4);
        Assert.Equal(0.0, window.VolumeIndicatorWave2.Opacity, 4);
        Assert.Null(window.VolumeIndicatorPercentText.Effect);
        Assert.Equal(TextFormattingMode.Ideal, TextOptions.GetTextFormattingMode(window));
        Assert.Equal(PenLineCap.Round, window.VolumeIndicatorMuteSlash.StrokeStartLineCap);
        Assert.Equal(PenLineJoin.Round, window.VolumeIndicatorSpeakerCone.StrokeLineJoin);
        Assert.Equal(0, brushes.Count(b => b.Color.A >= 0xF0));

        Set(window, "_currentVolume", 0f);
        Invoke(window, "ApplyVolumeStep", -120);
        Assert.Equal("0%", window.VolumeIndicatorPercentText.Text);

        Invoke(window, "DismissVolumeIndicatorImmediate", true, false);
        Assert.Equal(collapsedHeight, window.NotchBorder.Height, 3);
        Assert.Null(window.VolumeIndicatorContainer.Effect);

        // Verify full entrance + exit morph & transient blur cleanup
        Invoke(window, "ShowVolumeIndicator", .65f);
        await WpfFrameWaiter.UntilAsync(
            () => Math.Abs(window.VolumeIndicatorContainer.Opacity - 1.0) < 0.01 && window.VolumeIndicatorContainer.Effect == null,
            "volume HUD entrance morph + blur cleanup",
            ct);
        Assert.Equal(1.0, window.VolumeIndicatorContentScale.ScaleX, 2);
        Assert.Equal(1.0, window.VolumeIndicatorContentScale.ScaleY, 2);

        Invoke(window, "HideVolumeIndicator");
        await WpfFrameWaiter.UntilAsync(
            () => window.VolumeIndicatorContainer.Visibility == Visibility.Collapsed && !Get<bool>(window, "_isVolumeIndicatorExiting"),
            "volume HUD exit morph + blur cleanup",
            ct);
        Assert.Null(window.VolumeIndicatorContainer.Effect);
        Assert.Equal(1.0, window.CompactThumbnailScale.ScaleX, 2);
        ct.ThrowIfCancellationRequested();
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
    private static T Get<T>(MainWindow window, string name) => (T)(typeof(MainWindow).GetField(name, Private)?.GetValue(window) ?? typeof(MainWindow).GetProperty(name, Private)!.GetValue(window))!;
    private static void Set(MainWindow window, string name, object value) => typeof(MainWindow).GetField(name, Private)!.SetValue(window, value);
}

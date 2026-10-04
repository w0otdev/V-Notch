using System.Reflection;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Services;
using VNotch.Tests.Fakes;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class MainWindowGesturePresentationTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData(-100, -20)]
    [InlineData(100, 20)]
    [InlineData(10, 3)]
    public void DragFeedbackMovesNotchAndShadowTogetherAndSnapsBack(double input, double expected) => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = CreateFixture(new FakeMediaDetectionService());
        var window = fixture.Window;
        Invoke(window, "ApplyGestureDragFeedback", input);
        Assert.Equal(expected, window.NotchGestureTranslate.X);
        Assert.Equal(expected, window.NotchShadowGestureTranslate.X);
        Invoke(window, "AnimateGestureSnapBack");
        await WpfFrameWaiter.UntilAsync(() => window.NotchGestureTranslate.X == 0 && window.NotchShadowGestureTranslate.X == 0, "gesture snap back", ct);
        Assert.False(window.NotchGestureTranslate.HasAnimatedProperties);
        Assert.False(window.NotchShadowGestureTranslate.HasAnimatedProperties);
    });

    [Theory]
    [InlineData("PlayGestureSwipeFeedback", true)]
    [InlineData("PlayGestureSwipeFeedback", false)]
    [InlineData("PlayGestureSwipeDownFeedback", null)]
    [InlineData("PlayGestureDoubleTapFeedback", null)]
    [InlineData("PlayGestureMiddleClickFeedback", null)]
    public void GestureFeedbackRestoresScaleAndKeepsShadowAligned(string method, bool? left) => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = CreateFixture(new FakeMediaDetectionService());
        var window = fixture.Window;
        if (left.HasValue) Invoke(window, method, left.Value); else Invoke(window, method);
        await WpfFrameWaiter.NextAsync(ct);
        Assert.Equal(window.NotchScale.ScaleX, window.NotchShadowScale.ScaleX, 5);
        Assert.Equal(window.NotchScale.ScaleY, window.NotchShadowScale.ScaleY, 5);
        await WpfFrameWaiter.UntilAsync(() => window.NotchScale.ScaleX == 1 && window.NotchScale.ScaleY == 1 && window.NotchGestureTranslate.X == 0, "gesture scale settled", ct);
        Assert.Equal(1, window.NotchShadowScale.ScaleX);
        Assert.Equal(1, window.NotchShadowScale.ScaleY);
    });

    [Fact]
    public void GestureCommandsReachTheMediaServiceAndSuppressImmediateDuplicates() => SharedStaTestRunner.RunAsync(async ct =>
    {
        var media = new FakeMediaDetectionService();
        using var fixture = CreateFixture(media);
        var window = fixture.Window;
        foreach (var command in new[] { "OnGestureSwipeLeft", "OnGestureSwipeRight", "OnGestureDoubleTap" })
        {
            Set(window, "_lastMediaActionTime", DateTime.MinValue);
            int before = media.NextTrackCount + media.PreviousTrackCount + media.PlayPauseCount;
            Invoke(window, command);
            await WpfFrameWaiter.UntilAsync(() => media.NextTrackCount + media.PreviousTrackCount + media.PlayPauseCount == before + 1, "gesture media command", ct);
            Invoke(window, command);
            await WpfFrameWaiter.NextAsync(ct);
            await WpfFrameWaiter.NextAsync(ct);
            Assert.Equal(before + 1, media.NextTrackCount + media.PreviousTrackCount + media.PlayPauseCount);
        }
        Assert.Equal(1, media.NextTrackCount);
        Assert.Equal(1, media.PreviousTrackCount);
        Assert.Equal(1, media.PlayPauseCount);
        await WpfFrameWaiter.UntilAsync(() => window.NotchScale.ScaleX == 1 && window.NotchScale.ScaleY == 1, "gesture command feedback settled", ct);
    });

    private static GreetingAcceptanceTests.MainWindowFixture CreateFixture(FakeMediaDetectionService media) => new("en", greeting: false,
        configureSettings: settings => settings.EnableLocalOnlyMode = true,
        configureServices: services => services.AddSingleton<IMediaDetectionService>(media));
    private static void Set(MainWindow window, string name, object value) => typeof(MainWindow).GetField(name, Private)!.SetValue(window, value);
    private static object? Invoke(MainWindow window, string name, params object?[] args) => typeof(MainWindow).GetMethod(name, Private)!.Invoke(window, args);
}

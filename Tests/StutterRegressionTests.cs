using System.Reflection;
using System.Windows;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Contracts;
using VNotch.Controls;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class StutterRegressionTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ColdThumbnailTargetMatchesExpandedLayoutWithoutResizingShell(bool island) => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = new GreetingAcceptanceTests.MainWindowFixture("en", greeting: false,
            settings =>
            {
                settings.EnableDynamicIslandMode = island;
                settings.EnableLocalOnlyMode = true;
                settings.DisableMouseLeaveAutoClose = true;
            }, services => services.AddSingleton<IMediaDetectionService>(new Fakes.FakeMediaDetectionService()));
        var window = fixture.Window;
        window.SetDebugViewLock(true);
        window.ShowActivated = false;
        window.Show();
        await WpfFrameWaiter.UntilAsync(() => Field<bool>("_isStartupLayoutReady") && !IsAnimating(), "startup", ct);
        Field<IMediaDetectionService>("_mediaService").Stop();
        Call("SetupExpandedContentBeforeAnimation");
        double width = window.NotchBorder.Width;
        double height = window.NotchBorder.Height;
        var target = ((double X, double Y))Call("EnsureCachedThumbnailExpandTarget")!;
        Assert.Equal(width, window.NotchBorder.Width);
        Assert.Equal(height, window.NotchBorder.Height);
        window.SetDebugViewState("MediaExpanded");
        await WpfFrameWaiter.UntilAsync(() => !IsAnimating(), "media expansion", ct);
        window.UpdateLayout();
        var args = new object?[] { null };
        Assert.True((bool)typeof(MainWindow).GetMethod("TryComputeThumbnailExpandTarget", PrivateInstance)!.Invoke(window, args)!);
        var actual = ((double X, double Y))args[0]!;
        Assert.InRange(Math.Abs(actual.X - target.X), 0, 1.0);
        Assert.InRange(Math.Abs(actual.Y - target.Y), 0, 1.0);

        var artwork = System.Windows.Media.Imaging.BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 80, 120, 180, 255 }, 4);
        artwork.Freeze();
        typeof(MainWindow).GetMethod("AnimateThumbnailSwitchOnly", PrivateInstance)!.Invoke(window, [artwork, true]);
        Assert.False(window.ThumbnailOutBlur.HasAnimatedProperties);
        Assert.False(window.ThumbnailNextBlur.HasAnimatedProperties);
        Assert.False(window.CompactThumbnailOutBlur.HasAnimatedProperties);
        Assert.False(window.CompactThumbnailNextBlur.HasAnimatedProperties);
        await WpfFrameWaiter.UntilAsync(() => !Field<bool>("_isThumbnailSwitchActive"), "artwork handoff", ct);
        Assert.Same(artwork, window.ThumbnailImage.Source);
        Assert.Same(artwork, window.CompactThumbnail.Source);
        Assert.Equal(1, window.ThumbnailImage.Opacity);
        Assert.Equal(Visibility.Collapsed, window.ThumbnailImageNext.Visibility);

        T Field<T>(string name) => (T)typeof(MainWindow).GetField(name, PrivateInstance)!.GetValue(window)!;
        object? Call(string name) => typeof(MainWindow).GetMethod(name, PrivateInstance)!.Invoke(window, null);
        bool IsAnimating() => (bool)typeof(MainWindow).GetProperty("_isAnimating", PrivateInstance)!.GetValue(window)!;
    });

    [Fact]
    public void PinShadowExistsOnlyWhilePinIsRevealed() => SharedStaTestRunner.Run(() =>
    {
        var badge = new MediaPinBadge();
        Assert.Null(badge.Effect);
        badge.SetPinned(true, animate: false);
        Assert.NotNull(badge.Effect);
        badge.SetPinned(false, animate: false);
        Assert.Null(badge.Effect);
        var reveal = (DependencyProperty)typeof(MediaPinBadge).GetField("RevealProperty", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        badge.SetValue(reveal, 0.5);
        Assert.NotNull(badge.Effect);
        badge.SetValue(reveal, 0.0);
        Assert.Null(badge.Effect);
    });
}

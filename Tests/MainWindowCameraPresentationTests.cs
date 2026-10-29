using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Controllers;
using VNotch.Models;
using VNotch.Services;
using VNotch.Tests.Fakes;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class MainWindowCameraPresentationTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void CameraIsASeparateViewAndDoesNotOpenTheDeviceByDefault() => SharedStaTestRunner.Run(() =>
    {
        using var fixture = CreateFixture(false);
        var window = fixture.Window;
        Assert.True(window.CameraContent.IsAncestorOf(window.CameraSection));
        Assert.False(window.SecondaryContent.IsAncestorOf(window.CameraSection));
        Assert.False(Field<WebcamCaptureController>(window, "_camera").IsLifecycleActive);
        Assert.NotNull(window.ClipboardTrayView);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SyntheticFramesReuseBitmapGrowOnResolutionChangesAndDiscardStaleTokens(bool blur) => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = CreateFixture(blur);
        var window = fixture.Window;
        Set(window, "_isCameraView", true);
        var camera = Field<WebcamCaptureController>(window, "_camera");
        // Simulate the presentation state after capture starts; no device is opened.
        typeof(WebcamCaptureController).GetField("_isActive", Private)!.SetValue(camera, true);
        Invoke(window, "PrimeCameraPreviewMorphIn");
        byte[] firstPixels = Pixels(4, 3, 70);
        Invoke(window, "OnCameraFrameAvailable", firstPixels, 4, 3, camera.FadeToken);
        Invoke(window, "OnCameraFrameAvailable", Pixels(2, 2, 99), 2, 2, camera.FadeToken);
        await WpfFrameWaiter.UntilAsync(() => !Field<bool>(window, "_cameraFrameDispatchPending"), "first camera frame", ct);
        var first = Assert.IsType<WriteableBitmap>(window.CameraPreviewImage.Source);
        Assert.Equal(4, first.PixelWidth);
        Assert.Equal(firstPixels, CopyPixels(first));
        await WpfFrameWaiter.UntilAsync(() => window.CameraOverlay.Visibility == Visibility.Collapsed, "camera preview appeared", ct);
        await WpfFrameWaiter.UntilAsync(() => Math.Abs(window.CameraPreviewImage.Opacity - 1) < 1e-6, "camera preview morph settled", ct);
        Assert.Equal(1, window.CameraPreviewImage.Opacity, 5);
        Assert.Equal(0, window.CameraPreviewBlur.Radius);
        Invoke(window, "OnCameraFrameAvailable", Pixels(2, 2, 42), 2, 2, camera.FadeToken);
        await WpfFrameWaiter.UntilAsync(() => !Field<bool>(window, "_cameraFrameDispatchPending"), "smaller camera frame", ct);
        Assert.Same(first, window.CameraPreviewImage.Source);
        Assert.Equal(42, CopyPixels(first)[0]);
        Invoke(window, "OnCameraFrameAvailable", Pixels(5, 2, 13), 5, 2, camera.FadeToken);
        await WpfFrameWaiter.UntilAsync(() => !Field<bool>(window, "_cameraFrameDispatchPending"), "larger camera frame", ct);
        var grown = Assert.IsType<WriteableBitmap>(window.CameraPreviewImage.Source);
        Assert.NotSame(first, grown);
        Assert.Equal(5, grown.PixelWidth);
        Assert.Equal(3, grown.PixelHeight);
        Invoke(window, "OnCameraFrameAvailable", Pixels(5, 3, 88), 5, 3, camera.FadeToken - 1);
        await WpfFrameWaiter.UntilAsync(() => !Field<bool>(window, "_cameraFrameDispatchPending"), "stale camera frame discarded", ct);
        Assert.Equal(13, CopyPixels(grown)[0]);
        Invoke(window, "OnCameraFrameAvailable", Array.Empty<byte>(), 1, 1, camera.FadeToken);
        await WpfFrameWaiter.UntilAsync(() => !Field<bool>(window, "_cameraFrameDispatchPending"), "invalid camera frame released", ct);
        await (Task)Invoke(window, "StopCameraPreview")!;
        await WpfFrameWaiter.UntilAsync(() => window.CameraPreviewImage.Source == null, "preview stopped", ct);
        Assert.False(camera.IsLifecycleActive);
        Assert.Equal(1.06, window.CameraPreviewScale.ScaleX);
        Assert.Equal(blur ? 16 : 0, window.CameraPreviewBlur.Radius);
        Invoke(window, "StopCameraPreviewForViewExit", true);
        Assert.Equal(Visibility.Visible, window.CameraOverlay.Visibility);
        Invoke(window, "StopCameraPreviewForViewExit", false);
        Assert.Equal(Visibility.Collapsed, window.CameraOverlay.Visibility);
    });

    [Fact]
    public void CameraPreviewExitTransitionDefersVisualTeardownAndRestoresCleanlyOnCompletion() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = CreateFixture(false);
        var window = fixture.Window;
        Set(window, "_isCameraView", true);
        var camera = Field<WebcamCaptureController>(window, "_camera");

        typeof(WebcamCaptureController).GetField("_isActive", Private)!.SetValue(camera, true);
        Invoke(window, "PrimeCameraPreviewMorphIn");
        Invoke(window, "OnCameraFrameAvailable", Pixels(4, 3, 55), 4, 3, camera.FadeToken);
        await WpfFrameWaiter.UntilAsync(() => !Field<bool>(window, "_cameraFrameDispatchPending"), "camera frame received", ct);
        Assert.NotNull(window.CameraPreviewImage.Source);
        Assert.True(camera.IsLifecycleActive);

        // When switching views away from camera, transition begins:
        Invoke(window, "StopCameraPreviewForViewTransition");
        // Hardware is stopped immediately:
        Assert.False(camera.IsLifecycleActive);
        // But preview bitmap remains frozen for the exit crossfade:
        Assert.NotNull(window.CameraPreviewImage.Source);
        Assert.True(Field<bool>(window, "_pendingCameraPreviewVisualTeardown"));

        // On transition completion:
        Invoke(window, "FinalizePendingCameraPreviewTeardown");
        Assert.False(Field<bool>(window, "_pendingCameraPreviewVisualTeardown"));
        Assert.Null(window.CameraPreviewImage.Source);
        Assert.Equal(Visibility.Visible, window.CameraOverlay.Visibility);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CameraExitHoldsTheCurrentFrameThenFinishesAllVisuals(bool blur) => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = CreateFixture(blur);
        var window = fixture.Window;
        var camera = Field<WebcamCaptureController>(window, "_camera");
        typeof(WebcamCaptureController).GetField("_isActive", Private)!.SetValue(camera, true);
        var frame = new WriteableBitmap(2, 2, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
        window.CameraPreviewImage.Source = frame;
        window.CameraPreviewImage.Opacity = 0.73;
        window.CameraOverlay.Opacity = 0.2;
        window.CameraPreviewScale.ScaleX = 1.02;
        window.CameraPreviewScale.ScaleY = 1.03;
        window.CameraPreviewBlur.Radius = blur ? 4 : 0;

        await (Task)Invoke(window, "StopCameraPreview")!;
        var storyboard = Field<Storyboard>(window, "_cameraPreviewExitStoryboard");
        Assert.NotNull(storyboard);
        Assert.Equal(AnimationConfig.TargetFps, Timeline.GetDesiredFrameRate(storyboard));
        storyboard.SeekAlignedToLastTick(window, TimeSpan.FromMilliseconds(200), TimeSeekOrigin.BeginTime);
        Assert.Same(frame, window.CameraPreviewImage.Source);
        Assert.Equal(0.73, window.CameraPreviewImage.Opacity, 5);
        Assert.Equal(0.2, window.CameraOverlay.Opacity, 5);
        Assert.Equal(1.02, window.CameraPreviewScale.ScaleX, 5);
        Assert.Equal(1.03, window.CameraPreviewScale.ScaleY, 5);

        storyboard.SeekAlignedToLastTick(window, TimeSpan.FromMilliseconds(350), TimeSeekOrigin.BeginTime);
        Assert.InRange(window.CameraPreviewImage.Opacity, 0.01, 0.72);
        Assert.InRange(window.CameraOverlay.Opacity, 0.21, 1);
        if (!blur) Assert.Equal(0, window.CameraPreviewBlur.Radius);
        await WpfFrameWaiter.UntilAsync(() => window.CameraPreviewImage.Source == null, "camera exit completed", ct);
        Assert.Equal(0, window.CameraPreviewImage.Opacity);
        Assert.Equal(1, window.CameraOverlay.Opacity);
        Assert.Equal(1.06, window.CameraPreviewScale.ScaleX);
        Assert.Equal(1.06, window.CameraPreviewScale.ScaleY);
        Assert.Equal(blur ? 16 : 0, window.CameraPreviewBlur.Radius);
    });

    [Fact]
    public void RestartingPreviewCancelsTheOldExitStoryboard() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = CreateFixture(false);
        var window = fixture.Window;
        var camera = Field<WebcamCaptureController>(window, "_camera");
        typeof(WebcamCaptureController).GetField("_isActive", Private)!.SetValue(camera, true);
        window.CameraPreviewImage.Source = new WriteableBitmap(2, 2, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
        await (Task)Invoke(window, "StopCameraPreview")!;
        Assert.NotNull(Field<Storyboard?>(window, "_cameraPreviewExitStoryboard"));

        typeof(WebcamCaptureController).GetField("_isActive", Private)!.SetValue(camera, true);
        Invoke(window, "PrimeCameraPreviewMorphIn");
        Assert.Null(Field<Storyboard?>(window, "_cameraPreviewExitStoryboard"));
        Invoke(window, "OnCameraFrameAvailable", Pixels(4, 3, 42), 4, 3, camera.FadeToken);
        await WpfFrameWaiter.UntilAsync(() => Math.Abs(window.CameraPreviewImage.Opacity - 1) < 1e-6, "restarted preview appeared", ct);
        await Task.Delay(100, ct);
        Assert.NotNull(window.CameraPreviewImage.Source);
        Assert.Equal(1, window.CameraPreviewImage.Opacity);
        Assert.Equal(1, window.CameraPreviewScale.ScaleX);
    });

    [Fact]
    public void ReducedMotionStopsPreviewImmediately() => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        try
        {
            AnimationConfig.SetReduceMotion(true);
            await using var fixture = CreateFixture(false);
            var window = fixture.Window;
            window.CameraPreviewImage.Source = new WriteableBitmap(2, 2, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
            await (Task)Invoke(window, "StopCameraPreview")!;
            Assert.Null(window.CameraPreviewImage.Source);
            Assert.Null(Field<Storyboard?>(window, "_cameraPreviewExitStoryboard"));
            Assert.Equal(1, window.CameraOverlay.Opacity);
        }
        finally { AnimationConfig.SetReduceMotion(previous); }
    });

    [Theory]
    [InlineData(0.1)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    public void PreviewRemainsOpaqueWhenAppearanceOpacityChanges(double opacity) => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = CreateFixture(false);
        var window = fixture.Window;
        var settings = Field<NotchSettings>(window, "_settings");
        Invoke(window, "SetDesktopRevealOpacityImmediate", 1d);
        settings.Opacity = opacity;
        Invoke(window, "ApplyAppearanceOpacity");
        Assert.Null(window.NotchBackground.OpacityMask);
        Assert.Null(window.SecondaryContent.OpacityMask);
        Assert.Null(window.NotchShadowWrapper.OpacityMask);
        // The test harness coerces every Window's effective opacity to zero.
        Assert.Equal(opacity, window.ReadLocalValue(UIElement.OpacityProperty));

        Set(window, "_isCameraView", true);
        var camera = Field<WebcamCaptureController>(window, "_camera");
        typeof(WebcamCaptureController).GetField("_isActive", Private)!.SetValue(camera, true);
        window.CameraContent.Visibility = Visibility.Visible;
        window.CameraContent.Opacity = 1;
        Invoke(window, "PrimeCameraPreviewMorphIn");
        Invoke(window, "OnCameraFrameAvailable", Pixels(4, 3, 255), 4, 3, camera.FadeToken);
        await WpfFrameWaiter.UntilAsync(() => Math.Abs(window.CameraPreviewImage.Opacity - 1) < 1e-6, "opaque camera preview", ct);
        Assert.Equal(1d, window.ReadLocalValue(UIElement.OpacityProperty));
        Assert.Equal(opacity, Assert.IsAssignableFrom<Brush>(window.NotchBackground.OpacityMask).Opacity);
        Assert.Equal(opacity, Assert.IsAssignableFrom<Brush>(window.SecondaryContent.OpacityMask).Opacity);
        Assert.Equal(opacity, Assert.IsAssignableFrom<Brush>(window.NotchShadowWrapper.OpacityMask).Opacity);

        // Every ancestor participates in the final pixel alpha, so a local
        // image opacity of 1 alone is insufficient to prevent this regression.
        for (DependencyObject? ancestor = window.CameraPreviewImage; ancestor != null; ancestor = VisualTreeHelper.GetParent(ancestor))
        {
            if (ancestor is UIElement element && ancestor is not Window)
            {
                Assert.Equal(1, element.Opacity);
                Assert.Null(element.OpacityMask);
            }
        }

        settings.Opacity = 0.35;
        Invoke(window, "ApplyAppearanceOpacity");
        Assert.Equal(1d, window.ReadLocalValue(UIElement.OpacityProperty));
        Assert.Equal(1, window.CameraPreviewImage.Opacity);
        Assert.Equal(0.35, Assert.IsAssignableFrom<Brush>(window.NotchBackground.OpacityMask).Opacity);
        Assert.Null(window.CameraContent.OpacityMask);
        await (Task)Invoke(window, "StopCameraPreview")!;
        await WpfFrameWaiter.UntilAsync(() => window.CameraPreviewImage.Source == null, "camera opacity restored on stop", ct);
        Assert.Equal(0.35, window.ReadLocalValue(UIElement.OpacityProperty));
        Assert.Null(window.NotchBackground.OpacityMask);
        Assert.Null(window.SecondaryContent.OpacityMask);
        Assert.Null(window.NotchShadowWrapper.OpacityMask);
    });

    private static byte[] Pixels(int width, int height, byte value) => Enumerable.Repeat(value, width * height * 4).ToArray();
    private static byte[] CopyPixels(WriteableBitmap bitmap)
    {
        var bytes = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(bytes, bitmap.PixelWidth * 4, 0);
        return bytes;
    }
    private static GreetingAcceptanceTests.MainWindowFixture CreateFixture(bool blur) => new("en", greeting: false,
        configureSettings: settings => { settings.EnableLocalOnlyMode = true; settings.EnableBlurEffects = blur; },
        configureServices: services => services.AddSingleton<IMediaDetectionService>(new FakeMediaDetectionService()));
    private static T Field<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, Private)!.GetValue(window)!;
    private static void Set(MainWindow window, string name, object value)
    {
        var property = typeof(MainWindow).GetProperty(name, Private);
        if (property != null) property.SetValue(window, value);
        else typeof(MainWindow).GetField(name, Private)!.SetValue(window, value);
    }
    private static object? Invoke(MainWindow window, string name, params object?[] args) => typeof(MainWindow).GetMethod(name, Private)!.Invoke(window, args);
}

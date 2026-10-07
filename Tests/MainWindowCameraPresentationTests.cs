using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Controllers;
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
        using var fixture = CreateFixture(blur);
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
        await WpfFrameWaiter.UntilAsync(() => window.CameraPreviewImage.Opacity == 0.8, "camera preview morph settled", ct);
        Assert.Equal(0.8, window.CameraPreviewImage.Opacity, 5);
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
        using var fixture = CreateFixture(false);
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

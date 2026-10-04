using System.Reflection;
using System.Runtime.InteropServices.WindowsRuntime;
using VNotch.Controllers;
using Windows.Graphics.Imaging;
using Xunit;

namespace VNotch.Tests;

public sealed class WebcamFramePipelineTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SyntheticFramesAreCopiedAndConvertedBeforeBeingHandedToTheUi(bool gray)
    {
        using var controller = new WebcamCaptureController();
        int token = Activate(controller);
        byte[] input = gray ? Enumerable.Repeat((byte)128, 8 * 4).ToArray() : Enumerable.Range(0, 8 * 4).SelectMany(_ => new byte[] { 10, 20, 30, 255 }).ToArray();
        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(input.AsBuffer(), gray ? BitmapPixelFormat.Gray8 : BitmapPixelFormat.Bgra8, 8, 4, gray ? BitmapAlphaMode.Ignore : BitmapAlphaMode.Premultiplied);
        byte[]? output = null;
        int calls = 0;
        controller.FrameAvailable += (pixels, width, height, observedToken) =>
        {
            calls++;
            Assert.Equal(8, width);
            Assert.Equal(4, height);
            Assert.Equal(token, observedToken);
            output = pixels;
        };
        controller.ProcessFrame(bitmap, token);
        Assert.Equal(1, calls);
        Assert.NotNull(output);
        Assert.True(output.Length >= 8 * 4 * 4);
        if (gray) Assert.Equal(new byte[] { 128, 128, 128, 255 }, output.Take(4));
        else Assert.Equal(input, output.Take(input.Length));
        Assert.Equal(1, Get<int>(controller, "_frameBufferInUse"));
        Set(controller, "_lastFrameTimestamp", 0L);
        controller.ProcessFrame(bitmap, token);
        Assert.Equal(1, calls);
        controller.ReleaseFrameBuffer();
        Set(controller, "_lastFrameTimestamp", 0L);
        controller.ProcessFrame(bitmap, token);
        Assert.Equal(2, calls);
        Assert.Same(output, Get<byte[]>(controller, "_frameBuffer"));
        controller.ReleaseFrameBuffer();
        controller.DetachForSafeStop();
        controller.ProcessFrame(bitmap, token);
        Assert.Equal(2, calls);
        Assert.False(controller.IsActive);
    }

    [Fact]
    public void ANewResolutionGrowsTheBufferAndSmallerFramesReuseIt()
    {
        using var controller = new WebcamCaptureController();
        int token = Activate(controller);
        controller.FrameAvailable += (_, _, _, _) => controller.ReleaseFrameBuffer();
        using var first = new SoftwareBitmap(BitmapPixelFormat.Bgra8, 8, 4, BitmapAlphaMode.Premultiplied);
        controller.ProcessFrame(first, token);
        byte[] initial = Get<byte[]>(controller, "_frameBuffer");
        Set(controller, "_lastFrameTimestamp", 0L);
        using var larger = new SoftwareBitmap(BitmapPixelFormat.Bgra8, 16, 16, BitmapAlphaMode.Premultiplied);
        controller.ProcessFrame(larger, token);
        byte[] grown = Get<byte[]>(controller, "_frameBuffer");
        Assert.NotSame(initial, grown);
        Assert.True(grown.Length >= 16 * 16 * 4);
        Set(controller, "_lastFrameTimestamp", 0L);
        controller.ProcessFrame(first, token);
        Assert.Same(grown, Get<byte[]>(controller, "_frameBuffer"));
    }

    [Fact]
    public void MissingOrFailingSubscribersReleaseTheFrameLeaseAndExpiredTokensAreDiscarded()
    {
        using var controller = new WebcamCaptureController();
        int token = Activate(controller);
        using var frame = new SoftwareBitmap(BitmapPixelFormat.Bgra8, 8, 4, BitmapAlphaMode.Premultiplied);
        controller.ProcessFrame(frame, token);
        Assert.Equal(0, Get<int>(controller, "_frameBufferInUse"));
        int calls = 0;
        controller.FrameAvailable += (_, _, _, _) => { calls++; throw new InvalidOperationException("Fixture presentation failure"); };
        Set(controller, "_lastFrameTimestamp", 0L);
        Assert.Throws<InvalidOperationException>(() => controller.ProcessFrame(frame, token));
        Assert.Equal(1, calls);
        Assert.Equal(0, Get<int>(controller, "_frameBufferInUse"));
        controller.NextFadeToken();
        controller.ProcessFrame(frame, token);
        Assert.Equal(1, calls);
    }

    private static int Activate(WebcamCaptureController controller)
    {
        int token = controller.NextFadeToken();
        Set(controller, "_isActive", true);
        return token;
    }
    private static T Get<T>(WebcamCaptureController controller, string name) => (T)typeof(WebcamCaptureController).GetField(name, Private)!.GetValue(controller)!;
    private static void Set(WebcamCaptureController controller, string name, object value) => typeof(WebcamCaptureController).GetField(name, Private)!.SetValue(controller, value);
}

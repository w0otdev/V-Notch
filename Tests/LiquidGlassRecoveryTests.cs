using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using VNotch.Controllers;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class LiquidGlassRecoveryTests
{
    private const int FrameWidth = 8;
    private const int FrameHeight = 4;

    [Theory]
    [InlineData("missing")]
    [InlineData("stale")]
    [InlineData("unfiltered")]
    [InlineData("black")]
    public void FailedRecoveryProbesKeepTheWorkingFallback(string failure)
    {
        SharedStaTestRunner.Run(() =>
        {
            using var source = CreateSource();
            switch (failure)
            {
                case "missing": Install(source, Array.Empty<byte>(), 0, frame: false); break;
                case "stale": Install(source, Pixels(128), 1000); break;
                case "unfiltered": source.AddExcludeHwnd(new IntPtr(0x1234)); break;
                case "black": Install(source, new byte[FrameWidth * FrameHeight * 4], 0); break;
            }

            var controller = new LiquidGlassController(new Image(), () => IntPtr.Zero, () => null);
            Set(controller, "_mag", source);
            Set(controller, "_magFailStreak", 60);
            for (int probe = 0; probe < 4; probe++)
            {
                Assert.False(controller.TryRestoreMagnifier(0, 0, 4, 4));
                Assert.False(Get<bool>(controller, "_magReady"));
                Assert.Equal(60, Get<int>(controller, "_magFailStreak"));
            }
        });
    }

    [Fact]
    public void RecoveryChecksTheRequestedCropAndResumesWhenLivePixelsArrive()
    {
        SharedStaTestRunner.Run(() =>
        {
            using var source = CreateSource();
            var pixels = Pixels(128);
            for (int y = 0; y < 4; y++)
                Array.Clear(pixels, y * FrameWidth * 4, 4 * 4);
            Install(source, pixels, 0);
            var controller = new LiquidGlassController(new Image(), () => IntPtr.Zero, () => null);
            Set(controller, "_mag", source);
            Set(controller, "_magFailStreak", 60);

            Assert.False(controller.TryRestoreMagnifier(0, 0, 4, 4));
            Install(source, Pixels(128), 0);
            Assert.True(controller.TryRestoreMagnifier(0, 0, 4, 4));
            Assert.True(Get<bool>(controller, "_magReady"));
            Assert.Equal(0, Get<int>(controller, "_magFailStreak"));

            IntPtr destination = Marshal.AllocHGlobal(4 * 4 * 4);
            try
            {
                Assert.True(source.CaptureInto(0, 0, 4, 4, destination));
                Assert.Equal(128, Marshal.ReadByte(destination));
            }
            finally { Marshal.FreeHGlobal(destination); }
        });
    }

    [Fact]
    public void CaptureRejectsStalledCallbacksAndAcceptsFreshOpaqueBlackFrames()
    {
        using var source = CreateSource();
        var pixels = new byte[FrameWidth * FrameHeight * 4];
        for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        IntPtr destination = Marshal.AllocHGlobal(4 * 4 * 4);
        try
        {
            Install(source, pixels, 1000);
            Assert.False(source.CaptureInto(0, 0, 4, 4, destination));
            Assert.False(source.HasUsableFrame(0, 0, 4, 4));

            // Freshness comes from callback delivery, not a change in pixel values.
            Install(source, pixels, 0);
            Assert.True(source.CaptureInto(0, 0, 4, 4, destination));
            Assert.True(source.HasUsableFrame(0, 0, 4, 4));
            Assert.Equal(0, Marshal.ReadByte(destination));
            Assert.Equal(255, Marshal.ReadByte(destination, 3));
        }
        finally { Marshal.FreeHGlobal(destination); }
    }

    private static byte[] Pixels(byte value) => Enumerable.Repeat(value, FrameWidth * FrameHeight * 4).ToArray();

    private static void Install(MagnifierCaptureSource source, byte[] pixels, long ageMs, bool frame = true) =>
        source.InstallFrameForTest(true, true, frame ? FrameWidth : 0, frame ? FrameHeight : 0, pixels, ageMs);

    // The native source owns frame state; the test seam installs a ready source without a pump thread.
    private static MagnifierCaptureSource CreateSource()
    {
        var source = new MagnifierCaptureSource();
        Install(source, Pixels(128), 0);
        return source;
    }

    private static void Set(object target, string field, object value) =>
        target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private static T Get<T>(object target, string field) =>
        (T)target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
}

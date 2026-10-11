using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using VNotch.Controllers;
using Xunit;

namespace VNotch.Tests;

public sealed class LiquidGlassRecoveryTests
{
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
                case "missing": Set(source, "_hasCompletedFrame", false); break;
                case "stale": Set(source, "_completedFrameTicks", Environment.TickCount64 - 1000); break;
                case "unfiltered": Set(source, "_filterVersion", 1); break;
                case "black": Set(source, "_completedBuffer", new byte[8 * 4 * 4]); break;
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
            var pixels = Get<byte[]>(source, "_completedBuffer");
            for (int y = 0; y < 4; y++)
                Array.Clear(pixels, y * 8 * 4, 4 * 4);
            var controller = new LiquidGlassController(new Image(), () => IntPtr.Zero, () => null);
            Set(controller, "_mag", source);
            Set(controller, "_magFailStreak", 60);

            Assert.False(controller.TryRestoreMagnifier(0, 0, 4, 4));
            Array.Fill(pixels, (byte)128);
            Set(source, "_completedFrameTicks", Environment.TickCount64);
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

    private static MagnifierCaptureSource CreateSource()
    {
        var source = new MagnifierCaptureSource();
        Set(source, "<IsReady>k__BackingField", true);
        Set(source, "_running", true);
        Set(source, "_hasCompletedFrame", true);
        Set(source, "_completedWidth", 8);
        Set(source, "_completedHeight", 4);
        Set(source, "_completedBuffer", Enumerable.Repeat((byte)128, 8 * 4 * 4).ToArray());
        Set(source, "_completedFrameTicks", Environment.TickCount64);
        return source;
    }

    private static void Set(object target, string field, object value) =>
        target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private static T Get<T>(object target, string field) =>
        (T)target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
}
